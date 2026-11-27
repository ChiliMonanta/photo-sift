using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotoSift.Core.GoogleDrive.Auth;

namespace PhotoSift.Desktop.Platform;

public static class TokenStoreFactory
{
    public static ITokenStore Create() =>
        OperatingSystem.IsWindows()
            ? new DpapiTokenStore(DpapiTokenStore.DefaultPath)
            : new UnsupportedTokenStore();
}

/// <summary>
/// Saves the sign-in encrypted with Windows DPAPI for the current user, so only this Windows
/// account on this computer can decrypt it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenStore(string path) : ITokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PhotoSift.GoogleDrive.v1");

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoSift", "google-drive-sign-in.bin");

    public string? UnavailableReason => null;

    public StoredDriveAccount? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredDriveAccount>(json)
                   ?? throw new InvalidDataException("The saved Google Drive sign-in is empty.");
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            throw new InvalidDataException($"The saved Google Drive sign-in could not be read: {ex.Message}", ex);
        }
    }

    public void Save(StoredDriveAccount account)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encrypted = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(account), Entropy, DataProtectionScope.CurrentUser);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, path, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// Used where no secure storage is implemented yet (macOS Keychain and Linux libsecret are planned).
/// Google Drive cannot be connected; the sign-in is never written to a plain file instead.
/// </summary>
public sealed class UnsupportedTokenStore : ITokenStore
{
    public string? UnavailableReason => "Google Drive is not available on this operating system yet (Windows only for now).";

    public StoredDriveAccount? Load() => null;

    public void Save(StoredDriveAccount account) => throw new PlatformNotSupportedException(UnavailableReason);

    public void Delete()
    {
    }
}
