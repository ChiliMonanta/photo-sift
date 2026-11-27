using PhotoSift.Core.Caching;
using PhotoSift.Core.Deletion;
using PhotoSift.Core.Hashing;
using PhotoSift.Core.Scanning;
using PhotoSift.Desktop.Services;
using PhotoSift.Desktop.ViewModels;

namespace PhotoSift.Tests;

public sealed class MasterFoldersViewModelTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeFolderPicker _picker = new();
    private readonly MainWindowViewModel _vm;

    private readonly string _inPictures;
    private readonly string _inArchive;
    private readonly string _inDownloads;

    public MasterFoldersViewModelTests()
    {
        _inPictures = _temp.WriteImage("Pictures/a.jpg", seed: 1);
        _inArchive = _temp.WriteImage("Archive/a.jpg", seed: 1);
        _inDownloads = _temp.WriteImage("Downloads/a.jpg", seed: 1);

        var hasher = new Md5FileHasher();
        _vm = new MainWindowViewModel(
            new DuplicateScanner(hasher, NullHashCache.Instance),
            new SafeDeleter(hasher, new FakeTrashService()),
            _picker,
            new ThumbnailLoader());
        _vm.Folders.Add(_temp.Root);
    }

    public void Dispose() => _temp.Dispose();

    private async Task ScanAsync()
    {
        await _vm.ScanCommand.ExecuteAsync(null);
        Assert.True(_vm.CurrentScan?.IsComplete);
    }

    private Task AddMastersAsync(params string[] relativeFolders)
    {
        _picker.Next = relativeFolders.Select(_temp.PathOf).ToList();
        return _vm.AddMasterFoldersCommand.ExecuteAsync(null);
    }

    private FileEntryViewModel Entry(string path) =>
        _vm.Groups.SelectMany(g => g.Files).Single(f => f.Path == path);

    [Fact]
    public async Task Copies_in_every_master_folder_are_kept()
    {
        await ScanAsync();
        await AddMastersAsync("Pictures", "Archive");

        Assert.Equal(2, _vm.MasterFolders.Count);
        Assert.False(Entry(_inPictures).IsMarked);
        Assert.False(Entry(_inArchive).IsMarked);
        Assert.True(Entry(_inDownloads).IsMarked);
        Assert.True(Entry(_inPictures).IsInMasterFolder);
        Assert.True(Entry(_inArchive).IsInMasterFolder);
        Assert.False(Entry(_inDownloads).IsInMasterFolder);
    }

    [Fact]
    public async Task Adding_a_master_folder_unmarks_copies_the_rule_marked_before()
    {
        await ScanAsync();
        await AddMastersAsync("Pictures");
        Assert.True(Entry(_inArchive).IsMarked);

        await AddMastersAsync("Archive");

        Assert.False(Entry(_inArchive).IsMarked);
        Assert.True(Entry(_inDownloads).IsMarked);
        Assert.Equal(1, _vm.MarkedCount);
    }

    [Fact]
    public async Task Removing_a_master_folder_reapplies_the_rule()
    {
        await ScanAsync();
        await AddMastersAsync("Pictures", "Archive");

        _vm.SelectedMasterFolder = _vm.MasterFolders.Single(f => f == _temp.PathOf("Archive"));
        _vm.RemoveMasterFolderCommand.Execute(null);

        Assert.Equal([_temp.PathOf("Pictures")], _vm.MasterFolders);
        Assert.False(Entry(_inPictures).IsMarked);
        Assert.True(Entry(_inArchive).IsMarked);
        Assert.True(Entry(_inDownloads).IsMarked);
    }

    [Fact]
    public async Task Clearing_master_folders_removes_rule_marks_but_keeps_manual_marks()
    {
        _temp.WriteImage("Phone/b.jpg", seed: 2);
        var manual = _temp.WriteImage("Downloads/b.jpg", seed: 2);
        await ScanAsync();

        Entry(manual).IsMarked = true;
        await AddMastersAsync("Pictures");
        Assert.Equal(3, _vm.MarkedCount);

        _vm.ClearMasterFoldersCommand.Execute(null);

        Assert.Empty(_vm.MasterFolders);
        Assert.False(Entry(_inArchive).IsMarked);
        Assert.False(Entry(_inDownloads).IsMarked);
        Assert.True(Entry(manual).IsMarked);
        Assert.Equal(1, _vm.MarkedCount);
    }

    [Fact]
    public async Task Master_folders_added_before_scan_are_applied_after_scan()
    {
        await AddMastersAsync("Pictures", "Archive");
        await AddMastersAsync("Pictures");
        Assert.Equal(2, _vm.MasterFolders.Count);

        await ScanAsync();

        Assert.True(Entry(_inDownloads).IsMarked);
        Assert.False(Entry(_inPictures).IsMarked);
        Assert.False(Entry(_inArchive).IsMarked);
    }

    [Fact]
    public async Task Sample_photos_match_the_documented_master_folder_results()
    {
        // Uses a temporary copy of the generated images in samples/test-photos (see its README).
        CopyDirectory(FindSamplesFolder(), _temp.PathOf("samples"));
        _vm.Folders.Clear();
        _vm.Folders.Add(_temp.PathOf("samples"));
        await ScanAsync();

        await AddMastersAsync("samples/Pictures");
        Assert.Equal(
            ["Downloads/IMG_4021.jpg", "Phone backup/DCIM/PXL_20240712.jpg", "Phone backup/DCIM/mountain.png", "Phone backup/DCIM/sunset (1).jpg"],
            MarkedRelativePaths());

        await AddMastersAsync("samples/Downloads");
        Assert.Equal(
            ["Phone backup/DCIM/IMG_0001.heic", "Phone backup/DCIM/PXL_20240712.jpg", "Phone backup/DCIM/forest-copy.jpg",
             "Phone backup/DCIM/mountain.png", "Phone backup/DCIM/sunset (1).jpg"],
            MarkedRelativePaths());

        _vm.SelectedMasterFolder = _temp.PathOf("samples/Downloads");
        _vm.RemoveMasterFolderCommand.Execute(null);
        Assert.Equal(4, _vm.MarkedCount);
    }

    private List<string> MarkedRelativePaths() =>
        _vm.Groups.SelectMany(g => g.Files)
            .Where(f => f.IsMarked)
            .Select(f => Path.GetRelativePath(_temp.PathOf("samples"), f.Path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static string FindSamplesFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "test-photos");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("samples/test-photos was not found above the test output folder.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private sealed class FakeFolderPicker : IFolderPicker
    {
        public IReadOnlyList<string> Next { get; set; } = [];

        public Task<FolderPickResult> PickFoldersAsync(string title, bool allowMultiple) =>
            Task.FromResult(new FolderPickResult(Next, NonLocalCount: 0));
    }
}
