using PhotoSift.Core;
using PhotoSift.Core.Deletion;
using PhotoSift.Core.GoogleDrive;
using PhotoSift.Core.Hashing;
using PhotoSift.Core.Scanning;
using PhotoSift.Core.Selection;
using PhotoSift.Core.Sources;

namespace PhotoSift.Tests;

public sealed class GoogleDriveSourceTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeTrashService _trash = new();
    private readonly FakeDriveApi _drive = new();
    private readonly FakeDriveSession _session;
    private readonly FileSources _sources;

    private readonly string _photos;
    private readonly string _phone;

    public GoogleDriveSourceTests()
    {
        _session = new FakeDriveSession(_drive);
        _sources = new FileSources(
            new LocalFileSource(new Md5FileHasher(), trash: _trash),
            new GoogleDriveFileSource(_session));
        _photos = _drive.AddFolder("Photos");
        _phone = _drive.AddFolder("Phone", _photos);
    }

    public void Dispose() => _temp.Dispose();

    private string Account => _drive.Account.PermissionId;

    private string FolderLocation(string id) => DriveLocations.Folder(Account, id);

    private string FileLocation(string id) => DriveLocations.File(Account, id);

    /// <summary>A local image and a Drive file in Photos/Phone with the same MD5 (Drive reports it in upper case here).</summary>
    private async Task<(string Local, string DriveFile)> LocalAndDriveCopyAsync()
    {
        var local = _temp.WriteImage("Pictures/beach.jpg", seed: 1);
        var md5 = await new Md5FileHasher().ComputeHashAsync(local);
        var driveId = _drive.AddFile("IMG_4021.jpg", _phone, md5.ToUpperInvariant(), new FileInfo(local).Length);
        return (local, FileLocation(driveId));
    }

    private Task<ScanResult> ScanAsync(params string[] folders) => new DuplicateScanner(_sources).ScanAsync(folders);

    [Fact]
    public async Task Drive_folders_are_scanned_recursively_with_paths_and_metadata_hashes()
    {
        var md5 = new string('a', 32);
        var a = _drive.AddFile("a.jpg", _photos, md5);
        var b = _drive.AddFile("b.JPG", _phone, md5);

        var scan = await ScanAsync(FolderLocation(_photos));

        Assert.True(scan.IsComplete);
        var group = Assert.Single(scan.Groups);
        Assert.Equal([FileLocation(a), FileLocation(b)], group.Files.Select(f => f.Path));
        var phoneCopy = group.Files[1];
        Assert.Equal(GoogleDriveFileSource.SourceId, phoneCopy.SourceId);
        Assert.Equal("My Drive/Photos/Phone/b.JPG", phoneCopy.DisplayPath);
        Assert.Equal("My Drive/Photos/Phone", phoneCopy.DirectoryPath);
        Assert.Equal("b.JPG", phoneCopy.FileName);
        Assert.Equal(md5, phoneCopy.Hash);
    }

    [Fact]
    public async Task Local_and_Drive_copies_with_the_same_contents_form_one_group()
    {
        var (local, driveFile) = await LocalAndDriveCopyAsync();

        var scan = await ScanAsync(_temp.Root, FolderLocation(FakeDriveApi.RootId));

        var group = Assert.Single(scan.Groups);
        Assert.Equal([local, driveFile], group.Files.Select(f => f.Path));
        Assert.Equal(group.Files[0].Hash, group.Files[1].Hash);
    }

    [Fact]
    public async Task Local_copy_is_trashed_when_the_kept_Drive_copy_still_matches()
    {
        var (local, _) = await LocalAndDriveCopyAsync();
        var scan = await ScanAsync(_temp.Root, FolderLocation(FakeDriveApi.RootId));

        var report = await new SafeDeleter(_sources).MoveToTrashAsync(scan, [local]);

        Assert.Equal(DeletionStatus.MovedToTrash, Assert.Single(report.Outcomes).Status);
        Assert.Equal([local], _trash.Trashed);
    }

    [Theory]
    [InlineData("trashed")]
    [InlineData("deleted")]
    [InlineData("changed")]
    public async Task Local_copy_is_kept_when_the_Drive_copy_no_longer_matches(string change)
    {
        var (local, driveFile) = await LocalAndDriveCopyAsync();
        var scan = await ScanAsync(_temp.Root, FolderLocation(FakeDriveApi.RootId));
        DriveLocations.TryParse(driveFile, out _, out _, out var id);
        switch (change)
        {
            case "trashed": _drive.Update(id, i => i with { Trashed = true }); break;
            case "deleted": _drive.Remove(id); break;
            default: _drive.Update(id, i => i with { Md5Checksum = new string('0', 32) }); break;
        }

        var report = await new SafeDeleter(_sources).MoveToTrashAsync(scan, [local]);

        var outcome = Assert.Single(report.Outcomes);
        Assert.Equal(DeletionStatus.Skipped, outcome.Status);
        Assert.Empty(_trash.Trashed);
        Assert.True(File.Exists(local));
    }

    [Fact]
    public async Task Drive_files_are_never_trashed_in_the_read_only_version()
    {
        var (_, driveFile) = await LocalAndDriveCopyAsync();
        var scan = await ScanAsync(_temp.Root, FolderLocation(FakeDriveApi.RootId));

        var report = await new SafeDeleter(_sources).MoveToTrashAsync(scan, [driveFile]);

        var outcome = Assert.Single(report.Outcomes);
        Assert.Equal(DeletionStatus.Skipped, outcome.Status);
        Assert.Equal(GoogleDriveFileSource.ReadOnlyReason, outcome.Reason);
        Assert.Equal("My Drive/Photos/Phone/IMG_4021.jpg", outcome.DisplayPath);
    }

    [Fact]
    public async Task Shortcuts_and_files_without_a_checksum_are_notes_and_other_files_are_ignored()
    {
        _drive.AddFile("link.jpg", _photos, md5: null, mimeType: DriveItem.ShortcutMimeType);
        _drive.AddFile("no-checksum.jpg", _photos, md5: null);
        _drive.AddFile("notes.txt", _photos, new string('b', 32), mimeType: "text/plain");
        _drive.AddFile("Budget", _photos, md5: null, mimeType: "application/vnd.google-apps.spreadsheet");

        var scan = await ScanAsync(FolderLocation(_photos));

        Assert.True(scan.IsComplete);
        Assert.Equal(0, scan.FilesScanned);
        Assert.Equal(
            ["My Drive/Photos/link.jpg", "My Drive/Photos/no-checksum.jpg"],
            scan.Notices.Select(n => n.Path));
    }

    [Fact]
    public async Task An_unreadable_Drive_folder_makes_the_scan_incomplete()
    {
        _drive.FailListingFor.Add(_phone);

        var scan = await ScanAsync(FolderLocation(_photos));

        var error = Assert.Single(scan.Errors);
        Assert.Equal("My Drive/Photos/Phone", error.Path);
        Assert.Contains("simulated", error.Reason);
        Assert.False(scan.IsComplete);
    }

    [Fact]
    public async Task Drive_folders_are_errors_when_not_connected_or_from_another_account()
    {
        var otherAccount = DriveLocations.Folder("99999", _photos);
        var scan = await ScanAsync(otherAccount);
        Assert.Contains("different Google account", Assert.Single(scan.Errors).Reason);

        _session.IsConnected = false;
        scan = await ScanAsync(FolderLocation(_photos));
        Assert.Equal("Google Drive is not connected.", Assert.Single(scan.Errors).Reason);
        Assert.False(scan.IsComplete);
    }

    [Fact]
    public async Task Folders_outside_My_Drive_are_not_scanned()
    {
        var shared = _drive.AddForeignFolder("Shared with me");
        _drive.AddFile("x.jpg", shared, new string('c', 32));

        var scan = await ScanAsync(FolderLocation(shared));

        Assert.Contains("not in My Drive", Assert.Single(scan.Errors).Reason);
        Assert.Equal(0, scan.FilesScanned);
    }

    [Fact]
    public async Task Drive_master_folders_can_be_above_or_inside_the_scanned_folder()
    {
        var (local, driveFile) = await LocalAndDriveCopyAsync();
        var scan = await ScanAsync(_temp.Root, FolderLocation(_photos));

        // Above the scanned folder (My Drive), the folder itself, and a subfolder (Phone).
        foreach (var master in new[] { FakeDriveApi.RootId, _photos, _phone })
        {
            var selected = MasterFolderSelector.SelectCopiesOutsideMasters(scan.Groups, [FolderLocation(master)], _sources);
            Assert.Equal([local], selected.Select(f => f.Path));
        }

        var other = _drive.AddFolder("Other");
        Assert.Empty(MasterFolderSelector.SelectCopiesOutsideMasters(scan.Groups, [FolderLocation(other)], _sources));

        var localMaster = MasterFolderSelector.SelectCopiesOutsideMasters(scan.Groups, [_temp.Root], _sources);
        Assert.Equal([driveFile], localMaster.Select(f => f.Path));
    }

    [Theory]
    [InlineData("gdrive://123/file/abc", true, "123", false, "abc")]
    [InlineData("gdrive://123/folder/root-id", true, "123", true, "root-id")]
    [InlineData("gdrive://123/abc", false, "", false, "")]
    [InlineData("gdrive://123/other/abc", false, "", false, "")]
    [InlineData(@"C:\Photos", false, "", false, "")]
    public void Drive_locations_are_parsed(string location, bool valid, string account, bool isFolder, string id)
    {
        Assert.Equal(valid, DriveLocations.TryParse(location, out var parsedAccount, out var parsedIsFolder, out var parsedId));
        Assert.Equal((account, isFolder, id), (parsedAccount, parsedIsFolder, parsedId));
        Assert.False(PathUtil.IsLocal("gdrive://123/file/abc"));
    }

    [Theory]
    [InlineData(@"G:\My Drive\Photos", null, true)]
    [InlineData(@"G:\Min enhet\Photos", "Google Drive", true)]
    [InlineData(@"D:\Photos", "Data", false)]
    [InlineData("/Users/me/Library/CloudStorage/GoogleDrive-me@example.com/My Drive", null, true)]
    [InlineData("/home/me/Pictures", null, false)]
    [InlineData("gdrive://123/folder/abc", null, false)]
    public void Google_Drive_for_desktop_folders_are_recognized(string path, string? volumeLabel, bool expected) =>
        Assert.Equal(expected, DriveForDesktop.IsInside(path, _ => volumeLabel));
}
