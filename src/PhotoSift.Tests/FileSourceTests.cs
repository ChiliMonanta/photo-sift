using System.Runtime.CompilerServices;
using PhotoSift.Core;
using PhotoSift.Core.Deletion;
using PhotoSift.Core.Hashing;
using PhotoSift.Core.Scanning;
using PhotoSift.Core.Selection;
using PhotoSift.Core.Sources;

namespace PhotoSift.Tests;

public sealed class FileSourceTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeTrashService _trash = new();
    private readonly CountingHasher _hasher = new(new Md5FileHasher());
    private readonly FakeCloudSource _cloud = new();
    private readonly FileSources _sources;

    public FileSourceTests() =>
        _sources = new FileSources(new LocalFileSource(_hasher, trash: _trash), _cloud);

    public void Dispose() => _temp.Dispose();

    /// <summary>A local image plus a cloud file with the same contents (same MD5).</summary>
    private async Task<(string Local, string Cloud)> LocalAndCloudCopyAsync()
    {
        var local = _temp.WriteImage("Pictures/beach.jpg", seed: 1);
        var hash = await new Md5FileHasher().ComputeHashAsync(local);
        var cloud = _cloud.Add("My Drive/Phone/IMG_4021.jpg", hash, new FileInfo(local).Length);
        return (local, cloud);
    }

    private Task<ScanResult> ScanAsync(params string[] folders) =>
        new DuplicateScanner(_sources).ScanAsync(folders);

    [Fact]
    public async Task Local_and_cloud_copies_with_the_same_contents_form_one_group()
    {
        var (local, cloud) = await LocalAndCloudCopyAsync();

        var scan = await ScanAsync(_temp.Root, _cloud.FolderLocation("My Drive"));

        var group = Assert.Single(scan.Groups);
        Assert.Equal([local, cloud], group.Files.Select(f => f.Path));
        var cloudFile = group.Files[1];
        Assert.Equal("fake", cloudFile.SourceId);
        Assert.Equal("My Drive/Phone/IMG_4021.jpg", cloudFile.DisplayPath);
        Assert.Equal("IMG_4021.jpg", cloudFile.FileName);
        Assert.True(scan.IsComplete);
    }

    [Fact]
    public async Task Hashes_known_from_metadata_are_used_without_reading_the_file()
    {
        await LocalAndCloudCopyAsync();

        await ScanAsync(_temp.Root, _cloud.FolderLocation("My Drive"));

        Assert.Equal(1, _hasher.Calls); // only the local file was read
    }

    [Fact]
    public async Task Files_from_a_read_only_source_are_skipped_with_the_reason()
    {
        var (local, cloud) = await LocalAndCloudCopyAsync();
        var scan = await ScanAsync(_temp.Root, _cloud.FolderLocation("My Drive"));

        var report = await new SafeDeleter(_sources).MoveToTrashAsync(scan, [cloud]);

        var outcome = Assert.Single(report.Outcomes);
        Assert.Equal(DeletionStatus.Skipped, outcome.Status);
        Assert.Equal(FakeCloudSource.ReadOnlyReason, outcome.Reason);
        Assert.Equal("My Drive/Phone/IMG_4021.jpg", outcome.DisplayPath);
        Assert.False(_cloud.IsTrashed(cloud));
        Assert.True(File.Exists(local));
    }

    [Fact]
    public async Task Local_copy_is_trashed_when_a_verified_cloud_copy_is_kept()
    {
        var (local, _) = await LocalAndCloudCopyAsync();
        var scan = await ScanAsync(_temp.Root, _cloud.FolderLocation("My Drive"));

        var report = await new SafeDeleter(_sources).MoveToTrashAsync(scan, [local]);

        Assert.Equal(DeletionStatus.MovedToTrash, Assert.Single(report.Outcomes).Status);
        Assert.Equal([local], _trash.Trashed);
    }

    [Fact]
    public async Task Local_copy_is_kept_when_the_cloud_copy_changed_after_the_scan()
    {
        var (local, cloud) = await LocalAndCloudCopyAsync();
        var scan = await ScanAsync(_temp.Root, _cloud.FolderLocation("My Drive"));
        _cloud.ChangeContents(cloud);

        var report = await new SafeDeleter(_sources).MoveToTrashAsync(scan, [local]);

        var outcome = Assert.Single(report.Outcomes);
        Assert.Equal(DeletionStatus.Skipped, outcome.Status);
        Assert.Contains("None of the kept copies", outcome.Reason);
        Assert.Empty(_trash.Trashed);
        Assert.True(File.Exists(local));
    }

    [Fact]
    public async Task A_cloud_master_folder_keeps_the_cloud_copy_and_marks_the_local_one()
    {
        var (local, _) = await LocalAndCloudCopyAsync();
        var scan = await ScanAsync(_temp.Root, _cloud.FolderLocation("My Drive"));

        var selected = MasterFolderSelector.SelectCopiesOutsideMasters(
            scan.Groups, [_cloud.FolderLocation("My Drive/Phone")], _sources);

        Assert.Equal([local], selected.Select(f => f.Path));
    }

    [Fact]
    public async Task A_local_master_folder_never_contains_cloud_files()
    {
        var (_, cloud) = await LocalAndCloudCopyAsync();
        var scan = await ScanAsync(_temp.Root, _cloud.FolderLocation("My Drive"));

        var selected = MasterFolderSelector.SelectCopiesOutsideMasters(scan.Groups, [_temp.Root], _sources);

        Assert.Equal([cloud], selected.Select(f => f.Path));
    }

    [Fact]
    public async Task A_location_no_source_owns_is_an_error_and_makes_the_scan_incomplete()
    {
        var scan = await ScanAsync("other://somewhere", _temp.Root);

        var error = Assert.Single(scan.Errors);
        Assert.Equal("other://somewhere", error.Path);
        Assert.False(scan.IsComplete);
    }

    [Fact]
    public void Sources_must_have_unique_ids() =>
        Assert.Throws<ArgumentException>(() => new FileSources(new FakeCloudSource(), new FakeCloudSource()));

    [Theory]
    [InlineData(@"C:\Photos\a.jpg", true)]
    [InlineData(@"\\server\share\a.jpg", true)]
    [InlineData("/home/user/a.jpg", true)]
    [InlineData("c://not-a-scheme", true)]
    [InlineData("gdrive://account/1AbC", false)]
    [InlineData("fake://file/1", false)]
    public void Locations_with_a_uri_scheme_are_not_local(string location, bool isLocal) =>
        Assert.Equal(isLocal, PathUtil.IsLocal(location));

    [Fact]
    public void Cloud_locations_compare_case_sensitively_on_every_os()
    {
        Assert.False(PathUtil.Comparer.Equals("gdrive://a/1AbC", "gdrive://a/1abc"));
        Assert.True(PathUtil.Comparer.Equals("gdrive://a/1AbC", "gdrive://a/1AbC"));
        Assert.True(PathUtil.Comparer.Compare("/z/local.jpg", "gdrive://a/1") < 0);
    }
}

