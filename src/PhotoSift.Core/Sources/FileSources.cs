using PhotoSift.Core.Hashing;
using PhotoSift.Core.Scanning;

namespace PhotoSift.Core.Sources;

/// <summary>The file sources the app can scan, looked up by location or by id.</summary>
public sealed class FileSources
{
    public FileSources(params IEnumerable<IFileSource> sources)
    {
        All = sources.ToList();
        var duplicate = All.GroupBy(s => s.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"More than one file source has the id '{duplicate.Key}'.", nameof(sources));
        }
    }

    /// <summary>Only the local file system (without trash support). Enough for scanning and folder checks.</summary>
    public static FileSources Default { get; } = new(new LocalFileSource(new Md5FileHasher()));

    public IReadOnlyList<IFileSource> All { get; }

    public IFileSource? FindOwner(string location) => All.FirstOrDefault(s => s.Owns(location));

    public IFileSource? Get(string sourceId) => All.FirstOrDefault(s => s.Id == sourceId);

    /// <summary>
    /// Whether <paramref name="file"/> is inside <paramref name="folder"/> (including subfolders).
    /// A file is never inside a folder that belongs to a different source.
    /// </summary>
    public bool IsInFolder(ScannedFile file, string folder)
    {
        var owner = FindOwner(folder);
        return owner is not null && owner.Id == file.SourceId && owner.IsInFolder(file, folder);
    }
}
