using System.Net;
using PhotoSift.Core.GoogleDrive;
using PhotoSift.Core.GoogleDrive.Auth;
using PhotoSift.Desktop.Platform;

namespace PhotoSift.Tests;

public sealed class GoogleDriveAuthTests
{
    private const string TokenResponse =
        """{"access_token":"access-1","expires_in":3600,"refresh_token":"refresh-1","scope":"https://www.googleapis.com/auth/drive.metadata.readonly","token_type":"Bearer"}""";

    private static readonly OAuthClientCredentials Credentials = new("client-id.apps.googleusercontent.com", "client-secret");

    private static GoogleOAuthClient OAuth(FakeHttpHandler handler, TimeProvider? time = null) =>
        new(new HttpClient(handler), Credentials, time);

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    // ---- PKCE and the authorization request --------------------------------------

    [Fact]
    public void Pkce_challenge_matches_the_RFC_7636_example() =>
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public void Verifiers_and_states_are_random_and_long_enough()
    {
        var verifiers = Enumerable.Range(0, 20).Select(_ => Pkce.CreateVerifier()).ToList();
        Assert.Equal(20, verifiers.Distinct().Count());
        Assert.All(verifiers, v => Assert.InRange(v.Length, 43, 128));
        Assert.NotEqual(Pkce.CreateState(), Pkce.CreateState());
    }

