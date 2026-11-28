using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PhotoSift.Core.Caching;
using PhotoSift.Core.Deletion;
using PhotoSift.Core.GoogleDrive;
using PhotoSift.Core.GoogleDrive.Auth;
using PhotoSift.Core.Hashing;
using PhotoSift.Core.Import;
using PhotoSift.Core.Scanning;
using PhotoSift.Core.Sources;
using PhotoSift.Core.Upload;
using PhotoSift.Desktop.Platform;
using PhotoSift.Desktop.Services;
using PhotoSift.Desktop.ViewModels;
using PhotoSift.Desktop.Views;

namespace PhotoSift.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var (cache, cacheWarning) = OpenHashCache();
            var window = new MainWindow { Title = AppInfo.WindowTitle };

            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var credentials = GoogleClientConfig.Resolve();
            var drive = new DriveConnection(
                credentials is null ? null : new GoogleOAuthClient(http, credentials),
                TokenStoreFactory.Create(),
                tokens => new DriveRestClient(http, tokens));
            var driveViewModel = new GoogleDriveViewModel(drive, (uri, _) => OpenBrowserAsync(window, uri));
            driveViewModel.Initialize();

            var sources = new FileSources(
                new LocalFileSource(new Md5FileHasher(), cache, TrashServiceFactory.Create()),
                new GoogleDriveFileSource(drive));

            var folderPicker = new FolderPicker(window);
            var (import, importWarning) = OpenImport(cache, folderPicker);
            var (upload, uploadWarning) = OpenUpload(cache, driveViewModel, folderPicker);

            window.DataContext = new MainWindowViewModel(
                new DuplicateScanner(sources),
                new SafeDeleter(sources),
                folderPicker,
                new ThumbnailLoader(sources),
                cacheWarning ?? importWarning ?? uploadWarning,
                driveViewModel,
                import,
                upload);

            desktop.MainWindow = window;
            desktop.Exit += (_, _) =>
            {
                import?.Dispose();
                import?.Journal.Dispose();
                upload?.Journal.Dispose();
                (cache as IDisposable)?.Dispose();
                http.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Opens Google's sign-in page in the user's default browser (never an embedded web view).</summary>
    private static async Task OpenBrowserAsync(TopLevel window, Uri uri)
    {
        if (!await window.Launcher.LaunchUriAsync(uri))
        {
            throw new InvalidOperationException("The web browser could not be opened.");
        }
    }

    private static (ImportViewModel? Import, string? Warning) OpenImport(IHashCache cache, IFolderPicker folderPicker)
    {
        try
        {
            var journal = new ImportJournal(ImportJournal.DefaultPath);
            return (new ImportViewModel(new PhotoImporter(journal, cache), journal, PhoneProviderFactory.Create(), folderPicker), null);
        }
        catch (Exception ex)
        {
            return (null, $"Phone import is unavailable: the import journal could not be opened. {ex.Message}");
        }
    }

    private static (UploadViewModel? Upload, string? Warning) OpenUpload(
        IHashCache cache, GoogleDriveViewModel drive, IFolderPicker folderPicker)
    {
        try
        {
            var journal = new UploadJournal(UploadJournal.DefaultPath);
            return (new UploadViewModel(new DriveUploader(journal, hashCache: cache), journal, drive, folderPicker), null);
        }
        catch (Exception ex)
        {
            return (null, $"Uploading is unavailable: the upload journal could not be opened. {ex.Message}");
        }
    }

    private static (IHashCache Cache, string? Warning) OpenHashCache()
    {
        try
        {
            return (new SqliteHashCache(SqliteHashCache.DefaultPath), null);
        }
        catch (Exception ex)
        {
            return (NullHashCache.Instance, $"Hash cache could not be opened; scans will not reuse previous hashes. {ex.Message}");
        }
    }
}
