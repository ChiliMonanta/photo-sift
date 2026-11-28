using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoSift.Core.GoogleDrive;
using PhotoSift.Core.GoogleDrive.Auth;

namespace PhotoSift.Desktop.ViewModels;

/// <summary>Adds a Drive folder to the folders to scan, or to the master folders.</summary>
public delegate void AddDriveFolder(string location, string label, bool asMaster);

/// <summary>A Drive folder the user chose, e.g. to upload to.</summary>
/// <param name="Path">E.g. "My Drive/Photos".</param>
public sealed record DriveFolderChoice(string AccountId, string FolderId, string Path);

/// <summary>Connecting and disconnecting Google Drive, and opening the Drive folder browser.</summary>
public sealed partial class GoogleDriveViewModel : ObservableObject
{
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly DriveConnection _connection;
    private readonly Func<Uri, CancellationToken, Task> _openBrowser;
    private readonly HashSet<string> _busyParts = [];

    public GoogleDriveViewModel(DriveConnection connection, Func<Uri, CancellationToken, Task> openBrowser)
    {
        _connection = connection;
        _openBrowser = openBrowser;
        UpdateState();
    }

    /// <summary>Set by the main window: where folders chosen in the browser go.</summary>
    public AddDriveFolder? AddFolder { get; set; }

    public bool IsAvailable => _connection.UnavailableReason is null;

    /// <summary>The connection, for features that use Drive (scanning, uploading).</summary>
    public IDriveSession Session => _connection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(BrowseCommand))]
    public partial bool IsConnected { get; private set; }

    /// <summary>Connected with permission to add files. Sign-ins from before uploading existed are read-only.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyPropertyChangedFor(nameof(NeedsUploadPermission))]
    public partial bool CanUpload { get; private set; }

    /// <summary>Connected, but read-only: connecting again allows uploads.</summary>
    public bool NeedsUploadPermission => IsConnected && !CanUpload;

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

    /// <summary>
    /// Connecting and disconnecting is not allowed while a scan, deletion or upload runs.
    /// <paramref name="part"/> names who is busy, so one part finishing does not unlock another.
    /// </summary>
    public void SetAppBusy(bool busy, string part = "scan")
    {
        if (busy)
        {
            _busyParts.Add(part);
        }
        else
        {
            _busyParts.Remove(part);
        }

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
            StatusText = ConnectedText(account);
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

    private bool CanConnect() => IsAvailable && (!IsConnected || !CanUpload) && !IsBusy && _busyParts.Count == 0;

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

    private bool CanDisconnect() => IsConnected && !IsBusy && _busyParts.Count == 0;

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

    private bool CanBrowse() => IsConnected && !IsBusy && _busyParts.Count == 0;

    /// <summary>Opens the folder browser to choose one folder. Returns null if the user closes it.</summary>
    public async Task<DriveFolderChoice?> ChooseFolderAsync(string purpose)
    {
        if (_connection.Api is not { } api || _connection.Account is not { } account || Browser is not null)
        {
            return null;
        }

        var chosen = new TaskCompletionSource<DriveFolderChoice?>();
        var browser = new DriveBrowserViewModel(api, account, null, () =>
        {
            Browser = null;
            chosen.TrySetResult(null);
        })
        {
            ChooseLabel = purpose,
            Choose = choice =>
            {
                Browser = null;
                chosen.TrySetResult(choice);
            },
        };
        Browser = browser;
        await browser.LoadRootAsync();
        return await chosen.Task;
    }

    private void UpdateState()
    {
        IsConnected = _connection.Account is not null;
        CanUpload = _connection.CanUpload;
        OnPropertyChanged(nameof(NeedsUploadPermission));
        StatusText = _connection.UnavailableReason
                     ?? (_connection.Account is { } account
                         ? ConnectedText(account)
                         : _connection.NeedsReconnect
                             ? "PhotoSift now also shows previews from Google Drive, which needs a new permission. Connect Google Drive again."
                             : "Not connected. Connect to scan folders in your My Drive and to upload photos.");
        OnPropertyChanged(nameof(IsAvailable));
    }

    private string ConnectedText(DriveAccount account) => _connection.CanUpload
        ? $"Connected to Google Drive as {account.Email}."
        : $"Connected to Google Drive as {account.Email} (read-only). Click \"Allow uploads…\" to upload photos.";
}

public sealed record DriveFolderNode(string Id, string Name);

/// <summary>Browses the folders in My Drive and adds the current folder to the scan or master folders.</summary>
public sealed partial class DriveBrowserViewModel(IDriveApi api, DriveAccount account, AddDriveFolder? addFolder, Action close)
    : ObservableObject
{
    public ObservableCollection<DriveFolderNode> PathNodes { get; } = [];

    /// <summary>Set when the browser is used to choose one folder (e.g. to upload to) instead of adding folders to scan.</summary>
    public Action<DriveFolderChoice>? Choose { get; init; }

    /// <summary>The text of the choose button, e.g. "Upload to this folder".</summary>
    public string ChooseLabel { get; init; } = "Choose this folder";

    public bool IsChoosing => Choose is not null;

    public bool IsAdding => Choose is null;

    public string HintText => IsChoosing
        ? "Double-click a folder to open it. The button below chooses the folder shown above."
        : "Double-click a folder to open it. The buttons below add the folder shown above, including its subfolders.";

    public ObservableCollection<DriveFolderNode> Children { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial DriveFolderNode? SelectedChild { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(UpCommand), nameof(AddToScanCommand), nameof(AddAsMasterCommand),
        nameof(ChooseFolderCommand))]
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

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void ChooseFolder() => Choose?.Invoke(new DriveFolderChoice(account.PermissionId, PathNodes[^1].Id, CurrentPath));

    private bool CanChoose() => PathNodes.Count > 0 && !IsLoading && Choose is not null;

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
            ChooseFolderCommand.NotifyCanExecuteChanged();
        }
    }
}
