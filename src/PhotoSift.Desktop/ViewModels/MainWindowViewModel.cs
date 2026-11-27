using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoSift.Core;
using PhotoSift.Core.Deletion;
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
        string? startupWarning = null)
    {
        _scanner = scanner;
        _deleter = deleter;
        _folderPicker = folderPicker;
        _thumbnailLoader = thumbnailLoader;

        Folders.CollectionChanged += (_, _) => ScanCommand.NotifyCanExecuteChanged();
        Issues.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasIssues));
        if (startupWarning is not null)
        {
            Issues.Add(startupWarning);
        }
    }

    // ---- Folders -------------------------------------------------------------

    public ObservableCollection<string> Folders { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveFolderCommand))]
    public partial string? SelectedFolder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterFolderDisplay), nameof(HasMasterFolder))]
    [NotifyCanExecuteChangedFor(nameof(ClearMasterFolderCommand), nameof(ApplyMasterFolderCommand))]
    public partial string? MasterFolder { get; set; }

    public bool HasMasterFolder => MasterFolder is not null;

    public string MasterFolderDisplay => MasterFolder ?? "No master folder selected";

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
    private async Task ChooseMasterFolderAsync()
    {
        var result = await _folderPicker.PickFoldersAsync("Select the master folder", allowMultiple: false);
        ReportNonLocalFolders(result);
        if (result.LocalPaths.Count == 0)
        {
            return;
        }

        MasterFolder = PathUtil.Normalize(result.LocalPaths[0]);
        ApplyMasterFolder();
    }

    [RelayCommand(CanExecute = nameof(CanClearMasterFolder))]
    private void ClearMasterFolder()
    {
        MasterFolder = null;
        UpdateMasterFlags();
    }

    private bool CanClearMasterFolder() => MasterFolder is not null && IsIdle();

    /// <summary>
    /// In groups with a copy in the master folder, marks every copy outside it.
    /// Groups without a master copy, and copies inside the master folder, are left untouched.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyMasterFolder))]
    private void ApplyMasterFolder()
    {
        UpdateMasterFlags();
        if (MasterFolder is null || Groups.Count == 0)
        {
            return;
        }

        var toMark = MasterFolderSelector
            .SelectCopiesOutsideMaster(Groups.Select(g => g.Group), MasterFolder)
            .Select(f => f.Path)
            .ToHashSet(PathUtil.Comparer);

        foreach (var entry in AllFiles().Where(f => !f.IsTrashed && toMark.Contains(f.Path)))
        {
            entry.IsMarked = true;
        }

        StatusText = $"Master folder applied: {toMark.Count} copies outside the master folder are marked for deletion.";
    }

    private bool CanApplyMasterFolder() => MasterFolder is not null && IsIdle();

    // ---- Scanning ------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReviewDeletion))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(AddFoldersCommand), nameof(RemoveFolderCommand),
        nameof(ChooseMasterFolderCommand), nameof(ClearMasterFolderCommand), nameof(ApplyMasterFolderCommand),
        nameof(ReviewDeletionCommand), nameof(ClearMarksCommand))]
    public partial bool IsScanning { get; private set; }

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
        foreach (var group in result.Groups)
        {
            Groups.Add(new DuplicateGroupViewModel(group, _thumbnailLoader, OnMarkChanged));
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

        if (MasterFolder is not null)
        {
            ApplyMasterFolder();
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
        nameof(AddFoldersCommand), nameof(RemoveFolderCommand), nameof(ChooseMasterFolderCommand),
        nameof(ClearMasterFolderCommand), nameof(ApplyMasterFolderCommand), nameof(ClearMarksCommand))]
    public partial bool IsDeleting { get; private set; }

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
                    DeletionStatus.MovedToTrash => $"Moved to trash: {outcome.Path}",
                    DeletionStatus.Skipped => $"Skipped: {outcome.Path} — {outcome.Reason}",
                    _ => $"Failed: {outcome.Path} — {outcome.Reason}",
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

    private void UpdateMasterFlags()
    {
        foreach (var entry in AllFiles())
        {
            entry.IsInMasterFolder = MasterFolder is not null && PathUtil.IsInFolder(entry.Path, MasterFolder);
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
