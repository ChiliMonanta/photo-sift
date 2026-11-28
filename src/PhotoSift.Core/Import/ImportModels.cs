namespace PhotoSift.Core.Import;

/// <summary>A photo or video on a phone (or in a folder) that can be imported.</summary>
/// <param name="Key">How the source finds the file again, e.g. the device path.</param>
/// <param name="RelativePath">Path below the source, with '/' separators, e.g. "Internal shared storage/DCIM/Camera/IMG_1.jpg".
/// The copy keeps this structure, and it identifies the file in the import journal.</param>
/// <param name="Size">Size in bytes as reported by the source.</param>
/// <param name="ModifiedUtc">When the file was last changed, if the source knows; the copy gets this date.</param>
public sealed record ImportItem(string Key, string RelativePath, long Size, DateTime? ModifiedUtc)
{
    public string FileName => RelativePath[(RelativePath.LastIndexOf('/') + 1)..];
}

/// <summary>A phone or folder to import from. Read-only: nothing on the source is ever changed.</summary>
public interface IImportSource : IDisposable
{
    /// <summary>Stable identity of the source (e.g. manufacturer, model and serial number); keys the import journal.</summary>
    string Id { get; }

    /// <summary>Name shown to the user and used as the top folder of the copies, e.g. "Pixel 8".</summary>
    string DisplayName { get; }

    /// <summary>
    /// Lists the photos and videos to import. Reports the number of files found so far to
    /// <paramref name="found"/>; may be slow on phones.
    /// </summary>
    IAsyncEnumerable<ImportItem> EnumerateAsync(IProgress<int>? found, CancellationToken cancellationToken);

    /// <summary>Opens the file's contents for reading. Throws if the source no longer has it or stopped responding.</summary>
    Task<Stream> OpenReadAsync(ImportItem item, CancellationToken cancellationToken);
}

/// <summary>A phone that is connected right now.</summary>
/// <param name="DeviceId">Identifies the device to the platform when opening it.</param>
public sealed record PhoneInfo(string DeviceId, string Name, string Description);

/// <summary>Finds connected phones and opens them as import sources.</summary>
public interface IPhoneProvider
{
    /// <summary>Null when phones can be imported from on this computer; otherwise why not.</summary>
    string? UnavailableReason { get; }

    Task<IReadOnlyList<PhoneInfo>> GetPhonesAsync(CancellationToken cancellationToken);

    Task<IImportSource> OpenAsync(PhoneInfo phone, CancellationToken cancellationToken);
}

/// <summary>The file types copied from phones: the images PhotoSift scans, plus videos.</summary>
public static class MediaFormats
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".3gp", ".3g2", ".avi", ".mkv", ".mts", ".m2ts", ".webm", ".wmv",
    };

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static bool IsSupported(string path) => ImageFormats.IsSupported(path) || IsVideo(path);
}

/// <summary>What an import will do, worked out before anything is copied.</summary>
public sealed class ImportPlan
{
    public required IImportSource Source { get; init; }

    /// <summary>The folder the copies go in; each source gets its own subfolder.</summary>
    public required string DestinationRoot { get; init; }

    public required IReadOnlyList<ImportItem> ToCopy { get; init; }

    /// <summary>Files imported before and still in the destination; they are not read again.</summary>
    public required int AlreadyImported { get; init; }

    /// <summary>
    /// Files whose copy was interrupted last time (e.g. the app closed or the cable was pulled).
    /// Their incomplete copies were removed, and they are copied again.
    /// </summary>
    public required IReadOnlyList<string> Interrupted { get; init; }

    public long BytesToCopy => ToCopy.Sum(i => i.Size);

    /// <summary>Null when the import can start; otherwise why it cannot (e.g. not enough disk space).</summary>
    public string? Problem { get; init; }
}

public sealed record ImportProgress(
    int TotalFiles,
    int CopiedFiles,
    int FailedFiles,
    long TotalBytes,
    long CopiedBytes,
    string? CurrentFile,
    double BytesPerSecond)
{
    public int FilesLeft => TotalFiles - CopiedFiles - FailedFiles;
}

public enum ImportStatus
{
    Copied,

    /// <summary>An identical file was already at the destination, so nothing was copied.</summary>
    AlreadyThere,

    Failed,
}

public sealed record ImportOutcome(ImportItem Item, ImportStatus Status, string? DestinationPath, string? Reason)
{
    /// <summary>The destination drive ran out of space; the import cannot continue.</summary>
    public bool DiskFull { get; init; }
}

public sealed class ImportReport(IReadOnlyList<ImportOutcome> outcomes, bool wasCanceled, string? stopReason, int notStarted)
{
    public IReadOnlyList<ImportOutcome> Outcomes { get; } = outcomes;

    public bool WasCanceled { get; } = wasCanceled;

    /// <summary>Why the import stopped early without being canceled (e.g. the phone stopped responding), or null.</summary>
    public string? StopReason { get; } = stopReason;

    /// <summary>Files that were not attempted because the import stopped; they are copied next time.</summary>
    public int NotStarted { get; } = notStarted;

    public int CopiedCount => Outcomes.Count(o => o.Status == ImportStatus.Copied);

    public int AlreadyThereCount => Outcomes.Count(o => o.Status == ImportStatus.AlreadyThere);

    public int FailedCount => Outcomes.Count(o => o.Status == ImportStatus.Failed);
}
