using PhotoSift.Core.Caching;
using PhotoSift.Core.Sorting;
using PhotoSift.Desktop.Services;
using PhotoSift.Desktop.ViewModels;

namespace PhotoSift.Tests;

public sealed class PhotoSorterTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly PhotoSorter _sorter = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("Rome 2025", null)]
    [InlineData("", "Enter a folder name.")]
    [InlineData("a/b", "cannot contain")]
    [InlineData("a:b", "cannot contain")]
    [InlineData(" Rome", "cannot start or end")]
    [InlineData("Rome.", "cannot start or end")]
    [InlineData("..", "cannot start or end")]
    [InlineData("CON", "reserved")]
    [InlineData("nul.txt", "reserved")]
    public void Folder_names_are_checked(string name, string? problem)
    {
        var result = PhotoSorter.ValidateFolderName(name);

        if (problem is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Contains(problem, result);
        }
    }

    [Fact]
    public void Folders_are_created_but_never_replaced()
    {
        var created = _sorter.CreateFolder(_temp.Root, "Rome");

        Assert.True(Directory.Exists(created));
        Assert.Throws<IOException>(() => _sorter.CreateFolder(_temp.Root, "Rome"));
        Assert.Throws<ArgumentException>(() => _sorter.CreateFolder(_temp.Root, "../escape"));
    }

    [Fact]
    public void Moving_never_overwrites_and_can_be_undone()
    {
        var a = _temp.WriteImage("Inbox/IMG_1.jpg", seed: 1);
        var b = _temp.WriteImage("Inbox/IMG_2.jpg", seed: 2);
        var existing = _temp.WriteImage("Rome/IMG_1.jpg", seed: 3);
        var existingBytes = File.ReadAllBytes(existing);
        var aBytes = File.ReadAllBytes(a);

        var report = _sorter.Move([a, b], _temp.PathOf("Rome"));

        Assert.Empty(report.Problems);
        Assert.Equal(
            [new MovedFile(a, _temp.PathOf("Rome/IMG_1 (2).jpg")), new MovedFile(b, _temp.PathOf("Rome/IMG_2.jpg"))],
            report.Moved);
        Assert.Equal(existingBytes, File.ReadAllBytes(existing));
        Assert.Equal(aBytes, File.ReadAllBytes(_temp.PathOf("Rome/IMG_1 (2).jpg")));
        Assert.False(File.Exists(a));

        var undo = _sorter.Undo(report);

        Assert.Empty(undo.Problems);
        Assert.Equal(aBytes, File.ReadAllBytes(a));
        Assert.True(File.Exists(b));
        Assert.Equal(existingBytes, File.ReadAllBytes(existing));
        Assert.False(File.Exists(_temp.PathOf("Rome/IMG_1 (2).jpg")));
    }

    [Fact]
    public void Undo_does_not_replace_a_file_that_took_the_old_place()
    {
        var a = _temp.WriteImage("Inbox/IMG_1.jpg", seed: 1);
        var report = _sorter.Move([a], _temp.PathOf("Rome").Also(Directory.CreateDirectory));
        var newcomer = _temp.WriteImage("Inbox/IMG_1.jpg", seed: 9);
        var newcomerBytes = File.ReadAllBytes(newcomer);

        var undo = _sorter.Undo(report);

        Assert.Contains("taken", Assert.Single(undo.Problems).Reason);
        Assert.Equal(newcomerBytes, File.ReadAllBytes(newcomer));
        Assert.True(File.Exists(_temp.PathOf("Rome/IMG_1.jpg")));
    }

    [Fact]
    public void Missing_files_and_files_already_in_the_folder_are_reported()
    {
        var here = _temp.WriteImage("Rome/a.jpg", seed: 1);

        var report = _sorter.Move([here, _temp.PathOf("Inbox/gone.jpg")], _temp.PathOf("Rome"));

        Assert.Empty(report.Moved);
        Assert.Equal(["The file is already in that folder.", "The file is no longer there."], report.Problems.Select(p => p.Reason));
        Assert.True(File.Exists(here));
    }

    [Fact]
    public void Folders_can_be_renamed_but_not_onto_another_folder()
    {
        _temp.WriteImage("Trips/Rome/a.jpg", seed: 1);
        Directory.CreateDirectory(_temp.PathOf("Trips/Paris"));

        var renamed = _sorter.RenameFolder(_temp.PathOf("Trips/Rome"), "Rome 2025");

        Assert.Equal(_temp.PathOf("Trips/Rome 2025"), renamed);
        Assert.True(File.Exists(_temp.PathOf("Trips/Rome 2025/a.jpg")));
        Assert.Throws<IOException>(() => _sorter.RenameFolder(renamed, "Paris"));
        Assert.Throws<ArgumentException>(() => _sorter.RenameFolder(renamed, "a/b"));
        Assert.True(Directory.Exists(renamed));
    }

    [Fact]
    public void Changing_only_the_case_of_a_folder_name_works()
    {
        _temp.WriteImage("rome/a.jpg", seed: 1);

        var renamed = _sorter.RenameFolder(_temp.PathOf("rome"), "Rome");

        Assert.Equal("Rome", new DirectoryInfo(_temp.Root).GetDirectories().Single().Name);
        Assert.True(File.Exists(Path.Combine(renamed, "a.jpg")));
    }

    [Fact]
    public async Task Folders_are_deleted_by_moving_them_to_the_trash()
    {
        _temp.WriteImage("Old/a.jpg", seed: 1);
        _temp.WriteBytes("Old/sub/Thumbs.db", [1]);
        var trash = new FakeTrashService();

        Assert.Equal(new FolderContents(1, 1, 1), PhotoSorter.CountContents(_temp.PathOf("Old")));
        await new PhotoSorter(trash: trash).MoveFolderToTrashAsync(_temp.PathOf("Old"));

        Assert.Equal([_temp.PathOf("Old")], trash.Trashed);
        Assert.False(Directory.Exists(_temp.PathOf("Old")));
    }

    [Fact]
    public async Task A_folder_that_could_not_be_trashed_is_reported()
    {
        Directory.CreateDirectory(_temp.PathOf("Old"));
        var trash = new FakeTrashService();
        trash.FailFor.Add(_temp.PathOf("Old"));

        await Assert.ThrowsAsync<IOException>(() => new PhotoSorter(trash: trash).MoveFolderToTrashAsync(_temp.PathOf("Old")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sorter.MoveFolderToTrashAsync(_temp.PathOf("Old")));
        Assert.True(Directory.Exists(_temp.PathOf("Old")));
    }

    [Fact]
    public void Cached_hashes_follow_moved_files()
    {
        var cache = new MemoryHashCache();
        var a = _temp.WriteImage("Inbox/a.jpg", seed: 1);
        var info = new FileInfo(a);
        cache.Set(a, info.Length, info.LastWriteTimeUtc, "hash-a");

        new PhotoSorter(cache).Move([a], _temp.PathOf("Rome").Also(Directory.CreateDirectory));

        Assert.Equal("hash-a", cache.TryGet(_temp.PathOf("Rome/a.jpg"), info.Length, info.LastWriteTimeUtc));
    }

    private sealed class MemoryHashCache : IHashCache
    {
        private readonly Dictionary<string, (long, DateTime, string)> _entries = [];

        public string? TryGet(string path, long size, DateTime lastWriteUtc) =>
            _entries.TryGetValue(path, out var e) && e.Item1 == size && e.Item2 == lastWriteUtc ? e.Item3 : null;

        public void Set(string path, long size, DateTime lastWriteUtc, string hash) => _entries[path] = (size, lastWriteUtc, hash);
    }
}

