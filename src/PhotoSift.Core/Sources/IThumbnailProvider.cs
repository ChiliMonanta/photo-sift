using PhotoSift.Core.Scanning;

namespace PhotoSift.Core.Sources;

/// <summary>
/// Implemented by file sources that can supply a small preview image without reading the whole
/// file, e.g. Google Drive's generated thumbnails.
/// </summary>
public interface IThumbnailProvider
{
    /// <summary>
    /// Returns encoded image bytes (JPEG or PNG) about <paramref name="size"/> pixels on the longest
    /// side, or null when no preview is available. Does not throw for unavailable previews.
    /// </summary>
    Task<byte[]?> GetThumbnailAsync(ScannedFile file, int size, CancellationToken cancellationToken);
}
