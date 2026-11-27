using PhotoSift.Core.Caching;
using PhotoSift.Core.Deletion;
using PhotoSift.Core.Hashing;
using PhotoSift.Core.Scanning;
using PhotoSift.Core.Selection;

namespace PhotoSift.Tests;

public class MasterFolderSelectorTests
{
    [Fact]
    public async Task Marks_copies_outside_master_and_keeps_master_copies()
    {
        using var temp = new TempFolder();
        var master = temp.PathOf("master");
        var inMaster = temp.WriteImage("master/2024/keep.jpg", seed: 1);
        var inMasterToo = temp.WriteImage("master/other/keep-too.jpg", seed: 1);
        var outside1 = temp.WriteImage("downloads/IMG_1.jpg", seed: 1);
        var outside2 = temp.WriteImage("master-backup/renamed.jpg", seed: 1);
        temp.WriteImage("downloads/no-master-a.jpg", seed: 2);
        temp.WriteImage("phone/no-master-b.jpg", seed: 2);

        var scan = await Scan(temp);
        var selected = MasterFolderSelector.SelectCopiesOutsideMaster(scan.Groups, master).Select(f => f.Path).ToList();

        Assert.Equal([outside1, outside2], selected.Order());
        Assert.DoesNotContain(inMaster, selected);
        Assert.DoesNotContain(inMasterToo, selected);
    }

    [Fact]
    public async Task Nothing_selected_when_group_has_no_master_copy()
    {
        using var temp = new TempFolder();
        temp.WriteImage("a/x.jpg", seed: 1);
        temp.WriteImage("b/y.jpg", seed: 1);
        Directory.CreateDirectory(temp.PathOf("master"));

        var scan = await Scan(temp);

        Assert.Empty(MasterFolderSelector.SelectCopiesOutsideMaster(scan.Groups, temp.PathOf("master")));
    }

    internal static Task<ScanResult> Scan(TempFolder temp) =>
        new DuplicateScanner(new Md5FileHasher(), NullHashCache.Instance).ScanAsync([temp.Root]);
}

public class SafeDeleterTests
{
    private readonly FakeTrashService _trash = new();

    private SafeDeleter CreateDeleter() => new(new Md5FileHasher(), _trash);

    [Fact]
    public async Task Moves_verified_selected_copies_to_trash()
    {
        using var temp = new TempFolder();
        var keep = temp.WriteImage("keep.jpg", seed: 1);
        var dup1 = temp.WriteImage("dup1.jpg", seed: 1);
        var dup2 = temp.WriteImage("sub/dup2.jpg", seed: 1);
        var scan = await MasterFolderSelectorTests.Scan(temp);

        var report = await CreateDeleter().MoveToTrashAsync(scan, [dup1, dup2]);

        Assert.Equal(2, report.MovedToTrashCount);
        Assert.Equal([dup1, dup2], _trash.Trashed.Order());
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public async Task Refuses_incomplete_scan()
    {
        using var temp = new TempFolder();
        temp.WriteImage("a.jpg", seed: 1);
        var dup = temp.WriteImage("b.jpg", seed: 1);
        var unreadable = temp.WriteImage("c.jpg", seed: 2);
        var scan = await new DuplicateScanner(new FailingHasher(new Md5FileHasher(), unreadable), NullHashCache.Instance)
            .ScanAsync([temp.Root]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateDeleter().MoveToTrashAsync(scan, [dup]));
        Assert.Empty(_trash.Trashed);
        Assert.True(File.Exists(dup));
    }

    [Fact]
    public async Task Skips_files_changed_after_scan()
    {
        using var temp = new TempFolder();
        temp.WriteImage("a.jpg", seed: 1);
        var sameSize = temp.WriteImage("b.jpg", seed: 1);
        var differentSize = temp.WriteImage("c.jpg", seed: 1);
        var scan = await MasterFolderSelectorTests.Scan(temp);

        temp.WriteImage("b.jpg", seed: 99);
        temp.WriteImage("c.jpg", seed: 1, length: 10);

        var report = await CreateDeleter().MoveToTrashAsync(scan, [sameSize, differentSize]);

        Assert.Empty(_trash.Trashed);
        Assert.Equal(2, report.SkippedCount);
        Assert.Contains("contents have changed", Outcome(report, sameSize).Reason);
        Assert.Contains("size has changed", Outcome(report, differentSize).Reason);
    }

    [Fact]
    public async Task Skips_missing_files()
    {
        using var temp = new TempFolder();
        temp.WriteImage("a.jpg", seed: 1);
        var gone = temp.WriteImage("b.jpg", seed: 1);
        var scan = await MasterFolderSelectorTests.Scan(temp);
        File.Delete(gone);

        var report = await CreateDeleter().MoveToTrashAsync(scan, [gone]);

        Assert.Equal(DeletionStatus.Skipped, Outcome(report, gone).Status);
        Assert.Contains("no longer exists", Outcome(report, gone).Reason);
    }

    [Fact]
    public async Task Never_trashes_every_copy_in_a_group()
    {
        using var temp = new TempFolder();
        var a = temp.WriteImage("a.jpg", seed: 1);
        var b = temp.WriteImage("b.jpg", seed: 1);
        var scan = await MasterFolderSelectorTests.Scan(temp);

        var report = await CreateDeleter().MoveToTrashAsync(scan, [a, b]);

        Assert.Empty(_trash.Trashed);
        Assert.All(report.Outcomes, o => Assert.Equal(DeletionStatus.Skipped, o.Status));
        Assert.True(File.Exists(a) && File.Exists(b));
    }

    [Fact]
    public async Task Skips_group_when_no_kept_copy_still_matches()
    {
        using var temp = new TempFolder();
        temp.WriteImage("keep.jpg", seed: 1);
        var dup = temp.WriteImage("dup.jpg", seed: 1);
        var scan = await MasterFolderSelectorTests.Scan(temp);
        temp.WriteImage("keep.jpg", seed: 42);

        var report = await CreateDeleter().MoveToTrashAsync(scan, [dup]);

        Assert.Empty(_trash.Trashed);
        Assert.Contains("kept copies", Outcome(report, dup).Reason);
    }

    [Fact]
    public async Task Reports_trash_failures()
    {
        using var temp = new TempFolder();
        temp.WriteImage("a.jpg", seed: 1);
        var dup = temp.WriteImage("b.jpg", seed: 1);
        var scan = await MasterFolderSelectorTests.Scan(temp);
        _trash.FailFor.Add(dup);

        var report = await CreateDeleter().MoveToTrashAsync(scan, [dup]);

        Assert.Equal(DeletionStatus.Failed, Outcome(report, dup).Status);
        Assert.Contains("Trash is not available", Outcome(report, dup).Reason);
        Assert.True(File.Exists(dup));
    }

    [Fact]
    public async Task Skips_files_not_in_a_duplicate_group()
    {
        using var temp = new TempFolder();
        var unique = temp.WriteImage("unique.jpg", seed: 1);
        var scan = await MasterFolderSelectorTests.Scan(temp);

        var report = await CreateDeleter().MoveToTrashAsync(scan, [unique]);

        Assert.Equal(DeletionStatus.Skipped, Outcome(report, unique).Status);
        Assert.True(File.Exists(unique));
    }

    private static DeletionOutcome Outcome(DeletionReport report, string path) =>
        Assert.Single(report.Outcomes, o => o.Path == path);
}
