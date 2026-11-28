using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoSift.Core;
using PhotoSift.Core.Import;
using PhotoSift.Core.Sorting;
using PhotoSift.Desktop.Services;

namespace PhotoSift.Desktop.ViewModels;

/// <summary>
/// Sorting photos by hand (the "Sort photos" tab): a photo library folder, its folders, and the
/// photos in the selected folder, which can be moved to another folder.
/// </summary>
public sealed partial class SortViewModel : ObservableObject
{
    private const string LibrarySetting = "sort.library";

    private readonly PhotoSorter _sorter;
    private readonly IFolderPicker _folderPicker;
    private readonly ThumbnailLoader _thumbnails;
    private readonly ISettingsStore? _settings;
    private MoveReport? _lastMove;
    private int _photoLoad;

    public SortViewModel(PhotoSorter sorter, IFolderPicker folderPicker, ThumbnailLoader thumbnails, ISettingsStore? settings = null)
    {
        _sorter = sorter;
        _folderPicker = folderPicker;
        _thumbnails = thumbnails;
        _settings = settings;
        LibraryFolder = settings?.GetSetting(LibrarySetting);
    }

    /// <summary>The folder tree; one root, the library folder.</summary>
    public ObservableCollection<SortFolderViewModel> Folders { get; } = [];

    /// <summary>Every folder in the library, for the "Move to" list.</summary>
    public ObservableCollection<SortFolderViewModel> AllFolders { get; } = [];

