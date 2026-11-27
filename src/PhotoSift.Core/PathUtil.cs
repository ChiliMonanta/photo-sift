namespace PhotoSift.Core;

public static class PathUtil
{
    private static readonly StringComparer OsPathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>
    /// Compares file and folder locations. Local paths compare like the current OS's default
    /// file system: case-insensitive on Windows and macOS, case-sensitive elsewhere. Locations
    /// from other sources (<c>scheme://...</c>, e.g. Google Drive IDs) are always case-sensitive.
    /// </summary>
    public static StringComparer Comparer { get; } = new LocationComparer();

    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// True for local file system paths; false for locations of other sources, which start with
    /// a URI scheme of at least two characters followed by "://" (so "C:\" is local).
    /// </summary>
    public static bool IsLocal(string location)
    {
        var schemeEnd = location.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 2)
        {
            return true;
        }

        for (var i = 0; i < schemeEnd; i++)
        {
            var c = location[i];
            var isSchemeChar = i == 0
                ? char.IsAsciiLetterLower(c)
                : char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '+' or '-' or '.';
            if (!isSchemeChar)
            {
                return true;
            }
        }

        return false;
    }

    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// Returns true when <paramref name="filePath"/> is inside <paramref name="folder"/>
    /// or any of its subfolders. "C:\Photos2\a.jpg" is not inside "C:\Photos".
    /// </summary>
    public static bool IsInFolder(string filePath, string folder)
    {
        var file = Normalize(filePath);
        var root = Normalize(folder);

        // A drive or file system root ("C:\", "/") already ends with a separator.
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return file.StartsWith(prefix, Comparison);
    }

    private sealed class LocationComparer : StringComparer
    {
        public override int Compare(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return string.CompareOrdinal(x, y);
            }

            // Local paths sort before other sources, so the ordering stays consistent (transitive).
            var (xLocal, yLocal) = (IsLocal(x), IsLocal(y));
            if (xLocal != yLocal)
            {
                return xLocal ? -1 : 1;
            }

            return xLocal ? OsPathComparer.Compare(x, y) : string.CompareOrdinal(x, y);
        }

        public override bool Equals(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return x is null && y is null;
            }

            return IsLocal(x) && IsLocal(y) ? OsPathComparer.Equals(x, y) : string.Equals(x, y, StringComparison.Ordinal);
        }

        public override int GetHashCode(string obj) =>
            IsLocal(obj) ? OsPathComparer.GetHashCode(obj) : StringComparer.Ordinal.GetHashCode(obj);
    }
}
