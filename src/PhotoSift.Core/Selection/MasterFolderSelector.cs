using PhotoSift.Core.Scanning;
using PhotoSift.Core.Sources;

namespace PhotoSift.Core.Selection;

public static class MasterFolderSelector
{
    /// <summary>
    /// For each group that has at least one copy inside any of <paramref name="masterFolders"/>
    /// (including subfolders), returns every copy that is outside all master folders.
    /// Groups without a master copy contribute nothing, and copies inside a master
    /// folder are never returned. Master folders may belong to any source.
    /// </summary>
    /// <param name="sources">Decides whether a file is in a folder; defaults to the local file system only.</param>
    public static IReadOnlyList<ScannedFile> SelectCopiesOutsideMasters(
        IEnumerable<DuplicateGroup> groups,
        IReadOnlyCollection<string> masterFolders,
        FileSources? sources = null)
    {
        var selected = new List<ScannedFile>();
        if (masterFolders.Count == 0)
        {
            return selected;
        }

        foreach (var group in groups)
        {
            if (!group.Files.Any(f => IsInAnyMasterFolder(f, masterFolders, sources)))
            {
                continue;
            }

            selected.AddRange(group.Files.Where(f => !IsInAnyMasterFolder(f, masterFolders, sources)));
        }

        return selected;
    }

    public static bool IsInAnyMasterFolder(ScannedFile file, IEnumerable<string> masterFolders, FileSources? sources = null)
    {
        var resolved = sources ?? FileSources.Default;
        return masterFolders.Any(master => resolved.IsInFolder(file, master));
    }
}
