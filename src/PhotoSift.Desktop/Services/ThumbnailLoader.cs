using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using PhotoSift.Core.Scanning;
using PhotoSift.Core.Sources;

namespace PhotoSift.Desktop.Services;

public sealed record ThumbnailResult(Bitmap? Bitmap, string? Problem);

/// <summary>
/// Loads one small thumbnail per duplicate group, as cheaply as possible:
/// <list type="number">
/// <item>decoded from a local copy, when there is one in a format the built-in decoder reads;</item>
/// <item>otherwise downloaded from a source that provides previews (Google Drive), which also
/// covers HEIC/HEIF and RAW. At most <c>maxRemoteConcurrency</c> downloads run at once, and
/// downloaded thumbnails are kept in memory by content hash, so scanning again does not download them again.</item>
/// </list>
/// Groups are only asked for thumbnails when they are displayed.
/// </summary>
public sealed class ThumbnailLoader(
    FileSources? sources = null,
    int maxConcurrency = 3,
    int width = 192,
    int maxRemoteConcurrency = 2,
    int maxCachedRemote = 500)
{
    private static readonly HashSet<string> DecodableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".webp",
    };

    private readonly SemaphoreSlim _gate = new(maxConcurrency);
    private readonly SemaphoreSlim _remoteGate = new(maxRemoteConcurrency);
    private readonly ConcurrentDictionary<string, Bitmap> _remoteCache = new(StringComparer.Ordinal);

    public async Task<ThumbnailResult> LoadAsync(IReadOnlyList<ScannedFile> copies)
    {
        var local = await LoadLocalAsync(copies.Where(f => f.IsLocal).Select(f => f.Path).ToList()).ConfigureAwait(false);
        if (local.Bitmap is not null)
        {
            return local;
        }

        var remote = await LoadRemoteAsync(copies.Where(f => !f.IsLocal).ToList()).ConfigureAwait(false);
        return remote ?? local;
    }

    /// <summary>A thumbnail of one local file.</summary>
    public Task<ThumbnailResult> LoadFileAsync(string path) => LoadLocalAsync([path]);

    private async Task<ThumbnailResult> LoadLocalAsync(IReadOnlyList<string> paths)
    {
        var decodable = paths.Where(p => DecodableExtensions.Contains(Path.GetExtension(p))).ToList();
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

    /// <summary>Returns null when no copy comes from a source that provides previews.</summary>
    private async Task<ThumbnailResult?> LoadRemoteAsync(IReadOnlyList<ScannedFile> copies)
    {
        var withProvider = copies
            .Select(f => (File: f, Provider: sources?.Get(f.SourceId) as IThumbnailProvider))
            .Where(c => c.Provider is not null)
            .ToList();
        if (withProvider.Count == 0)
        {
            return null;
        }

        if (_remoteCache.TryGetValue(withProvider[0].File.Hash, out var cached))
        {
            return new ThumbnailResult(cached, null);
        }

        string? lastProblem = null;
        foreach (var (file, provider) in withProvider)
        {
            await _remoteGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var data = await provider!.GetThumbnailAsync(file, width, CancellationToken.None).ConfigureAwait(false);
                if (data.Bytes is not { } bytes)
                {
                    lastProblem = data.Problem;
                    continue;
                }

                var bitmap = await Task.Run(() =>
                {
                    using var stream = new MemoryStream(bytes);
                    return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality);
                }).ConfigureAwait(false);
                if (_remoteCache.Count < maxCachedRemote)
                {
                    _remoteCache.TryAdd(file.Hash, bitmap);
                }

                return new ThumbnailResult(bitmap, null);
            }
            catch (Exception ex)
            {
                // Try the next copy.
                lastProblem = $"The Google Drive preview could not be shown: {ex.Message}";
            }
            finally
            {
                _remoteGate.Release();
            }
        }

        return new ThumbnailResult(null, lastProblem ?? "No preview available from Google Drive.");
    }
}
