using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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
/// Google Drive API v3 over HTTPS, read-only. Retries rate limits and server errors a few times,
/// and gets a new access token once if the API rejects one.
/// </summary>
public sealed class DriveRestClient(
    HttpClient http,
    IAccessTokenProvider tokens,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IDriveApi
{
    public const string BaseUri = "https://www.googleapis.com/drive/v3/";

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

    /// <summary>Escapes a value inside single quotes in a Drive search query.</summary>
    internal static string EscapeQueryValue(string value) => value.Replace(@"\", @"\\").Replace("'", @"\'");

    private async Task<JsonDocument> GetJsonAsync(string relativeUri, CancellationToken cancellationToken)
    {
        var refreshedToken = false;
        for (var attempt = 1; ; attempt++)
        {
            var token = await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUri + relativeUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return JsonDocument.Parse(body);
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

            throw new DriveApiException(
                (int)response.StatusCode,
                $"Google Drive returned {(int)response.StatusCode} {response.ReasonPhrase}: {message ?? "no details"}");
        }
    }

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
