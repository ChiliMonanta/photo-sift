using PhotoSift.Core.Scanning;

namespace PhotoSift.Core.Sources;

/// <summary>Encoded image bytes (JPEG or PNG), or why there is no preview.</summary>
public sealed record ThumbnailData(byte[]? Bytes, string? Problem)
{
    public static ThumbnailData Unavailable(string problem) => new(null, problem);
}

/// <summary>
/// Implemented by file sources that can supply a small preview image without reading the whole
/// file, e.g. Google Drive's generated thumbnails.
/// </summary>
public interface IThumbnailProvider
{
    /// <summary>
    /// Returns a preview about <paramref name="size"/> pixels on the longest side, or the reason
    /// there is none. Does not throw for unavailable previews.
    /// </summary>
    Task<ThumbnailData> GetThumbnailAsync(ScannedFile file, int size, CancellationToken cancellationToken);
}
