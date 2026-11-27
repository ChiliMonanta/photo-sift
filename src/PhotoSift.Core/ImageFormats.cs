namespace PhotoSift.Core;

/// <summary>
/// The image file types PhotoSift scans. All other files are excluded.
/// </summary>
public static class ImageFormats
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // JPEG
        ".jpg", ".jpeg", ".jpe", ".jfif",
        // PNG
        ".png",
        // WebP
        ".webp",
        // HEIC / HEIF
        ".heic", ".heif",
        // RAW
        ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".dng",
        ".orf", ".rw2", ".raf", ".pef", ".srw", ".x3f", ".3fr", ".iiq", ".rwl",
    };

    public static IReadOnlyCollection<string> Extensions => SupportedExtensions;

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path));
}
