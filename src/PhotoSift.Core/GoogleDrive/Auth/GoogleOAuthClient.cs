using System.Text.Json;

namespace PhotoSift.Core.GoogleDrive.Auth;

/// <summary>
/// The app's OAuth client from Google Cloud. For desktop apps both values are public
/// (RFC 8252 "public client"); see docs/google-oauth-setup.md.
/// </summary>
public sealed record OAuthClientCredentials(string ClientId, string? ClientSecret);

public sealed record OAuthTokens(string AccessToken, DateTimeOffset ExpiresAt, string? RefreshToken);

public sealed class OAuthException(string message, string? error = null) : Exception(message)
{
    /// <summary>The OAuth error code, e.g. "invalid_grant" or "access_denied".</summary>
    public string? Error { get; } = error;

    /// <summary>The refresh token expired or was revoked; the user must sign in again.</summary>
    public bool IsInvalidGrant => Error == "invalid_grant";
}

/// <summary>
/// Google sign-in for installed apps: system browser, loopback redirect, PKCE (S256) and a
/// random <c>state</c>. Requests only the read-only Drive metadata scope.
/// </summary>
public sealed class GoogleOAuthClient(HttpClient http, OAuthClientCredentials credentials, TimeProvider? time = null)
{
    public const string Scope = "https://www.googleapis.com/auth/drive.metadata.readonly";
    public const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string ClientId => credentials.ClientId;

    public Uri BuildAuthorizationUri(string redirectUri, string state, string codeChallenge)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = credentials.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = Scope,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline", // returns a refresh token, so the user stays signed in
            ["prompt"] = "consent",
        };
        return new Uri(AuthorizationEndpoint + "?" + string.Join("&",
            query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")));
    }

    /// <summary>
    /// Opens Google's consent page with <paramref name="openBrowser"/>, waits for the redirect,
    /// and exchanges the code for tokens. Cancel <paramref name="cancellationToken"/> to give up.
    /// </summary>
    public async Task<OAuthTokens> SignInAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken cancellationToken)
    {
        using var receiver = LoopbackReceiver.Start();
        var verifier = Pkce.CreateVerifier();
        var state = Pkce.CreateState();

        await openBrowser(BuildAuthorizationUri(receiver.RedirectUri, state, Pkce.ChallengeFor(verifier)), cancellationToken)
            .ConfigureAwait(false);
        var redirect = await receiver.WaitForRedirectAsync(state, cancellationToken).ConfigureAwait(false);

        if (redirect.TryGetValue("error", out var error))
        {
            throw new OAuthException(
                error == "access_denied"
                    ? "Google Drive access was not granted."
                    : $"Google sign-in failed: {error}",
                error);
        }

        var tokens = await RequestTokensAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = redirect["code"],
            ["code_verifier"] = verifier,
            ["redirect_uri"] = receiver.RedirectUri,
        }, requireScope: true, cancellationToken).ConfigureAwait(false);

        return tokens.RefreshToken is null
            ? throw new OAuthException("Google did not return a refresh token. Try connecting again.")
            : tokens;
    }

    /// <summary>Gets a new access token. Throws <see cref="OAuthException"/> with <see cref="OAuthException.IsInvalidGrant"/> when the user must sign in again.</summary>
    public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        RequestTokensAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, requireScope: false, cancellationToken);

    /// <summary>Revokes the app's access to the account (the refresh token and all its access tokens).</summary>
    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
        using var response = await http.PostAsync(RevokeEndpoint, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var (error, description) = ParseError(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            // "invalid_token" means it is already revoked or expired, which is the goal.
            if (error != "invalid_token")
            {
                throw new OAuthException($"Google could not revoke access: {description ?? error ?? response.ReasonPhrase}", error);
            }
        }
    }

    private async Task<OAuthTokens> RequestTokensAsync(
        Dictionary<string, string> form, bool requireScope, CancellationToken cancellationToken)
    {
        form["client_id"] = credentials.ClientId;
        if (!string.IsNullOrEmpty(credentials.ClientSecret))
        {
            form["client_secret"] = credentials.ClientSecret;
        }

        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var (error, description) = ParseError(body);
            throw new OAuthException(
                error == "invalid_grant"
                    ? "The Google Drive sign-in has expired or was revoked. Connect Google Drive again."
                    : $"Google sign-in failed: {description ?? error ?? response.ReasonPhrase}",
                error);
        }

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new OAuthException("Google returned no access token.");
        }

        if (requireScope)
        {
            var scopes = root.TryGetProperty("scope", out var s) ? (s.GetString() ?? string.Empty).Split(' ') : [];
            if (!scopes.Contains(Scope))
            {
                throw new OAuthException(
                    "PhotoSift needs permission to see information about your Google Drive files. Connect again and allow it.",
                    "insufficient_scope");
            }
        }

        var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 3600;
        var refreshToken = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        return new OAuthTokens(accessToken, _time.GetUtcNow().AddSeconds(expiresIn), refreshToken);
    }

    private static (string? Error, string? Description) ParseError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            var description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            return (error, description);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
