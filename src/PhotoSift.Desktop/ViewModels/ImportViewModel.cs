using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoSift.Core.Import;
using PhotoSift.Desktop.Services;

namespace PhotoSift.Desktop.ViewModels;

/// <summary>A phone or folder in the "Import from" list.</summary>
public sealed record ImportSourceOption(string Label, PhoneInfo? Phone, string? Folder)
{
    public override string ToString() => Label;
}

/// <summary>Copies photos and videos from a phone or folder to the computer (the "Import from phone" tab).</summary>
public sealed partial class ImportViewModel : ObservableObject, IDisposable
{
    private const string DestinationSetting = "import.destination";

    private readonly PhotoImporter _importer;
    private readonly ImportJournal _journal;
    private readonly IPhoneProvider _phones;
    private readonly IFolderPicker _folderPicker;
    private ImportPlan? _plan;

    public ImportViewModel(PhotoImporter importer, ImportJournal journal, IPhoneProvider phones, IFolderPicker folderPicker)
    {
        _importer = importer;
        _journal = journal;
        _phones = phones;
        _folderPicker = folderPicker;
        DestinationFolder = journal.GetSetting(DestinationSetting)
                            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.Create);
    }

    public ImportJournal Journal => _journal;

    public ObservableCollection<ImportSourceOption> Sources { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    public string? PhonesUnavailableReason => _phones.UnavailableReason;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(StartCommand))]
    public partial ImportSourceOption? SelectedSource { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(StartCommand))]
    public partial string DestinationFolder { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(StartCommand), nameof(RefreshPhonesCommand),
        nameof(AddFolderSourceCommand), nameof(ChangeDestinationCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsCopying { get; private set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    public partial string StatusText { get; private set; } =
        "Connect your phone with a USB cable, then choose it below. Nothing on the phone is changed or deleted.";

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

    public bool HasProblems => Problems.Count > 0;

    // ---- Sources and destination -------------------------------------------------------

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RefreshPhonesAsync()
    {
        if (_phones.UnavailableReason is { } reason)
        {
            StatusText = reason;
            return;
        }

        IsBusy = true;
        StatusText = "Looking for phones…";
        try
        {
            var previous = SelectedSource;
            var phones = await _phones.GetPhonesAsync(CancellationToken.None);
            foreach (var old in Sources.Where(s => s.Phone is not null).ToList())
            {
                Sources.Remove(old);
            }

            foreach (var (phone, index) in phones.Select((p, i) => (p, i)))
            {
                var label = string.IsNullOrEmpty(phone.Description) ? phone.Name : $"{phone.Name} ({phone.Description})";
                Sources.Insert(index, new ImportSourceOption(label, phone, null));
            }

            SelectedSource = Sources.FirstOrDefault(s => s.Phone?.DeviceId == previous?.Phone?.DeviceId && previous?.Phone is not null)
                             ?? Sources.FirstOrDefault(s => s.Folder is not null && s.Folder == previous?.Folder)
                             ?? Sources.FirstOrDefault();
            StatusText = phones.Count == 0
                ? "No phone found. Android: unlock it and choose \"File transfer\" in the USB notification. " +
                  "iPhone: unlock it and tap \"Trust\". Then click Refresh."
                : $"Found {phones.Count} device(s). Choose one and click Check.";
        }
        catch (Exception ex)
        {
            StatusText = $"Phones could not be listed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task AddFolderSourceAsync()
    {
        var result = await _folderPicker.PickFoldersAsync("Import from folder", allowMultiple: false);
        if (result.LocalPaths.FirstOrDefault() is not { } folder)
        {
            return;
        }

        var option = Sources.FirstOrDefault(s => s.Folder == folder) ?? new ImportSourceOption($"Folder: {folder}", null, folder);
        if (!Sources.Contains(option))
        {
            Sources.Add(option);
        }

        SelectedSource = option;
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ChangeDestinationAsync()
    {
        var result = await _folderPicker.PickFoldersAsync("Copy photos to", allowMultiple: false);
        if (result.LocalPaths.FirstOrDefault() is { } folder)
        {
            DestinationFolder = folder;
            _journal.SetSetting(DestinationSetting, folder);
        }
    }

    partial void OnSelectedSourceChanged(ImportSourceOption? value) => ResetPlan();

    partial void OnDestinationFolderChanged(string value) => ResetPlan();

    // ---- Check and copy -------------------------------------------------------------------

    /// <summary>Reads the phone and shows how many files are new, without copying anything.</summary>
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanStart))]
    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            await PlanAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Copies the new files. After a cancel or an interruption, running it again continues where it stopped.</summary>
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            var plan = _plan ?? await PlanAsync(cancellationToken);
            if (plan is null || plan.Problem is not null)
            {
                return;
            }

            if (plan.ToCopy.Count == 0)
            {
                StatusText = "Everything on this source is already imported.";
                return;
            }

            IsCopying = true;
            IsProgressIndeterminate = false;
            ProgressMaximum = Math.Max(1, plan.BytesToCopy);
            ProgressValue = 0;
            StatusText = $"Copying to {Path.Combine(plan.DestinationRoot, PhotoImporter.SafeName(plan.Source.DisplayName))}…";
            var progress = new Progress<ImportProgress>(ShowProgress);

            var report = await _importer.RunAsync(plan, progress, cancellationToken);
            ShowReport(report);
        }
        catch (Exception ex)
        {
            StatusText = $"The import failed: {ex.Message}";
        }
        finally
        {
            // The next Start plans again, so it continues with whatever is left.
            ResetPlan();
            IsCopying = false;
            IsBusy = false;
        }
    }

    private bool CanStart() => SelectedSource is not null && !string.IsNullOrEmpty(DestinationFolder) && !IsBusy;

    private async Task<ImportPlan?> PlanAsync(CancellationToken cancellationToken)
    {
        ResetPlan();
        Problems.Clear();
        OnPropertyChanged(nameof(HasProblems));
        SummaryText = string.Empty;
        FilesLeftText = string.Empty;
        CurrentFileText = string.Empty;
        IsProgressIndeterminate = true;
        StatusText = "Reading the list of photos and videos…";

        IImportSource? source = null;
        try
        {
            source = SelectedSource!.Phone is { } phone
                ? await _phones.OpenAsync(phone, cancellationToken)
                : new FolderImportSource(SelectedSource.Folder!);
            var found = new Progress<int>(n => StatusText = $"Reading the list of photos and videos… {n:N0} found");
            var plan = await _importer.PlanAsync(source, DestinationFolder, found, cancellationToken);
            _plan = plan;
            source = null; // owned by the plan now

            SummaryText = $"{plan.ToCopy.Count:N0} new files ({Formatting.FileSize(plan.BytesToCopy)}) · " +
                          $"{plan.AlreadyImported:N0} already imported";
            foreach (var interrupted in plan.Interrupted)
            {
                AddProblem($"The copy of {interrupted} was interrupted last time. The incomplete copy was removed; it is copied again.");
            }

            StatusText = plan.Problem ?? (plan.ToCopy.Count == 0
                ? "Everything on this source is already imported."
                : "Click Start copy to copy the new files.");
            return plan;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Canceled.";
            return null;
        }
        catch (Exception ex)
        {
            StatusText = $"The source could not be read: {ex.Message}";
            return null;
        }
        finally
        {
            source?.Dispose();
            IsProgressIndeterminate = false;
        }
    }

    private void ShowProgress(ImportProgress p)
    {
        ProgressValue = p.CopiedBytes;
        FilesLeftText = $"{p.FilesLeft:N0} of {p.TotalFiles:N0} files left · " +
                        $"{Formatting.FileSize(p.CopiedBytes)} of {Formatting.FileSize(p.TotalBytes)}" +
                        (p.BytesPerSecond > 0
                            ? $" · {Formatting.FileSize((long)p.BytesPerSecond)}/s · {TimeLeft((p.TotalBytes - p.CopiedBytes) / p.BytesPerSecond)}"
                            : string.Empty) +
                        (p.FailedFiles > 0 ? $" · {p.FailedFiles:N0} failed" : string.Empty);
        CurrentFileText = p.CurrentFile ?? string.Empty;
    }

    private void ShowReport(ImportReport report)
    {
        foreach (var failed in report.Outcomes.Where(o => o.Status == ImportStatus.Failed))
        {
            AddProblem($"{failed.Item.RelativePath}: {failed.Reason}");
        }

        var done = $"{report.CopiedCount:N0} copied" +
                   (report.AlreadyThereCount > 0 ? $", {report.AlreadyThereCount:N0} were already on the computer" : string.Empty) +
                   (report.FailedCount > 0 ? $", {report.FailedCount:N0} failed" : string.Empty) + ".";
        var left = report.NotStarted + report.FailedCount;
        StatusText = report switch
        {
            { WasCanceled: true } => $"Import canceled: {done} {left:N0} file(s) left. Click Start copy to continue.",
            { StopReason: { } reason } => $"Import stopped: {done} {reason}",
            _ when report.FailedCount > 0 => $"Import finished: {done} Click Start copy to try the failed files again.",
            _ => $"Import finished: {done}",
        };
        CurrentFileText = string.Empty;
    }

    private void AddProblem(string problem)
    {
        Problems.Add(problem);
        OnPropertyChanged(nameof(HasProblems));
    }

    private void ResetPlan()
    {
        _plan?.Source.Dispose();
        _plan = null;
    }

    private static string TimeLeft(double seconds) => seconds switch
    {
        < 60 => "less than a minute left",
        < 3600 => $"about {Math.Ceiling(seconds / 60):0} min left",
        _ => $"about {seconds / 3600:0.0} h left",
    };

    public void Dispose() => ResetPlan();
}
