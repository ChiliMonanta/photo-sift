using System.Runtime.CompilerServices;
using PhotoSift.Core.Caching;
using PhotoSift.Core.Hashing;
using PhotoSift.Core.Import;

namespace PhotoSift.Tests;

public sealed class PhotoImportTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly ImportJournal _journal;
    private readonly FakePhone _phone = new();

    public PhotoImportTests() => _journal = new ImportJournal(_temp.PathOf("journal/import.db"));

    public void Dispose()
    {
        _journal.Dispose();
        _temp.Dispose();
    }

    private string Destination => _temp.PathOf("Pictures");

    private string CopyOf(string relativePath) =>
        Path.Combine(Destination, "Pixel 8", Path.Combine(relativePath.Split('/')));

    private PhotoImporter Importer(IHashCache? cache = null, long? freeSpace = null) =>
        new(_journal, cache, _ => freeSpace);

    private async Task<(ImportPlan Plan, ImportReport Report)> ImportAsync(
        PhotoImporter? importer = null, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        importer ??= Importer();
        var plan = await importer.PlanAsync(_phone, Destination, cancellationToken: CancellationToken.None);
        var report = await importer.RunAsync(plan, progress, cancellationToken);
        return (plan, report);
    }

    private static string[] PartialFiles(string folder) =>
        Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*" + PhotoImporter.PartialSuffix, SearchOption.AllDirectories)
            : [];

    [Fact]
    public async Task Photos_and_videos_are_copied_with_the_phones_folders_and_dates()
    {
        var photo = _phone.Add("Internal shared storage/DCIM/Camera/IMG_1.jpg", seed: 1, modified: new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        var video = _phone.Add("Internal shared storage/DCIM/Camera/VID_1.mp4", seed: 2);
        _phone.Add("Internal shared storage/DCIM/.thumbnails/notes.txt", seed: 3);

        var (plan, report) = await ImportAsync();

        Assert.Equal(2, plan.ToCopy.Count);
        Assert.Equal(2, report.CopiedCount);
        Assert.Equal(_phone.Bytes(photo), File.ReadAllBytes(CopyOf(photo)));
        Assert.Equal(_phone.Bytes(video), File.ReadAllBytes(CopyOf(video)));
        Assert.Equal(new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(CopyOf(photo)));
        Assert.False(File.Exists(CopyOf("Internal shared storage/DCIM/.thumbnails/notes.txt")));
        Assert.Empty(PartialFiles(Destination));
    }

    [Fact]
    public async Task Files_imported_before_are_skipped_without_reading_them_again()
    {
        _phone.Add("DCIM/IMG_1.jpg", seed: 1);
        _phone.Add("DCIM/IMG_2.jpg", seed: 2);
        await ImportAsync();
        _phone.Add("DCIM/IMG_3.jpg", seed: 3);
        _phone.Opened.Clear();

        var (plan, report) = await ImportAsync();

        Assert.Equal(2, plan.AlreadyImported);
        Assert.Equal(["DCIM/IMG_3.jpg"], plan.ToCopy.Select(i => i.RelativePath));
        Assert.Equal(["DCIM/IMG_3.jpg"], _phone.Opened);
        Assert.Equal(1, report.CopiedCount);
    }

    [Fact]
    public async Task Files_the_phone_moves_to_another_folder_are_not_imported_again()
    {
        var photo = _phone.Add("Internal Storage/202505_a/IMG_0001.HEIC", seed: 1);
        await ImportAsync();
        _phone.Move(photo, "Internal Storage/202505_b/IMG_0001.HEIC"); // iOS regrouped its month folders
        _phone.Add("Internal Storage/202505_b/IMG_0001.JPG", seed: 2);   // same number, different file

        var (plan, _) = await ImportAsync();

        Assert.Equal(1, plan.AlreadyImported);
        Assert.Equal(["Internal Storage/202505_b/IMG_0001.JPG"], plan.ToCopy.Select(i => i.RelativePath));
    }

    [Fact]
    public async Task Imported_photos_that_were_moved_away_are_not_imported_again()
    {
        var photo = _phone.Add("DCIM/IMG_1.jpg", seed: 1);
        await ImportAsync();
        File.Delete(CopyOf(photo));

        var (plan, _) = await ImportAsync();

        Assert.Equal(1, plan.AlreadyImported);
        Assert.Empty(plan.ToCopy);
    }

    [Fact]
    public async Task A_canceled_copy_leaves_no_broken_file_and_continues_where_it_stopped()
    {
        var first = _phone.Add("DCIM/IMG_1.jpg", seed: 1);
        var second = _phone.Add("DCIM/IMG_2.jpg", seed: 2, length: 3 * 1024 * 1024);
        var third = _phone.Add("DCIM/IMG_3.jpg", seed: 3);
        using var cts = new CancellationTokenSource();
        _phone.CancelAfterBytes(second, 1024 * 1024, cts);

        var (_, report) = await ImportAsync(cancellationToken: cts.Token);

        Assert.True(report.WasCanceled);
        Assert.Equal(1, report.CopiedCount);
        Assert.Equal(2, report.NotStarted);
        Assert.True(File.Exists(CopyOf(first)));
        Assert.False(File.Exists(CopyOf(second)));
        Assert.Empty(PartialFiles(Destination));

        _phone.Opened.Clear();
        var (plan, resumed) = await ImportAsync();

        Assert.Equal(1, plan.AlreadyImported);
        Assert.Equal([second, third], _phone.Opened);
        Assert.Equal(2, resumed.CopiedCount);
        Assert.Equal(_phone.Bytes(second), File.ReadAllBytes(CopyOf(second)));
    }

    [Fact]
    public async Task After_a_crash_the_interrupted_file_is_found_its_partial_copy_removed_and_copied_again()
    {
        var photo = _phone.Add("DCIM/IMG_1.jpg", seed: 1);
        var item = new ImportItem(photo, photo, _phone.Bytes(photo).Length, null);
        // What a crash in the middle of a copy leaves behind: a "copying" entry and a partial file.
        var partial = CopyOf(photo) + PhotoImporter.PartialSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        File.WriteAllBytes(partial, _phone.Bytes(photo)[..100]);
        _journal.MarkCopying(_phone.Id, item, partial);

        var (plan, report) = await ImportAsync();

        Assert.Equal([photo], plan.Interrupted);
        Assert.Equal(1, report.CopiedCount);
        Assert.Equal(_phone.Bytes(photo), File.ReadAllBytes(CopyOf(photo)));
        Assert.Empty(PartialFiles(Destination));
    }

    [Fact]
    public async Task A_crash_after_renaming_but_before_recording_does_not_create_a_second_copy()
    {
        var photo = _phone.Add("DCIM/IMG_1.jpg", seed: 1);
        await ImportAsync();
        // Simulate the crash window: the journal still says "copying", the complete file has its real name.
        _journal.MarkCopying(_phone.Id, new ImportItem(photo, photo, _phone.Bytes(photo).Length, null), CopyOf(photo) + PhotoImporter.PartialSuffix);

        var (plan, report) = await ImportAsync();

        Assert.Equal([photo], plan.Interrupted);
        Assert.Equal(ImportStatus.AlreadyThere, Assert.Single(report.Outcomes).Status);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(CopyOf(photo))!));
    }

    [Fact]
    public async Task A_file_that_arrives_shorter_than_reported_is_discarded_and_reported()
    {
        var photo = _phone.Add("DCIM/IMG_1.jpg", seed: 1);
        _phone.Truncate(photo, 1000);

        var (_, report) = await ImportAsync();

        var outcome = Assert.Single(report.Outcomes);
        Assert.Equal(ImportStatus.Failed, outcome.Status);
        Assert.Contains("Keep Originals", outcome.Reason);
        Assert.False(File.Exists(CopyOf(photo)));
        Assert.Empty(PartialFiles(Destination));

        var (plan, _) = await ImportAsync();
        Assert.Single(plan.ToCopy); // tried again next time
    }

    [Fact]
    public async Task A_phone_that_stops_responding_stops_the_import_and_the_rest_is_copied_next_time()
    {
        var paths = Enumerable.Range(1, 6).Select(i => _phone.Add($"DCIM/IMG_{i}.jpg", seed: i)).ToList();
        _phone.FailFrom(paths[1]); // disconnected after the first file

        var (_, report) = await ImportAsync();

        Assert.Equal(1, report.CopiedCount);
        Assert.Equal(PhotoImporter.MaxFailuresInARow, report.FailedCount);
        Assert.Equal(2, report.NotStarted);
        Assert.Contains("connected and unlocked", report.StopReason);

        _phone.FailFrom(null);
        var (plan, resumed) = await ImportAsync();
        Assert.Equal(5, plan.ToCopy.Count);
        Assert.Equal(5, resumed.CopiedCount);
    }

    [Fact]
    public async Task A_different_file_with_the_same_name_gets_a_new_name_and_an_identical_one_is_not_copied_twice()
    {
        var different = _phone.Add("DCIM/IMG_1.jpg", seed: 1);
        var identical = _phone.Add("DCIM/IMG_2.jpg", seed: 2);
        _temp.WriteBytes(Path.GetRelativePath(_temp.Root, CopyOf(different)), [1, 2, 3]);
        _temp.WriteBytes(Path.GetRelativePath(_temp.Root, CopyOf(identical)), _phone.Bytes(identical));

        var (_, report) = await ImportAsync();

        Assert.Equal([1, 2, 3], File.ReadAllBytes(CopyOf(different)));
        var renamed = Path.Combine(Path.GetDirectoryName(CopyOf(different))!, "IMG_1 (2).jpg");
        Assert.Equal(_phone.Bytes(different), File.ReadAllBytes(renamed));
        Assert.Equal(ImportStatus.AlreadyThere, report.Outcomes.Single(o => o.Item.RelativePath == identical).Status);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(CopyOf(identical))!, "IMG_2*"));
    }

    [Fact]
    public async Task The_import_does_not_start_without_enough_free_space()
    {
        _phone.Add("DCIM/IMG_1.jpg", seed: 1);

        var plan = await Importer(freeSpace: 10_000).PlanAsync(_phone, Destination);

        Assert.Contains("Not enough free space", plan.Problem);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Importer().RunAsync(plan));
    }

    [Fact]
    public async Task Progress_counts_down_the_files_left()
    {
        for (var i = 1; i <= 3; i++)
        {
            _phone.Add($"DCIM/IMG_{i}.jpg", seed: i);
        }

        var reports = new List<ImportProgress>();
        await ImportAsync(progress: new SynchronousProgress<ImportProgress>(reports.Add));

        Assert.Equal(3, reports[0].FilesLeft);
        Assert.Equal("DCIM/IMG_1.jpg", reports[0].CurrentFile);
        Assert.Equal(0, reports[^1].FilesLeft);
        Assert.Equal(reports[^1].TotalBytes, reports[^1].CopiedBytes);
        Assert.Equal(reports.Select(r => r.FilesLeft).OrderByDescending(n => n), reports.Select(r => r.FilesLeft));
    }

    [Fact]
    public async Task Imported_files_are_added_to_the_hash_cache_for_the_next_duplicate_scan()
    {
        var photo = _phone.Add("DCIM/IMG_1.jpg", seed: 1);
        var cache = new InMemoryHashCache();

        await ImportAsync(Importer(cache));

        var copy = new FileInfo(CopyOf(photo));
        Assert.Equal(await new Md5FileHasher().ComputeHashAsync(copy.FullName), cache.TryGet(copy.FullName, copy.Length, copy.LastWriteTimeUtc));
    }

    [Theory]
    [InlineData("DCIM/../../Windows/evil.jpg", new[] { "DCIM", "_", "_", "Windows", "evil.jpg" })]
    [InlineData("DCIM/a<b>:c.jpg", new[] { "DCIM", "a_b__c.jpg" })]
    [InlineData("DCIM/name. ", new[] { "DCIM", "name" })]
    public void Destination_paths_are_safe_and_stay_inside_the_destination(string relativePath, string[] expected)
    {
        var root = _temp.PathOf("dest");
        var path = PhotoImporter.DestinationPathFor(root, relativePath);

        Assert.Equal(Path.Combine([root, .. expected]), path);
        Assert.StartsWith(root + Path.DirectorySeparatorChar, Path.GetFullPath(path));
    }

    [Fact]
    public async Task Folders_can_be_imported_but_not_into_themselves()
    {
        var card = _temp.PathOf("Card");
        _temp.WriteImage("Card/DCIM/100CANON/IMG_1.jpg", seed: 1);
        _temp.WriteBytes("Card/DCIM/100CANON/MVI_1.mov", [1, 2, 3]);
        _temp.WriteBytes("Card/MISC/settings.dat", [4]);
        using var source = new FolderImportSource(card);

        var plan = await Importer().PlanAsync(source, Destination);
        var inside = await Importer().PlanAsync(source, Path.Combine(card, "Copies"));

        Assert.Equal(["DCIM/100CANON/IMG_1.jpg", "DCIM/100CANON/MVI_1.mov"], plan.ToCopy.Select(i => i.RelativePath).Order());
        Assert.Null(plan.Problem);
        Assert.Contains("outside the folder", inside.Problem);
    }

    // ---- Test doubles -----------------------------------------------------------------

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class InMemoryHashCache : IHashCache
    {
        private readonly Dictionary<string, (long, DateTime, string)> _entries = [];

        public string? TryGet(string path, long size, DateTime lastWriteUtc) =>
            _entries.TryGetValue(path, out var e) && e.Item1 == size && e.Item2 == lastWriteUtc ? e.Item3 : null;

        public void Set(string path, long size, DateTime lastWriteUtc, string hash) => _entries[path] = (size, lastWriteUtc, hash);
    }

    /// <summary>An in-memory phone that can cancel, truncate, or stop responding at chosen files.</summary>
    private sealed class FakePhone : IImportSource
    {
        private readonly Dictionary<string, (byte[] Bytes, DateTime? Modified)> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _truncateTo = new(StringComparer.Ordinal);
        private (string Path, int Bytes, CancellationTokenSource Cts)? _cancel;
        private string? _failFrom;

        public string Id => "phone:Google|Pixel 8|ABC123";

        public string DisplayName => "Pixel 8";

        public List<string> Opened { get; } = [];

        public string Add(string relativePath, int seed, int length = 4096, DateTime? modified = null)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            _files[relativePath] = (bytes, modified);
            return relativePath;
        }

        public byte[] Bytes(string path) => _files[path].Bytes;

        public void Move(string from, string to)
        {
            _files[to] = _files[from];
            _files.Remove(from);
        }

        public void Truncate(string path, int bytes) => _truncateTo[path] = bytes;

        public void CancelAfterBytes(string path, int bytes, CancellationTokenSource cts) => _cancel = (path, bytes, cts);

        /// <summary>Every file from <paramref name="path"/> on (in order) fails to open; null to stop failing.</summary>
        public void FailFrom(string? path) => _failFrom = path;

        public async IAsyncEnumerable<ImportItem> EnumerateAsync(IProgress<int>? found, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            var count = 0;
            foreach (var (path, file) in _files)
            {
                found?.Report(++count);
                yield return new ImportItem(path, path, file.Bytes.Length, file.Modified);
            }
        }

        public Task<Stream> OpenReadAsync(ImportItem item, CancellationToken cancellationToken)
        {
            Opened.Add(item.RelativePath);
            if (_failFrom is not null && string.CompareOrdinal(item.RelativePath, _failFrom) >= 0)
            {
                throw new IOException("The device is not responding (simulated).");
            }

            var bytes = _files[item.Key].Bytes;
            if (_truncateTo.TryGetValue(item.Key, out var length))
            {
                bytes = bytes[..length];
            }

            Stream stream = new MemoryStream(bytes);
            if (_cancel is { } cancel && cancel.Path == item.Key)
            {
                stream = new CancelingStream(stream, cancel.Bytes, cancel.Cts);
            }

            return Task.FromResult(stream);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Requests cancellation once <c>limit</c> bytes were read, like a user clicking Cancel mid-file.</summary>
    private sealed class CancelingStream(Stream inner, int limit, CancellationTokenSource cts) : Stream
    {
        private int _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read >= limit)
            {
                await cts.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }

            var n = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, 64 * 1024)], cancellationToken);
            _read += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
