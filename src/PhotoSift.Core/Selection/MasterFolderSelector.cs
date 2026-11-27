using PhotoSift.Core.Scanning;

namespace PhotoSift.Core.Selection;

public static class MasterFolderSelector
{
    /// <summary>
    /// For each group that has at least one copy inside any of <paramref name="masterFolders"/>
    /// (including subfolders), returns every copy that is outside all master folders.
    /// Groups without a master copy contribute nothing, and copies inside a master
    /// folder are never returned.
    /// </summary>
    public static IReadOnlyList<ScannedFile> SelectCopiesOutsideMasters(
        IEnumerable<DuplicateGroup> groups,
        IReadOnlyCollection<string> masterFolders)
    {
        var selected = new List<ScannedFile>();
        if (masterFolders.Count == 0)
        {
            return selected;
        }

        foreach (var group in groups)
        {
            if (!group.Files.Any(f => IsInAnyMasterFolder(f.Path, masterFolders)))
            {
                continue;
            }

            selected.AddRange(group.Files.Where(f => !IsInAnyMasterFolder(f.Path, masterFolders)));
        }

        return selected;
    }

    public static bool IsInAnyMasterFolder(string path, IEnumerable<string> masterFolders) =>
        masterFolders.Any(master => PathUtil.IsInFolder(path, master));
}
