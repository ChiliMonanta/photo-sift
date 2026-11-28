using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoSift.Core;
using PhotoSift.Core.Deletion;
using PhotoSift.Core.GoogleDrive;
using PhotoSift.Core.Scanning;
using PhotoSift.Core.Selection;
using PhotoSift.Desktop.Services;

namespace PhotoSift.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly DuplicateScanner _scanner;
    private readonly SafeDeleter _deleter;
    private readonly IFolderPicker _folderPicker;
    private readonly ThumbnailLoader _thumbnailLoader;

    public MainWindowViewModel(
        DuplicateScanner scanner,
        SafeDeleter deleter,
        IFolderPicker folderPicker,
        ThumbnailLoader thumbnailLoader,
        string? startupWarning = null,
        GoogleDriveViewModel? drive = null,
        ImportViewModel? import = null)
    {
        Import = import;
        _scanner = scanner;
        _deleter = deleter;
        _folderPicker = folderPicker;
        _thumbnailLoader = thumbnailLoader;
        Drive = drive;
        if (drive is not null)
        {
            drive.AddFolder = AddDriveFolder;
        }

        Folders.CollectionChanged += (_, _) => ScanCommand.NotifyCanExecuteChanged();
        MasterFolders.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasMasterFolders));
            ClearMasterFoldersCommand.NotifyCanExecuteChanged();
            ApplyMasterFoldersCommand.NotifyCanExecuteChanged();
        };
        Issues.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasIssues));
        if (startupWarning is not null)
        {
            Issues.Add(startupWarning);
        }
    }

    /// <summary>Google Drive connection and folder browser; null when the app runs without Drive support.</summary>
    public GoogleDriveViewModel? Drive { get; }

    public bool HasDrive => Drive is not null;

    /// <summary>Phone import (the "Import from phone" tab); null when the import journal could not be opened.</summary>
    public ImportViewModel? Import { get; }

    public bool HasImport => Import is not null;

    // ---- Folders -------------------------------------------------------------

    public ObservableCollection<string> Folders { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveFolderCommand))]
    public partial string? SelectedFolder { get; set; }

    /// <summary>Folders whose copies are kept; copies outside all of them are marked by the master rule.</summary>
    public ObservableCollection<string> MasterFolders { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveMasterFolderCommand))]
    public partial string? SelectedMasterFolder { get; set; }

    public bool HasMasterFolders => MasterFolders.Count > 0;

    /// <summary>Files marked by the master rule (not by the user), so the rule can be undone when masters change.</summary>
    private readonly HashSet<string> _markedByMasterRule = new(PathUtil.Comparer);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task AddFoldersAsync()
    {
        var result = await _folderPicker.PickFoldersAsync("Select folders to scan", allowMultiple: true);
        foreach (var path in result.LocalPaths)
        {
            var normalized = PathUtil.Normalize(path);
            if (!Folders.Contains(normalized, PathUtil.Comparer))
            {
                Folders.Add(normalized);
            }
        }

        ReportNonLocalFolders(result);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveFolder))]
    private void RemoveFolder()
    {
        if (SelectedFolder is not null)
        {
            Folders.Remove(SelectedFolder);
        }
    }

    private bool CanRemoveFolder() => SelectedFolder is not null && IsIdle();

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task AddMasterFoldersAsync()
    {
        var result = await _folderPicker.PickFoldersAsync("Select master folders", allowMultiple: true);
        ReportNonLocalFolders(result);

        var added = false;
        foreach (var path in result.LocalPaths)
        {
            var normalized = PathUtil.Normalize(path);
            if (!MasterFolders.Contains(normalized, PathUtil.Comparer))
            {
                MasterFolders.Add(normalized);
                added = true;
            }
        }

        if (added)
        {
            ApplyMasterFolders();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveMasterFolder))]
    private void RemoveMasterFolder()
    {
        if (SelectedMasterFolder is not null && MasterFolders.Remove(SelectedMasterFolder))
        {
            ApplyMasterFolders();
        }
    }

    private bool CanRemoveMasterFolder() => SelectedMasterFolder is not null && IsIdle();

    /// <summary>Adds a folder chosen in the Google Drive browser to the folders to scan or the master folders.</summary>
    public void AddDriveFolder(string location, string label, bool asMaster)
    {
        if (!IsIdle())
        {
            return;
        }

        LocationLabels.Register(location, label);
        var list = asMaster ? MasterFolders : Folders;
        if (list.Contains(location, PathUtil.Comparer))
        {
            return;
        }

        list.Add(location);
        if (asMaster)
        {
            ApplyMasterFolders();
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeMasterFolders))]
    private void ClearMasterFolders()
    {
        MasterFolders.Clear();
        ApplyMasterFolders();
    }

    /// <summary>
    /// In groups with a copy in any master folder, marks every copy outside all master folders.
    /// Groups without a master copy, and copies inside a master folder, are left untouched.
    /// Marks the rule set earlier that no longer apply (e.g. after a master folder was removed)
    /// are cleared; marks the user set manually are never changed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeMasterFolders))]
    private void ApplyMasterFolders()
    {
        UpdateMasterFlags();

        var toMark = MasterFolderSelector
            .SelectCopiesOutsideMasters(Groups.Select(g => g.Group), MasterFolders, _scanner.Sources)
            .Select(f => f.Path)
            .ToHashSet(PathUtil.Comparer);

        var readOnlyKept = 0;
        var unmarked = 0;
        foreach (var entry in AllFiles().Where(f => _markedByMasterRule.Contains(f.Path) && !toMark.Contains(f.Path)))
        {
            if (entry.IsMarked)
            {
                entry.IsMarked = false;
                unmarked++;
            }

            _markedByMasterRule.Remove(entry.Path);
        }

        foreach (var entry in AllFiles().Where(f => !f.IsTrashed && !f.IsMarked && toMark.Contains(f.Path)))
        {
            if (entry.IsReadOnly)
            {
                readOnlyKept++;
                continue;
            }

            entry.IsMarked = true;
            if (entry.IsMarked)
            {
                _markedByMasterRule.Add(entry.Path);
            }
        }

        if (Groups.Count == 0)
        {
            return;
        }

        StatusText = MasterFolders.Count == 0
            ? $"No master folders: {unmarked} mark(s) set by the master rule were removed."
            : $"Master folders applied: {toMark.Count - readOnlyKept} copies outside the master folders are marked for deletion." +
              (readOnlyKept > 0 ? $" {readOnlyKept} read-only Google Drive copies outside them are kept." : string.Empty);
    }

    private bool CanChangeMasterFolders() => MasterFolders.Count > 0 && IsIdle();

    // ---- Scanning ------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReviewDeletion))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(AddFoldersCommand), nameof(RemoveFolderCommand),
        nameof(AddMasterFoldersCommand), nameof(RemoveMasterFolderCommand), nameof(ClearMasterFoldersCommand),
        nameof(ApplyMasterFoldersCommand), nameof(ReviewDeletionCommand), nameof(ClearMarksCommand))]
    public partial bool IsScanning { get; private set; }

    partial void OnIsScanningChanged(bool value) => Drive?.SetAppBusy(!IsIdle());

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial double ProgressMaximum { get; private set; } = 1;

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "Add one or more folders, then start a scan.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReviewDeletion), nameof(IsScanIncomplete), nameof(ScanSummary))]
    [NotifyCanExecuteChangedFor(nameof(ReviewDeletionCommand))]
    public partial ScanResult? CurrentScan { get; private set; }

    public bool IsScanIncomplete => CurrentScan is { IsComplete: false };

    public string ScanSummary => CurrentScan switch
    {
        null => string.Empty,
        { WasCanceled: true } => "Scan canceled — results are incomplete. Review is possible, but deletion is disabled.",
        { IsComplete: false } => $"{CurrentScan.Errors.Count} file(s) or folder(s) could not be scanned — results are incomplete. Deletion is disabled.",
        _ => string.Empty,
    };

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    public ObservableCollection<string> Issues { get; } = [];

    public bool HasIssues => Issues.Count > 0;

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanScan))]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        IsScanning = true;
        CurrentScan = null;
        Groups.Clear();
        Issues.Clear();
        MarkedCount = 0;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        StatusText = "Scanning…";
        WarnAboutDriveForDesktop();

        var progress = new Progress<ScanProgress>(p =>
        {
            if (!IsScanning)
            {
                return;
            }

            IsProgressIndeterminate = !p.EnumerationComplete;
            ProgressMaximum = Math.Max(1, p.FilesDiscovered);
            ProgressValue = p.FilesProcessed;
            StatusText = p.EnumerationComplete
                ? $"Hashing… {p.FilesProcessed:N0} of {p.FilesDiscovered:N0} images"
                : $"Finding images… {p.FilesDiscovered:N0} found, {p.FilesProcessed:N0} hashed";
        });

        try
        {
            var result = await _scanner.ScanAsync(Folders.ToList(), progress, cancellationToken);
            ShowResult(result);
        }
        catch (Exception ex)
        {
            StatusText = "The scan failed.";
            Issues.Add($"Scan failed: {ex.Message}");
        }
        finally
        {
            IsScanning = false;
            IsProgressIndeterminate = false;
        }
    }

    private bool CanScan() => Folders.Count > 0 && IsIdle();

    private void ShowResult(ScanResult result)
    {
        _markedByMasterRule.Clear();
        foreach (var group in result.Groups)
        {
            Groups.Add(new DuplicateGroupViewModel(group, _thumbnailLoader, OnMarkChanged, ReadOnlyReason));
        }

        foreach (var error in result.Errors)
        {
            Issues.Add($"Error: {error.Path} — {error.Reason}");
        }

        foreach (var notice in result.Notices)
        {
            Issues.Add($"Note: {notice.Path} — {notice.Message}");
        }

        CurrentScan = result;
        ProgressMaximum = Math.Max(1, result.FilesScanned);
        ProgressValue = result.FilesScanned;

        var copies = result.Groups.Sum(g => g.Files.Count);
        StatusText = $"{(result.WasCanceled ? "Scan canceled" : "Scan finished")}: {result.FilesScanned:N0} images hashed, " +
                     $"{result.Groups.Count:N0} duplicate groups ({copies:N0} files), {result.Errors.Count:N0} errors.";

        if (MasterFolders.Count > 0)
        {
            ApplyMasterFolders();
        }
    }

    // ---- Marking and deletion ------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReviewDeletion), nameof(MarkedSummary))]
    [NotifyCanExecuteChangedFor(nameof(ReviewDeletionCommand), nameof(ClearMarksCommand))]
    public partial int MarkedCount { get; private set; }

    public string MarkedSummary => $"{MarkedCount:N0} file(s) marked for deletion";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReviewDeletion))]
    [NotifyCanExecuteChangedFor(nameof(ReviewDeletionCommand), nameof(ConfirmDeletionCommand), nameof(ScanCommand),
        nameof(AddFoldersCommand), nameof(RemoveFolderCommand), nameof(AddMasterFoldersCommand),
        nameof(RemoveMasterFolderCommand), nameof(ClearMasterFoldersCommand), nameof(ApplyMasterFoldersCommand),
        nameof(ClearMarksCommand))]
    public partial bool IsDeleting { get; private set; }

    partial void OnIsDeletingChanged(bool value) => Drive?.SetAppBusy(!IsIdle());

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverlayVisible))]
    public partial bool IsConfirmingDeletion { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverlayVisible))]
    public partial bool IsShowingDeletionReport { get; private set; }

    public bool IsOverlayVisible => IsConfirmingDeletion || IsShowingDeletionReport;

    [ObservableProperty]
    public partial string DeletionReportSummary { get; private set; } = string.Empty;

    public ObservableCollection<FileEntryViewModel> PendingDeletion { get; } = [];

    public ObservableCollection<string> DeletionReportLines { get; } = [];

    public bool CanReviewDeletion => CurrentScan is { IsComplete: true } && MarkedCount > 0 && IsIdle();

    [RelayCommand(CanExecute = nameof(CanClearMarks))]
    private void ClearMarks()
    {
        foreach (var entry in AllFiles())
        {
            entry.IsMarked = false;
        }
    }

    private bool CanClearMarks() => MarkedCount > 0 && IsIdle();

    [RelayCommand(CanExecute = nameof(CanReviewDeletion))]
    private void ReviewDeletion()
    {
        PendingDeletion.Clear();
        foreach (var entry in AllFiles().Where(f => f.IsMarked && !f.IsTrashed))
        {
            PendingDeletion.Add(entry);
        }

        IsConfirmingDeletion = true;
    }

    [RelayCommand]
    private void CancelDeletion()
    {
        IsConfirmingDeletion = false;
        PendingDeletion.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanConfirmDeletion))]
    private async Task ConfirmDeletionAsync()
    {
        if (CurrentScan is not { IsComplete: true } scan)
        {
            IsConfirmingDeletion = false;
            StatusText = "Deletion is only possible after a complete scan.";
            return;
        }

        var entries = PendingDeletion.ToList();
        IsConfirmingDeletion = false;
        IsDeleting = true;
        StatusText = $"Verifying and moving {entries.Count:N0} file(s) to the trash…";

        try
        {
            var report = await _deleter.MoveToTrashAsync(scan, entries.Select(e => e.Path));
            var byPath = entries.ToDictionary(e => e.Path, PathUtil.Comparer);

            DeletionReportLines.Clear();
            foreach (var outcome in report.Outcomes)
            {
                if (outcome.Status == DeletionStatus.MovedToTrash && byPath.TryGetValue(outcome.Path, out var entry))
                {
                    entry.IsMarked = false;
                    entry.IsTrashed = true;
                }

                DeletionReportLines.Add(outcome.Status switch
                {
                    DeletionStatus.MovedToTrash => $"Moved to trash: {outcome.DisplayPath}",
                    DeletionStatus.Skipped => $"Skipped: {outcome.DisplayPath} — {outcome.Reason}",
                    _ => $"Failed: {outcome.DisplayPath} — {outcome.Reason}",
                });
            }

            DeletionReportSummary =
                $"{report.MovedToTrashCount:N0} moved to trash, {report.SkippedCount:N0} skipped, {report.FailedCount:N0} failed.";
            StatusText = DeletionReportSummary;
        }
        catch (Exception ex)
        {
            DeletionReportLines.Clear();
            DeletionReportLines.Add(ex.Message);
            DeletionReportSummary = "Deletion failed. No further files were processed.";
            StatusText = DeletionReportSummary;
        }
        finally
        {
            PendingDeletion.Clear();
            IsDeleting = false;
            IsShowingDeletionReport = true;
        }
    }

    private bool CanConfirmDeletion() => CurrentScan is { IsComplete: true } && !IsDeleting && !IsScanning;

    [RelayCommand]
    private void CloseDeletionReport() => IsShowingDeletionReport = false;

    // ---- Helpers -------------------------------------------------------------

    private bool IsIdle() => !IsScanning && !IsDeleting;

    private IEnumerable<FileEntryViewModel> AllFiles() => Groups.SelectMany(g => g.Files);

    private void OnMarkChanged(bool marked) => MarkedCount += marked ? 1 : -1;

    private string? ReadOnlyReason(ScannedFile file) =>
        _deleter.Sources.Get(file.SourceId) is { } source ? source.TrashUnavailableReason : "The file's source is not available.";

    /// <summary>
    /// Scanning a Google Drive for desktop folder together with Drive itself finds the same files twice,
    /// and deleting the local "copy" deletes the file from Drive as well.
    /// </summary>
    private void WarnAboutDriveForDesktop()
    {
        if (!Folders.Any(DriveLocations.IsDrive))
        {
            return;
        }

        foreach (var folder in Folders.Where(f => PathUtil.IsLocal(f) && DriveForDesktop.IsInside(f)))
        {
            Issues.Add($"Warning: {folder} looks like a Google Drive for desktop folder. Its files may also be found through " +
                       "Google Drive and show up as duplicates of themselves; moving such a local copy to the trash also deletes it from Drive.");
        }
    }

    private void UpdateMasterFlags()
    {
        foreach (var entry in AllFiles())
        {
            entry.IsInMasterFolder = MasterFolderSelector.IsInAnyMasterFolder(entry.File, MasterFolders, _scanner.Sources);
        }
    }

    private void ReportNonLocalFolders(FolderPickResult result)
    {
        if (result.NonLocalCount > 0)
        {
            Issues.Add($"{result.NonLocalCount} selected folder(s) are not local folders and were ignored.");
        }
    }
}
