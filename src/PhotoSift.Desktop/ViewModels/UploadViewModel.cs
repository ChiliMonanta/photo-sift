using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoSift.Core.Upload;
using PhotoSift.Desktop.Services;

namespace PhotoSift.Desktop.ViewModels;

/// <summary>Uploads photos and videos from a local folder to a Google Drive folder (the "Upload to Google Drive" tab).</summary>
public sealed partial class UploadViewModel : ObservableObject
{
    private const string LocalFolderSetting = "upload.localFolder";
    private const string DriveAccountSetting = "upload.driveAccount";
    private const string DriveFolderIdSetting = "upload.driveFolderId";
    private const string DriveFolderPathSetting = "upload.driveFolderPath";
    private const string BusyPart = "upload";

    private readonly DriveUploader _uploader;
    private readonly UploadJournal _journal;
    private readonly GoogleDriveViewModel _drive;
    private readonly IFolderPicker _folderPicker;
    private UploadPlan? _plan;

    // Progress callbacks are posted to the UI thread and can arrive after a step finished; they are
    // ignored then, so they cannot overwrite the final status.
    private int _step;

    public UploadViewModel(DriveUploader uploader, UploadJournal journal, GoogleDriveViewModel drive, IFolderPicker folderPicker)
    {
        _uploader = uploader;
        _journal = journal;
        _drive = drive;
        _folderPicker = folderPicker;
        LocalFolder = journal.GetSetting(LocalFolderSetting);
        if (journal.GetSetting(DriveAccountSetting) is { } account &&
            journal.GetSetting(DriveFolderIdSetting) is { } id &&
            journal.GetSetting(DriveFolderPathSetting) is { } path)
        {
            DriveFolder = new DriveFolderChoice(account, id, path);
        }

        drive.PropertyChanged += OnDriveChanged;
    }

    public UploadJournal Journal => _journal;

    public ObservableCollection<string> Problems { get; } = [];

