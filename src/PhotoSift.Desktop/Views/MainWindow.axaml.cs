using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PhotoSift.Desktop.ViewModels;

namespace PhotoSift.Desktop.Views;

public partial class MainWindow : Window
{
    /// <summary>Photos dragged within PhotoSift; never offered to other apps.</summary>
    private static readonly DataFormat<DraggedPhotos> PhotosFormat = DataFormat.CreateInProcessFormat<DraggedPhotos>("PhotoSift.Photos");

    /// <summary>How far the pointer must move before a press becomes a drag.</summary>
    private const double DragThreshold = 6;

    private PointerPressedEventArgs? _pressed;
    private Point _pressedAt;
    private SortPhotoViewModel? _pressedPhoto;

    public MainWindow()
    {
        InitializeComponent();

        // Tunnel, so the photo tiles (toggle buttons) still see their clicks.
        SortPhotoGrid.AddHandler(PointerPressedEvent, OnPhotoPointerPressed, RoutingStrategies.Tunnel);
        SortPhotoGrid.AddHandler(PointerMovedEvent, OnPhotoPointerMoved, RoutingStrategies.Tunnel);
        SortPhotoGrid.AddHandler(PointerReleasedEvent, (_, _) => _pressed = null, RoutingStrategies.Tunnel);

        SortFolderTree.AddHandler(DragDrop.DragOverEvent, OnFolderDragOver);
        SortFolderTree.AddHandler(DragDrop.DragLeaveEvent, OnFolderDragLeave);
        SortFolderTree.AddHandler(DragDrop.DropEvent, OnFolderDrop);
    }

    private SortViewModel? Sort => (DataContext as MainWindowViewModel)?.Sort;

    private void OnDriveFolderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DriveBrowserViewModel browser && browser.OpenCommand.CanExecute(null))
        {
            browser.OpenCommand.Execute(null);
        }
    }

    private void OnPhotoPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = null;
        if (e.GetCurrentPoint(SortPhotoGrid).Properties.IsLeftButtonPressed &&
            (e.Source as Control)?.DataContext is SortPhotoViewModel photo)
        {
            _pressed = e;
            _pressedAt = e.GetPosition(SortPhotoGrid);
            _pressedPhoto = photo;
        }
    }

    private async void OnPhotoPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } pressed || _pressedPhoto is not { } photo || Sort is not { } sort)
        {
            return;
        }

        var delta = e.GetPosition(SortPhotoGrid) - _pressedAt;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
        {
            return;
        }

        _pressed = null;
        var photos = sort.StartDrag(photo);
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(PhotosFormat, new DraggedPhotos(photos)));
        try
        {
            await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Move);
        }
        finally
        {
            sort.ShowDropTarget(null);
        }
    }

    private void OnFolderDragOver(object? sender, DragEventArgs e)
    {
        var target = FolderAt(e);
        var canDrop = e.DataTransfer.Contains(PhotosFormat) && Sort?.CanDropOn(target) == true;
        e.DragEffects = canDrop ? DragDropEffects.Move : DragDropEffects.None;
        Sort?.ShowDropTarget(canDrop ? target : null);
        e.Handled = true;
    }

    private void OnFolderDragLeave(object? sender, DragEventArgs e) => Sort?.ShowDropTarget(null);

    private async void OnFolderDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Sort is { } sort && FolderAt(e) is { } target && e.DataTransfer.TryGetValue(PhotosFormat) is { } dragged)
        {
            e.DragEffects = DragDropEffects.Move;
            await sort.DropAsync(target, dragged.Photos);
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            Sort?.ShowDropTarget(null);
        }
    }

    /// <summary>The folder of the tree item under the pointer.</summary>
    private static SortFolderViewModel? FolderAt(DragEventArgs e) =>
        (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()?.DataContext as SortFolderViewModel;

    private sealed record DraggedPhotos(IReadOnlyList<SortPhotoViewModel> Photos);
}
