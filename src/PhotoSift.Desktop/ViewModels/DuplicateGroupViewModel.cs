using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoSift.Core.Scanning;
using PhotoSift.Desktop.Services;

namespace PhotoSift.Desktop.ViewModels;

public sealed partial class FileEntryViewModel : ObservableObject
{
    private readonly DuplicateGroupViewModel _group;
    private bool _isMarked;

    internal FileEntryViewModel(ScannedFile file, DuplicateGroupViewModel group)
    {
        File = file;
        _group = group;
    }

    public ScannedFile File { get; }

    public string Path => File.Path;

    public string FileName => File.FileName;

    public string DirectoryPath => File.DirectoryPath;

    /// <summary>
    /// Marks the file for deletion. Marking is refused when it would leave no unmarked
    /// copy in the group: at least one copy must always be kept.
    /// </summary>
    public bool IsMarked
    {
        get => _isMarked;
        set
        {
            if (value == _isMarked)
            {
                return;
            }

            if (value && !_group.HasOtherKeptCopy(this))
            {
                // Push the unchanged value back to the view so the checkbox stays unchecked.
                OnPropertyChanged();
                return;
            }

            SetProperty(ref _isMarked, value);
            _group.OnEntryMarkChanged(value);
        }
    }

    [ObservableProperty]
    public partial bool IsTrashed { get; set; }

    [ObservableProperty]
    public partial bool IsInMasterFolder { get; set; }

    public bool CanMark => !IsTrashed && (IsMarked || _group.HasOtherKeptCopy(this));

    /// <summary>True for the last unmarked copy in a group, which cannot be marked.</summary>
    public bool IsLastKeptCopy => !IsTrashed && !IsMarked && !_group.HasOtherKeptCopy(this);

    internal void RefreshMarkAvailability()
    {
        OnPropertyChanged(nameof(CanMark));
        OnPropertyChanged(nameof(IsLastKeptCopy));
    }

    partial void OnIsTrashedChanged(bool value) => _group.RefreshMarkAvailability();
}

public sealed partial class DuplicateGroupViewModel : ObservableObject
{
    private readonly ThumbnailLoader _thumbnailLoader;
    private readonly Action<bool> _onMarkChanged;
    private Bitmap? _thumbnail;
    private bool _thumbnailRequested;

    public DuplicateGroupViewModel(DuplicateGroup group, ThumbnailLoader thumbnailLoader, Action<bool> onMarkChanged)
    {
        Group = group;
        _thumbnailLoader = thumbnailLoader;
        _onMarkChanged = onMarkChanged;
        Files = group.Files.Select(f => new FileEntryViewModel(f, this)).ToList();
    }

    /// <summary>Whether a copy other than <paramref name="entry"/> is still kept (not marked and not trashed).</summary>
    internal bool HasOtherKeptCopy(FileEntryViewModel entry) =>
        Files.Any(f => !ReferenceEquals(f, entry) && !f.IsMarked && !f.IsTrashed);

    internal void OnEntryMarkChanged(bool marked)
    {
        _onMarkChanged(marked);
        RefreshMarkAvailability();
    }

    internal void RefreshMarkAvailability()
    {
        foreach (var file in Files)
        {
            file.RefreshMarkAvailability();
        }
    }

    public DuplicateGroup Group { get; }

    public IReadOnlyList<FileEntryViewModel> Files { get; }

    public string Header => $"{Files.Count} identical copies · {Formatting.FileSize(Group.Size)} each";

    /// <summary>
    /// Loaded lazily the first time the view binds to it, i.e. when the group is displayed.
    /// One thumbnail is shared by all files in the group.
    /// </summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested)
            {
                _thumbnailRequested = true;
                _ = LoadThumbnailAsync();
            }

            return _thumbnail;
        }
    }

    [ObservableProperty]
    public partial bool HasThumbnail { get; private set; }

    [ObservableProperty]
    public partial string ThumbnailStatus { get; private set; } = "Loading preview…";

    private async Task LoadThumbnailAsync()
    {
        try
        {
            var result = await _thumbnailLoader.LoadAsync(Group.Files.Select(f => f.Path).ToList());
            _thumbnail = result.Bitmap;
            HasThumbnail = result.Bitmap is not null;
            ThumbnailStatus = result.Problem ?? string.Empty;
            OnPropertyChanged(nameof(Thumbnail));
        }
        catch (Exception ex)
        {
            ThumbnailStatus = $"Preview could not be generated: {ex.Message}";
        }
    }
}
