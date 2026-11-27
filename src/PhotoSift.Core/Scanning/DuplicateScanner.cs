using System.Collections.Concurrent;
using System.Threading.Channels;
using PhotoSift.Core.Caching;
using PhotoSift.Core.Hashing;
using PhotoSift.Core.Sources;

namespace PhotoSift.Core.Scanning;

/// <summary>
/// Scans folders for exact duplicate images. A producer enumerates supported image files from
/// each folder's source and a pool of consumers hashes them in parallel. Files from different
/// sources are compared with each other, so a local photo and a cloud copy form one group.
/// </summary>
public sealed class DuplicateScanner
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly int _workerCount;

    public DuplicateScanner(FileSources sources, int? workerCount = null)
    {
        Sources = sources;
        _workerCount = workerCount ?? Math.Clamp(Environment.ProcessorCount, 1, 8);
    }

    /// <summary>Scans the local file system only.</summary>
    public DuplicateScanner(IFileHasher hasher, IHashCache cache, int? workerCount = null)
        : this(new FileSources(new LocalFileSource(hasher, cache)), workerCount)
    {
    }

    public FileSources Sources { get; }

    /// <param name="rootFolders">Folder locations; each is scanned by the source that owns it.</param>
    public async Task<ScanResult> ScanAsync(
        IReadOnlyList<string> rootFolders,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var state = new ScanState();
        var channel = Channel.CreateBounded<Candidate>(new BoundedChannelOptions(1024)
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

    private async Task ProduceAsync(
        IReadOnlyList<string> rootFolders,
        ChannelWriter<Candidate> writer,
        ScanState state,
        CancellationToken cancellationToken)
    {
        try
        {
            // Each folder goes to the source that owns it; sources are scanned in the order
            // their first folder appears.
            var foldersBySource = new List<(IFileSource Source, List<string> Folders)>();
            foreach (var folder in rootFolders)
            {
                var source = Sources.FindOwner(folder);
                if (source is null)
                {
                    state.AddError(new ScanError(folder, "No available source can scan this location."));
                    continue;
                }

                var entry = foldersBySource.FirstOrDefault(e => e.Source == source);
                if (entry.Source is null)
                {
                    foldersBySource.Add(entry = (source, []));
                }

                entry.Folders.Add(folder);
            }

            var seen = new HashSet<string>(PathUtil.Comparer);
            foreach (var (source, folders) in foldersBySource)
            {
                await foreach (var file in source.EnumerateAsync(folders, state, cancellationToken).ConfigureAwait(false))
                {
                    // A file must never be grouped as a duplicate of itself.
                    if (!seen.Add(file.Location))
                    {
                        continue;
                    }

                    state.IncrementDiscovered();
                    await writer.WriteAsync(new Candidate(source, file), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            state.EnumerationComplete = true;
            writer.Complete();
        }
    }

    private static async Task ConsumeAsync(
        ChannelReader<Candidate> reader,
        ScanState state,
        CancellationToken cancellationToken)
    {
        await foreach (var (source, file) in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                // Sources report lowercase hex, but normalize so local and cloud copies always match.
                var hash = (await source.GetHashAsync(file, state, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
                state.Files.Add(new ScannedFile(file.Location, file.Size, file.LastWriteUtc, hash)
                {
                    SourceId = source.Id,
                    DisplayPath = file.DisplayPath,
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                state.AddError(new ScanError(file.DisplayPath, $"File could not be read: {ex.Message}"));
            }
            finally
            {
                state.IncrementProcessed();
            }
        }
    }

    private sealed record Candidate(IFileSource Source, SourceFile File);

    private sealed class ScanState : IScanSink
    {
        private readonly ConcurrentDictionary<string, byte> _onceKeys = new(StringComparer.Ordinal);
        private int _discovered;
        private int _processed;

        public ConcurrentBag<ScannedFile> Files { get; } = [];

        public ConcurrentBag<ScanError> Errors { get; } = [];

        public ConcurrentBag<ScanNotice> Notices { get; } = [];

        public volatile bool EnumerationComplete;

        public void AddError(ScanError error) => Errors.Add(error);

        public void AddNotice(ScanNotice notice) => Notices.Add(notice);

        public void AddNoticeOnce(string key, ScanNotice notice)
        {
            if (_onceKeys.TryAdd(key, 0))
            {
                Notices.Add(notice);
            }
        }

        public void IncrementDiscovered() => Interlocked.Increment(ref _discovered);

        public void IncrementProcessed() => Interlocked.Increment(ref _processed);

        public ScanProgress Snapshot() =>
            new(Volatile.Read(ref _discovered), Volatile.Read(ref _processed), EnumerationComplete);
    }
}
