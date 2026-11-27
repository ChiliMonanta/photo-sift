using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PhotoSift.Core.Scanning;
using PhotoSift.Core.Sources;

namespace PhotoSift.Core.GoogleDrive;

/// <summary>
/// Image files in the signed-in user's My Drive, read-only. Hashes come from Drive's
/// <c>md5Checksum</c> metadata, so no file contents are downloaded. Previews use the small
/// thumbnails Drive generates, which also exist for formats PhotoSift cannot decode (HEIC, RAW).
/// </summary>
public sealed class GoogleDriveFileSource(IDriveSession session) : IFileSource, IThumbnailProvider
{
    /// <summary>After this many files in a row whose thumbnails Google refuses, stop asking for thumbnails.</summary>
    public const int MaxRefusedThumbnails = 3;

    public const string SourceId = "gdrive";

    public const string ReadOnlyReason =
        "Google Drive is connected read-only; this version cannot move Drive files to the trash.";

    // Folder ids containing each scanned file (all ancestors up to My Drive), for master folder checks.
    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> _ancestorsByFile = new(StringComparer.Ordinal);

    // Thumbnail links from the scan's file listing, so previews need no extra metadata requests.
    private readonly ConcurrentDictionary<string, string> _thumbnailLinks = new(StringComparer.Ordinal);
    private int _refusedThumbnailsInARow;

    public string Id => SourceId;

    public string? TrashUnavailableReason => ReadOnlyReason;

    public bool Owns(string location) => DriveLocations.IsDrive(location);

