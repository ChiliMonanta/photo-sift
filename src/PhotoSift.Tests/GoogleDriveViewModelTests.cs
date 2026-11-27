using PhotoSift.Core.Deletion;
using PhotoSift.Core.GoogleDrive;
using PhotoSift.Core.Hashing;
using PhotoSift.Core.Scanning;
using PhotoSift.Core.Sources;
using PhotoSift.Desktop.Services;
using PhotoSift.Desktop.ViewModels;

namespace PhotoSift.Tests;

public sealed class GoogleDriveViewModelTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeDriveApi _drive = new();
    private readonly MainWindowViewModel _vm;
    private readonly string _phone;

    public GoogleDriveViewModelTests()
    {
        var sources = new FileSources(
            new LocalFileSource(new Md5FileHasher(), trash: new FakeTrashService()),
            new GoogleDriveFileSource(new FakeDriveSession(_drive)));
        _vm = new MainWindowViewModel(new DuplicateScanner(sources), new SafeDeleter(sources), new NoFolderPicker(), new ThumbnailLoader());
        _phone = _drive.AddFolder("Phone");
    }

    public void Dispose() => _temp.Dispose();

    private string DriveFolder(string id) => DriveLocations.Folder(_drive.Account.PermissionId, id);

    /// <summary>Pictures/a.jpg locally, and the same image in My Drive/Phone.</summary>
    private async Task ScanLocalAndDriveCopyAsync()
    {
        var local = _temp.WriteImage("Pictures/a.jpg", seed: 1);
        _drive.AddFile("a.jpg", _phone, await new Md5FileHasher().ComputeHashAsync(local), new FileInfo(local).Length);
        _vm.Folders.Add(_temp.Root);
        _vm.AddDriveFolder(DriveFolder(FakeDriveApi.RootId), "Google Drive: My Drive", asMaster: false);

        await _vm.ScanCommand.ExecuteAsync(null);
        Assert.True(_vm.CurrentScan?.IsComplete);
    }

    private FileEntryViewModel DriveEntry => _vm.Groups.Single().Files.Single(f => f.IsGoogleDrive);

    private FileEntryViewModel LocalEntry => _vm.Groups.Single().Files.Single(f => !f.IsGoogleDrive);

    [Fact]
    public async Task Drive_copies_are_read_only_and_cannot_be_marked()
    {
        await ScanLocalAndDriveCopyAsync();

        Assert.True(DriveEntry.IsReadOnly);
        Assert.False(DriveEntry.CanMark);
        Assert.False(DriveEntry.IsLastKeptCopy);
        Assert.Equal("My Drive/Phone/a.jpg", DriveEntry.DisplayPath);

        DriveEntry.IsMarked = true;

        Assert.False(DriveEntry.IsMarked);
        Assert.True(LocalEntry.CanMark);
    }

    [Fact]
    public async Task A_local_master_folder_keeps_Drive_copies_and_says_so()
    {
        await ScanLocalAndDriveCopyAsync();

        _vm.MasterFolders.Add(_temp.PathOf("Pictures"));
        _vm.ApplyMasterFoldersCommand.Execute(null);

        Assert.False(DriveEntry.IsMarked);
        Assert.Equal(0, _vm.MarkedCount);
        Assert.Contains("1 read-only Google Drive copies outside them are kept", _vm.StatusText);
    }

    [Fact]
    public async Task A_Drive_master_folder_marks_the_local_copy()
    {
        await ScanLocalAndDriveCopyAsync();

        _vm.AddDriveFolder(DriveFolder(_phone), "Google Drive: My Drive/Phone", asMaster: true);

        Assert.True(LocalEntry.IsMarked);
        Assert.True(DriveEntry.IsInMasterFolder);
        Assert.Equal("Google Drive: My Drive/Phone", LocationLabels.Get(DriveFolder(_phone)));
    }

    [Fact]
    public async Task The_Drive_browser_opens_folders_and_adds_the_current_one()
    {
        var added = new List<(string Location, string Label, bool AsMaster)>();
        var closed = false;
        _drive.AddFolder("2024", _phone);
        _drive.AddFile("loose.jpg", FakeDriveApi.RootId, new string('a', 32));
        var browser = new DriveBrowserViewModel(_drive, _drive.Account, (l, n, m) => added.Add((l, n, m)), () => closed = true);

        await browser.LoadRootAsync();
        Assert.Equal("My Drive", browser.CurrentPath);
        Assert.Equal(["Phone"], browser.Children.Select(c => c.Name)); // folders only
        Assert.False(browser.UpCommand.CanExecute(null));

        browser.SelectedChild = browser.Children[0];
        await browser.OpenCommand.ExecuteAsync(null);
        Assert.Equal("My Drive/Phone", browser.CurrentPath);
        Assert.Equal(["2024"], browser.Children.Select(c => c.Name));

        browser.AddAsMasterCommand.Execute(null);
        await browser.UpCommand.ExecuteAsync(null);
        browser.AddToScanCommand.Execute(null);
        browser.CloseCommand.Execute(null);

        Assert.Equal(
            [(DriveFolder(_phone), "Google Drive: My Drive/Phone", true), (DriveFolder(FakeDriveApi.RootId), "Google Drive: My Drive", false)],
            added);
        Assert.True(closed);
    }

    [Fact]
    public async Task Scanning_a_Drive_for_desktop_folder_together_with_Drive_warns()
    {
        var mirror = _temp.PathOf("My Drive");
        Directory.CreateDirectory(mirror);
        _vm.Folders.Add(mirror);
        _vm.AddDriveFolder(DriveFolder(FakeDriveApi.RootId), "Google Drive: My Drive", asMaster: false);

        await _vm.ScanCommand.ExecuteAsync(null);

        Assert.Contains(_vm.Issues, i => i.StartsWith("Warning:", StringComparison.Ordinal) && i.Contains("Google Drive for desktop"));
    }

    private sealed class NoFolderPicker : IFolderPicker
    {
        public Task<FolderPickResult> PickFoldersAsync(string title, bool allowMultiple) =>
            Task.FromResult(new FolderPickResult([], 0));
    }
}
