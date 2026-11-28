using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PhotoSift.Core.GoogleDrive;

/// <summary>Supplies OAuth access tokens for API calls.</summary>
public interface IAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);

    /// <summary>Called when the API rejected a token (401), so the next call gets a fresh one.</summary>
    void InvalidateAccessToken(string accessToken);
}

/// <summary>
/// Google Drive API v3 over HTTPS. Retries rate limits and server errors of metadata requests a
/// few times, and gets a new access token once if the API rejects one. Upload chunks are not
/// retried here: the uploader asks Google how much arrived and continues from there.
/// </summary>
public sealed class DriveRestClient(
    HttpClient http,
    IAccessTokenProvider tokens,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IDriveApi
{
    public const string BaseUri = "https://www.googleapis.com/drive/v3/";
    public const string UploadUri = "https://www.googleapis.com/upload/drive/v3/files";

    private const string ItemFields = "id,name,mimeType,size,md5Checksum,modifiedTime,parents,trashed,thumbnailLink";
    private const int MaxThumbnailBytes = 2 * 1024 * 1024;
    private const int MaxAttempts = 4;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task<DriveAccount> GetAccountAsync(CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync("about?fields=user(emailAddress,permissionId)", cancellationToken).ConfigureAwait(false);
        var user = json.RootElement.GetProperty("user");
        return new DriveAccount(
            user.GetProperty("permissionId").GetString() ?? throw new DriveApiException(0, "Google did not return an account id."),
            user.TryGetProperty("emailAddress", out var email) ? email.GetString() ?? string.Empty : string.Empty);
    }

    public async Task<DriveItem> GetItemAsync(string id, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync($"files/{Uri.EscapeDataString(id)}?fields={ItemFields}", cancellationToken).ConfigureAwait(false);
        return ParseItem(json.RootElement);
    }

    public async Task<IReadOnlyList<DriveItem>> ListChildrenAsync(string folderId, bool foldersOnly, CancellationToken cancellationToken)
    {
        var query = $"'{EscapeQueryValue(folderId)}' in parents and trashed = false";
        if (foldersOnly)
        {
            query += $" and mimeType = '{DriveItem.FolderMimeType}'";
        }

        var items = new List<DriveItem>();
        string? pageToken = null;
        do
        {
            var uri = "files?spaces=drive&pageSize=1000&orderBy=name" +
                      $"&fields=nextPageToken,files({ItemFields})&q={Uri.EscapeDataString(query)}" +
                      (pageToken is null ? string.Empty : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            using var json = await GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
            if (json.RootElement.TryGetProperty("files", out var files))
            {
                items.AddRange(files.EnumerateArray().Select(ParseItem));
            }

            pageToken = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        }
        while (pageToken is not null);

        return items;
    }

    public async Task<byte[]> DownloadThumbnailAsync(string thumbnailLink, CancellationToken cancellationToken)
    {
        // The access token is sent with the request, so only to Google's own image hosts over HTTPS.
        if (!DriveThumbnails.IsTrustedLink(thumbnailLink))
        {
            throw new DriveApiException(0, "Google Drive returned a thumbnail link to an unexpected address.");
        }

        var refreshedToken = false;
        while (true)
        {
            var token = await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, thumbnailLink);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshedToken)
            {
                tokens.InvalidateAccessToken(token);
                refreshedToken = true;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DriveApiException((int)response.StatusCode, $"Google Drive returned {(int)response.StatusCode} for a thumbnail.");
            }

            if (response.Content.Headers.ContentLength > MaxThumbnailBytes)
            {
                throw new DriveApiException(0, "The Google Drive thumbnail is unexpectedly large.");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length <= MaxThumbnailBytes
                ? bytes
                : throw new DriveApiException(0, "The Google Drive thumbnail is unexpectedly large.");
        }
    }

    public async Task<DriveItem> CreateFolderAsync(string name, string parentId, CancellationToken cancellationToken)
    {
        using var json = await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{BaseUri}files?fields={ItemFields}")
        {
            Content = JsonContent.Create(new { name, mimeType = DriveItem.FolderMimeType, parents = new[] { parentId } }),
        }, cancellationToken).ConfigureAwait(false);
        return ParseItem(json.RootElement);
    }

    public async Task<Uri> StartUploadAsync(DriveNewFile file, CancellationToken cancellationToken)
    {
        var refreshedToken = false;
        while (true)
        {
            var token = await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            var metadata = new Dictionary<string, object> { ["name"] = file.Name, ["parents"] = new[] { file.ParentId } };
            if (file.ModifiedUtc is { } modified)
            {
                metadata["modifiedTime"] = modified.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{UploadUri}?uploadType=resumable&fields={ItemFields}")
            {
                Content = JsonContent.Create(metadata),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-Upload-Content-Length", file.Size.ToString(CultureInfo.InvariantCulture));
            if (file.MimeType is not null)
            {
                request.Headers.Add("X-Upload-Content-Type", file.MimeType);
            }

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshedToken)
            {
                tokens.InvalidateAccessToken(token);
                refreshedToken = true;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var (message, reason) = ParseError(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                throw Error(response, message, reason);
            }

            var session = response.Headers.Location;
            return session is not null && IsTrustedUploadSession(session)
                ? session
                : throw new DriveApiException(0, "Google Drive did not return a valid upload address.");
        }
    }

    public Task<DriveUploadStatus> UploadChunkAsync(
        Uri session, ReadOnlyMemory<byte> chunk, long offset, long totalSize, CancellationToken cancellationToken)
    {
        var end = offset + chunk.Length - 1;
        return SendUploadAsync(session, () => new ReadOnlyMemoryContent(chunk), $"bytes {offset}-{end}/{totalSize}", cancellationToken);
    }

    public Task<DriveUploadStatus> GetUploadStatusAsync(Uri session, long totalSize, CancellationToken cancellationToken) =>
        SendUploadAsync(session, () => new ByteArrayContent([]), $"bytes */{totalSize}", cancellationToken);

    public async Task TrashAsync(string id, CancellationToken cancellationToken)
    {
        using var json = await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Patch, $"{BaseUri}files/{Uri.EscapeDataString(id)}?fields=id")
        {
            Content = JsonContent.Create(new { trashed = true }),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Upload sessions get the access token, so they must be on Google's API host over HTTPS.</summary>
    internal static bool IsTrustedUploadSession(Uri session) =>
        session is { IsAbsoluteUri: true, Scheme: "https", Host: "www.googleapis.com" } &&
        session.AbsolutePath.StartsWith("/upload/drive/", StringComparison.Ordinal);

    private async Task<DriveUploadStatus> SendUploadAsync(
        Uri session, Func<HttpContent> createContent, string contentRange, CancellationToken cancellationToken)
    {
        if (!IsTrustedUploadSession(session))
        {
            throw new DriveApiException(0, "The upload address is not a Google Drive address.");
        }

        var refreshedToken = false;
        while (true)
        {
            var token = await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = createContent() };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content.Headers.Add("Content-Range", contentRange);

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode == 308)
            {
                // "Resume Incomplete": the Range header says which bytes arrived (none if it is missing).
                var received = 0L;
                if (response.Headers.TryGetValues("Range", out var ranges) &&
                    ranges.FirstOrDefault() is { } range && range.LastIndexOf('-') is var dash and > 0 &&
                    long.TryParse(range[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var last))
                {
                    received = last + 1;
                }

                return new DriveUploadStatus(received, null);
            }

            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(body);
                var file = ParseItem(json.RootElement);
                return new DriveUploadStatus(file.Size ?? 0, file);
            }

            var (message, reason) = ParseError(body);
            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshedToken)
            {
                tokens.InvalidateAccessToken(token);
                refreshedToken = true;
                continue;
            }

            throw Error(response, message, reason);
        }
    }

    /// <summary>Escapes a value inside single quotes in a Drive search query.</summary>
    internal static string EscapeQueryValue(string value) => value.Replace(@"\", @"\\").Replace("'", @"\'");

    private Task<JsonDocument> GetJsonAsync(string relativeUri, CancellationToken cancellationToken) =>
        SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, BaseUri + relativeUri), cancellationToken);

    private async Task<JsonDocument> SendJsonAsync(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        var refreshedToken = false;
        for (var attempt = 1; ; attempt++)
        {
            var token = await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            using var request = createRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return JsonDocument.Parse(body.Length == 0 ? "{}" : body);
            }

            var (message, reason) = ParseError(body);
            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshedToken)
            {
                tokens.InvalidateAccessToken(token);
                refreshedToken = true;
                continue;
            }

            if (IsRetryable(response.StatusCode, reason) && attempt < MaxAttempts)
            {
                await _delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw Error(response, message, reason);
        }
    }

    private static DriveApiException Error(HttpResponseMessage response, string? message, string? reason) =>
        new((int)response.StatusCode,
            $"Google Drive returned {(int)response.StatusCode} {response.ReasonPhrase}: {message ?? "no details"}",
            reason);

    private static bool IsRetryable(HttpStatusCode status, string? reason) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout ||
        (status == HttpStatusCode.Forbidden && reason is "rateLimitExceeded" or "userRateLimitExceeded");

    private static (string? Message, string? Reason) ParseError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            string? reason = null;
            if (error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                reason = errors.EnumerateArray()
                    .Select(e => e.TryGetProperty("reason", out var r) ? r.GetString() : null)
                    .FirstOrDefault(r => r is not null);
            }

            return (message, reason);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static DriveItem ParseItem(JsonElement e)
    {
        string? Str(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var size = Str("size");
        var modified = Str("modifiedTime");
        var parents = e.TryGetProperty("parents", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList()
            : [];

        return new DriveItem(
            Str("id") ?? throw new DriveApiException(0, "Google Drive returned an item without an id."),
            Str("name") ?? string.Empty,
            Str("mimeType") ?? string.Empty,
            size is null ? null : long.Parse(size, CultureInfo.InvariantCulture),
            Str("md5Checksum"),
            modified is null
                ? DateTime.UnixEpoch
                : DateTime.Parse(modified, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            parents,
            e.TryGetProperty("trashed", out var t) && t.ValueKind == JsonValueKind.True,
            Str("thumbnailLink"));
    }
}
