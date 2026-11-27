namespace PhotoSift.Core.Scanning;

/// <summary>A scanned image file and the MD5 hash of its contents at scan time.</summary>
/// <param name="Path">
/// The file's unique location: an absolute path for local files, or a source URI such as
/// <c>gdrive://account/fileId</c> for other sources (see <see cref="Sources.IFileSource"/>).
/// </param>
public sealed record ScannedFile(string Path, long Size, DateTime LastWriteUtc, string Hash)
{
    /// <summary>The <see cref="Sources.IFileSource.Id"/> of the source the file was found in.</summary>
    public string SourceId { get; init; } = Sources.LocalFileSource.SourceId;

    /// <summary>Human-readable path shown to the user. Equal to <see cref="Path"/> for local files.</summary>
    public string DisplayPath { get; init; } = Path;

    public string FileName => System.IO.Path.GetFileName(DisplayPath);

    public string DirectoryPath => System.IO.Path.GetDirectoryName(DisplayPath) ?? string.Empty;
}

/// <summary>Two or more files with identical contents.</summary>
public sealed class DuplicateGroup
{
    public DuplicateGroup(string hash, long size, IReadOnlyList<ScannedFile> files)
    {
        if (files.Count < 2)
        {
            throw new ArgumentException("A duplicate group must contain at least two files.", nameof(files));
        }

        Hash = hash;
        Size = size;
        Files = files;
    }

    public string Hash { get; }

    public long Size { get; }

    public IReadOnlyList<ScannedFile> Files { get; }
}

/// <summary>A file or folder that could not be scanned. Any error makes the scan incomplete.</summary>
public sealed record ScanError(string Path, string Reason);

/// <summary>Informational message about an item that was deliberately skipped (does not make the scan incomplete).</summary>
public sealed record ScanNotice(string Path, string Message);

public sealed record ScanProgress(int FilesDiscovered, int FilesProcessed, bool EnumerationComplete);

public sealed class ScanResult
{
    private readonly Dictionary<string, DuplicateGroup> _groupByPath;

    public ScanResult(
        IReadOnlyList<string> rootFolders,
        IReadOnlyList<DuplicateGroup> groups,
        IReadOnlyList<ScanError> errors,
        IReadOnlyList<ScanNotice> notices,
        int filesScanned,
        bool wasCanceled)
    {
        RootFolders = rootFolders;
        Groups = groups;
        Errors = errors;
        Notices = notices;
        FilesScanned = filesScanned;
        WasCanceled = wasCanceled;

        _groupByPath = new Dictionary<string, DuplicateGroup>(Core.PathUtil.Comparer);
        foreach (var group in groups)
        {
            foreach (var file in group.Files)
            {
                _groupByPath[file.Path] = group;
            }
        }
    }

    public IReadOnlyList<string> RootFolders { get; }

    public IReadOnlyList<DuplicateGroup> Groups { get; }

    public IReadOnlyList<ScanError> Errors { get; }

    public IReadOnlyList<ScanNotice> Notices { get; }

    public int FilesScanned { get; }

    public bool WasCanceled { get; }

    /// <summary>
    /// A scan is complete only if it was not canceled and every file and folder could be read.
    /// Deletion is only allowed from complete scans.
    /// </summary>
    public bool IsComplete => !WasCanceled && Errors.Count == 0;

    public DuplicateGroup? FindGroup(string path) =>
        _groupByPath.GetValueOrDefault(path);
}
