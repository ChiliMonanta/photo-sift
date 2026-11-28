using System.Net;
using PhotoSift.Core.GoogleDrive;
using PhotoSift.Core.Hashing;
using PhotoSift.Core.Upload;

namespace PhotoSift.Tests;

public sealed class DriveUploadTests : IDisposable
{
    private const int Chunk = 256 * 1024;

    private readonly TempFolder _temp = new();
    private readonly FakeDriveApi _drive = new();
    private readonly UploadJournal _journal;
    private readonly List<TimeSpan> _delays = [];
    private readonly string _photos;

    public DriveUploadTests()
    {
        _journal = new UploadJournal(_temp.PathOf("upload.db"));
        _photos = _drive.AddFolder("Photos");
    }

    public void Dispose()
    {
        _journal.Dispose();
        _temp.Dispose();
    }

    private string Local => _temp.PathOf("Pixel 8");

    private DriveFolderTarget Target => new(_drive.Account.PermissionId, _photos, "My Drive/Photos");

    private DriveUploader Uploader(int chunkSize = Chunk) => new(_journal, delay: (d, _) =>
    {
        _delays.Add(d);
        return Task.CompletedTask;
    }, chunkSize: chunkSize);

    private Task<UploadPlan> PlanAsync(DriveUploader? uploader = null) =>
        (uploader ?? Uploader()).PlanAsync(_drive, Local, Target);

    private async Task<UploadReport> UploadAsync(DriveUploader? uploader = null, CancellationToken cancellationToken = default)
    {
        uploader ??= Uploader();
        var plan = await uploader.PlanAsync(_drive, Local, Target, cancellationToken: cancellationToken);
        return await uploader.RunAsync(plan, cancellationToken: cancellationToken);
    }

    private static string Md5Of(string path) => new Md5FileHasher().ComputeHashAsync(path).GetAwaiter().GetResult();

    [Fact]
    public async Task New_files_are_uploaded_in_the_local_folder_structure_and_verified()
    {
        var image = _temp.WriteImage("Pixel 8/DCIM/Camera/IMG_1.jpg", seed: 1, length: 3 * Chunk + 17);
        _temp.WriteBytes("Pixel 8/DCIM/Camera/VID_1.mp4", [1, 2, 3, 4]);
        _temp.WriteBytes("Pixel 8/notes.txt", [9]);

        var report = await UploadAsync();

        Assert.Equal(2, report.UploadedCount);
        Assert.Equal(0, report.FailedCount);
        var uploaded = _drive.Find("IMG_1.jpg")!;
        Assert.Equal("My Drive/Photos/Pixel 8/DCIM/Camera/IMG_1.jpg", _drive.PathOf(uploaded.Id));
        Assert.Equal(await File.ReadAllBytesAsync(image), _drive.ContentOf(uploaded.Id));
        Assert.Equal(Md5Of(image), uploaded.Md5Checksum);
        Assert.Equal([0L, Chunk, 2L * Chunk, 3L * Chunk, 0L], _drive.ChunkOffsets);
        Assert.Equal("image/jpeg", _drive.StartedUploads[0].MimeType);
        Assert.Equal(File.GetLastWriteTimeUtc(image), _drive.StartedUploads[0].ModifiedUtc);
        Assert.Null(_drive.Find("notes.txt"));
        Assert.Contains(report.Outcomes, o => o.DrivePath == "My Drive/Photos/Pixel 8/DCIM/Camera/IMG_1.jpg");
    }

    [Fact]
    public async Task Files_already_anywhere_in_the_Drive_folder_and_files_uploaded_before_are_skipped()
    {
        var a = _temp.WriteImage("Pixel 8/a.jpg", seed: 1);
        _temp.WriteImage("Pixel 8/b.jpg", seed: 2);
        _drive.AddFile("renamed.jpg", _drive.AddFolder("2024", _photos), Md5Of(a));

        var first = await PlanAsync();
        Assert.Equal(1, first.AlreadyOnDrive);
        Assert.Equal(["b.jpg"], first.ToUpload.Select(i => i.File.RelativePath));
        await Uploader().RunAsync(first);

        var second = await PlanAsync();
        Assert.Empty(second.ToUpload);
        Assert.Equal(1, second.UploadedBefore);
        Assert.Equal(1, second.AlreadyOnDrive);
    }

    [Fact]
    public async Task Identical_local_files_are_uploaded_once()
    {
        _temp.WriteImage("Pixel 8/a.jpg", seed: 1);
        _temp.WriteImage("Pixel 8/copy/a.jpg", seed: 1);

        var plan = await PlanAsync();

        Assert.Single(plan.ToUpload);
        Assert.Equal(1, plan.DuplicatesInFolder);
    }

