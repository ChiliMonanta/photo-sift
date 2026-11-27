namespace PhotoSift.Core.GoogleDrive;

/// <summary>
/// Recognizes local folders that are synced by Google Drive for desktop. If such a folder is
/// scanned together with Drive through the API, the same file shows up twice, and deleting the
/// local "copy" also deletes it from Drive.
/// </summary>
public static class DriveForDesktop
{
    /// <summary>
    /// Whether <paramref name="localPath"/> looks like it is inside Google Drive for desktop:
    /// on its virtual drive (volume label "Google Drive", e.g. <c>G:\My Drive</c>), in a
    /// <c>My Drive</c> mirror folder, or in <c>~/Library/CloudStorage/GoogleDrive-…</c> on macOS.
    /// This is a heuristic for a warning, not a guarantee.
    /// </summary>
    /// <param name="volumeLabelOf">Returns the volume label of a drive root, or null; defaults to <see cref="DriveInfo"/>.</param>
    public static bool IsInside(string localPath, Func<string, string?>? volumeLabelOf = null)
    {
        if (!PathUtil.IsLocal(localPath))
        {
            return false;
        }

        var normalized = localPath.Replace('\\', '/');
        if (normalized.Contains("/Library/CloudStorage/GoogleDrive-", StringComparison.Ordinal))
        {
            return true;
        }

        if (normalized.Split('/').Contains("My Drive", StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // Drive for desktop's virtual drive has a drive letter (Windows only).
        var hasDriveLetter = localPath.Length >= 3 && char.IsAsciiLetter(localPath[0]) && localPath[1] == ':' &&
                             localPath[2] is '\\' or '/';
        if (!hasDriveLetter)
        {
            return false;
        }

        var label = (volumeLabelOf ?? VolumeLabel)(localPath[..3]);
        return string.Equals(label, "Google Drive", StringComparison.OrdinalIgnoreCase);
    }

    private static string? VolumeLabel(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.VolumeLabel : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
