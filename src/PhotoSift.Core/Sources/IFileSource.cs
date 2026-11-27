using PhotoSift.Core.Scanning;

namespace PhotoSift.Core.Sources;

/// <summary>
/// A place image files can be scanned from: the local file system, or a cloud storage
/// account such as Google Drive.
/// </summary>
/// <remarks>
/// Every file and folder is identified by a <em>location</em> string that is unique across
/// sources. Local locations are absolute paths; other sources use URIs such as
/// <c>gdrive://account/fileId</c> (see <see cref="PathUtil.IsLocal"/>).
/// </remarks>
public interface IFileSource
{
    /// <summary>Stable identifier stored in <see cref="ScannedFile.SourceId"/>, e.g. "local".</summary>
    string Id { get; }

    /// <summary>Whether a file or folder location belongs to this source.</summary>
    bool Owns(string location);

    /// <summary>
    /// Lists the supported image files in <paramref name="folders"/> and their subfolders.
    /// Unreadable folders are reported as errors and items deliberately skipped as notices;
    /// both go to <paramref name="sink"/>, and enumeration continues with the other items.
    /// </summary>
    IAsyncEnumerable<SourceFile> EnumerateAsync(
        IReadOnlyList<string> folders,
        IScanSink sink,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the MD5 hash (lowercase hex) of the file's contents. Sources that know the hash
    /// from metadata return <see cref="SourceFile.KnownHash"/> without reading the file.
    /// Throws if the file cannot be read.
    /// </summary>
    Task<string> GetHashAsync(SourceFile file, IScanSink sink, CancellationToken cancellationToken);

    /// <summary>Whether <paramref name="file"/> is inside <paramref name="folder"/> or any of its subfolders.</summary>
    bool IsInFolder(ScannedFile file, string folder);

    /// <summary>
    /// Re-checks the file just before deletion. Returns null when it still exists with the size and
    /// hash recorded during the scan, otherwise the reason it does not. May throw if it cannot be checked.
    /// </summary>
    Task<string?> VerifyAsync(ScannedFile file, CancellationToken cancellationToken);

    /// <summary>Null when files from this source can be moved to the trash; otherwise the reason they cannot.</summary>
    string? TrashUnavailableReason { get; }

    /// <summary>Moves the file to the source's trash. Never deletes permanently; throws on failure.</summary>
    Task MoveToTrashAsync(ScannedFile file, CancellationToken cancellationToken);
}

/// <summary>A file found by a source during enumeration, before it has been hashed.</summary>
/// <param name="Location">Unique location (see <see cref="IFileSource"/>).</param>
/// <param name="DisplayPath">Human-readable path shown to the user.</param>
/// <param name="KnownHash">The content hash when the source provides it without reading the file.</param>
public sealed record SourceFile(string Location, string DisplayPath, long Size, DateTime LastWriteUtc, string? KnownHash = null);

/// <summary>Collects problems found while scanning. Implementations are thread-safe.</summary>
public interface IScanSink
{
    void AddError(ScanError error);

    void AddNotice(ScanNotice notice);

    /// <summary>Adds the notice only the first time <paramref name="key"/> is used during a scan.</summary>
    void AddNoticeOnce(string key, ScanNotice notice);
}
