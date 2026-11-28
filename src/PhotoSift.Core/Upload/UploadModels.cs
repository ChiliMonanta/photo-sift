using PhotoSift.Core.GoogleDrive;
using PhotoSift.Core.Import;

namespace PhotoSift.Core.Upload;

/// <summary>The Drive folder files are uploaded to.</summary>
/// <param name="AccountId">The account's permission id; a folder chosen in another account is not used.</param>
/// <param name="DisplayPath">E.g. "My Drive/Photos".</param>
public sealed record DriveFolderTarget(string AccountId, string FolderId, string DisplayPath);

/// <summary>A local file to upload.</summary>
/// <param name="File">Key is the full local path; RelativePath is below the chosen local folder.</param>
/// <param name="Md5">The file's MD5 when it was checked; Drive's MD5 of the upload must match it.</param>
/// <param name="Resume">An upload of this file that was interrupted last time, if any.</param>
public sealed record UploadItem(ImportItem File, string Md5, UploadResume? Resume = null);

/// <summary>An interrupted upload that can continue where it stopped.</summary>
public sealed record UploadResume(Uri Session, string DriveName, string ParentId);

/// <summary>What an upload will do, worked out before anything is sent.</summary>
public sealed class UploadPlan
{
    public required IDriveApi Api { get; init; }

    public required string LocalFolder { get; init; }

    public required DriveFolderTarget Target { get; init; }

    /// <summary>The folder created in the Drive folder for the uploads: the local folder's name.</summary>
    public required string TopFolderName { get; init; }

    /// <summary>Keys the upload journal: the account, the Drive folder, and the local folder.</summary>
    public required string JournalKey { get; init; }

    public required IReadOnlyList<UploadItem> ToUpload { get; init; }

    /// <summary>Files with the same contents (MD5) already somewhere in the Drive folder.</summary>
    public required int AlreadyOnDrive { get; init; }

    /// <summary>Files uploaded before and unchanged since; they are not read again.</summary>
    public required int UploadedBefore { get; init; }

    /// <summary>Files identical to another file in the same upload; only one copy is uploaded.</summary>
    public required int DuplicatesInFolder { get; init; }

    /// <summary>Files that cannot be uploaded (e.g. unreadable or empty), with the reason.</summary>
    public required IReadOnlyList<string> Problems { get; init; }

    public long BytesToUpload => ToUpload.Sum(i => i.File.Size);

    public int Resumed => ToUpload.Count(i => i.Resume is not null);

    internal DriveFolderTree Tree { get; init; } = new();
}

public sealed record UploadProgress(
    int TotalFiles,
    int UploadedFiles,
    int FailedFiles,
    long TotalBytes,
    long UploadedBytes,
    string? CurrentFile,
    double BytesPerSecond)
{
    public int FilesLeft => TotalFiles - UploadedFiles - FailedFiles;
}

public enum UploadOutcomeStatus
{
    Uploaded,

    Failed,
}

/// <param name="DrivePath">Where the file is in Drive, e.g. "My Drive/Photos/Pixel 8/DCIM/IMG_1.jpg".</param>
public sealed record UploadOutcome(UploadItem Item, UploadOutcomeStatus Status, string? DrivePath, string? Reason);

public sealed class UploadReport(IReadOnlyList<UploadOutcome> outcomes, bool wasCanceled, string? stopReason, int notStarted)
{
    public IReadOnlyList<UploadOutcome> Outcomes { get; } = outcomes;

    public bool WasCanceled { get; } = wasCanceled;

    /// <summary>Why the upload stopped early without being canceled (e.g. no connection), or null.</summary>
    public string? StopReason { get; } = stopReason;

    /// <summary>Files that were not attempted because the upload stopped; they are uploaded next time.</summary>
    public int NotStarted { get; } = notStarted;

    public int UploadedCount => Outcomes.Count(o => o.Status == UploadOutcomeStatus.Uploaded);

    public int FailedCount => Outcomes.Count(o => o.Status == UploadOutcomeStatus.Failed);
}

/// <summary>The folders and file contents (MD5) in the Drive folder, read before uploading.</summary>
internal sealed class DriveFolderTree
{
    /// <summary>Folders by their path below the Drive folder ("" is the Drive folder itself, "a/b" below it).</summary>
    public Dictionary<string, DriveFolderNodeInfo> Folders { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Md5s { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class DriveFolderNodeInfo(string id)
{
    public string Id { get; } = id;

    /// <summary>Names of the files in the folder (Drive names are case-sensitive and need not be unique).</summary>
    public HashSet<string> FileNames { get; } = new(StringComparer.Ordinal);
}