    public async IAsyncEnumerable<SourceFile> EnumerateAsync(
        IReadOnlyList<string> folders,
        IScanSink sink,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var visitedFolders = new HashSet<string>(StringComparer.Ordinal);
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var folder in folders)
        {
            var (api, account, folderId, problem) = Resolve(folder, expectFolder: true);
            if (problem is not null)
            {
                sink.AddError(new ScanError(folder, problem));
                continue;
            }

            IReadOnlyList<DriveItem>? chain = null;
            string? chainProblem = null;
            try
            {
                chain = await GetPathFromRootAsync(api!, folderId, cancellationToken).ConfigureAwait(false);
                if (chain is null)
                {
                    chainProblem = "Folder is not in My Drive. Shared drives and files shared with you are not scanned.";
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                chainProblem = DescribeFailure(ex, "Folder could not be read");
            }

            if (chainProblem is not null)
            {
                sink.AddError(new ScanError(folder, chainProblem));
                continue;
            }

            var pending = new Stack<(string Id, string DisplayPath, string[] Ancestors)>();
            pending.Push((folderId, DisplayPathOf(chain!), chain!.Select(i => i.Id).ToArray()));

            while (pending.TryPop(out var current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visitedFolders.Add(current.Id))
                {
                    continue;
                }

                IReadOnlyList<DriveItem> children;
                try
                {
                    children = await api!.ListChildrenAsync(current.Id, foldersOnly: false, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    sink.AddError(new ScanError(current.DisplayPath, DescribeFailure(ex, "Folder could not be read")));
                    continue;
                }

                foreach (var child in children)
                {
                    var displayPath = $"{current.DisplayPath}/{child.Name}";
                    if (child.IsFolder)
                    {
                        pending.Push((child.Id, displayPath, [.. current.Ancestors, child.Id]));
                    }
                    else if (child.IsShortcut)
                    {
                        sink.AddNotice(new ScanNotice(displayPath, "Skipped Google Drive shortcut."));
                    }
                    else if (ImageFormats.IsSupported(child.Name))
                    {
                        if (string.IsNullOrEmpty(child.Md5Checksum))
                        {
                            sink.AddNotice(new ScanNotice(displayPath, "Skipped: Google Drive has no checksum for this file."));
                            continue;
                        }

                        var location = DriveLocations.File(account!.PermissionId, child.Id);
                        if (!seenFiles.Add(location))
                        {
                            continue;
                        }

                        _ancestorsByFile[location] = current.Ancestors.ToHashSet(StringComparer.Ordinal);
                        if (child.ThumbnailLink is { } thumbnailLink)
                        {
                            _thumbnailLinks[location] = thumbnailLink;
                        }

                        yield return new SourceFile(
                            location,
                            displayPath,
                            child.Size ?? 0,
                            child.ModifiedUtc,
                            KnownHash: child.Md5Checksum.ToLowerInvariant());
                    }
                }
            }
        }
    }

    public Task<string> GetHashAsync(SourceFile file, IScanSink sink, CancellationToken cancellationToken) =>
        Task.FromResult(file.KnownHash ?? throw new InvalidOperationException("Google Drive returned no checksum for this file."));

    public bool IsInFolder(ScannedFile file, string folder) =>
        DriveLocations.TryParse(folder, out var folderAccount, out var isFolder, out var folderId) &&
        isFolder &&
        DriveLocations.TryParse(file.Path, out var fileAccount, out _, out _) &&
        folderAccount == fileAccount &&
        _ancestorsByFile.TryGetValue(file.Path, out var ancestors) &&
        ancestors.Contains(folderId);

    public async Task<string?> VerifyAsync(ScannedFile file, CancellationToken cancellationToken)
    {
        var (api, _, fileId, problem) = Resolve(file.Path, expectFolder: false);
        if (problem is not null)
        {
            return problem;
        }

        DriveItem item;
        try
        {
            item = await api!.GetItemAsync(fileId, cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.IsNotFound)
        {
            return "File no longer exists in Google Drive.";
        }

        if (item.Trashed)
        {
            return "File has been moved to the Google Drive trash.";
        }

        if (item.Size != file.Size)
        {
            return "File size has changed since the scan.";
        }

        return string.Equals(item.Md5Checksum, file.Hash, StringComparison.OrdinalIgnoreCase)
            ? null
            : "File contents have changed since the scan.";
    }

    public Task MoveToTrashAsync(ScannedFile file, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(ReadOnlyReason);

    /// <summary>Whether Google refused several thumbnails in a row, so no more are requested.</summary>
    public bool ThumbnailsRefused => Volatile.Read(ref _refusedThumbnailsInARow) >= MaxRefusedThumbnails;

    /// <summary>
    /// Downloads Drive's thumbnail for the file. Uses the link from the scan; when it has expired,
    /// gets a fresh link once. Returns null when there is no preview.
    /// </summary>
    public async Task<byte[]?> GetThumbnailAsync(ScannedFile file, int size, CancellationToken cancellationToken)
    {
        var (api, _, fileId, problem) = Resolve(file.Path, expectFolder: false);
        if (problem is not null || ThumbnailsRefused)
        {
            return null;
        }

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                if (!_thumbnailLinks.TryGetValue(file.Path, out var link))
                {
                    link = (await api!.GetItemAsync(fileId, cancellationToken).ConfigureAwait(false)).ThumbnailLink;
                    if (link is null)
                    {
                        return null; // Drive has no preview for this file.
                    }

                    _thumbnailLinks[file.Path] = link;
                }

                try
                {
                    var bytes = await api!.DownloadThumbnailAsync(DriveThumbnails.WithSize(link, size), cancellationToken)
                        .ConfigureAwait(false);
                    Volatile.Write(ref _refusedThumbnailsInARow, 0);
                    return bytes;
                }
                catch (DriveApiException ex) when (ex.StatusCode is 403 or 404 or 410 && attempt == 1)
                {
                    // The link probably expired (they last a few hours); get a new one.
                    _thumbnailLinks.TryRemove(file.Path, out _);
                }
                catch (DriveApiException ex) when (ex.StatusCode is 401 or 403 or 404 or 410)
                {
                    Interlocked.Increment(ref _refusedThumbnailsInARow);
                    return null;
                }
            }
        }
        catch (Exception ex) when (ex is DriveApiException or DriveSignInRequiredException or HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// The folders from the My Drive root down to <paramref name="folderId"/>, or null when the
    /// folder is not in My Drive (e.g. in a shared drive, or shared with the user).
    /// </summary>
    public static async Task<IReadOnlyList<DriveItem>?> GetPathFromRootAsync(IDriveApi api, string folderId, CancellationToken cancellationToken)
    {
        var root = await api.GetItemAsync("root", cancellationToken).ConfigureAwait(false);
        var chain = new List<DriveItem>();
        var id = folderId;
        while (true)
        {
            var item = id == root.Id ? root : await api.GetItemAsync(id, cancellationToken).ConfigureAwait(false);
            chain.Add(item);
            if (item.Id == root.Id)
            {
                chain.Reverse();
                return chain;
            }

            if (item.Parents.Count == 0 || chain.Count > 100)
            {
                return null;
            }

            id = item.Parents[0];
        }
    }

    /// <summary>"My Drive/Photos/2024" for a chain of folders from the root.</summary>
    public static string DisplayPathOf(IReadOnlyList<DriveItem> chain) =>
        string.Join("/", chain.Select((item, i) => i == 0 ? DriveLocations.RootName : item.Name));

    private (IDriveApi? Api, DriveAccount? Account, string Id, string? Problem) Resolve(string location, bool expectFolder)
    {
        if (!DriveLocations.TryParse(location, out var permissionId, out var isFolder, out var id) || isFolder != expectFolder)
        {
            return (null, null, string.Empty, "Not a valid Google Drive location.");
        }

        if (session.Account is not { } account || session.Api is not { } api)
        {
            return (null, null, id, "Google Drive is not connected.");
        }

        return account.PermissionId != permissionId
            ? (null, null, id, "This location belongs to a different Google account than the one connected.")
            : (api, account, id, null);
    }

    private static string DescribeFailure(Exception ex, string what) => ex switch
    {
        DriveSignInRequiredException => ex.Message,
        DriveApiException { IsNotFound: true } => $"{what}: it no longer exists in Google Drive.",
        _ => $"{what}: {ex.Message}",
    };
}
