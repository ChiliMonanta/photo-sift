using PhotoSift.Core.Import;
using PhotoSift.Desktop.Services;
using PhotoSift.Desktop.ViewModels;

namespace PhotoSift.Tests;

public sealed class ImportViewModelTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly ImportJournal _journal;
    private readonly ImportViewModel _vm;
    private readonly FakePicker _picker = new();

    public ImportViewModelTests()
    {
        _temp.WriteImage("Phone/DCIM/Camera/IMG_1.jpg", seed: 1);
        _temp.WriteBytes("Phone/DCIM/Camera/VID_1.mp4", [1, 2, 3, 4]);
        _journal = new ImportJournal(_temp.PathOf("journal.db"));
        _vm = new ImportViewModel(new PhotoImporter(_journal, freeSpaceOf: _ => null), _journal, new FakePhones(_temp.PathOf("Phone")), _picker);
        _picker.Next = [_temp.PathOf("Pictures")];
        _vm.ChangeDestinationCommand.Execute(null);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _journal.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task A_found_phone_is_selected_and_copied_and_a_second_run_finds_nothing_new()
    {
        await _vm.RefreshPhonesCommand.ExecuteAsync(null);
        Assert.Equal("Pixel 8 (Google Pixel 8)", _vm.SelectedSource?.Label);

        await _vm.CheckCommand.ExecuteAsync(null);
        Assert.StartsWith("2 new files (", _vm.SummaryText);
        Assert.EndsWith(" · 0 already imported", _vm.SummaryText);

        await _vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("Import finished: 2 copied.", _vm.StatusText);
        Assert.True(File.Exists(_temp.PathOf("Pictures/Pixel 8/DCIM/Camera/IMG_1.jpg")));
        Assert.True(File.Exists(_temp.PathOf("Pictures/Pixel 8/DCIM/Camera/VID_1.mp4")));

        await _vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("Everything on this source is already imported.", _vm.StatusText);
        Assert.StartsWith("0 new files", _vm.SummaryText);
    }

    [Fact]
    public void The_destination_is_remembered()
    {
        using var again = new ImportViewModel(new PhotoImporter(_journal), _journal, new FakePhones(_temp.Root), _picker);

        Assert.Equal(_temp.PathOf("Pictures"), again.DestinationFolder);
    }

    [Fact]
    public async Task Folders_can_be_imported_from()
    {
        _picker.Next = [_temp.PathOf("Phone")];
        await _vm.AddFolderSourceCommand.ExecuteAsync(null);

        await _vm.StartCommand.ExecuteAsync(null);

        Assert.Equal($"Folder: {_temp.PathOf("Phone")}", _vm.SelectedSource?.Label);
        Assert.True(File.Exists(_temp.PathOf("Pictures/Phone/DCIM/Camera/IMG_1.jpg")));
    }

    private sealed class FakePhones(string folder) : IPhoneProvider
    {
        public string? UnavailableReason => null;

        public Task<IReadOnlyList<PhoneInfo>> GetPhonesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PhoneInfo>>([new PhoneInfo("usb#1", "Pixel 8", "Google Pixel 8")]);

        public Task<IImportSource> OpenAsync(PhoneInfo phone, CancellationToken cancellationToken) =>
            Task.FromResult<IImportSource>(new NamedSource(new FolderImportSource(folder), phone.Name));
    }

    /// <summary>A folder that presents itself as a named phone.</summary>
    private sealed class NamedSource(FolderImportSource inner, string name) : IImportSource
    {
        public string Id => "phone:" + name;

        public string DisplayName => name;

        public IAsyncEnumerable<ImportItem> EnumerateAsync(IProgress<int>? found, CancellationToken cancellationToken) =>
            inner.EnumerateAsync(found, cancellationToken);

        public Task<Stream> OpenReadAsync(ImportItem item, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(item, cancellationToken);

        public void Dispose() => inner.Dispose();
    }

    private sealed class FakePicker : IFolderPicker
    {
        public IReadOnlyList<string> Next { get; set; } = [];

        public Task<FolderPickResult> PickFoldersAsync(string title, bool allowMultiple) =>
            Task.FromResult(new FolderPickResult(Next, 0));
    }
}
