using System.Collections.Concurrent;
using System.Threading.Channels;
using PhotoSift.Core.Caching;
using PhotoSift.Core.Hashing;

namespace PhotoSift.Core.Scanning;

/// <summary>
/// Scans folders for exact duplicate images. A producer enumerates supported image files
/// and a pool of consumers hashes them in parallel (using the hash cache when possible).
/// </summary>
public sealed class DuplicateScanner
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly IFileHasher _hasher;
    private readonly IHashCache _cache;
    private readonly int _workerCount;

    public DuplicateScanner(IFileHasher hasher, IHashCache cache, int? workerCount = null)
    {
        _hasher = hasher;
        _cache = cache;
        _workerCount = workerCount ?? Math.Clamp(Environment.ProcessorCount, 1, 8);
    }

    public async Task<ScanResult> ScanAsync(
        IReadOnlyList<string> rootFolders,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var state = new ScanState();
        var channel = Channel.CreateBounded<FileCandidate>(new BoundedChannelOptions(1024)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = Task.Run(() => ProduceAsync(rootFolders, channel.Writer, state, cancellationToken), CancellationToken.None);
        var consumers = Enumerable.Range(0, _workerCount)
            .Select(_ => Task.Run(() => ConsumeAsync(channel.Reader, state, cancellationToken), CancellationToken.None))
            .ToArray();

        var work = Task.WhenAll([producer, .. consumers]);
        var wasCanceled = false;

        try
        {
            if (progress is null)
            {
                await work.ConfigureAwait(false);
            }
            else
            {
                while (await Task.WhenAny(work, Task.Delay(ProgressInterval, CancellationToken.None)).ConfigureAwait(false) != work)
                {
                    progress.Report(state.Snapshot());
                }

                await work.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            wasCanceled = true;
        }

        progress?.Report(state.Snapshot());

        var groups = state.Files
            .GroupBy(f => f.Hash, StringComparer.Ordinal)
            .Where(g => g.Count() >= 2)
            .Select(g => new DuplicateGroup(
                g.Key,
                g.First().Size,
                g.OrderBy(f => f.Path, PathUtil.Comparer).ToList()))
            .OrderByDescending(g => g.Size)
            .ThenBy(g => g.Files[0].Path, PathUtil.Comparer)
            .ToList();

        return new ScanResult(
            rootFolders.ToList(),
            groups,
            state.Errors.OrderBy(e => e.Path, PathUtil.Comparer).ToList(),
            state.Notices.OrderBy(n => n.Path, PathUtil.Comparer).ToList(),
            state.Files.Count,
            wasCanceled);
    }

    private static async Task ProduceAsync(
        IReadOnlyList<string> rootFolders,
        ChannelWriter<FileCandidate> writer,
        ScanState state,
        CancellationToken cancellationToken)
    {
        try
        {
            var visitedDirectories = new HashSet<string>(PathUtil.Comparer);
            var seenFiles = new HashSet<string>(PathUtil.Comparer);

            foreach (var rootFolder in rootFolders)
            {
                string root;
                try
                {
                    root = PathUtil.Normalize(rootFolder);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
                {
                    state.Errors.Add(new ScanError(rootFolder, $"Invalid folder path: {ex.Message}"));
                    continue;
                }

                if (!Directory.Exists(root))
                {
                    state.Errors.Add(new ScanError(root, "Folder does not exist or is not accessible."));
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

                    List<FileSystemInfo> entries;
                    try
                    {
                        entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        state.Errors.Add(new ScanError(directory, $"Folder could not be read: {ex.Message}"));
                        continue;
                    }

                    foreach (var entry in entries.OrderBy(e => e.Name, PathUtil.Comparer))
                    {
                        // Symbolic links and junctions are not followed: a linked file is the same
                        // physical file as its target, so deleting one "copy" would delete both.
                        if (entry.LinkTarget is not null)
                        {
                            if (entry is DirectoryInfo || ImageFormats.IsSupported(entry.Name))
                            {
                                state.Notices.Add(new ScanNotice(entry.FullName, "Skipped symbolic link or junction."));
                            }

                            continue;
                        }

                        switch (entry)
                        {
                            case DirectoryInfo subdirectory:
                                pending.Push(subdirectory.FullName);
                                break;

                            case FileInfo file when ImageFormats.IsSupported(file.Name) && seenFiles.Add(file.FullName):
                                state.IncrementDiscovered();
                                await writer.WriteAsync(
                                    new FileCandidate(file.FullName, file.Length, file.LastWriteTimeUtc),
                                    cancellationToken).ConfigureAwait(false);
                                break;
                        }
                    }
                }
            }
        }
        finally
        {
            state.EnumerationComplete = true;
            writer.Complete();
        }
    }

    private async Task ConsumeAsync(
        ChannelReader<FileCandidate> reader,
        ScanState state,
        CancellationToken cancellationToken)
    {
        await foreach (var candidate in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var hash = TryGetCachedHash(candidate, state);
                if (hash is null)
                {
                    hash = await _hasher.ComputeHashAsync(candidate.Path, cancellationToken).ConfigureAwait(false);
                    TryCacheHash(candidate, hash, state);
                }

                state.Files.Add(new ScannedFile(candidate.Path, candidate.Size, candidate.LastWriteUtc, hash));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                state.Errors.Add(new ScanError(candidate.Path, $"File could not be read: {ex.Message}"));
            }
            finally
            {
                state.IncrementProcessed();
            }
        }
    }

    private string? TryGetCachedHash(FileCandidate candidate, ScanState state)
    {
        try
        {
            return _cache.TryGet(candidate.Path, candidate.Size, candidate.LastWriteUtc);
        }
        catch (Exception ex)
        {
            state.ReportCacheFailure(candidate.Path, ex);
            return null;
        }
    }

    private void TryCacheHash(FileCandidate candidate, string hash, ScanState state)
    {
        try
        {
            _cache.Set(candidate.Path, candidate.Size, candidate.LastWriteUtc, hash);
        }
        catch (Exception ex)
        {
            state.ReportCacheFailure(candidate.Path, ex);
        }
    }

    private sealed record FileCandidate(string Path, long Size, DateTime LastWriteUtc);

    private sealed class ScanState
    {
        private int _discovered;
        private int _processed;
        private int _cacheFailureReported;

        public ConcurrentBag<ScannedFile> Files { get; } = [];

        public ConcurrentBag<ScanError> Errors { get; } = [];

        public ConcurrentBag<ScanNotice> Notices { get; } = [];

        public volatile bool EnumerationComplete;

        public void IncrementDiscovered() => Interlocked.Increment(ref _discovered);

        public void IncrementProcessed() => Interlocked.Increment(ref _processed);

        public ScanProgress Snapshot() =>
            new(Volatile.Read(ref _discovered), Volatile.Read(ref _processed), EnumerationComplete);

        /// <summary>The cache is an optimization; failures are reported once and do not affect results.</summary>
        public void ReportCacheFailure(string path, Exception ex)
        {
            if (Interlocked.Exchange(ref _cacheFailureReported, 1) == 0)
            {
                Notices.Add(new ScanNotice(path, $"Hash cache unavailable; files are hashed without caching: {ex.Message}"));
            }
        }
    }
}
