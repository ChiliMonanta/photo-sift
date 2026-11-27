using System.Reflection;
using System.Text.Json;
using PhotoSift.Core.GoogleDrive.Auth;

namespace PhotoSift.Desktop.Services;

/// <summary>
/// Finds the Google OAuth client (see docs/google-oauth-setup.md), first match wins:
/// <list type="number">
/// <item>Environment variables <c>PHOTOSIFT_GOOGLE_CLIENT_ID</c> and <c>PHOTOSIFT_GOOGLE_CLIENT_SECRET</c> (your own client).</item>
/// <item>Debug builds only: .NET user secrets <c>GoogleDrive:ClientId</c> and <c>GoogleDrive:ClientSecret</c>.</item>
/// <item>Values built into release builds by the release workflow.</item>
/// </list>
/// </summary>
public static class GoogleClientConfig
{
    public const string ClientIdVariable = "PHOTOSIFT_GOOGLE_CLIENT_ID";
    public const string ClientSecretVariable = "PHOTOSIFT_GOOGLE_CLIENT_SECRET";

    private const string UserSecretsId = "photosift-desktop";

    public static OAuthClientCredentials? Resolve() =>
        FromEnvironment() ?? FromUserSecrets() ?? FromAssembly();

    private static OAuthClientCredentials? FromEnvironment() =>
        Create(Environment.GetEnvironmentVariable(ClientIdVariable), Environment.GetEnvironmentVariable(ClientSecretVariable));

    private static OAuthClientCredentials? FromUserSecrets()
    {
#if DEBUG
        // The file `dotnet user-secrets set` writes; read directly to avoid extra packages.
        var path = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets", UserSecretsId, "secrets.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".microsoft", "usersecrets", UserSecretsId, "secrets.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return values is null
                ? null
                : Create(values.GetValueOrDefault("GoogleDrive:ClientId"), values.GetValueOrDefault("GoogleDrive:ClientSecret"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
#else
        _ = UserSecretsId;
        return null;
#endif
    }

    private static OAuthClientCredentials? FromAssembly()
    {
        var metadata = typeof(GoogleClientConfig).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToList();
        string? Value(string key) => metadata.FirstOrDefault(m => m.Key == key)?.Value;
        return Create(Value("GoogleDriveClientId"), Value("GoogleDriveClientSecret"));
    }

    private static OAuthClientCredentials? Create(string? clientId, string? clientSecret) =>
        string.IsNullOrWhiteSpace(clientId)
            ? null
            : new OAuthClientCredentials(clientId.Trim(), string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret.Trim());
}
