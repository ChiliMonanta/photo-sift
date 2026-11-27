using PhotoSift.Core.Scanning;

namespace PhotoSift.Core.Selection;

public static class MasterFolderSelector
{
    /// <summary>
    /// For each group that has at least one copy inside <paramref name="masterFolder"/>
    /// (including subfolders), returns every copy outside the master folder.
    /// Groups without a master copy contribute nothing, and copies inside the master
    /// folder are never returned.
    /// </summary>
    public static IReadOnlyList<ScannedFile> SelectCopiesOutsideMaster(
        IEnumerable<DuplicateGroup> groups,
        string masterFolder)
    {
        var selected = new List<ScannedFile>();

        foreach (var group in groups)
        {
            var inMaster = group.Files.Where(f => PathUtil.IsInFolder(f.Path, masterFolder)).ToList();
            if (inMaster.Count == 0)
            {
                continue;
            }

            selected.AddRange(group.Files.Where(f => !PathUtil.IsInFolder(f.Path, masterFolder)));
        }

        return selected;
    }
}