    public ObservableCollection<SortPhotoViewModel> Photos { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    public bool HasProblems => Problems.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial string? LibraryFolder { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateFolderCommand), nameof(MoveCommand))]
    public partial SortFolderViewModel? SelectedFolder { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveCommand))]
    public partial SortFolderViewModel? MoveTarget { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateFolderCommand))]
    public partial string NewFolderName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangeLibraryCommand), nameof(RefreshCommand), nameof(CreateFolderCommand),
        nameof(MoveCommand), nameof(UndoCommand), nameof(SelectAllCommand), nameof(SelectNoneCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } =
        "Choose your photo folder. Create folders, select photos, and move them into the folders.";

    [ObservableProperty]
    public partial string SelectionText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasPhotos { get; private set; }

    public int SelectedCount => Photos.Count(p => p.IsSelected);

    // ---- Library and folders ------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ChangeLibraryAsync()
    {
        var result = await _folderPicker.PickFoldersAsync("Choose your photo folder", allowMultiple: false);
        if (result.LocalPaths.FirstOrDefault() is { } folder)
        {
            LibraryFolder = folder;
            _settings?.SetSetting(LibrarySetting, folder);
            await LoadTreeAsync(selectPath: folder);
        }
    }

    /// <summary>Reads the folders again, e.g. after changes outside PhotoSift.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadTreeAsync(SelectedFolder?.Path);

    private bool CanRefresh() => !IsBusy && LibraryFolder is not null;

    /// <summary>Loads the remembered library at startup.</summary>
    public Task InitializeAsync() => LibraryFolder is null ? Task.CompletedTask : LoadTreeAsync(LibraryFolder);

    [RelayCommand(CanExecute = nameof(CanCreateFolder))]
    private void CreateFolder()
    {
        var parent = SelectedFolder!;
        var name = NewFolderName.Trim();
        if (PhotoSorter.ValidateFolderName(name) is { } problem)
        {
            StatusText = problem;
            return;
        }

        try
        {
            var path = _sorter.CreateFolder(parent.Path, name);
            var folder = new SortFolderViewModel(path, name, parent, 0);
            InsertSorted(parent.Children, folder);
            parent.IsExpanded = true;
            RebuildAllFolders();
            MoveTarget = folder;
            NewFolderName = string.Empty;
            StatusText = $"Created \"{name}\". Select photos and click Move to put them in it.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = $"The folder could not be created: {ex.Message}";
        }
    }

    private bool CanCreateFolder() => !IsBusy && SelectedFolder is not null && !string.IsNullOrWhiteSpace(NewFolderName);

    partial void OnSelectedFolderChanged(SortFolderViewModel? value) => _ = LoadPhotosAsync(value);

    // ---- Selecting and moving ----------------------------------------------------------

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void SelectAll()
    {
        foreach (var photo in Photos)
        {
            photo.IsSelected = true;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void SelectNone()
    {
        foreach (var photo in Photos)
        {
            photo.IsSelected = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanMove))]
    private async Task MoveAsync()
    {
        var target = MoveTarget!;
        var source = SelectedFolder!;
        var selected = Photos.Where(p => p.IsSelected).ToList();
        IsBusy = true;
        Problems.Clear();
        OnPropertyChanged(nameof(HasProblems));
        try
        {
            var report = await Task.Run(() => _sorter.Move(selected.Select(p => p.Path).ToList(), target.Path));
            _lastMove = report.Moved.Count > 0 ? report : null;
            var movedPaths = report.Moved.Select(m => m.From).ToHashSet(PathUtil.Comparer);
            foreach (var photo in selected.Where(p => movedPaths.Contains(p.Path)))
            {
                Photos.Remove(photo);
            }

            source.PhotoCount -= report.Moved.Count;
            target.PhotoCount += report.Moved.Count;
            ShowProblems(report);
            StatusText = $"Moved {report.Moved.Count:N0} photo(s) to \"{target.DisplayPath}\"." +
                         (report.Problems.Count > 0 ? $" {report.Problems.Count:N0} could not be moved; see Problems." : string.Empty);
        }
        finally
        {
            IsBusy = false;
            UpdateSelection();
        }
    }

    private bool CanMove() => !IsBusy && MoveTarget is not null && SelectedFolder is not null && MoveTarget != SelectedFolder &&
                              SelectedCount > 0;

    /// <summary>Moves the photos of the last move back to where they were.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        var last = _lastMove!;
        _lastMove = null;
        IsBusy = true;
        Problems.Clear();
        OnPropertyChanged(nameof(HasProblems));
        try
        {
            var report = await Task.Run(() => _sorter.Undo(last));
            ShowProblems(report);
            StatusText = $"Moved {report.Moved.Count:N0} photo(s) back." +
                         (report.Problems.Count > 0 ? $" {report.Problems.Count:N0} could not be moved back; see Problems." : string.Empty);
        }
        finally
        {
            IsBusy = false;
        }

        await LoadTreeAsync(SelectedFolder?.Path, keepStatus: true);
    }

    private bool CanUndo() => !IsBusy && _lastMove is not null;

    private bool IsIdle() => !IsBusy;

    // ---- Loading ---------------------------------------------------------------------------

    private async Task LoadTreeAsync(string? selectPath, bool keepStatus = false)
    {
        if (LibraryFolder is not { } library)
        {
            return;
        }

        IsBusy = true;
        if (!keepStatus)
        {
            StatusText = "Reading the folders…";
        }

        try
        {
            var problems = new List<string>();
            var root = await Task.Run(() => ReadFolder(library, Path.GetFileName(Path.TrimEndingDirectorySeparator(library)) is { Length: > 0 } n ? n : library,
                null, problems));
            Folders.Clear();
            Folders.Add(root);
            root.IsExpanded = true;
            RebuildAllFolders();
            foreach (var problem in problems)
            {
                AddProblem(problem);
            }

            SelectedFolder = (selectPath is null ? null : AllFolders.FirstOrDefault(f => PathUtil.Comparer.Equals(f.Path, selectPath))) ?? root;
            ExpandTo(SelectedFolder);
            if (!keepStatus)
            {
                StatusText = "Select photos, choose a folder under \"Move to\", and click Move. Create new folders with \"New folder\".";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Folders.Clear();
            AllFolders.Clear();
            Photos.Clear();
            StatusText = $"The folder could not be read: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static SortFolderViewModel ReadFolder(string path, string name, SortFolderViewModel? parent, List<string> problems)
    {
        var directory = new DirectoryInfo(path);
        var count = 0;
        var subfolders = new List<DirectoryInfo>();
        try
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.Hidden) ||
                    entry.Attributes.HasFlag(FileAttributes.System))
                {
                    continue;
                }

                if (entry is DirectoryInfo subfolder)
                {
                    subfolders.Add(subfolder);
                }
                else if (MediaFormats.IsSupported(entry.Name))
                {
                    count++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (parent is null)
            {
                throw;
            }

            problems.Add($"{path}: could not be read: {ex.Message}");
        }

        var folder = new SortFolderViewModel(path, name, parent, count);
        foreach (var subfolder in subfolders.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            folder.Children.Add(ReadFolder(subfolder.FullName, subfolder.Name, folder, problems));
        }

        return folder;
    }

    private async Task LoadPhotosAsync(SortFolderViewModel? folder)
    {
        var load = ++_photoLoad;
        Photos.Clear();
        UpdateSelection();
        if (folder is null)
        {
            return;
        }

        try
        {
            var files = await Task.Run(() => new DirectoryInfo(folder.Path).EnumerateFiles()
                .Where(f => f.LinkTarget is null && !f.Attributes.HasFlag(FileAttributes.Hidden) && MediaFormats.IsSupported(f.Name))
                .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(f => f.FullName)
                .ToList());
            if (load != _photoLoad)
            {
                return; // another folder was selected meanwhile
            }

            foreach (var file in files)
            {
                var photo = new SortPhotoViewModel(file, _thumbnails);
                photo.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SortPhotoViewModel.IsSelected))
                    {
                        UpdateSelection();
                    }
                };
                Photos.Add(photo);
            }

            folder.PhotoCount = files.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"The photos in {folder.Path} could not be read: {ex.Message}";
        }

        UpdateSelection();
    }

    private void UpdateSelection()
    {
        HasPhotos = Photos.Count > 0;
        var selected = SelectedCount;
        SelectionText = Photos.Count == 0
            ? SelectedFolder is null ? string.Empty : "No photos or videos in this folder."
            : $"{selected:N0} of {Photos.Count:N0} selected";
        MoveCommand.NotifyCanExecuteChanged();
    }

    private void ShowProblems(MoveReport report)
    {
        foreach (var problem in report.Problems)
        {
            AddProblem($"{problem.Path}: {problem.Reason}");
        }
    }

    private void AddProblem(string problem)
    {
        Problems.Add(problem);
        OnPropertyChanged(nameof(HasProblems));
    }

    private void RebuildAllFolders()
    {
        var target = MoveTarget;
        AllFolders.Clear();
        foreach (var root in Folders)
        {
            AddWithChildren(root);
        }

        MoveTarget = target is not null && AllFolders.Contains(target) ? target : null;

        void AddWithChildren(SortFolderViewModel folder)
        {
            AllFolders.Add(folder);
            foreach (var child in folder.Children)
            {
                AddWithChildren(child);
            }
        }
    }

    private static void ExpandTo(SortFolderViewModel? folder)
    {
        for (var f = folder?.Parent; f is not null; f = f.Parent)
        {
            f.IsExpanded = true;
        }
    }

    private static void InsertSorted(ObservableCollection<SortFolderViewModel> list, SortFolderViewModel folder)
    {
        var index = 0;
        while (index < list.Count && StringComparer.CurrentCultureIgnoreCase.Compare(list[index].Name, folder.Name) < 0)
        {
            index++;
        }

        list.Insert(index, folder);
    }
}

