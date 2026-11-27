using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace PhotoSift.Desktop.Services;

public sealed record FolderPickResult(IReadOnlyList<string> LocalPaths, int NonLocalCount);

public interface IFolderPicker
{
    Task<FolderPickResult> PickFoldersAsync(string title, bool allowMultiple);
}

public sealed class FolderPicker(TopLevel owner) : IFolderPicker
{
    private string? _lastPickedFolder;

    public async Task<FolderPickResult> PickFoldersAsync(string title, bool allowMultiple)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            SuggestedStartLocation = await GetStartLocationAsync(),
        });

        var localPaths = folders.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (localPaths.Count > 0)
        {
            _lastPickedFolder = localPaths[0];
        }

        return new FolderPickResult(localPaths, folders.Count - localPaths.Count);
    }

    /// <summary>
    /// Opens the dialog one level above the last picked folder, so sibling folders
    /// can be picked without navigating back up each time.
    /// </summary>
    private async Task<IStorageFolder?> GetStartLocationAsync()
    {
        if (_lastPickedFolder is null)
        {
            return null;
        }

        var startPath = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_lastPickedFolder)) ?? _lastPickedFolder;
        try
        {
            return await owner.StorageProvider.TryGetFolderFromPathAsync(startPath);
        }
        catch (Exception)
        {
            // The folder may have been removed or be inaccessible; fall back to the OS default location.
            return null;
        }
    }
}
