using Avalonia.Controls;
using Avalonia.Input;
using PhotoSift.Desktop.ViewModels;

namespace PhotoSift.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void OnDriveFolderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DriveBrowserViewModel browser && browser.OpenCommand.CanExecute(null))
        {
            browser.OpenCommand.Execute(null);
        }
    }
}
