using System.Net;
using System.Text;
using System.Web;
using PhotoSift.Core.GoogleDrive;
using PhotoSift.Core.GoogleDrive.Auth;

namespace PhotoSift.Tests;

/// <summary>An in-memory My Drive. The root folder has the id "root-id" and the alias "root".</summary>
public sealed class FakeDriveApi : IDriveApi
{
    public const string RootId = "root-id";

    private readonly Dictionary<string, DriveItem> _items = new(StringComparer.Ordinal);
    private int _nextId;

    public FakeDriveApi() =>
        _items[RootId] = new DriveItem(RootId, "My Drive", DriveItem.FolderMimeType, null, null, DateTime.UnixEpoch, []);

    public DriveAccount Account { get; set; } = new("12345", "user@example.com");

    public HashSet<string> FailListingFor { get; } = new(StringComparer.Ordinal);

    public int GetItemCalls { get; private set; }

    /// <summary>Every thumbnail link that was downloaded (or attempted).</summary>
    public List<string> ThumbnailRequests { get; } = [];

    /// <summary>Simulates a scope that does not allow thumbnails: every download is refused (403).</summary>
    public bool RefuseThumbnails { get; set; }

    /// <summary>Gives every file a new thumbnail link; the old links are refused, as when they expire.</summary>
    public void ExpireThumbnailLinks()
    {
        foreach (var item in _items.Values.Where(i => i.ThumbnailLink is not null).ToList())
        {
            _items[item.Id] = item with { ThumbnailLink = item.ThumbnailLink!.Replace("=s220", "x=s220") };
        }
    }

    public static byte[] ThumbnailBytesFor(string id) => [1, 2, (byte)id.Length];

    public string AddFolder(string name, string parentId = RootId) =>
        Add(new DriveItem(NewId(), name, DriveItem.FolderMimeType, null, null, DateTime.UnixEpoch, [parentId]));

    /// <summary>A folder that is not in My Drive (e.g. in a shared drive): it has no parent in this tree.</summary>
    public string AddForeignFolder(string name) =>
        Add(new DriveItem(NewId(), name, DriveItem.FolderMimeType, null, null, DateTime.UnixEpoch, []));

    public string AddFile(string name, string parentId, string? md5, long size = 4096, string mimeType = "image/jpeg", bool thumbnail = true)
    {
        var id = NewId();
        return Add(new DriveItem(id, name, mimeType, size, md5, new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), [parentId],
            ThumbnailLink: thumbnail ? $"https://lh3.googleusercontent.com/drive-storage/{id}=s220" : null));
    }

    public void Update(string id, Func<DriveItem, DriveItem> change) => _items[id] = change(_items[id]);

    public void Remove(string id) => _items.Remove(id);

    public Task<DriveAccount> GetAccountAsync(CancellationToken cancellationToken) => Task.FromResult(Account);

    public Task<DriveItem> GetItemAsync(string id, CancellationToken cancellationToken)
    {
        GetItemCalls++;
        return _items.TryGetValue(id == "root" ? RootId : id, out var item)
            ? Task.FromResult(item)
            : throw new DriveApiException(404, "File not found.");
    }

    public Task<byte[]> DownloadThumbnailAsync(string thumbnailLink, CancellationToken cancellationToken)
    {
        ThumbnailRequests.Add(thumbnailLink);
        var current = _items.Values.FirstOrDefault(i =>
            i.ThumbnailLink is { } link && DriveThumbnails.WithSize(link, 0) == DriveThumbnails.WithSize(thumbnailLink, 0));
        return RefuseThumbnails || current is null
            ? throw new DriveApiException(403, "Forbidden (simulated).")
            : Task.FromResult(ThumbnailBytesFor(current.Id));
    }

    public Task<IReadOnlyList<DriveItem>> ListChildrenAsync(string folderId, bool foldersOnly, CancellationToken cancellationToken)
    {
        if (FailListingFor.Contains(folderId))
        {
            throw new DriveApiException(500, "Internal error (simulated).");
        }

        IReadOnlyList<DriveItem> children = _items.Values
            .Where(i => i.Parents.Contains(folderId) && !i.Trashed && (!foldersOnly || i.IsFolder))
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(children);
    }

    private string NewId() => $"id{++_nextId}";

    private string Add(DriveItem item)
    {
        _items[item.Id] = item;
        return item.Id;
    }
}

public sealed class FakeDriveSession(FakeDriveApi api) : IDriveSession
{
    public bool IsConnected { get; set; } = true;

    public DriveAccount? Account => IsConnected ? api.Account : null;

    public IDriveApi? Api => IsConnected ? api : null;
}

/// <summary>Answers HTTP requests with a function and records each request with its body.</summary>
public sealed class FakeHttpHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Parses a form-encoded request body.</summary>
    public static Dictionary<string, string> Form(string body)
    {
        var parsed = HttpUtility.ParseQueryString(body);
        return parsed.AllKeys.OfType<string>().ToDictionary(k => k, k => parsed[k]!);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add((request, body));
        }

        return respond(request, body);
    }
}

public sealed class FakeTokenStore : ITokenStore
{
    public StoredDriveAccount? Saved { get; set; }

    public string? UnavailableReason { get; set; }

    public StoredDriveAccount? Load() => Saved;

    public void Save(StoredDriveAccount account) => Saved = account;

    public void Delete() => Saved = null;
}

public sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// Plays the user's browser: reads Google's authorization URL and sends requests to PhotoSift's
/// loopback redirect, optionally with a few bad requests first.
/// </summary>
public sealed class FakeBrowser
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { UseProxy = false });

    public Uri? OpenedUri { get; private set; }

    public Dictionary<string, string> AuthorizationQuery { get; private set; } = [];

    /// <summary>Query string the "browser" returns to the redirect URI, or null to use code=the-code with the right state.</summary>
    public Func<string, string>? RedirectQuery { get; set; }

    /// <summary>Requests sent before the real redirect, and the status codes PhotoSift answered with.</summary>
    public List<string> BadRequestPaths { get; } = [];

    public List<HttpStatusCode> BadRequestStatuses { get; } = [];

    public HttpStatusCode? RedirectStatus { get; private set; }

    public Task OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        OpenedUri = uri;
        AuthorizationQuery = FakeHttpHandler.Form(uri.Query.TrimStart('?'));
        var redirectUri = AuthorizationQuery["redirect_uri"];
        var state = AuthorizationQuery["state"];

        // Like a real browser, this happens after PhotoSift has started waiting.
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var path in BadRequestPaths)
                {
                    using var bad = await Client.GetAsync(redirectUri.TrimEnd('/') + path, cancellationToken);
                    BadRequestStatuses.Add(bad.StatusCode);
                }

                var query = RedirectQuery?.Invoke(state) ?? $"code=the-code&state={Uri.EscapeDataString(state)}";
                using var response = await Client.GetAsync($"{redirectUri}?{query}", cancellationToken);
                RedirectStatus = response.StatusCode;
            }
            catch (Exception ex)
            {
                Failure = ex;
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>Set if a request to PhotoSift failed, e.g. because the connection was reset.</summary>
    public Exception? Failure { get; private set; }
}
