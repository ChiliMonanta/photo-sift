using Avalonia.Media.Imaging;

namespace PhotoSift.Desktop.Services;

public sealed record ThumbnailResult(Bitmap? Bitmap, string? Problem);

/// <summary>
/// Decodes small thumbnails with a limit on concurrent requests so the UI stays responsive.
/// Formats the built-in decoder cannot read (HEIC/HEIF, RAW) yield a placeholder.
/// </summary>
public sealed class ThumbnailLoader(int maxConcurrency = 3, int width = 192)
{
    private static readonly HashSet<string> DecodableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".webp",
    };

    private readonly SemaphoreSlim _gate = new(maxConcurrency);

    public async Task<ThumbnailResult> LoadAsync(IReadOnlyList<string> candidatePaths)
    {
        var decodable = candidatePaths.Where(p => DecodableExtensions.Contains(Path.GetExtension(p))).ToList();
        if (decodable.Count == 0)
        {
            return new ThumbnailResult(null, "Preview is not available for this format.");
        }

        string? lastProblem = null;
        foreach (var path in decodable)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var bitmap = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(path);
                    return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality);
                }).ConfigureAwait(false);
                return new ThumbnailResult(bitmap, null);
            }
            catch (Exception ex)
            {
                // Try the next copy; the files are identical but one may be inaccessible.
                lastProblem = $"Preview could not be generated: {ex.Message}";
            }
            finally
            {
                _gate.Release();
            }
        }

        return new ThumbnailResult(null, lastProblem);
    }
}
