using PhotoSift.Core.Scanning;
using PhotoSift.Desktop.Services;
using PhotoSift.Desktop.ViewModels;

namespace PhotoSift.Tests;

public class DuplicateGroupViewModelTests
{
    private int _markedCount;

    private DuplicateGroupViewModel CreateGroup(int copies)
    {
        var files = Enumerable.Range(1, copies)
            .Select(i => new ScannedFile(Path.Combine(Path.GetTempPath(), $"copy{i}.jpg"), 100, DateTime.UtcNow, "hash"))
            .ToList();
        return new DuplicateGroupViewModel(
            new DuplicateGroup("hash", 100, files),
            new ThumbnailLoader(),
            marked => _markedCount += marked ? 1 : -1);
    }

    [Fact]
    public void Last_unmarked_copy_cannot_be_marked()
    {
        var group = CreateGroup(3);
        group.Files[0].IsMarked = true;
        group.Files[1].IsMarked = true;

        var last = group.Files[2];
        Assert.False(last.CanMark);
        Assert.True(last.IsLastKeptCopy);

        last.IsMarked = true;

        Assert.False(last.IsMarked);
        Assert.Equal(2, _markedCount);
    }

    [Fact]
    public void Unmarking_a_copy_makes_the_others_markable_again()
    {
        var group = CreateGroup(2);
        group.Files[0].IsMarked = true;
        Assert.False(group.Files[1].CanMark);

        group.Files[0].IsMarked = false;

        Assert.True(group.Files[0].CanMark);
        Assert.True(group.Files[1].CanMark);
        Assert.False(group.Files[1].IsLastKeptCopy);
        Assert.Equal(0, _markedCount);
    }

    [Fact]
    public void Marked_copies_can_always_be_unmarked()
    {
        var group = CreateGroup(2);
        group.Files[0].IsMarked = true;

        Assert.True(group.Files[0].CanMark);
    }

    [Fact]
    public void Trashed_copies_do_not_count_as_kept()
    {
        var group = CreateGroup(3);
        group.Files[0].IsMarked = true;
        group.Files[0].IsMarked = false;
        group.Files[0].IsTrashed = true;
        group.Files[1].IsMarked = true;

        Assert.False(group.Files[0].CanMark);
        Assert.False(group.Files[2].CanMark);
        Assert.True(group.Files[2].IsLastKeptCopy);

        group.Files[2].IsMarked = true;
        Assert.False(group.Files[2].IsMarked);
    }
}
