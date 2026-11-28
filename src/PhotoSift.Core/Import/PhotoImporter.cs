using System.Diagnostics;
using System.Security.Cryptography;
using PhotoSift.Core.Caching;
using PhotoSift.Core.Hashing;

namespace PhotoSift.Core.Import;

/// <summary>
/// Copies photos and videos from a phone (or folder) to the computer, safely and resumably.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Copy only: nothing on the source is changed or deleted.</item>
/// <item>Each file is written to a <c>.photosift-partial</c> file first. It gets its real name only after
/// its size matches the source and the data on disk has the same MD5 as the data read. So an
/// interrupted copy (cancel, crash, power loss, pulled cable) never leaves a broken file under a real name.</item>
/// <item>The journal records each copy before it starts. The next plan finds copies that were cut
/// off, removes their partial files, and copies those files again.</item>
/// <item>Files imported before are skipped without being read again. A different file with the same
/// name gets a new name ("IMG_1 (2).jpg"); an identical file already at the destination is not copied twice.</item>
/// <item>The copies keep the source's folder structure under a folder named after the source, and its modification dates.</item>
/// </list>
/// </remarks>
public sealed class PhotoImporter(
    ImportJournal journal,
    IHashCache? hashCache = null,
    Func<string, long?>? freeSpaceOf = null)
{
    public const string PartialSuffix = ".photosift-partial";

    /// <summary>After this many failed files in a row, the import stops: the source has probably stopped responding.</summary>
    public const int MaxFailuresInARow = 3;

    /// <summary>Space kept free on the destination drive, on top of the files to copy.</summary>
    public const long FreeSpaceMargin = 200L * 1024 * 1024;

    private const int BufferSize = 1024 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly Md5FileHasher _hasher = new();
    private readonly Func<string, long?> _freeSpaceOf = freeSpaceOf ?? AvailableFreeSpace;

    /// <summary>
    /// Lists what to copy: recovers copies interrupted last time, skips files imported before,
    /// and checks that the destination has room.
    /// </summary>
    public async Task<ImportPlan> PlanAsync(
        IImportSource source, string destinationRoot, IProgress<int>? found = null, CancellationToken cancellationToken = default)
    {
        destinationRoot = Path.GetFullPath(destinationRoot);
        var interrupted = RecoverInterrupted(source.Id);
        var entries = journal.GetEntries(source.Id);

        var toCopy = new List<ImportItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var alreadyImported = 0;
        await foreach (var item in source.EnumerateAsync(found, cancellationToken).ConfigureAwait(false))
        {
            if (!MediaFormats.IsSupported(item.FileName) || !seen.Add(item.RelativePath))
            {
                continue;
            }

            // Imported before (same path and size): skip, even if the copy was moved or deleted since,
            // so organizing or deleting imported photos does not bring them back.
            if (entries.TryGetValue(item.RelativePath, out var entry) && entry.Status == JournalStatus.Done && entry.Size == item.Size)
            {
                alreadyImported++;
                continue;
            }

            toCopy.Add(item);
        }

        return new ImportPlan
        {
            Source = source,
            DestinationRoot = destinationRoot,
            ToCopy = toCopy,
            AlreadyImported = alreadyImported,
            Interrupted = interrupted,
            Problem = CheckDestination(source, destinationRoot, toCopy.Sum(i => i.Size)),
        };
    }

    public async Task<ImportReport> RunAsync(
        ImportPlan plan, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (plan.Problem is not null)
        {
            throw new InvalidOperationException(plan.Problem);
        }

        var sourceFolder = Path.Combine(plan.DestinationRoot, SafeName(plan.Source.DisplayName));
        var outcomes = new List<ImportOutcome>();
        var totalBytes = plan.BytesToCopy;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        long finishedBytes = 0;
        var failuresInARow = 0;
        var canceled = false;
        string? stopReason = null;

        void Report(string? current, long currentFileBytes, bool force)
        {
            if (progress is null || (!force && stopwatch.Elapsed - lastReport < ProgressInterval))
            {
                return;
            }

            lastReport = stopwatch.Elapsed;
            var copied = finishedBytes + currentFileBytes;
            var seconds = stopwatch.Elapsed.TotalSeconds;
            progress.Report(new ImportProgress(
                plan.ToCopy.Count,
                outcomes.Count(o => o.Status != ImportStatus.Failed),
                outcomes.Count(o => o.Status == ImportStatus.Failed),
                totalBytes,
                copied,
                current,
                seconds > 0.5 ? copied / seconds : 0));
        }

        foreach (var item in plan.ToCopy)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            Report(item.RelativePath, 0, force: true);
            ImportOutcome outcome;
            try
            {
                outcome = await ImportOneAsync(
                        plan.Source, item, sourceFolder, bytes => Report(item.RelativePath, bytes, force: false), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            outcomes.Add(outcome);
            finishedBytes += item.Size;

            if (outcome.Status != ImportStatus.Failed)
            {
                failuresInARow = 0;
                continue;
            }

            if (outcome.DiskFull)
            {
                stopReason = "The destination drive is full. Free up space and continue the import.";
                break;
            }

            if (++failuresInARow >= MaxFailuresInARow)
            {
                stopReason = $"{MaxFailuresInARow} files in a row could not be copied, so the import stopped. " +
                             "Check that the phone is connected and unlocked, then continue the import.";
                break;
            }
        }

        Report(null, 0, force: true);
        return new ImportReport(outcomes, canceled, stopReason, plan.ToCopy.Count - outcomes.Count);
    }

    /// <summary>
    /// Where a file is copied: the source's folder structure below a folder named after the source.
    /// Names are made safe for the file system, and ".." can never leave the destination.
    /// </summary>
    public static string DestinationPathFor(string sourceFolder, string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafeName).ToArray();
        return segments.Length == 0
            ? throw new ArgumentException("The file has no name.", nameof(relativePath))
            : Path.Combine([sourceFolder, .. segments]);
    }

    /// <summary>A valid file or folder name on every OS; never "." or "..".</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var safe = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return safe.Length == 0 || safe.All(c => c == '.') ? "_" : safe;
    }

    private async Task<ImportOutcome> ImportOneAsync(
        IImportSource source, ImportItem item, string sourceFolder, Action<long> onBytes, CancellationToken cancellationToken)
    {
        string destination;
        try
        {
            destination = DestinationPathFor(sourceFolder, item.RelativePath);
        }
        catch (ArgumentException ex)
        {
            return new ImportOutcome(item, ImportStatus.Failed, null, ex.Message);
        }

        var partial = destination + PartialSuffix;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            journal.MarkCopying(source.Id, item, partial);

            var (md5, bytes) = await CopyToPartialAsync(source, item, partial, onBytes, cancellationToken).ConfigureAwait(false);
            if (bytes != item.Size)
            {
                throw new ImportFileException(
                    $"The source sent {bytes:N0} bytes, but the file is {item.Size:N0} bytes. The copy was discarded. " +
                    "(On an iPhone, set Settings > Photos > Transfer to Mac or PC to \"Keep Originals\".)");
            }

            var onDisk = await _hasher.ComputeHashAsync(partial, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(onDisk, md5, StringComparison.OrdinalIgnoreCase))
            {
                throw new ImportFileException("The copy on disk does not match the data read from the source. The copy was discarded.");
            }

            var final = destination;
            if (File.Exists(destination))
            {
                var existing = await _hasher.ComputeHashAsync(destination, cancellationToken).ConfigureAwait(false);
                if (string.Equals(existing, md5, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(partial);
                    journal.MarkDone(source.Id, item, destination, md5);
                    return new ImportOutcome(item, ImportStatus.AlreadyThere, destination, null);
                }

                final = UniquePath(destination);
            }

            File.Move(partial, final);
            if (item.ModifiedUtc is { } modified)
            {
                File.SetLastWriteTimeUtc(final, modified);
            }

            journal.MarkDone(source.Id, item, final, md5);
            TryCacheHash(final, bytes, md5);
            return new ImportOutcome(item, ImportStatus.Copied, final, null);
        }
        catch (Exception ex)
        {
            // Never leave an incomplete copy behind. If the partial file cannot be removed now, the
            // journal keeps it as "copying", so the next plan removes it.
            if (TryDelete(partial))
            {
                journal.Remove(source.Id, item.RelativePath);
            }

            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            var diskFull = IsDiskFull(ex);
            var reason = ex switch
            {
                ImportFileException => ex.Message,
                _ when diskFull => "The destination drive is full.",
                _ => $"Could not copy: {ex.Message}",
            };
            return new ImportOutcome(item, ImportStatus.Failed, null, reason) { DiskFull = diskFull };
        }
    }

    private static async Task<(string Md5, long Bytes)> CopyToPartialAsync(
        IImportSource source, ImportItem item, string partial, Action<long> onBytes, CancellationToken cancellationToken)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[BufferSize];
        long total = 0;

        await using var input = await source.OpenReadAsync(item, cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            md5.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            onBytes(total);
        }

        // Make sure the data is on the disk, not only in the OS cache, before the file is renamed.
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        return (Convert.ToHexStringLower(md5.GetHashAndReset()), total);
    }

    /// <summary>Removes partial files of copies that were cut off last time and returns those files.</summary>
    private List<string> RecoverInterrupted(string sourceId)
    {
        var interrupted = new List<string>();
        foreach (var entry in journal.GetEntries(sourceId).Values.Where(e => e.Status == JournalStatus.Copying))
        {
            if (entry.PartialPath is null || TryDelete(entry.PartialPath))
            {
                journal.Remove(sourceId, entry.RelativePath);
            }

            interrupted.Add(entry.RelativePath);
        }

        return interrupted;
    }

    private string? CheckDestination(IImportSource source, string destinationRoot, long bytesToCopy)
    {
        if (source is FolderImportSource folder && PathUtil.IsInFolder(destinationRoot, folder.Folder))
        {
            return "Choose a destination outside the folder you import from.";
        }

        var free = _freeSpaceOf(destinationRoot);
        if (bytesToCopy > 0 && free is { } available && available < bytesToCopy + FreeSpaceMargin)
        {
            return $"Not enough free space: the files need {SizeText.Format(bytesToCopy)}, " +
                   $"and the destination has {SizeText.Format(available)} free.";
        }

        return null;
    }

    private static string UniquePath(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(directory, $"{name} ({n}){extension}");
            if (!File.Exists(candidate) && !File.Exists(candidate + PartialSuffix))
            {
                return candidate;
            }
        }
    }

    private void TryCacheHash(string path, long size, string md5)
    {
        try
        {
            // The next duplicate scan of the imported files can then skip hashing them.
            hashCache?.Set(path, size, File.GetLastWriteTimeUtc(path), md5);
        }
        catch (Exception)
        {
            // The cache is only an optimization.
        }
    }

    /// <summary>ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL on Windows, ENOSPC elsewhere. Messages are localized, so codes are used.</summary>
    private static bool IsDiskFull(Exception ex) =>
        ex is IOException && (ex.HResult & 0xFFFF) is 0x70 or 0x27 || ex is IOException { HResult: 28 };

    /// <summary>Deletes the file if it exists; false if it could not be deleted.</summary>
    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long? AvailableFreeSpace(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class ImportFileException(string message) : Exception(message);
}

public static class SizeText
{
    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };
}
