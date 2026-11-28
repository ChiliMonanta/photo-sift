using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoSift.Core.GoogleDrive;
using PhotoSift.Core.GoogleDrive.Auth;

namespace PhotoSift.Desktop.ViewModels;

/// <summary>Adds a Drive folder to the folders to scan, or to the master folders.</summary>
public delegate void AddDriveFolder(string location, string label, bool asMaster);

/// <summary>Connecting and disconnecting Google Drive, and opening the Drive folder browser.</summary>
public sealed partial class GoogleDriveViewModel : ObservableObject
{
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly DriveConnection _connection;
    private readonly Func<Uri, CancellationToken, Task> _openBrowser;
    private bool _isAppBusy;

    public GoogleDriveViewModel(DriveConnection connection, Func<Uri, CancellationToken, Task> openBrowser)
    {
        _connection = connection;
        _openBrowser = openBrowser;
        UpdateState();
    }

    /// <summary>Set by the main window: where folders chosen in the browser go.</summary>
    public AddDriveFolder? AddFolder { get; set; }

    public bool IsAvailable => _connection.UnavailableReason is null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(BrowseCommand))]
    public partial bool IsConnected { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(BrowseCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowsing))]
    public partial DriveBrowserViewModel? Browser { get; private set; }

    public bool IsBrowsing => Browser is not null;

    /// <summary>Loads the saved sign-in at startup.</summary>
    public void Initialize()
    {
        try
        {
            _connection.Restore();
            UpdateState();
        }
        catch (Exception ex)
        {
            UpdateState();
            StatusText = $"{ex.Message} Connect Google Drive again.";
        }
    }

    /// <summary>Connecting and disconnecting is not allowed while a scan or deletion runs.</summary>
    public void SetAppBusy(bool busy)
    {
        _isAppBusy = busy;
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        BrowseCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        StatusText = "Waiting for you to sign in to Google in your browser…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInTimeout);
        try
        {
            var account = await _connection.ConnectAsync(_openBrowser, timeout.Token);
            UpdateState();
            StatusText = $"Connected to Google Drive as {account.Email} (read-only).";
        }
        catch (OperationCanceledException)
        {
            UpdateState();
            StatusText = cancellationToken.IsCancellationRequested
                ? "Google sign-in was canceled."
                : "Google sign-in timed out. Try again.";
        }
        catch (Exception ex)
        {
            UpdateState();
            StatusText = $"Google Drive could not be connected: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanConnect() => IsAvailable && !IsConnected && !IsBusy && !_isAppBusy;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        IsBusy = true;
        Browser = null;
        try
        {
            await _connection.DisconnectAsync(CancellationToken.None);
            UpdateState();
            StatusText = "Google Drive disconnected. PhotoSift's access was removed from your Google account.";
        }
        catch (Exception ex)
        {
            UpdateState();
            StatusText = $"Disconnected on this computer, but Google could not be reached to remove PhotoSift's access ({ex.Message}). " +
                         "You can remove it at https://myaccount.google.com/permissions.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDisconnect() => IsConnected && !IsBusy && !_isAppBusy;

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseAsync()
    {
        if (_connection.Api is not { } api || _connection.Account is not { } account)
        {
            return;
        }

        var browser = new DriveBrowserViewModel(api, account, AddFolder, () => Browser = null);
        Browser = browser;
        await browser.LoadRootAsync();
    }

    private bool CanBrowse() => IsConnected && !IsBusy && !_isAppBusy;

    private void UpdateState()
    {
        IsConnected = _connection.Account is not null;
        StatusText = _connection.UnavailableReason
                     ?? (_connection.Account is { } account
                         ? $"Connected to Google Drive as {account.Email} (read-only)."
                         : _connection.NeedsReconnect
                             ? "PhotoSift now also shows previews from Google Drive, which needs a new permission. Connect Google Drive again."
                             : "Not connected. Connect to scan folders in your My Drive (read-only).");
        OnPropertyChanged(nameof(IsAvailable));
    }
}

public sealed record DriveFolderNode(string Id, string Name);

/// <summary>Browses the folders in My Drive and adds the current folder to the scan or master folders.</summary>
public sealed partial class DriveBrowserViewModel(IDriveApi api, DriveAccount account, AddDriveFolder? addFolder, Action close)
    : ObservableObject
{
    public ObservableCollection<DriveFolderNode> PathNodes { get; } = [];

    public ObservableCollection<DriveFolderNode> Children { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial DriveFolderNode? SelectedChild { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(UpCommand), nameof(AddToScanCommand), nameof(AddAsMasterCommand))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    public string CurrentPath => string.Join("/", PathNodes.Select(n => n.Name));

    public bool IsEmpty => !IsLoading && Children.Count == 0;

    public async Task LoadRootAsync()
    {
        await LoadAsync(async ct =>
        {
            var root = await api.GetItemAsync("root", ct);
            PathNodes.Clear();
            PathNodes.Add(new DriveFolderNode(root.Id, DriveLocations.RootName));
        });
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        if (SelectedChild is { } child)
        {
            await LoadAsync(_ =>
            {
                PathNodes.Add(child);
                return Task.CompletedTask;
            });
        }
    }

    private bool CanOpen() => SelectedChild is not null && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private async Task UpAsync()
    {
        await LoadAsync(_ =>
        {
            PathNodes.RemoveAt(PathNodes.Count - 1);
            return Task.CompletedTask;
        });
    }

    private bool CanGoUp() => PathNodes.Count > 1 && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddToScan() => Add(asMaster: false);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddAsMaster() => Add(asMaster: true);

    private bool CanAdd() => PathNodes.Count > 0 && !IsLoading && addFolder is not null;

    [RelayCommand]
    private void Close() => close();

    private void Add(bool asMaster)
    {
        var location = DriveLocations.Folder(account.PermissionId, PathNodes[^1].Id);
        addFolder?.Invoke(location, $"Google Drive: {CurrentPath}", asMaster);
        StatusText = asMaster
            ? $"Added \"{CurrentPath}\" to the master folders."
            : $"Added \"{CurrentPath}\" to the folders to scan.";
    }

    private async Task LoadAsync(Func<CancellationToken, Task> navigate)
    {
        IsLoading = true;
        StatusText = "Loading…";
        try
        {
            await navigate(CancellationToken.None);
            Children.Clear();
            SelectedChild = null;
            var folders = await api.ListChildrenAsync(PathNodes[^1].Id, foldersOnly: true, CancellationToken.None);
            foreach (var folder in folders)
            {
                Children.Add(new DriveFolderNode(folder.Id, folder.Name));
            }

            StatusText = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText = $"Google Drive could not be read: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(CurrentPath));
            OnPropertyChanged(nameof(IsEmpty));
            UpCommand.NotifyCanExecuteChanged();
            AddToScanCommand.NotifyCanExecuteChanged();
            AddAsMasterCommand.NotifyCanExecuteChanged();
        }
    }
}