/// <summary>A folder in the photo library.</summary>
public sealed partial class SortFolderViewModel(string path, string name, SortFolderViewModel? parent, int photoCount)
    : ObservableObject
{
    public string Path { get; } = path;

    public string Name { get; } = name;

    public SortFolderViewModel? Parent { get; } = parent;

    public ObservableCollection<SortFolderViewModel> Children { get; } = [];

    /// <summary>Photos and videos directly in this folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    public partial int PhotoCount { get; set; } = photoCount;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public string Label => PhotoCount > 0 ? $"{Name} ({PhotoCount:N0})" : Name;

    /// <summary>The path from the library folder, e.g. "Pictures/Trips/Rome".</summary>
    public string DisplayPath => Parent is null ? Name : $"{Parent.DisplayPath}/{Name}";

    public override string ToString() => DisplayPath;
}

/// <summary>A photo or video in the selected folder. The preview loads when it is first shown.</summary>
public sealed partial class SortPhotoViewModel(string path, ThumbnailLoader thumbnails) : ObservableObject
{
    private bool _loadStarted;

    public string Path { get; } = path;

    public string FileName { get; } = System.IO.Path.GetFileName(path);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public Bitmap? Thumbnail
    {
        get
        {
            if (!_loadStarted)
            {
                _loadStarted = true;
                _ = LoadThumbnailAsync();
            }

            return field;
        }

        private set => SetProperty(ref field, value);
    }

    [ObservableProperty]
    public partial bool HasThumbnail { get; private set; }

    [ObservableProperty]
    public partial string ThumbnailStatus { get; private set; } = "Loading…";

    [RelayCommand]
    private void Toggle() => IsSelected = !IsSelected;

    private async Task LoadThumbnailAsync()
    {
        var result = MediaFormats.IsVideo(Path)
            ? new ThumbnailResult(null, "Video")
            : await thumbnails.LoadFileAsync(Path);
        ThumbnailStatus = result.Bitmap is null ? result.Problem ?? "No preview" : string.Empty;
        HasThumbnail = result.Bitmap is not null;
        Thumbnail = result.Bitmap;
    }
}
