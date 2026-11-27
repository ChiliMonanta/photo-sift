using System.Runtime.CompilerServices;
using PhotoSift.Core.Caching;
using PhotoSift.Core.Deletion;
using PhotoSift.Core.Hashing;
using PhotoSift.Core.Scanning;

namespace PhotoSift.Core.Sources;

/// <summary>Image files on the local file system. Locations are absolute paths.</summary>
/// <param name="cache">Optional hash cache, used during scans only (never when verifying before deletion).</param>
/// <param name="trash">Moves files to the OS trash; without it, files from this source cannot be deleted.</param>
public sealed class LocalFileSource(IFileHasher hasher, IHashCache? cache = null, ITrashService? trash = null) : IFileSource
{
    public const string SourceId = "local";

    private const string CacheFailureKey = "local:cache-failure";

    private readonly IHashCache _cache = cache ?? NullHashCache.Instance;

    public string Id => SourceId;

    public bool Owns(string location) => PathUtil.IsLocal(location);

    public string? TrashUnavailableReason =>
        trash is null ? "Moving local files to the trash is not available." : null;

    public async IAsyncEnumerable<SourceFile> EnumerateAsync(
        IReadOnlyList<string> folders,
        IScanSink sink,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var visitedDirectories = new HashSet<string>(PathUtil.Comparer);
        var seenFiles = new HashSet<string>(PathUtil.Comparer);

        foreach (var folder in folders)
        {
            var root = TryNormalizeRoot(folder, sink);
            if (root is null)
            {
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Overlapping root folders must not make a file appear twice, or it would
                // be grouped as a duplicate of itself.
                if (!visitedDirectories.Add(directory))
                {
                    continue;
                }

                var entries = TryListDirectory(directory, sink);
                foreach (var entry in entries.OrderBy(e => e.Name, PathUtil.Comparer))
                {
                    // Symbolic links and junctions are not followed: a linked file is the same
                    // physical file as its target, so deleting one "copy" would delete both.
                    if (entry.LinkTarget is not null)
                    {
                        if (entry is DirectoryInfo || ImageFormats.IsSupported(entry.Name))
                        {
                            sink.AddNotice(new ScanNotice(entry.FullName, "Skipped symbolic link or junction."));
                        }

                        continue;
                    }

                    switch (entry)
                    {
                        case DirectoryInfo subdirectory:
                            pending.Push(subdirectory.FullName);
                            break;

                        case FileInfo file when ImageFormats.IsSupported(file.Name) && seenFiles.Add(file.FullName):
                            yield return new SourceFile(file.FullName, file.FullName, file.Length, file.LastWriteTimeUtc);
                            break;
                    }
                }
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<string> GetHashAsync(SourceFile file, IScanSink sink, CancellationToken cancellationToken)
    {
        var hash = TryGetCachedHash(file, sink);
        if (hash is not null)
        {
            return hash;
        }

        hash = await hasher.ComputeHashAsync(file.Location, cancellationToken).ConfigureAwait(false);
        TryCacheHash(file, hash, sink);
        return hash;
    }

    public bool IsInFolder(ScannedFile file, string folder) => PathUtil.IsInFolder(file.Path, folder);

    public async Task<string?> VerifyAsync(ScannedFile file, CancellationToken cancellationToken)
    {
        var info = new FileInfo(file.Path);
        if (!info.Exists)
        {
            return "File no longer exists.";
        }

        if (info.Length != file.Size)
        {
            return "File size has changed since the scan.";
        }

        var hash = await hasher.ComputeHashAsync(file.Path, cancellationToken).ConfigureAwait(false);
        return string.Equals(hash, file.Hash, StringComparison.OrdinalIgnoreCase)
            ? null
            : "File contents have changed since the scan.";
    }

    public Task MoveToTrashAsync(ScannedFile file, CancellationToken cancellationToken) =>
        trash is null
            ? throw new InvalidOperationException(TrashUnavailableReason)
            : trash.MoveToTrashAsync(file.Path, cancellationToken);

    private static string? TryNormalizeRoot(string folder, IScanSink sink)
    {
        string root;
        try
        {
            root = PathUtil.Normalize(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            sink.AddError(new ScanError(folder, $"Invalid folder path: {ex.Message}"));
            return null;
        }

        if (!Directory.Exists(root))
        {
            sink.AddError(new ScanError(root, "Folder does not exist or is not accessible."));
            return null;
        }

        return root;
    }

    private static List<FileSystemInfo> TryListDirectory(string directory, IScanSink sink)
    {
        try
        {
            return new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            sink.AddError(new ScanError(directory, $"Folder could not be read: {ex.Message}"));
            return [];
        }
    }

    private string? TryGetCachedHash(SourceFile file, IScanSink sink)
    {
        try
        {
            return _cache.TryGet(file.Location, file.Size, file.LastWriteUtc);
        }
        catch (Exception ex)
        {
            ReportCacheFailure(file, ex, sink);
            return null;
        }
    }

    private void TryCacheHash(SourceFile file, string hash, IScanSink sink)
    {
        try
        {
            _cache.Set(file.Location, file.Size, file.LastWriteUtc, hash);
        }
        catch (Exception ex)
        {
            ReportCacheFailure(file, ex, sink);
        }
    }

    /// <summary>The cache is an optimization; failures are reported once per scan and do not affect results.</summary>
    private static void ReportCacheFailure(SourceFile file, Exception ex, IScanSink sink) =>
        sink.AddNoticeOnce(CacheFailureKey, new ScanNotice(file.DisplayPath, $"Hash cache unavailable; files are hashed without caching: {ex.Message}"));
}
