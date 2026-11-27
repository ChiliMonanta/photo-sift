using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PhotoSift.Core.Caching;
using PhotoSift.Core.Deletion;
using PhotoSift.Core.Hashing;
using PhotoSift.Core.Scanning;
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
            var hasher = new Md5FileHasher();
            var window = new MainWindow { Title = AppInfo.WindowTitle };

            window.DataContext = new MainWindowViewModel(
                new DuplicateScanner(hasher, cache),
                new SafeDeleter(hasher, TrashServiceFactory.Create()),
                new FolderPicker(window),
                new ThumbnailLoader(),
                cacheWarning);

            desktop.MainWindow = window;
            desktop.Exit += (_, _) => (cache as IDisposable)?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
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
