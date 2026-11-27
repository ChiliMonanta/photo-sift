using PhotoSift.Core;
using PhotoSift.Core.Caching;
using PhotoSift.Core.Hashing;

namespace PhotoSift.Tests;

public class ImageFormatsTests
{
    [Theory]
    [InlineData("a.jpg")]
    [InlineData("a.JPEG")]
    [InlineData("a.png")]
    [InlineData("a.webp")]
    [InlineData("a.heic")]
    [InlineData("a.HEIF")]
    [InlineData("a.cr2")]
    [InlineData("a.nef")]
    [InlineData("a.arw")]
    [InlineData("a.dng")]
    public void Supported_image_formats_are_accepted(string name) =>
        Assert.True(ImageFormats.IsSupported(name));

    [Theory]
    [InlineData("a.txt")]
    [InlineData("a.mp4")]
    [InlineData("a.pdf")]
    [InlineData("a.jpg.bak")]
    [InlineData("jpg")]
    public void Non_image_files_are_excluded(string name) =>
        Assert.False(ImageFormats.IsSupported(name));
}

public class PathUtilTests
{
    [Fact]
    public void File_in_folder_or_subfolder_is_inside()
    {
        var root = Path.Combine(Path.GetTempPath(), "Photos");
        Assert.True(PathUtil.IsInFolder(Path.Combine(root, "a.jpg"), root));
        Assert.True(PathUtil.IsInFolder(Path.Combine(root, "2024", "a.jpg"), root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Sibling_folder_with_same_prefix_is_not_inside()
    {
        var root = Path.Combine(Path.GetTempPath(), "Photos");
        Assert.False(PathUtil.IsInFolder(Path.Combine(Path.GetTempPath(), "Photos2", "a.jpg"), root));
    }
}

public class Md5FileHasherTests
{
    [Fact]
    public async Task Computes_known_md5()
    {
        using var temp = new TempFolder();
        var path = temp.WriteBytes("abc.jpg", "abc"u8.ToArray());

        var hash = await new Md5FileHasher().ComputeHashAsync(path);

        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", hash);
    }
}

public class SqliteHashCacheTests
{
    [Fact]
    public void Returns_hash_only_while_size_and_timestamp_match()
    {
        using var temp = new TempFolder();
        var time = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);

        using (var cache = new SqliteHashCache(temp.PathOf("cache.db")))
        {
            cache.Set("/photos/a.jpg", 100, time, "abc123");
        }

        // Persisted between instances.
        using var reopened = new SqliteHashCache(temp.PathOf("cache.db"));
        Assert.Equal("abc123", reopened.TryGet("/photos/a.jpg", 100, time));
        Assert.Null(reopened.TryGet("/photos/a.jpg", 101, time));
        Assert.Null(reopened.TryGet("/photos/a.jpg", 100, time.AddSeconds(1)));
        Assert.Null(reopened.TryGet("/photos/b.jpg", 100, time));

        reopened.Set("/photos/a.jpg", 101, time, "def456");
        Assert.Equal("def456", reopened.TryGet("/photos/a.jpg", 101, time));
    }
}