    [Fact]
    public async Task A_different_file_with_a_taken_name_gets_a_new_name()
    {
        _temp.WriteImage("Pixel 8/IMG_1.jpg", seed: 1);
        _drive.AddFile("IMG_1.jpg", _drive.AddFolder("Pixel 8", _photos), md5: "something-else");

        await UploadAsync();

        Assert.NotNull(_drive.Find("IMG_1 (2).jpg"));
        Assert.Single(_drive.Items, i => i.Name == "Pixel 8");
    }

    [Fact]
    public async Task A_canceled_upload_continues_where_it_stopped()
    {
        var image = _temp.WriteImage("Pixel 8/big.jpg", seed: 1, length: 4 * Chunk);
        using var cancel = new CancellationTokenSource();
        _drive.FailChunk = n =>
        {
            if (n == 2)
            {
                cancel.Cancel();
            }

            return null;
        };

        var canceled = await UploadAsync(cancellationToken: cancel.Token);
        Assert.True(canceled.WasCanceled);
        Assert.Null(_drive.Find("big.jpg"));

        _drive.FailChunk = null;
        var plan = await PlanAsync();
        Assert.Equal(1, plan.Resumed);
        var report = await Uploader().RunAsync(plan);

        Assert.Equal(1, report.UploadedCount);
        Assert.Single(_drive.StartedUploads);
        Assert.Equal([0L, Chunk, 2L * Chunk, 3L * Chunk], _drive.ChunkOffsets);
        Assert.Equal(await File.ReadAllBytesAsync(image), _drive.ContentOf(_drive.Find("big.jpg")!.Id));
    }

    [Fact]
    public async Task After_a_network_error_the_upload_asks_Google_what_arrived_and_continues()
    {
        var image = _temp.WriteImage("Pixel 8/a.jpg", seed: 1, length: 3 * Chunk);
        _drive.StoreFailedChunks = true; // the bytes arrived, but the answer was lost
        _drive.FailChunk = n => n == 2 ? new HttpRequestException("Connection reset (simulated).") : null;

        var report = await UploadAsync();

        Assert.Equal(1, report.UploadedCount);
        Assert.Equal([0L, Chunk, 2L * Chunk], _drive.ChunkOffsets);
        Assert.Equal([TimeSpan.FromSeconds(1)], _delays);
        Assert.Equal(await File.ReadAllBytesAsync(image), _drive.ContentOf(_drive.Find("a.jpg")!.Id));
    }

    [Fact]
    public async Task An_expired_session_is_started_again()
    {
        _temp.WriteImage("Pixel 8/a.jpg", seed: 1, length: 2 * Chunk);
        using var cancel = new CancellationTokenSource();
        _drive.FailChunk = n =>
        {
            cancel.Cancel();
            return null;
        };
        await UploadAsync(cancellationToken: cancel.Token);
        _drive.FailChunk = null;
        _drive.ExpireSessions();

        var report = await UploadAsync();

        Assert.Equal(1, report.UploadedCount);
        Assert.Equal(2, _drive.StartedUploads.Count);
    }

    [Fact]
    public async Task An_upload_that_does_not_match_is_moved_to_the_Drive_trash_and_reported()
    {
        _temp.WriteImage("Pixel 8/a.jpg", seed: 1);
        _drive.CorruptUploads = true;

        var report = await UploadAsync();

        var failed = Assert.Single(report.Outcomes);
        Assert.Equal(UploadOutcomeStatus.Failed, failed.Status);
        Assert.Contains("moved to the Google Drive trash", failed.Reason);
        Assert.Single(_drive.Trashed);
        Assert.Null(_drive.Find("a.jpg"));

        _drive.CorruptUploads = false;
        Assert.Single((await PlanAsync()).ToUpload);
    }

    [Fact]
    public async Task A_file_changed_after_the_check_is_not_uploaded()
    {
        var path = _temp.WriteImage("Pixel 8/a.jpg", seed: 1);
        var uploader = Uploader();
        var plan = await uploader.PlanAsync(_drive, Local, Target);
        await File.AppendAllTextAsync(path, "changed");

        var report = await uploader.RunAsync(plan);

        Assert.Contains("changed", Assert.Single(report.Outcomes).Reason);
        Assert.Empty(_drive.StartedUploads);
    }

    [Fact]
    public async Task A_full_Google_storage_stops_the_upload()
    {
        _temp.WriteImage("Pixel 8/a.jpg", seed: 1);
        _temp.WriteImage("Pixel 8/b.jpg", seed: 2);
        _drive.FailStartUpload = new DriveApiException(403, "The user's Drive storage quota has been exceeded.", "storageQuotaExceeded");

        var report = await UploadAsync();

        Assert.Contains("storage is full", report.StopReason);
        Assert.Equal(1, report.NotStarted);
    }

    [Fact]
    public async Task Repeated_failures_stop_the_upload()
    {
        for (var i = 0; i < 5; i++)
        {
            _temp.WriteImage($"Pixel 8/{i}.jpg", seed: i);
        }

        _drive.FailStartUpload = new HttpRequestException("No connection (simulated).");

        var report = await UploadAsync();

        Assert.Equal(DriveUploader.MaxFailuresInARow, report.FailedCount);
        Assert.Contains("internet connection", report.StopReason);
        Assert.Equal(2, report.NotStarted);
    }