/// <summary>
/// A read-only, in-memory cloud storage source standing in for Google Drive in tests.
/// Hashes come from metadata, like Drive's md5Checksum. Files: <c>fake://file/N</c>;
/// folders: <c>fake://folder/&lt;path&gt;</c>.
/// </summary>
public sealed class FakeCloudSource : IFileSource
{
    public const string ReadOnlyReason = "The fake cloud is connected read-only.";

    private const string FilePrefix = "fake://file/";
    private const string FolderPrefix = "fake://folder/";

    private readonly Dictionary<string, (string DisplayPath, string Hash, long Size)> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _trashed = new(StringComparer.Ordinal);

    public string Id => "fake";

    public string? TrashUnavailableReason => ReadOnlyReason;

    public string Add(string displayPath, string hash, long size)
    {
        var location = FilePrefix + (_files.Count + 1);
        _files[location] = (displayPath, hash, size);
        return location;
    }

    public string FolderLocation(string displayPath) => FolderPrefix + displayPath;

    public void ChangeContents(string location) =>
        _files[location] = _files[location] with { Hash = new string('0', 32) };

    public bool IsTrashed(string location) => _trashed.Contains(location);

    public bool Owns(string location) => location.StartsWith("fake://", StringComparison.Ordinal);

    public async IAsyncEnumerable<SourceFile> EnumerateAsync(
        IReadOnlyList<string> folders,
        IScanSink sink,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        foreach (var (location, file) in _files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (folders.Any(folder => IsUnder(file.DisplayPath, folder)))
            {
                yield return new SourceFile(location, file.DisplayPath, file.Size, DateTime.UnixEpoch, KnownHash: file.Hash);
            }
        }
    }

    public Task<string> GetHashAsync(SourceFile file, IScanSink sink, CancellationToken cancellationToken) =>
        Task.FromResult(file.KnownHash ?? throw new InvalidOperationException("No hash in metadata."));

    public bool IsInFolder(ScannedFile file, string folder) => IsUnder(file.DisplayPath, folder);

    public Task<string?> VerifyAsync(ScannedFile file, CancellationToken cancellationToken) =>
        Task.FromResult(!_files.TryGetValue(file.Path, out var current) || _trashed.Contains(file.Path)
            ? "File no longer exists."
            : current.Hash == file.Hash ? null : "File contents have changed since the scan.");

    public Task MoveToTrashAsync(ScannedFile file, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyReason);

    private static bool IsUnder(string displayPath, string folderLocation) =>
        folderLocation.StartsWith(FolderPrefix, StringComparison.Ordinal) &&
        displayPath.StartsWith(folderLocation[FolderPrefix.Length..] + "/", StringComparison.Ordinal);
}