    public bool HasProblems => Problems.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(StartCommand))]
    public partial string? LocalFolder { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(DriveFolderText))]
    public partial DriveFolderChoice? DriveFolder { get; private set; }

    public string DriveFolderText => DriveFolder is { } folder && IsCurrentAccount(folder)
        ? folder.Path
        : "No folder chosen";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(StartCommand), nameof(ChangeLocalFolderCommand),
        nameof(ChooseDriveFolderCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    /// <summary>Why uploading is not possible right now (not connected, or read-only sign-in), or empty.</summary>
    public string ConnectionText => !_drive.IsAvailable
        ? "Google Drive is not available in this build."
        : !_drive.IsConnected
            ? "Connect Google Drive above to upload."
            : !_drive.CanUpload
                ? "Your Google Drive connection is read-only. Click \"Allow uploads…\" above and allow PhotoSift to add files."
                : string.Empty;

    public bool HasConnectionText => ConnectionText.Length > 0;

    [ObservableProperty]
    public partial string StatusText { get; private set; } =
        "Choose a folder on this computer and a Google Drive folder. Only new photos and videos are uploaded; nothing is deleted.";

    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial double ProgressMaximum { get; private set; } = 1;

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; private set; }

    [ObservableProperty]
    public partial string FilesLeftText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentFileText { get; private set; } = string.Empty;

    // ---- Folders ---------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ChangeLocalFolderAsync()
    {
        var result = await _folderPicker.PickFoldersAsync("Upload from folder", allowMultiple: false);
        if (result.LocalPaths.FirstOrDefault() is { } folder)
        {
            LocalFolder = folder;
            _journal.SetSetting(LocalFolderSetting, folder);
        }
    }

    [RelayCommand(CanExecute = nameof(CanChooseDriveFolder))]
    private async Task ChooseDriveFolderAsync()
    {
        if (await _drive.ChooseFolderAsync("Upload to this folder") is { } folder)
        {
            DriveFolder = folder;
            _journal.SetSetting(DriveAccountSetting, folder.AccountId);
            _journal.SetSetting(DriveFolderIdSetting, folder.FolderId);
            _journal.SetSetting(DriveFolderPathSetting, folder.Path);
        }
    }

    private bool CanChooseDriveFolder() => IsIdle && _drive.IsConnected;

    partial void OnLocalFolderChanged(string? value) => _plan = null;

    partial void OnDriveFolderChanged(DriveFolderChoice? value) => _plan = null;

    // ---- Check and upload ----------------------------------------------------------------

    /// <summary>Reads both folders and shows how many files are new, without uploading anything.</summary>
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanStart))]
    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        SetBusy(true);
        try
        {
            await PlanAsync(cancellationToken);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Uploads the new files. After a cancel or an interruption, running it again continues where it stopped.</summary>
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        SetBusy(true);
        try
        {
            var plan = _plan ?? await PlanAsync(cancellationToken);
            if (plan is null)
            {
                return;
            }

            if (plan.ToUpload.Count == 0)
            {
                StatusText = "Everything in this folder is already in Google Drive.";
                return;
            }

            IsProgressIndeterminate = false;
            ProgressMaximum = Math.Max(1, plan.BytesToUpload);
            ProgressValue = 0;
            StatusText = $"Uploading to {plan.Target.DisplayPath}/{plan.TopFolderName}…";
            var step = ++_step;
            var progress = new Progress<UploadProgress>(p =>
            {
                if (step == _step)
                {
                    ShowProgress(p);
                }
            });

            var report = await _uploader.RunAsync(plan, progress, cancellationToken);
            _step++;
            ShowReport(report);
        }
        catch (Exception ex)
        {
            _step++;
            StatusText = $"The upload failed: {ex.Message}";
        }
        finally
        {
            // The next Start checks again, so it continues with whatever is left.
            _plan = null;
            SetBusy(false);
        }
    }

    private bool CanStart() =>
        !IsBusy && !string.IsNullOrEmpty(LocalFolder) && DriveFolder is { } folder && IsCurrentAccount(folder) && _drive.CanUpload;

    private async Task<UploadPlan?> PlanAsync(CancellationToken cancellationToken)
    {
        _plan = null;
        Problems.Clear();
        OnPropertyChanged(nameof(HasProblems));
        SummaryText = string.Empty;
        FilesLeftText = string.Empty;
        CurrentFileText = string.Empty;
        IsProgressIndeterminate = true;
        StatusText = "Reading the folders…";
        var step = ++_step;
        try
        {
            if (_drive.Session.Api is not { } api)
            {
                StatusText = "Connect Google Drive first.";
                return null;
            }

            var folder = DriveFolder!;
            var status = new Progress<string>(text =>
            {
                if (step == _step)
                {
                    StatusText = text;
                }
            });
            var plan = await _uploader.PlanAsync(
                api, LocalFolder!, new DriveFolderTarget(folder.AccountId, folder.FolderId, folder.Path), status, cancellationToken);
            _step++;
            _plan = plan;

            SummaryText = $"{plan.ToUpload.Count:N0} new files ({Formatting.FileSize(plan.BytesToUpload)}) · " +
                          $"{plan.AlreadyOnDrive:N0} already in Google Drive · {plan.UploadedBefore:N0} uploaded before" +
                          (plan.DuplicatesInFolder > 0 ? $" · {plan.DuplicatesInFolder:N0} identical copies skipped" : string.Empty);
            foreach (var problem in plan.Problems)
            {
                AddProblem(problem);
            }

            StatusText = plan.ToUpload.Count == 0
                ? "Everything in this folder is already in Google Drive."
                : plan.Resumed > 0
                    ? $"Click Start upload to upload the new files. {plan.Resumed:N0} interrupted upload(s) continue where they stopped."
                    : $"Click Start upload to upload the new files to {plan.Target.DisplayPath}/{plan.TopFolderName}.";
            return plan;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _step++;
            StatusText = "Canceled.";
            return null;
        }
        catch (Exception ex)
        {
            _step++;
            StatusText = $"The folders could not be read: {ex.Message}";
            return null;
        }
        finally
        {
            IsProgressIndeterminate = false;
        }
    }

    private void ShowProgress(UploadProgress p)
    {
        ProgressValue = p.UploadedBytes;
        FilesLeftText = $"{p.FilesLeft:N0} of {p.TotalFiles:N0} files left · " +
                        $"{Formatting.FileSize(p.UploadedBytes)} of {Formatting.FileSize(p.TotalBytes)}" +
                        (p.BytesPerSecond > 0
                            ? $" · {Formatting.FileSize((long)p.BytesPerSecond)}/s · {Formatting.TimeLeft((p.TotalBytes - p.UploadedBytes) / p.BytesPerSecond)}"
                            : string.Empty) +
                        (p.FailedFiles > 0 ? $" · {p.FailedFiles:N0} failed" : string.Empty);
        CurrentFileText = p.CurrentFile ?? string.Empty;
    }

    private void ShowReport(UploadReport report)
    {
        foreach (var failed in report.Outcomes.Where(o => o.Status == UploadOutcomeStatus.Failed))
        {
            AddProblem($"{failed.Item.File.RelativePath}: {failed.Reason}");
        }

        var done = $"{report.UploadedCount:N0} uploaded" +
                   (report.FailedCount > 0 ? $", {report.FailedCount:N0} failed" : string.Empty) + ".";
        var left = report.NotStarted + report.FailedCount;
        StatusText = report switch
        {
            { WasCanceled: true } => $"Upload canceled: {done} {left:N0} file(s) left. Click Start upload to continue.",
            { StopReason: { } reason } => $"Upload stopped: {done} {reason}",
            _ when report.FailedCount > 0 => $"Upload finished: {done} Click Start upload to try the failed files again.",
            _ => $"Upload finished: {done}",
        };
        CurrentFileText = string.Empty;
    }

    private void AddProblem(string problem)
    {
        Problems.Add(problem);
        OnPropertyChanged(nameof(HasProblems));
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        _drive.SetAppBusy(busy, BusyPart);
    }

    private bool IsCurrentAccount(DriveFolderChoice folder) => _drive.Session.Account?.PermissionId == folder.AccountId;

    private void OnDriveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GoogleDriveViewModel.IsConnected) or nameof(GoogleDriveViewModel.CanUpload)
            or nameof(GoogleDriveViewModel.IsAvailable))
        {
            OnPropertyChanged(nameof(ConnectionText));
            OnPropertyChanged(nameof(HasConnectionText));
            OnPropertyChanged(nameof(DriveFolderText));
            CheckCommand.NotifyCanExecuteChanged();
            StartCommand.NotifyCanExecuteChanged();
            ChooseDriveFolderCommand.NotifyCanExecuteChanged();
        }
    }
}