    [Fact]
    public void Taken_names_get_a_number()
    {
        Assert.Equal("a.jpg", DriveUploader.UniqueName("a.jpg", new HashSet<string>()));
        Assert.Equal("a (3).jpg", DriveUploader.UniqueName("a.jpg", new HashSet<string> { "a.jpg", "a (2).jpg" }));
    }
}

public sealed class DriveRestUploadTests
{
    private sealed class StaticTokens : IAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("token-1");

        public void InvalidateAccessToken(string accessToken)
        {
        }
    }

    private static readonly Uri Session = new("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=abc");

    [Fact]
    public async Task Starting_an_upload_sends_the_metadata_and_returns_the_session()
    {
        var handler = new FakeHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Location = Session;
            return response;
        });
        var client = new DriveRestClient(new HttpClient(handler), new StaticTokens());

        var session = await client.StartUploadAsync(
            new DriveNewFile("a.jpg", "folder-1", 1234, "image/jpeg", new DateTime(2025, 5, 1, 10, 0, 0, DateTimeKind.Utc)),
            CancellationToken.None);

        Assert.Equal(Session, session);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.StartsWith(DriveRestClient.UploadUri + "?uploadType=resumable", request.RequestUri!.AbsoluteUri);
        Assert.Equal("1234", request.Headers.GetValues("X-Upload-Content-Length").Single());
        Assert.Equal("image/jpeg", request.Headers.GetValues("X-Upload-Content-Type").Single());
        Assert.Contains("\"name\":\"a.jpg\"", body);
        Assert.Contains("\"parents\":[\"folder-1\"]", body);
        Assert.Contains("\"modifiedTime\":\"2025-05-01T10:00:00.000Z\"", body);
    }

    [Fact]
    public async Task A_session_address_outside_Google_is_refused()
    {
        var handler = new FakeHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Location = new Uri("https://attacker.example/upload");
            return response;
        });
        var client = new DriveRestClient(new HttpClient(handler), new StaticTokens());

        await Assert.ThrowsAsync<DriveApiException>(() =>
            client.StartUploadAsync(new DriveNewFile("a.jpg", "f", 1, null, null), CancellationToken.None));
        await Assert.ThrowsAsync<DriveApiException>(() =>
            client.GetUploadStatusAsync(new Uri("https://attacker.example/upload/drive/x"), 1, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Chunks_send_their_range_and_read_how_much_arrived()
    {
        var responses = new Queue<HttpResponseMessage>();
        var partial = new HttpResponseMessage((HttpStatusCode)308);
        partial.Headers.Add("Range", "bytes=0-262143");
        responses.Enqueue(partial);
        responses.Enqueue(new HttpResponseMessage((HttpStatusCode)308));
        responses.Enqueue(FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"id":"new-1","name":"a.jpg","mimeType":"image/jpeg","size":"300000","md5Checksum":"abc","parents":["f"]}"""));
        var handler = new FakeHttpHandler((_, _) => responses.Dequeue());
        var client = new DriveRestClient(new HttpClient(handler), new StaticTokens());

        var first = await client.UploadChunkAsync(Session, new byte[262144], 0, 300000, CancellationToken.None);
        var none = await client.GetUploadStatusAsync(Session, 300000, CancellationToken.None);
        var done = await client.UploadChunkAsync(Session, new byte[37856], 262144, 300000, CancellationToken.None);

        Assert.Equal(new DriveUploadStatus(262144, null), first);
        Assert.Equal(new DriveUploadStatus(0, null), none);
        Assert.Equal("new-1", done.File?.Id);
        Assert.Equal("abc", done.File?.Md5Checksum);
        Assert.Equal("bytes 0-262143/300000", handler.Requests[0].Request.Content!.Headers.GetValues("Content-Range").Single());
        Assert.Equal("bytes */300000", handler.Requests[1].Request.Content!.Headers.GetValues("Content-Range").Single());
        Assert.Equal("bytes 262144-299999/300000", handler.Requests[2].Request.Content!.Headers.GetValues("Content-Range").Single());
        Assert.All(handler.Requests, r => Assert.Equal("token-1", r.Request.Headers.Authorization?.Parameter));
    }

    [Fact]
    public async Task Errors_keep_Googles_reason()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.Forbidden,
            """{"error":{"message":"Quota exceeded","errors":[{"reason":"storageQuotaExceeded"}]}}"""));
        var client = new DriveRestClient(new HttpClient(handler), new StaticTokens());

        var error = await Assert.ThrowsAsync<DriveApiException>(() =>
            client.UploadChunkAsync(Session, new byte[1], 0, 1, CancellationToken.None));

        Assert.True(error.IsStorageFull);
    }
}
