using PhotoSift.Core.Hashing;
using PhotoSift.Core.Scanning;
using PhotoSift.Core.Sources;

namespace PhotoSift.Core.Deletion;

public enum DeletionStatus
{
    MovedToTrash,
    Skipped,
    Failed,
}

public sealed record DeletionOutcome(string Path, DeletionStatus Status, string? Reason)
{
    /// <summary>Human-readable path shown to the user. Equal to <see cref="Path"/> for local files.</summary>
    public string DisplayPath { get; init; } = Path;
}

public sealed class DeletionReport(IReadOnlyList<DeletionOutcome> outcomes)
{
    public IReadOnlyList<DeletionOutcome> Outcomes { get; } = outcomes;

    public int MovedToTrashCount => Outcomes.Count(o => o.Status == DeletionStatus.MovedToTrash);

    public int SkippedCount => Outcomes.Count(o => o.Status == DeletionStatus.Skipped);

    public int FailedCount => Outcomes.Count(o => o.Status == DeletionStatus.Failed);
}

/// <summary>
/// Moves selected duplicates to the trash after revalidating them against the scan.
/// </summary>
/// <remarks>
/// Safety rules:
/// <list type="bullet">
/// <item>Only complete scans can be used.</item>
/// <item>Each file must still exist with the size and hash recorded during the scan.</item>
/// <item>At least one copy in each group must be kept, and one kept copy must still match
/// the scanned hash, so the content is never lost. Kept copies may be in any source.</item>
/// <item>Each file is verified and trashed by its own source; files from sources that cannot
/// move files to the trash (e.g. read-only access) are skipped and reported.</item>
/// </list>
/// </remarks>
public sealed class SafeDeleter(FileSources sources)
{
    /// <summary>Deletes from the local file system only.</summary>
    public SafeDeleter(IFileHasher hasher, ITrashService trash)
        : this(new FileSources(new LocalFileSource(hasher, trash: trash)))
    {
    }

    public FileSources Sources => sources;

    public async Task<DeletionReport> MoveToTrashAsync(
        ScanResult scan,
        IEnumerable<string> selectedPaths,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!scan.IsComplete)
        {
            throw new InvalidOperationException("Files can only be deleted from a complete scan.");
        }

        var selected = new HashSet<string>(selectedPaths, PathUtil.Comparer);
        var outcomes = new List<DeletionOutcome>();
        var processed = 0;

        var byGroup = new Dictionary<DuplicateGroup, List<ScannedFile>>();
        foreach (var path in selected.Order(PathUtil.Comparer))
        {
            var group = scan.FindGroup(path);
            if (group is null)
            {
                outcomes.Add(new DeletionOutcome(path, DeletionStatus.Skipped, "File is not part of a duplicate group in this scan."));
                progress?.Report(++processed);
                continue;
            }

            if (!byGroup.TryGetValue(group, out var files))
            {
                byGroup[group] = files = [];
            }

            files.Add(group.Files.First(f => PathUtil.Comparer.Equals(f.Path, path)));
        }

        foreach (var (group, filesToTrash) in byGroup)
        {
            var kept = group.Files.Where(f => !selected.Contains(f.Path)).ToList();

            string? groupSkipReason = null;
            if (cancellationToken.IsCancellationRequested)
            {
                groupSkipReason = "Deletion was canceled.";
            }
            else if (kept.Count == 0)
            {
                groupSkipReason = "Every copy in this group is selected; at least one copy must be kept.";
            }
            else if (!await AnyCopyVerifiedAsync(kept, cancellationToken).ConfigureAwait(false))
            {
                groupSkipReason = "None of the kept copies still matches the scan, so deleting could lose the only intact copy.";
            }

            foreach (var file in filesToTrash)
            {
                outcomes.Add(groupSkipReason is not null
                    ? Outcome(file, DeletionStatus.Skipped, groupSkipReason)
                    : await TrashFileAsync(file, cancellationToken).ConfigureAwait(false));
                progress?.Report(++processed);
            }
        }

        return new DeletionReport(outcomes);
    }

    private async Task<DeletionOutcome> TrashFileAsync(ScannedFile file, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Outcome(file, DeletionStatus.Skipped, "Deletion was canceled.");
        }

        var source = sources.Get(file.SourceId);
        if (source is null)
        {
            return Outcome(file, DeletionStatus.Skipped, "The file's source is not available.");
        }

        if (source.TrashUnavailableReason is { } unavailable)
        {
            return Outcome(file, DeletionStatus.Skipped, unavailable);
        }

        var problem = await VerifyAsync(file, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return Outcome(file, DeletionStatus.Skipped, problem);
        }

        try
        {
            await source.MoveToTrashAsync(file, cancellationToken).ConfigureAwait(false);
            return Outcome(file, DeletionStatus.MovedToTrash, null);
        }
        catch (Exception ex)
        {
            return Outcome(file, DeletionStatus.Failed, $"Could not move to trash: {ex.Message}");
        }
    }

    private static DeletionOutcome Outcome(ScannedFile file, DeletionStatus status, string? reason) =>
        new(file.Path, status, reason) { DisplayPath = file.DisplayPath };

    private async Task<bool> AnyCopyVerifiedAsync(IEnumerable<ScannedFile> copies, CancellationToken cancellationToken)
    {
        foreach (var copy in copies)
        {
            if (await VerifyAsync(copy, cancellationToken).ConfigureAwait(false) is null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns null when the file still matches the scan, otherwise the reason it does not.</summary>
    private async Task<string?> VerifyAsync(ScannedFile file, CancellationToken cancellationToken)
    {
        var source = sources.Get(file.SourceId);
        if (source is null)
        {
            return "The file's source is not available.";
        }

        try
        {
            return await source.VerifyAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return "Deletion was canceled.";
        }
        catch (Exception ex)
        {
            return $"File could not be verified: {ex.Message}";
        }
    }
}
