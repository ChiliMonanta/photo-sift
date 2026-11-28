namespace PhotoSift.Core.GoogleDrive.Auth;

/// <summary>The saved sign-in: the refresh token, the account it belongs to, and the scopes the user granted.</summary>
/// <param name="Scope">Space-separated granted scopes; null for sign-ins saved before PhotoSift recorded them.</param>
public sealed record StoredDriveAccount(string RefreshToken, string PermissionId, string Email, string? Scope = null);

/// <summary>Keeps the saved sign-in in the operating system's secure storage, never in a plain file.</summary>
public interface ITokenStore
{
    /// <summary>Null when the store can be used; otherwise why it cannot (e.g. unsupported OS).</summary>
    string? UnavailableReason { get; }

    /// <summary>Returns null when nothing is saved. Throws when saved data cannot be read.</summary>
    StoredDriveAccount? Load();

    void Save(StoredDriveAccount account);

    void Delete();
}

/// <summary>
/// The Google Drive connection: signing in and out, the saved sign-in, and access tokens for the API.
/// Only one account is connected at a time.
/// </summary>
public sealed class DriveConnection : IDriveSession, IAccessTokenProvider
{
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(1);

    private readonly GoogleOAuthClient? _oauth;
    private readonly ITokenStore _store;
    private readonly IDriveApi _api;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private StoredDriveAccount? _stored;
    private OAuthTokens? _current;

    // A saved sign-in for permissions PhotoSift no longer asks for; replaced (and revoked) on the next connect.
    private StoredDriveAccount? _outdated;

    /// <param name="oauth">Null when no OAuth client is configured for this build.</param>
    /// <param name="apiFactory">Creates the Drive API client that gets its tokens from this connection.</param>
    public DriveConnection(
        GoogleOAuthClient? oauth,
        ITokenStore store,
        Func<IAccessTokenProvider, IDriveApi> apiFactory,
        TimeProvider? time = null)
    {
        _oauth = oauth;
        _store = store;
        _time = time ?? TimeProvider.System;
        _api = apiFactory(this);
    }

    /// <summary>Null when Google Drive can be connected; otherwise the reason it cannot.</summary>
    public string? UnavailableReason => _oauth is null
        ? "This build has no Google OAuth client configured (see docs/google-oauth-setup.md)."
        : _store.UnavailableReason;

    public DriveAccount? Account => _stored is null ? null : new DriveAccount(_stored.PermissionId, _stored.Email);

    public IDriveApi? Api => _stored is null ? null : _api;

    /// <summary>
    /// True when the connected account allows uploading. Sign-ins made before uploading existed are
    /// read-only; connecting again (while connected) upgrades them.
    /// </summary>
    public bool CanUpload => _stored is not null && GoogleOAuthClient.IncludesUploadScope(_stored.Scope);

    /// <summary>
    /// True when the saved sign-in was made with permissions PhotoSift no longer uses (e.g. before
    /// previews needed the read-only Drive scope). The user must connect again.
    /// </summary>
    public bool NeedsReconnect => _outdated is not null;

    /// <summary>Loads the saved sign-in, if any. Does not contact Google.</summary>
    public void Restore()
    {
        if (UnavailableReason is not null)
        {
            return;
        }

        var stored = _store.Load();
        _current = null;
        if (stored is not null && !GoogleOAuthClient.IncludesRequiredScope(stored.Scope))
        {
            _outdated = stored;
            _stored = null;
            return;
        }

        _stored = stored;
    }

    /// <summary>
    /// Signs in through the browser, then saves the sign-in. Replaces any connected account.
    /// Continues on a thread-pool thread; callers update their UI after it returns.
    /// </summary>
    public async Task<DriveAccount> ConnectAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken cancellationToken)
    {
        if (UnavailableReason is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        var tokens = await _oauth!.SignInAsync(openBrowser, cancellationToken).ConfigureAwait(false);
        var previous = (_stored, _current);
        try
        {
            // The API client asks this connection for tokens, so use the new ones while reading the account.
            _stored = new StoredDriveAccount(tokens.RefreshToken!, string.Empty, string.Empty);
            _current = tokens;
            var account = await _api.GetAccountAsync(cancellationToken).ConfigureAwait(false);

            var stored = new StoredDriveAccount(tokens.RefreshToken!, account.PermissionId, account.Email, tokens.Scope);
            _store.Save(stored);
            _stored = stored;
        }
        catch
        {
            // Nothing was saved, so return to the previous account.
            (_stored, _current) = previous;
            throw;
        }

        // The new sign-in replaced an outdated one; remove PhotoSift's old access at Google too.
        // (A read-only sign-in upgraded to allow uploads is not revoked: it is the same grant.)
        if (_outdated is { } outdated)
        {
            _outdated = null;
            try
            {
                await _oauth.RevokeAsync(outdated.RefreshToken, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OAuthException or HttpRequestException)
            {
                // Best effort: the new sign-in works either way, and the old one expires when unused.
            }
        }

        return Account!;
    }

    /// <summary>
    /// Revokes PhotoSift's access at Google and deletes the saved sign-in. The local sign-in is
    /// deleted even if revoking fails; the exception is then rethrown so the user can be told.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var stored = _stored ?? _outdated;
        _stored = null;
        _outdated = null;
        _current = null;
        _store.Delete();

        if (stored is not null && _oauth is not null)
        {
            await _oauth.RevokeAsync(stored.RefreshToken, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = _stored ?? throw new DriveSignInRequiredException("Google Drive is not connected.");
            if (_current is { } current && current.ExpiresAt - ExpiryMargin > _time.GetUtcNow())
            {
                return current.AccessToken;
            }

            try
            {
                _current = await _oauth!.RefreshAsync(stored.RefreshToken, cancellationToken).ConfigureAwait(false);
            }
            catch (OAuthException ex) when (ex.IsInvalidGrant)
            {
                throw new DriveSignInRequiredException(
                    "The Google Drive sign-in has expired or was revoked. Disconnect and connect Google Drive again.");
            }

            return _current.AccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public void InvalidateAccessToken(string accessToken)
    {
        if (_current?.AccessToken == accessToken)
        {
            _current = null;
        }
    }
}
