using System.Net;
using PhotoSift.Core.GoogleDrive.Auth;
using PhotoSift.Core.Upload;
using PhotoSift.Desktop.Services;
using PhotoSift.Desktop.ViewModels;

namespace PhotoSift.Tests;

public sealed class UploadViewModelTests : IDisposable
{
    private const string TokenResponse =
        """{"access_token":"access-1","expires_in":3600,"refresh_token":"refresh-2","scope":"https://www.googleapis.com/auth/drive"}""";

    private readonly TempFolder _temp = new();
    private readonly FakeDriveApi _api = new();
    private readonly FakeTokenStore _store = new();
    private readonly UploadJournal _journal;
    private readonly FakePicker _picker = new();
    private readonly string _photos;

    public UploadViewModelTests()
    {
        _journal = new UploadJournal(_temp.PathOf("upload.db"));
        _photos = _api.AddFolder("Photos");
        _temp.WriteImage("Pixel 8/DCIM/IMG_1.jpg", seed: 1);
        _temp.WriteImage("Pixel 8/DCIM/IMG_2.jpg", seed: 2);
    }

    public void Dispose()
    {
        _journal.Dispose();
        _temp.Dispose();
    }

    private (GoogleDriveViewModel Drive, UploadViewModel Upload) Create(string scope)
    {
        _store.Saved = new StoredDriveAccount("refresh-1", _api.Account.PermissionId, _api.Account.Email, scope);
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, TokenResponse));
        var connection = new DriveConnection(
            new GoogleOAuthClient(new HttpClient(handler), new OAuthClientCredentials("id", "secret")), _store, _ => _api);
        var drive = new GoogleDriveViewModel(connection, new FakeBrowser().OpenAsync);
        drive.Initialize();
        return (drive, new UploadViewModel(new DriveUploader(_journal), _journal, drive, _picker));
    }

    private static async Task ChooseAsync(GoogleDriveViewModel drive, UploadViewModel upload, string folderName)
    {
        var choosing = upload.ChooseDriveFolderCommand.ExecuteAsync(null);
        var browser = drive.Browser!;
        Assert.True(browser.IsChoosing);
        browser.SelectedChild = browser.Children.Single(c => c.Name == folderName);
        await browser.OpenCommand.ExecuteAsync(null);
        browser.ChooseFolderCommand.Execute(null);
        await choosing;
        Assert.Null(drive.Browser);
    }

    [Fact]
    public async Task Files_are_uploaded_to_the_chosen_folder_and_a_second_run_finds_nothing_new()
    {
        var (drive, upload) = Create(GoogleOAuthClient.Scope);
        _picker.Next = [_temp.PathOf("Pixel 8")];
        await upload.ChangeLocalFolderCommand.ExecuteAsync(null);
        await ChooseAsync(drive, upload, "Photos");
        Assert.Equal("My Drive/Photos", upload.DriveFolderText);
        Assert.False(upload.HasConnectionText);

        await upload.CheckCommand.ExecuteAsync(null);
        Assert.StartsWith("2 new files (", upload.SummaryText);
        Assert.StartsWith("Click Start upload", upload.StatusText);

        await upload.StartCommand.ExecuteAsync(null);
        Assert.Equal("Upload finished: 2 uploaded.", upload.StatusText);
        Assert.Equal("My Drive/Photos/Pixel 8/DCIM/IMG_1.jpg", _api.PathOf(_api.Find("IMG_1.jpg")!.Id));
        Assert.True(drive.DisconnectCommand.CanExecute(null));

        await upload.StartCommand.ExecuteAsync(null);
        Assert.Equal("Everything in this folder is already in Google Drive.", upload.StatusText);
        Assert.EndsWith("· 2 uploaded before", upload.SummaryText);
    }

    [Fact]
    public async Task The_folders_are_remembered()
    {
        var (drive, upload) = Create(GoogleOAuthClient.Scope);
        _picker.Next = [_temp.PathOf("Pixel 8")];
        await upload.ChangeLocalFolderCommand.ExecuteAsync(null);
        await ChooseAsync(drive, upload, "Photos");

        var again = new UploadViewModel(new DriveUploader(_journal), _journal, drive, _picker);

        Assert.Equal(_temp.PathOf("Pixel 8"), again.LocalFolder);
        Assert.Equal(new DriveFolderChoice(_api.Account.PermissionId, _photos, "My Drive/Photos"), again.DriveFolder);
        Assert.True(again.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_read_only_sign_in_can_scan_but_must_allow_uploads_first()
    {
        var (drive, upload) = Create(GoogleOAuthClient.ReadOnlyScope);
        _picker.Next = [_temp.PathOf("Pixel 8")];
        await upload.ChangeLocalFolderCommand.ExecuteAsync(null);
        await ChooseAsync(drive, upload, "Photos");

        Assert.True(drive.IsConnected);
        Assert.True(drive.NeedsUploadPermission);
        Assert.Contains("Allow uploads", upload.ConnectionText);
        Assert.False(upload.StartCommand.CanExecute(null));
        Assert.True(drive.ConnectCommand.CanExecute(null));

        await drive.ConnectCommand.ExecuteAsync(null);

        Assert.True(drive.CanUpload);
        Assert.False(drive.NeedsUploadPermission);
        Assert.False(upload.HasConnectionText);
        Assert.True(upload.StartCommand.CanExecute(null));
        Assert.Equal("refresh-2", _store.Saved?.RefreshToken);
    }

    [Fact]
    public void Without_a_connection_the_tab_says_to_connect()
    {
        var (drive, upload) = Create(GoogleOAuthClient.Scope);
        _store.Saved = null;
        drive.Initialize();

        Assert.False(drive.IsConnected);
        Assert.Equal("Connect Google Drive above to upload.", upload.ConnectionText);
        Assert.False(upload.ChooseDriveFolderCommand.CanExecute(null));
    }

    private sealed class FakePicker : IFolderPicker
    {
        public IReadOnlyList<string> Next { get; set; } = [];

        public Task<FolderPickResult> PickFoldersAsync(string title, bool allowMultiple) =>
            Task.FromResult(new FolderPickResult(Next, 0));
    }
}
