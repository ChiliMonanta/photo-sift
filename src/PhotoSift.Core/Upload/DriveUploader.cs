using System.Diagnostics;
using PhotoSift.Core.Caching;
using PhotoSift.Core.GoogleDrive;
using PhotoSift.Core.Hashing;
using PhotoSift.Core.Import;

namespace PhotoSift.Core.Upload;

/// <summary>
/// Uploads photos and videos from a local folder to a Google Drive folder, safely and resumably.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Add only: existing Drive files are never changed or deleted, and local files are only read.</item>
/// <item>Files whose contents (MD5) are already anywhere in the Drive folder are skipped, as are
/// files uploaded before. Identical local files are uploaded once.</item>
/// <item>The files keep the local folder structure, in a folder named after the local folder.
/// A different file with the same name gets a new name ("IMG_1 (2).jpg").</item>
/// <item>Uploads use Google's resumable sessions: a file appears in Drive only after all its bytes
/// arrived. The journal records each session, so an interrupted upload continues where it stopped.</item>
/// <item>Each uploaded file is checked: Drive's MD5 must match the local file's. If not, the upload is
/// moved to the Drive trash (it can be restored there) and reported.</item>
/// </list>
/// </remarks>
public sealed class DriveUploader(
    UploadJournal journal,
    IFileHasher? hasher = null,
    IHashCache? hashCache = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    int chunkSize = DriveUploader.DefaultChunkSize)
{
    /// <summary>Google requires chunks in multiples of 256 KiB.</summary>
    public const int DefaultChunkSize = 16 * 256 * 1024;

    /// <summary>After this many failed files in a row, the upload stops: the connection is probably down.</summary>
    public const int MaxFailuresInARow = 3;

    /// <summary>Times a chunk is retried after a network or server error before the file fails.</summary>
    public const int MaxRetriesPerFile = 5;

    private const char PathSeparator = '\u001f';
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif",
        [".bmp"] = "image/bmp", [".webp"] = "image/webp", [".heic"] = "image/heic", [".heif"] = "image/heif",
        [".tif"] = "image/tiff", [".tiff"] = "image/tiff", [".mp4"] = "video/mp4", [".m4v"] = "video/x-m4v",
        [".mov"] = "video/quicktime", [".3gp"] = "video/3gpp", [".avi"] = "video/x-msvideo", [".mkv"] = "video/x-matroska",
        [".webm"] = "video/webm", [".wmv"] = "video/x-ms-wmv",
    };

    private readonly IFileHasher _hasher = hasher ?? new Md5FileHasher();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly int _chunkSize = chunkSize > 0 && chunkSize % (256 * 1024) == 0
        ? chunkSize
        : throw new ArgumentException("The chunk size must be a multiple of 256 KiB.", nameof(chunkSize));

    /// <summary>
    /// Reads the Drive folder and the local folder, and works out what to upload. Sends nothing.
    /// </summary>
    /// <param name="status">Receives short texts about the current step.</param>
    public async Task<UploadPlan> PlanAsync(
        IDriveApi api,
        string localFolder,
        DriveFolderTarget target,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        var source = new FolderImportSource(localFolder);
        var journalKey = string.Join("|", target.AccountId, target.FolderId, source.Folder);
        var entries = journal.GetEntries(journalKey);

        status?.Report("Reading the Google Drive folder…");
        var tree = await ReadTreeAsync(api, target.FolderId, status, cancellationToken).ConfigureAwait(false);

        status?.Report("Reading the local folder…");
        var files = new List<ImportItem>();
        await foreach (var item in source.EnumerateAsync(null, cancellationToken).ConfigureAwait(false))
        {
            files.Add(item);
        }

        var toUpload = new List<UploadItem>();
        var problems = new List<string>();
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int alreadyOnDrive = 0, uploadedBefore = 0, duplicates = 0, checkedFiles = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            status?.Report($"Checking the local files… {++checkedFiles:N0} of {files.Count:N0}");
            entries.TryGetValue(file.RelativePath, out var entry);
            if (entry is { Status: UploadStatus.Done } && Unchanged(entry, file))
            {
                uploadedBefore++;
                continue;
            }

            if (file.Size == 0)
            {
                problems.Add($"{file.RelativePath}: the file is empty, so it is not uploaded.");
                continue;
            }

            string md5;
            try
            {
                md5 = await HashAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{file.RelativePath}: could not be read: {ex.Message}");
                continue;
            }

            if (tree.Md5s.Contains(md5))
            {
                alreadyOnDrive++;
                continue;
            }

            if (!planned.Add(md5))
            {
                duplicates++;
                continue;
            }

            var resume = entry is { Status: UploadStatus.Uploading, Session: { } session, DriveName: { } name, ParentId: { } parent }
                         && Unchanged(entry, file) && string.Equals(entry.Md5, md5, StringComparison.OrdinalIgnoreCase)
                ? new UploadResume(session, name, parent)
                : null;
            toUpload.Add(new UploadItem(file, md5, resume));
        }

        return new UploadPlan
        {
            Api = api,
            LocalFolder = source.Folder,
            Target = target,
            TopFolderName = source.DisplayName,
            JournalKey = journalKey,
            ToUpload = toUpload,
            AlreadyOnDrive = alreadyOnDrive,
            UploadedBefore = uploadedBefore,
            DuplicatesInFolder = duplicates,
            Problems = problems,
            Tree = tree,
        };
    }

    public async Task<UploadReport> RunAsync(
        UploadPlan plan, IProgress<UploadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var outcomes = new List<UploadOutcome>();
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        long finishedBytes = 0;
        var failuresInARow = 0;
        var canceled = false;
        string? stopReason = null;

        void Report(string? current, long currentFileBytes, bool force)
        {
            if (progress is null || (!force && stopwatch.Elapsed - lastReport < ProgressInterval))
            {
                return;
            }

            lastReport = stopwatch.Elapsed;
            var sent = finishedBytes + currentFileBytes;
            var seconds = stopwatch.Elapsed.TotalSeconds;
            progress.Report(new UploadProgress(
                plan.ToUpload.Count,
                outcomes.Count(o => o.Status == UploadOutcomeStatus.Uploaded),
                outcomes.Count(o => o.Status == UploadOutcomeStatus.Failed),
                plan.BytesToUpload,
                sent,
                current,
                seconds > 0.5 ? sent / seconds : 0));
        }

        foreach (var item in plan.ToUpload)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            Report(item.File.RelativePath, 0, force: true);
            UploadOutcome outcome;
            try
            {
                outcome = await UploadOneAsync(plan, item, bytes => Report(item.File.RelativePath, bytes, force: false), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }
            catch (DriveSignInRequiredException ex)
            {
                stopReason = ex.Message;
                break;
            }
            catch (DriveApiException ex) when (ex.IsStorageFull)
            {
                outcomes.Add(new UploadOutcome(item, UploadOutcomeStatus.Failed, null, "Your Google storage is full."));
                stopReason = "Your Google storage is full. Free up space in Google Drive and continue the upload.";
                break;
            }

            outcomes.Add(outcome);
            finishedBytes += item.File.Size;
            if (outcome.Status == UploadOutcomeStatus.Uploaded)
            {
                failuresInARow = 0;
            }
            else if (++failuresInARow >= MaxFailuresInARow)
            {
                stopReason = $"{MaxFailuresInARow} files in a row could not be uploaded, so the upload stopped. " +
                             "Check your internet connection, then continue the upload.";
                break;
            }
        }

        Report(null, 0, force: true);
        return new UploadReport(outcomes, canceled, stopReason, plan.ToUpload.Count - outcomes.Count);
    }

    /// <summary>A name not yet used in the folder: "IMG_1.jpg", else "IMG_1 (2).jpg", and so on.</summary>
    public static string UniqueName(string name, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(name))
        {
            return name;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){extension}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private async Task<UploadOutcome> UploadOneAsync(
        UploadPlan plan, UploadItem item, Action<long> onBytes, CancellationToken cancellationToken)
    {
        var api = plan.Api;
        var file = item.File;
        var segments = file.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var folderSegments = new[] { plan.TopFolderName }.Concat(segments[..^1]).ToArray();
        var drivePath = string.Join("/", [plan.Target.DisplayPath, .. folderSegments, segments[^1]]);

        try
        {
            var local = new FileInfo(file.Key);
            if (!local.Exists || local.Length != file.Size || local.LastWriteTimeUtc != file.ModifiedUtc)
            {
                return Failed("The file changed or was removed after it was checked. Run the upload again.");
            }

            var folder = await EnsureFolderAsync(api, plan.Tree, folderSegments, cancellationToken).ConfigureAwait(false);
            var resume = item.Resume is { } r && r.ParentId == folder.Id ? r : null;
            var name = resume?.DriveName ?? UniqueName(segments[^1], folder.FileNames);
            drivePath = string.Join("/", [plan.Target.DisplayPath, .. folderSegments, name]);

            var uploaded = await SendAsync(plan, item, resume, name, folder.Id, onBytes, cancellationToken).ConfigureAwait(false);
            if (uploaded.Md5Checksum is { } driveMd5
                    ? !string.Equals(driveMd5, item.Md5, StringComparison.OrdinalIgnoreCase)
                    : uploaded.Size != file.Size)
            {
                journal.Remove(plan.JournalKey, file.RelativePath);
                return Failed(await TrashMismatchAsync(api, uploaded, cancellationToken).ConfigureAwait(false));
            }

            journal.MarkDone(plan.JournalKey, item, uploaded.Id);
            folder.FileNames.Add(uploaded.Name);
            plan.Tree.Md5s.Add(item.Md5);
            return new UploadOutcome(item, UploadOutcomeStatus.Uploaded, drivePath, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException
                                       or DriveApiException { IsStorageFull: false }
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The journal keeps a started session, so the next upload continues it.
            return Failed(ex is OperationCanceledException ? "The connection to Google Drive timed out." : ex.Message);
        }

        UploadOutcome Failed(string reason) => new(item, UploadOutcomeStatus.Failed, drivePath, reason);
    }

    /// <summary>Sends the file in chunks, continuing a started session when possible. Returns the new Drive file.</summary>
    private async Task<DriveItem> SendAsync(
        UploadPlan plan,
        UploadItem item,
        UploadResume? resume,
        string name,
        string parentId,
        Action<long> onBytes,
        CancellationToken cancellationToken)
    {
        var api = plan.Api;
        var file = item.File;
        var session = resume?.Session;
        var askStatus = session is not null;
        var restarted = false;
        var retries = 0;
        long offset = 0;
        var buffer = new byte[(int)Math.Min(_chunkSize, file.Size)];

        await using var stream = new FileStream(
            file.Key, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
        while (true)
        {
            try
            {
                if (session is null)
                {
                    session = await api.StartUploadAsync(
                        new DriveNewFile(name, parentId, file.Size, MimeTypes.GetValueOrDefault(Path.GetExtension(name)), file.ModifiedUtc),
                        cancellationToken).ConfigureAwait(false);
                    journal.MarkUploading(plan.JournalKey, item, session, name, parentId);
                    offset = 0;
                    askStatus = false;
                }

                DriveUploadStatus result;
                if (askStatus)
                {
                    result = await api.GetUploadStatusAsync(session, file.Size, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    stream.Position = offset;
                    var read = await stream.ReadAtLeastAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, file.Size - offset)), 1, throwOnEndOfStream: false,
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new IOException("The file became shorter during the upload.");
                    }

                    result = await api.UploadChunkAsync(session, buffer.AsMemory(0, read), offset, file.Size, cancellationToken)
                        .ConfigureAwait(false);
                }

                askStatus = false;
                retries = 0;
                if (result.File is { } done)
                {
                    onBytes(file.Size);
                    return done;
                }

                offset = result.ReceivedBytes;
                onBytes(offset);
            }
            catch (DriveApiException ex) when (ex.IsUploadSessionGone && session is not null && !restarted)
            {
                // The session expired (they last about a week); start over with a new one.
                restarted = true;
                session = null;
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken) && retries < MaxRetriesPerFile)
            {
                retries++;
                await _delay(TimeSpan.FromSeconds(Math.Pow(2, retries - 1)), cancellationToken).ConfigureAwait(false);

                // Some bytes may have arrived before the error; ask Google where to continue.
                askStatus = session is not null;
            }
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        HttpRequestException => true,
        DriveApiException api => api.IsTransient,
        OperationCanceledException => !cancellationToken.IsCancellationRequested, // HTTP timeout
        _ => false,
    };

    private static async Task<string> TrashMismatchAsync(IDriveApi api, DriveItem uploaded, CancellationToken cancellationToken)
    {
        const string mismatch = "The file in Google Drive does not match the file on this computer (it may have changed during the upload).";
        try
        {
            await api.TrashAsync(uploaded.Id, cancellationToken).ConfigureAwait(false);
            return $"{mismatch} The uploaded copy was moved to the Google Drive trash. Run the upload again.";
        }
        catch (Exception ex) when (ex is DriveApiException or HttpRequestException)
        {
            return $"{mismatch} The uploaded copy \"{uploaded.Name}\" could not be moved to the Google Drive trash ({ex.Message}); remove it there.";
        }
    }

    /// <summary>Finds or creates the folders for a file, below the Drive folder.</summary>
    private static async Task<DriveFolderNodeInfo> EnsureFolderAsync(
        IDriveApi api, DriveFolderTree tree, IReadOnlyList<string> segments, CancellationToken cancellationToken)
    {
        var node = tree.Folders[string.Empty];
        var path = string.Empty;
        foreach (var segment in segments)
        {
            path = path.Length == 0 ? segment : path + PathSeparator + segment;
            if (!tree.Folders.TryGetValue(path, out var child))
            {
                var created = await api.CreateFolderAsync(segment, node.Id, cancellationToken).ConfigureAwait(false);
                child = new DriveFolderNodeInfo(created.Id);
                tree.Folders[path] = child;
            }

            node = child;
        }

        return node;
    }

    private static async Task<DriveFolderTree> ReadTreeAsync(
        IDriveApi api, string folderId, IProgress<string>? status, CancellationToken cancellationToken)
    {
        var tree = new DriveFolderTree();
        var root = new DriveFolderNodeInfo(folderId);
        tree.Folders[string.Empty] = root;
        var pending = new Queue<(string Path, DriveFolderNodeInfo Node)>([(string.Empty, root)]);
        var visited = new HashSet<string>(StringComparer.Ordinal) { folderId };
        var files = 0;
        while (pending.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var child in await api.ListChildrenAsync(current.Node.Id, foldersOnly: false, cancellationToken).ConfigureAwait(false))
            {
                if (child.IsFolder)
                {
                    if (!visited.Add(child.Id))
                    {
                        continue;
                    }

                    // Drive allows two folders with the same name; uploads go into the first one, but
                    // the others are still searched for identical files.
                    var path = current.Path.Length == 0 ? child.Name : current.Path + PathSeparator + child.Name;
                    var node = new DriveFolderNodeInfo(child.Id);
                    if (!tree.Folders.TryAdd(path, node))
                    {
                        path += PathSeparator + child.Id;
                        tree.Folders[path] = node;
                    }

                    pending.Enqueue((path, node));
                }
                else if (!child.IsShortcut)
                {
                    current.Node.FileNames.Add(child.Name);
                    if (child.Md5Checksum is { } md5)
                    {
                        tree.Md5s.Add(md5);
                    }

                    status?.Report($"Reading the Google Drive folder… {++files:N0} files");
                }
            }
        }

        return tree;
    }

    private async Task<string> HashAsync(ImportItem file, CancellationToken cancellationToken)
    {
        var modified = file.ModifiedUtc ?? DateTime.UnixEpoch;
        if (hashCache?.TryGet(file.Key, file.Size, modified) is { } cached)
        {
            return cached;
        }

        var md5 = await _hasher.ComputeHashAsync(file.Key, cancellationToken).ConfigureAwait(false);
        try
        {
            hashCache?.Set(file.Key, file.Size, modified, md5);
        }
        catch (Exception)
        {
            // The cache is only an optimization.
        }

        return md5;
    }

    private static bool Unchanged(UploadJournalEntry entry, ImportItem file) =>
        entry.Size == file.Size && entry.ModifiedUtc == (file.ModifiedUtc ?? DateTime.UnixEpoch);
}
