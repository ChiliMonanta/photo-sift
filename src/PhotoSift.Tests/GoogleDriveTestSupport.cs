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

    public string AddFolder(string name, string parentId = RootId) =>
        Add(new DriveItem(NewId(), name, DriveItem.FolderMimeType, null, null, DateTime.UnixEpoch, [parentId]));

    /// <summary>A folder that is not in My Drive (e.g. in a shared drive): it has no parent in this tree.</summary>
    public string AddForeignFolder(string name) =>
        Add(new DriveItem(NewId(), name, DriveItem.FolderMimeType, null, null, DateTime.UnixEpoch, []));

    public string AddFile(string name, string parentId, string? md5, long size = 4096, string mimeType = "image/jpeg") =>
        Add(new DriveItem(NewId(), name, mimeType, size, md5, new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), [parentId]));

    public void Update(string id, Func<DriveItem, DriveItem> change) => _items[id] = change(_items[id]);

    public void Remove(string id) => _items.Remove(id);

    public Task<DriveAccount> GetAccountAsync(CancellationToken cancellationToken) => Task.FromResult(Account);

    public Task<DriveItem> GetItemAsync(string id, CancellationToken cancellationToken) =>
        _items.TryGetValue(id == "root" ? RootId : id, out var item)
            ? Task.FromResult(item)
            : throw new DriveApiException(404, "File not found.");

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
