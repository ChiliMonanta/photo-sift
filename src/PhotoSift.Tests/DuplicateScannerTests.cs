using PhotoSift.Core.Caching;
using PhotoSift.Core.Hashing;
using PhotoSift.Core.Scanning;

namespace PhotoSift.Tests;

public class DuplicateScannerTests
{
    private static DuplicateScanner CreateScanner(IFileHasher? hasher = null, IHashCache? cache = null) =>
        new(hasher ?? new Md5FileHasher(), cache ?? NullHashCache.Instance, workerCount: 4);

    [Fact]
    public async Task Groups_identical_files_regardless_of_name_or_folder()
    {
        using var temp = new TempFolder();
        var a = temp.WriteImage("2023/IMG_0001.jpg", seed: 1);
        var b = temp.WriteImage("backup/nested/deep/copy of photo.jpg", seed: 1);
        temp.WriteImage("2023/IMG_0002.jpg", seed: 2);

        var result = await CreateScanner().ScanAsync([temp.Root]);

        var group = Assert.Single(result.Groups);
        Assert.Equal([a, b], group.Files.Select(f => f.Path).Order());
        Assert.Equal(3, result.FilesScanned);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public async Task Groups_contain_all_copies_including_three_or_more()
    {
        using var temp = new TempFolder();
        temp.WriteImage("a/x.jpg", seed: 7);
        temp.WriteImage("b/y.jpg", seed: 7);
        temp.WriteImage("c/z.jpg", seed: 7);
        temp.WriteImage("d/w.jpg", seed: 7);

        var result = await CreateScanner().ScanAsync([temp.Root]);

        Assert.Equal(4, Assert.Single(result.Groups).Files.Count);
    }

    [Fact]
    public async Task Modified_images_are_not_grouped()
    {
        using var temp = new TempFolder();
        var original = temp.WriteImage("a.jpg", seed: 3);
        var bytes = File.ReadAllBytes(original);
        bytes[^1] ^= 0xFF;
        temp.WriteBytes("a-edited.jpg", bytes);

        var result = await CreateScanner().ScanAsync([temp.Root]);

        Assert.Empty(result.Groups);
        Assert.Equal(2, result.FilesScanned);
    }

    [Fact]
    public async Task Non_image_files_are_excluded_even_with_identical_contents()
    {
        using var temp = new TempFolder();
        var image = temp.WriteImage("a.jpg", seed: 4);
        File.Copy(image, temp.PathOf("a.txt"));
        File.Copy(image, temp.PathOf("a.mp4"));

        var result = await CreateScanner().ScanAsync([temp.Root]);

        Assert.Empty(result.Groups);
        Assert.Equal(1, result.FilesScanned);
    }

    [Fact]
    public async Task Scans_multiple_roots()
    {
        using var first = new TempFolder();
        using var second = new TempFolder();
        first.WriteImage("a.png", seed: 5);
        second.WriteImage("b.png", seed: 5);

        var result = await CreateScanner().ScanAsync([first.Root, second.Root]);

        Assert.Equal(2, Assert.Single(result.Groups).Files.Count);
    }

    [Fact]
    public async Task Overlapping_roots_do_not_make_a_file_a_duplicate_of_itself()
    {
        using var temp = new TempFolder();
        temp.WriteImage("sub/only.jpg", seed: 6);

        var result = await CreateScanner().ScanAsync([temp.Root, temp.PathOf("sub"), temp.Root]);

        Assert.Empty(result.Groups);
        Assert.Equal(1, result.FilesScanned);
    }

    [Fact]
    public async Task Unreadable_file_is_reported_and_scan_continues_but_is_incomplete()
    {
        using var temp = new TempFolder();
        var a = temp.WriteImage("a.jpg", seed: 8);
        var b = temp.WriteImage("b.jpg", seed: 8);
        var unreadable = temp.WriteImage("locked.jpg", seed: 9);

        var result = await CreateScanner(new FailingHasher(new Md5FileHasher(), unreadable)).ScanAsync([temp.Root]);

        var error = Assert.Single(result.Errors);
        Assert.Equal(unreadable, error.Path);
        Assert.Contains("Access denied", error.Reason);
        Assert.Equal([a, b], Assert.Single(result.Groups).Files.Select(f => f.Path).Order());
        Assert.False(result.IsComplete);
        Assert.False(result.WasCanceled);
    }

    [Fact]
    public async Task Missing_root_folder_is_reported_and_scan_is_incomplete()
    {
        using var temp = new TempFolder();
        temp.WriteImage("a.jpg", seed: 1);
        var missing = temp.PathOf("does-not-exist");

        var result = await CreateScanner().ScanAsync([missing, temp.Root]);

        Assert.Equal(missing, Assert.Single(result.Errors).Path);
        Assert.Equal(1, result.FilesScanned);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task Canceled_scan_returns_partial_incomplete_result()
    {
        using var temp = new TempFolder();
        for (var i = 0; i < 50; i++)
        {
            temp.WriteImage($"img{i}.jpg", seed: i);
        }

        using var cts = new CancellationTokenSource();
        var hasher = new CancelAfterHasher(new Md5FileHasher(), cts, cancelAfter: 5);

        var result = await CreateScanner(hasher).ScanAsync([temp.Root], cancellationToken: cts.Token);

        Assert.True(result.WasCanceled);
        Assert.False(result.IsComplete);
        Assert.True(result.FilesScanned < 50);
    }

    [Fact]
    public async Task Reports_progress_until_all_files_processed()
    {
        using var temp = new TempFolder();
        temp.WriteImage("a.jpg", seed: 1);
        temp.WriteImage("b.jpg", seed: 2);
        var reports = new List<ScanProgress>();
        var progress = new SynchronousProgress<ScanProgress>(reports.Add);

        await CreateScanner().ScanAsync([temp.Root], progress);

        var last = reports.Last();
        Assert.Equal(2, last.FilesDiscovered);
        Assert.Equal(2, last.FilesProcessed);
        Assert.True(last.EnumerationComplete);
    }

    [Fact]
    public async Task Cached_hashes_are_reused_for_unchanged_files()
    {
        using var temp = new TempFolder();
        temp.WriteImage("a.jpg", seed: 1);
        temp.WriteImage("b.jpg", seed: 1);
        var changed = temp.WriteImage("c.jpg", seed: 2);

        using var cache = new SqliteHashCache(temp.PathOf("cache/hashes.db"));
        var hasher = new CountingHasher(new Md5FileHasher());
        var scanner = CreateScanner(hasher, cache);

        await scanner.ScanAsync([temp.Root]);
        Assert.Equal(3, hasher.Calls);

        File.Copy(temp.PathOf("a.jpg"), changed, overwrite: true);
        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddMinutes(1));

        var second = await scanner.ScanAsync([temp.Root]);

        Assert.Equal(4, hasher.Calls);
        Assert.Equal(3, Assert.Single(second.Groups).Files.Count);
    }

    private sealed class CancelAfterHasher(IFileHasher inner, CancellationTokenSource cts, int cancelAfter) : IFileHasher
    {
        private int _calls;

        public Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) >= cancelAfter)
            {
                cts.Cancel();
            }

            return inner.ComputeHashAsync(path, cancellationToken);
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        private readonly Lock _lock = new();

        public void Report(T value)
        {
            lock (_lock)
            {
                handler(value);
            }
        }
    }
}