    [Fact]
    public void Authorization_request_uses_pkce_state_a_loopback_redirect_and_only_the_metadata_scope()
    {
        var uri = OAuth(new FakeHttpHandler((_, _) => throw new InvalidOperationException()))
            .BuildAuthorizationUri("http://127.0.0.1:5000/", "the-state", "the-challenge");
        var query = FakeHttpHandler.Form(uri.Query.TrimStart('?'));

        Assert.StartsWith(GoogleOAuthClient.AuthorizationEndpoint + "?", uri.AbsoluteUri);
        Assert.Equal(Credentials.ClientId, query["client_id"]);
        Assert.Equal("http://127.0.0.1:5000/", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(GoogleOAuthClient.Scope, query["scope"]);
        Assert.Equal("the-challenge", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("the-state", query["state"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.DoesNotContain("client_secret", query.Keys);
    }

    // ---- Sign-in through the loopback redirect --------------------------------------

    [Fact]
    public async Task Sign_in_ignores_bad_redirects_and_exchanges_the_code_with_the_verifier()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, TokenResponse));
        var browser = new FakeBrowser();
        browser.BadRequestPaths.Add("/favicon.ico");
        browser.BadRequestPaths.Add("/?code=attacker-code&state=wrong-state");

        var tokens = await OAuth(handler).SignInAsync(browser.OpenAsync, Timeout());

        Assert.Equal(("access-1", "refresh-1"), (tokens.AccessToken, tokens.RefreshToken));
        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.BadRequest], browser.BadRequestStatuses);
        Assert.StartsWith("http://127.0.0.1:", browser.AuthorizationQuery["redirect_uri"]);

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(GoogleOAuthClient.TokenEndpoint, request.RequestUri!.AbsoluteUri);
        var form = FakeHttpHandler.Form(body);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("the-code", form["code"]);
        Assert.Equal(browser.AuthorizationQuery["redirect_uri"], form["redirect_uri"]);
        Assert.Equal(Credentials.ClientSecret, form["client_secret"]);
        Assert.Equal(browser.AuthorizationQuery["code_challenge"], Pkce.ChallengeFor(form["code_verifier"]));
    }

    [Fact]
    public async Task Sign_in_fails_when_the_user_declines()
    {
        var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException("No token request expected."));
        var browser = new FakeBrowser { RedirectQuery = state => $"error=access_denied&state={Uri.EscapeDataString(state)}" };

        var ex = await Assert.ThrowsAsync<OAuthException>(() => OAuth(handler).SignInAsync(browser.OpenAsync, Timeout()));

        Assert.Equal("access_denied", ex.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Sign_in_fails_when_the_drive_scope_was_not_granted()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"access_token":"a","expires_in":3600,"refresh_token":"r","scope":"openid"}"""));

        var ex = await Assert.ThrowsAsync<OAuthException>(() => OAuth(handler).SignInAsync(new FakeBrowser().OpenAsync, Timeout()));

        Assert.Equal("insufficient_scope", ex.Error);
    }

    [Fact]
    public async Task Sign_in_can_be_canceled_while_waiting_for_the_browser()
    {
        using var cts = new CancellationTokenSource();
        var oauth = OAuth(new FakeHttpHandler((_, _) => throw new InvalidOperationException()));

        var signIn = oauth.SignInAsync((_, _) => Task.CompletedTask, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn);
    }

    // ---- The connection: saved sign-in and access tokens -----------------------------

    private sealed class Connection
    {
        public FakeTokenStore Store { get; } = new();

        public ManualTimeProvider Time { get; } = new();

        public FakeDriveApi Drive { get; } = new();

        public List<string> TokenRequests { get; } = [];

        public HttpStatusCode RefreshStatus { get; set; } = HttpStatusCode.OK;

        public HttpStatusCode RevokeStatus { get; set; } = HttpStatusCode.OK;

        public DriveConnection Create()
        {
            var handler = new FakeHttpHandler((request, body) =>
            {
                if (request.RequestUri!.AbsoluteUri == GoogleOAuthClient.RevokeEndpoint)
                {
                    TokenRequests.Add("revoke:" + FakeHttpHandler.Form(body)["token"]);
                    return FakeHttpHandler.Json(RevokeStatus, RevokeStatus == HttpStatusCode.OK ? "{}" : """{"error":"server_error"}""");
                }

                var form = FakeHttpHandler.Form(body);
                TokenRequests.Add(form["grant_type"]);
                if (form["grant_type"] == "refresh_token" && RefreshStatus != HttpStatusCode.OK)
                {
                    return FakeHttpHandler.Json(RefreshStatus, """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""");
                }

                var access = $"access-{TokenRequests.Count}";
                return FakeHttpHandler.Json(HttpStatusCode.OK, TokenResponse.Replace("access-1", access));
            });
            return new DriveConnection(new GoogleOAuthClient(new HttpClient(handler), Credentials, Time), Store, _ => Drive, Time);
        }
    }

    [Fact]
    public async Task Connecting_saves_the_account_and_refresh_token()
    {
        var setup = new Connection();
        var connection = setup.Create();

        var account = await connection.ConnectAsync(new FakeBrowser().OpenAsync, Timeout());

        Assert.Equal(setup.Drive.Account, account);
        Assert.Equal(account, connection.Account);
        Assert.Equal(new StoredDriveAccount("refresh-1", "12345", "user@example.com"), setup.Store.Saved);
    }

    [Fact]
    public async Task Access_tokens_are_reused_until_they_expire_then_refreshed()
    {
        var setup = new Connection();
        setup.Store.Saved = new StoredDriveAccount("refresh-1", "12345", "user@example.com");
        var connection = setup.Create();
        connection.Restore();

        var first = await connection.GetAccessTokenAsync(CancellationToken.None);
        var again = await connection.GetAccessTokenAsync(CancellationToken.None);
        setup.Time.Now += TimeSpan.FromMinutes(59.5);
        var refreshed = await connection.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal(first, again);
        Assert.NotEqual(first, refreshed);
        Assert.Equal(["refresh_token", "refresh_token"], setup.TokenRequests);
    }

    [Fact]
    public async Task An_expired_sign_in_asks_the_user_to_connect_again()
    {
        var setup = new Connection { RefreshStatus = HttpStatusCode.BadRequest };
        setup.Store.Saved = new StoredDriveAccount("refresh-1", "12345", "user@example.com");
        var connection = setup.Create();
        connection.Restore();

        var ex = await Assert.ThrowsAsync<DriveSignInRequiredException>(() => connection.GetAccessTokenAsync(CancellationToken.None));
        Assert.Contains("connect Google Drive again", ex.Message);
    }

    [Fact]
    public async Task Disconnecting_revokes_access_and_deletes_the_saved_sign_in()
    {
        var setup = new Connection();
        setup.Store.Saved = new StoredDriveAccount("refresh-1", "12345", "user@example.com");
        var connection = setup.Create();
        connection.Restore();

        await connection.DisconnectAsync(CancellationToken.None);

        Assert.Null(setup.Store.Saved);
        Assert.Null(connection.Account);
        Assert.Null(connection.Api);
        Assert.Equal(["revoke:refresh-1"], setup.TokenRequests);
    }

    [Fact]
    public async Task The_saved_sign_in_is_deleted_even_when_revoking_fails()
    {
        var setup = new Connection { RevokeStatus = HttpStatusCode.ServiceUnavailable };
        setup.Store.Saved = new StoredDriveAccount("refresh-1", "12345", "user@example.com");
        var connection = setup.Create();
        connection.Restore();

        await Assert.ThrowsAsync<OAuthException>(() => connection.DisconnectAsync(CancellationToken.None));

        Assert.Null(setup.Store.Saved);
        Assert.Null(connection.Account);
    }

    [Fact]
    public void Drive_is_unavailable_without_a_client_or_secure_storage()
    {
        var store = new FakeTokenStore();
        Assert.Contains("no Google OAuth client", new DriveConnection(null, store, _ => new FakeDriveApi()).UnavailableReason);

        store.UnavailableReason = "No secure storage.";
        var oauth = new GoogleOAuthClient(new HttpClient(), Credentials);
        Assert.Equal("No secure storage.", new DriveConnection(oauth, store, _ => new FakeDriveApi()).UnavailableReason);
    }

    [Fact]
    public void Windows_saves_the_sign_in_encrypted_for_the_current_user()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempFolder();
        var path = temp.PathOf("sign-in.bin");
        var store = new DpapiTokenStore(path);
        var account = new StoredDriveAccount("1//refresh-token-value", "12345", "user@example.com");

        store.Save(account);

        Assert.Equal(account, new DpapiTokenStore(path).Load());
        Assert.DoesNotContain("refresh-token-value", File.ReadAllText(path));
        store.Delete();
        Assert.Null(store.Load());
    }

    // ---- The Drive REST client ----------------------------------------------------------

    private sealed class Tokens : IAccessTokenProvider
    {
        public int Issued { get; private set; }

        public List<string> Invalidated { get; } = [];

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult($"token-{++Issued}");

        public void InvalidateAccessToken(string accessToken) => Invalidated.Add(accessToken);
    }

    private static DriveRestClient Rest(FakeHttpHandler handler, Tokens tokens) =>
        new(new HttpClient(handler), tokens, (_, _) => Task.CompletedTask);

    [Fact]
    public async Task Listing_a_folder_reads_every_page_with_an_escaped_query()
    {
        var handler = new FakeHttpHandler((request, _) => FakeHttpHandler.Json(HttpStatusCode.OK,
            request.RequestUri!.Query.Contains("pageToken=p2")
                ? """{"files":[{"id":"2","name":"b.jpg","mimeType":"image/jpeg","size":"10","md5Checksum":"ab","modifiedTime":"2024-05-01T10:00:00.000Z","parents":["it's"]}]}"""
                : """{"nextPageToken":"p2","files":[{"id":"1","name":"a.jpg","mimeType":"image/jpeg","size":"5","parents":["it's"]}]}"""));

        var items = await Rest(handler, new Tokens()).ListChildrenAsync("it's", foldersOnly: false, CancellationToken.None);

        Assert.Equal(["a.jpg", "b.jpg"], items.Select(i => i.Name));
        Assert.Equal((10L, "ab"), (items[1].Size, items[1].Md5Checksum));
        Assert.Equal(new DateTime(2024, 5, 1, 10, 0, 0, DateTimeKind.Utc), items[1].ModifiedUtc);
        var query = FakeHttpHandler.Form(handler.Requests[0].Request.RequestUri!.Query.TrimStart('?'));
        Assert.Equal(@"'it\'s' in parents and trashed = false", query["q"]);
        Assert.Equal("Bearer", handler.Requests[0].Request.Headers.Authorization!.Scheme);
        Assert.StartsWith(DriveRestClient.BaseUri, handler.Requests[0].Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task A_rejected_access_token_is_replaced_once()
    {
        var calls = 0;
        var handler = new FakeHttpHandler((_, _) => ++calls == 1
            ? FakeHttpHandler.Json(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid Credentials"}}""")
            : FakeHttpHandler.Json(HttpStatusCode.OK, """{"id":"root-id","name":"My Drive","mimeType":"application/vnd.google-apps.folder"}"""));
        var tokens = new Tokens();

        var root = await Rest(handler, tokens).GetItemAsync("root", CancellationToken.None);

        Assert.True(root.IsFolder);
        Assert.Equal(["token-1"], tokens.Invalidated);
        Assert.Equal("token-2", handler.Requests[1].Request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Server_errors_are_retried_and_then_reported()
    {
        var handler = new FakeHttpHandler((_, _) =>
            FakeHttpHandler.Json(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"Backend Error"}}"""));

        var ex = await Assert.ThrowsAsync<DriveApiException>(() => Rest(handler, new Tokens()).GetItemAsync("x", CancellationToken.None));

        Assert.Equal(503, ex.StatusCode);
        Assert.Contains("Backend Error", ex.Message);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Missing_files_are_reported_as_not_found_without_retrying()
    {
        var handler = new FakeHttpHandler((_, _) =>
            FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"error":{"message":"File not found: x."}}"""));

        var ex = await Assert.ThrowsAsync<DriveApiException>(() => Rest(handler, new Tokens()).GetItemAsync("x", CancellationToken.None));

        Assert.True(ex.IsNotFound);
        Assert.Single(handler.Requests);
    }
}
