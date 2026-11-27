using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PhotoSift.Core.Caching;
using PhotoSift.Core.Deletion;
using PhotoSift.Core.GoogleDrive;
using PhotoSift.Core.GoogleDrive.Auth;
using PhotoSift.Core.Hashing;
using PhotoSift.Core.Scanning;
using PhotoSift.Core.Sources;
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

            window.DataContext = new MainWindowViewModel(
                new DuplicateScanner(sources),
                new SafeDeleter(sources),
                new FolderPicker(window),
                new ThumbnailLoader(),
                cacheWarning,
                driveViewModel);

            desktop.MainWindow = window;
            desktop.Exit += (_, _) =>
            {
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
