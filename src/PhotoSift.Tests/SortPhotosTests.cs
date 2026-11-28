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