public sealed class SortViewModelTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Picker _picker = new();
    private readonly Settings _settings = new();

    public SortViewModelTests()
    {
        _temp.WriteImage("Photos/Inbox/IMG_1.jpg", seed: 1);
        _temp.WriteImage("Photos/Inbox/IMG_2.jpg", seed: 2);
        _temp.WriteBytes("Photos/Inbox/VID_1.mp4", [1, 2, 3]);
        _temp.WriteBytes("Photos/Inbox/notes.txt", [1]);
        Directory.CreateDirectory(_temp.PathOf("Photos/Trips"));
    }

    public void Dispose() => _temp.Dispose();

    private async Task<SortViewModel> OpenAsync()
    {
        var vm = new SortViewModel(new PhotoSorter(), _picker, new ThumbnailLoader(), _settings);
        _picker.Next = [_temp.PathOf("Photos")];
        await vm.ChangeLibraryCommand.ExecuteAsync(null);
        return vm;
    }

    private static SortFolderViewModel Folder(SortViewModel vm, string displayPath) =>
        vm.AllFolders.Single(f => f.DisplayPath == displayPath);

    private static async Task SelectFolderAsync(SortViewModel vm, SortFolderViewModel folder)
    {
        vm.SelectedFolder = folder;
        for (var i = 0; i < 100 && vm.Photos.Count != folder.PhotoCount; i++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task The_library_shows_its_folders_with_photo_counts()
    {
        var vm = await OpenAsync();

        Assert.Equal(["Photos", "Photos/Inbox", "Photos/Trips"], vm.AllFolders.Select(f => f.DisplayPath));
        Assert.Equal("Inbox (3)", Folder(vm, "Photos/Inbox").Label);
        Assert.Equal(_temp.PathOf("Photos"), _settings.Values["sort.library"]);
    }

    [Fact]
    public async Task Photos_can_be_moved_into_a_new_folder_and_back()
    {
        var vm = await OpenAsync();
        await SelectFolderAsync(vm, Folder(vm, "Photos/Inbox"));
        Assert.Equal(["IMG_1.jpg", "IMG_2.jpg", "VID_1.mp4"], vm.Photos.Select(p => p.FileName));

        vm.SelectedFolder = Folder(vm, "Photos/Trips");
        vm.NewFolderName = "Rome";
        vm.CreateFolderCommand.Execute(null);
        var rome = Folder(vm, "Photos/Trips/Rome");
        Assert.True(Directory.Exists(rome.Path));
        Assert.Same(rome, vm.MoveTarget);

        await SelectFolderAsync(vm, Folder(vm, "Photos/Inbox"));
        vm.Photos[0].IsSelected = true;
        vm.Photos[2].IsSelected = true;
        Assert.Equal("2 of 3 selected", vm.SelectionText);
        Assert.True(vm.MoveCommand.CanExecute(null));

        await vm.MoveCommand.ExecuteAsync(null);

        Assert.True(File.Exists(_temp.PathOf("Photos/Trips/Rome/IMG_1.jpg")));
        Assert.True(File.Exists(_temp.PathOf("Photos/Trips/Rome/VID_1.mp4")));
        Assert.Equal(["IMG_2.jpg"], vm.Photos.Select(p => p.FileName));
        Assert.Equal(2, rome.PhotoCount);
        Assert.Equal("Moved 2 photo(s) to \"Photos/Trips/Rome\".", vm.StatusText);

        await vm.UndoCommand.ExecuteAsync(null);

        Assert.True(File.Exists(_temp.PathOf("Photos/Inbox/IMG_1.jpg")));
        Assert.False(File.Exists(_temp.PathOf("Photos/Trips/Rome/IMG_1.jpg")));
        Assert.Equal("Moved 2 photo(s) back.", vm.StatusText);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task Invalid_or_existing_folder_names_are_explained()
    {
        var vm = await OpenAsync();

        vm.NewFolderName = "a?b";
        vm.CreateFolderCommand.Execute(null);
        Assert.Contains("cannot contain", vm.StatusText);

        vm.NewFolderName = "Trips";
        vm.CreateFolderCommand.Execute(null);
        Assert.Contains("already exists", vm.StatusText);
    }

    [Fact]
    public async Task Moving_to_the_same_folder_is_not_offered()
    {
        var vm = await OpenAsync();
        var inbox = Folder(vm, "Photos/Inbox");
        await SelectFolderAsync(vm, inbox);
        vm.SelectAllCommand.Execute(null);

        vm.MoveTarget = inbox;

        Assert.False(vm.MoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Dragging_an_unselected_photo_moves_only_it()
    {
        var vm = await OpenAsync();
        await SelectFolderAsync(vm, Folder(vm, "Photos/Inbox"));
        vm.Photos[0].IsSelected = true;

        var dragged = vm.StartDrag(vm.Photos[1]);

        Assert.Equal(["IMG_2.jpg"], dragged.Select(p => p.FileName));
        Assert.Equal([false, true, false], vm.Photos.Select(p => p.IsSelected));
    }

    [Fact]
    public async Task Dragging_a_selected_photo_drops_all_selected_photos_on_the_folder()
    {
        var vm = await OpenAsync();
        var trips = Folder(vm, "Photos/Trips");
        await SelectFolderAsync(vm, Folder(vm, "Photos/Inbox"));
        vm.Photos[0].IsSelected = true;
        vm.Photos[1].IsSelected = true;

        var dragged = vm.StartDrag(vm.Photos[1]);
        vm.ShowDropTarget(trips);
        Assert.True(trips.IsDropTarget);
        await vm.DropAsync(trips, dragged);

        Assert.False(trips.IsDropTarget);
        Assert.True(File.Exists(_temp.PathOf("Photos/Trips/IMG_1.jpg")));
        Assert.True(File.Exists(_temp.PathOf("Photos/Trips/IMG_2.jpg")));
        Assert.Equal(["VID_1.mp4"], vm.Photos.Select(p => p.FileName));
        Assert.True(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task Photos_cannot_be_dropped_on_their_own_folder()
    {
        var vm = await OpenAsync();
        var inbox = Folder(vm, "Photos/Inbox");
        await SelectFolderAsync(vm, inbox);

        var dragged = vm.StartDrag(vm.Photos[0]);
        vm.ShowDropTarget(inbox);
        await vm.DropAsync(inbox, dragged);

        Assert.False(vm.CanDropOn(inbox));
        Assert.False(inbox.IsDropTarget);
        Assert.Equal(3, vm.Photos.Count);
        Assert.True(File.Exists(_temp.PathOf("Photos/Inbox/IMG_1.jpg")));
    }

    [Fact]
    public async Task The_selected_folder_can_be_renamed_but_not_the_photo_folder()
    {
        var vm = await OpenAsync();
        vm.SelectedFolder = vm.Folders[0];
        vm.NewFolderName = "Other";
        Assert.False(vm.RenameFolderCommand.CanExecute(null));

        vm.SelectedFolder = Folder(vm, "Photos/Trips");
        await vm.RenameFolderCommand.ExecuteAsync(null);

        Assert.True(Directory.Exists(_temp.PathOf("Photos/Other")));
        Assert.Equal("Photos/Other", vm.SelectedFolder?.DisplayPath);
        Assert.Equal("Renamed \"Trips\" to \"Other\".", vm.StatusText);
    }

    [Fact]
    public async Task Deleting_a_folder_asks_first_and_moves_it_to_the_trash()
    {
        var trash = new FakeTrashService();
        var vm = new SortViewModel(new PhotoSorter(trash: trash), _picker, new ThumbnailLoader(), _settings);
        _picker.Next = [_temp.PathOf("Photos")];
        await vm.ChangeLibraryCommand.ExecuteAsync(null);
        await SelectFolderAsync(vm, Folder(vm, "Photos/Inbox"));

        await vm.DeleteFolderCommand.ExecuteAsync(null);
        Assert.True(vm.IsConfirmingDelete);
        Assert.Equal(
            "Move \"Photos/Inbox\" to the trash, with 3 photo(s) and video(s), 1 other file(s)? You can restore it from the trash.",
            vm.DeleteConfirmationText);
        vm.CancelDeleteCommand.Execute(null);
        Assert.Empty(trash.Trashed);

        await vm.DeleteFolderCommand.ExecuteAsync(null);
        await vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Equal([_temp.PathOf("Photos/Inbox")], trash.Trashed);
        Assert.Equal(["Photos", "Photos/Trips"], vm.AllFolders.Select(f => f.DisplayPath));
        Assert.Equal("Photos", vm.SelectedFolder?.DisplayPath);
        Assert.Equal("Moved \"Photos/Inbox\" to the trash.", vm.StatusText);
    }

    [Fact]
    public async Task A_folder_that_changed_after_the_question_is_asked_about_again()
    {
        var trash = new FakeTrashService();
        var vm = new SortViewModel(new PhotoSorter(trash: trash), _picker, new ThumbnailLoader(), _settings);
        _picker.Next = [_temp.PathOf("Photos")];
        await vm.ChangeLibraryCommand.ExecuteAsync(null);
        vm.SelectedFolder = Folder(vm, "Photos/Trips");

        await vm.DeleteFolderCommand.ExecuteAsync(null);
        Assert.Equal("Move the empty folder \"Photos/Trips\" to the trash?", vm.DeleteConfirmationText);
        _temp.WriteImage("Photos/Trips/new.jpg", seed: 5);
        await vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Empty(trash.Trashed);
        Assert.True(vm.IsConfirmingDelete);
        Assert.StartsWith("The folder changed. Move \"Photos/Trips\" to the trash, with 1 photo(s)", vm.DeleteConfirmationText);
    }

    [Fact]
    public async Task The_photo_folder_itself_cannot_be_deleted()
    {
        var vm = await OpenAsync();
        vm.SelectedFolder = vm.Folders[0];

        Assert.False(vm.DeleteFolderCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_library_is_remembered()
    {
        await OpenAsync();

        var again = new SortViewModel(new PhotoSorter(), _picker, new ThumbnailLoader(), _settings);
        await again.InitializeAsync();

        Assert.Equal(3, again.AllFolders.Count);
    }

    private sealed class Picker : IFolderPicker
    {
        public IReadOnlyList<string> Next { get; set; } = [];

        public Task<FolderPickResult> PickFoldersAsync(string title, bool allowMultiple) =>
            Task.FromResult(new FolderPickResult(Next, 0));
    }

    private sealed class Settings : PhotoSift.Core.ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? GetSetting(string key) => Values.GetValueOrDefault(key);

        public void SetSetting(string key, string value) => Values[key] = value;
    }
}

internal static class TestExtensions
{
    public static string Also(this string value, Func<string, object> action)
    {
        action(value);
        return value;
    }
}
