namespace PhotoSift.Core;

public static class PathUtil
{
    /// <summary>
    /// Path comparison that matches the default file system behavior of the current OS:
    /// case-insensitive on Windows and macOS, case-sensitive elsewhere.
    /// </summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

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
}
