using PhotoSift.Core.Caching;
using PhotoSift.Core.Deletion;
using PhotoSift.Core.Import;

namespace PhotoSift.Core.Sorting;

/// <summary>A file that was moved.</summary>
public sealed record MovedFile(string From, string To);

/// <summary>A file that was not moved, and why.</summary>
public sealed record MoveProblem(string Path, string Reason);

public sealed record MoveReport(IReadOnlyList<MovedFile> Moved, IReadOnlyList<MoveProblem> Problems);

/// <summary>What a folder contains, including its subfolders.</summary>
/// <param name="Photos">Photos and videos.</param>
/// <param name="OtherFiles">All other files, including hidden ones.</param>
public sealed record FolderContents(int Photos, int OtherFiles, int Subfolders)
{
    public bool IsEmpty => Photos == 0 && OtherFiles == 0;
}

/// <summary>
/// Creates folders and moves photos between them, for sorting a photo library by hand.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Files are only moved (renamed), never copied and deleted, so a move cannot lose data:
/// moves between drives are refused.</item>
/// <item>Nothing is overwritten: a file with a taken name gets a new name ("IMG_1 (2).jpg").</item>
/// <item>A move can be undone; each file goes back to where it was, if that place is still free.</item>
/// <item>Cached hashes follow the files, so the next duplicate scan does not hash them again.</item>
/// <item>Folders are deleted by moving them to the OS trash, never permanently.</item>
/// </list>
/// </remarks>
public sealed class PhotoSorter(IHashCache? hashCache = null, ITrashService? trash = null)
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] InvalidNameChars =
        Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).Distinct().ToArray();

    /// <summary>Null when <paramref name="name"/> is a valid folder name on every OS; otherwise why not.</summary>
    public static string? ValidateFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a folder name.";
        }

        if (name != name.Trim() || name.EndsWith('.'))
        {
            return "A folder name cannot start or end with a space, or end with a period.";
        }

        if (name.IndexOfAny(InvalidNameChars) >= 0 || name.Any(char.IsControl))
        {
            return "A folder name cannot contain any of these characters: < > : \" / \\ | ? *";
        }

        if (ReservedNames.Contains(name.Split('.')[0]))
        {
            return $"\"{name}\" is reserved by Windows and cannot be used as a folder name.";
        }

        return name.Length > 255 ? "The folder name is too long." : null;
    }

    /// <summary>Creates a subfolder. Throws <see cref="ArgumentException"/> for an invalid name and <see cref="IOException"/> if it exists.</summary>
    public string CreateFolder(string parent, string name)
    {
        if (ValidateFolderName(name) is { } problem)
        {
            throw new ArgumentException(problem, nameof(name));
        }

        var path = Path.Combine(parent, name);
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new IOException($"\"{name}\" already exists in this folder.");
        }

        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Renames a folder in place. Throws <see cref="ArgumentException"/> for an invalid name and
    /// <see cref="IOException"/> if the name is taken. Changing only upper and lower case is allowed.
    /// </summary>
    public string RenameFolder(string folder, string newName)
    {
        if (ValidateFolderName(newName) is { } problem)
        {
            throw new ArgumentException(problem, nameof(newName));
        }

        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var parent = Path.GetDirectoryName(folder) ?? throw new IOException("A drive cannot be renamed.");
        var destination = Path.Combine(parent, newName);
        if (string.Equals(folder, destination, StringComparison.Ordinal))
        {
            return folder;
        }

        var caseOnly = string.Equals(folder, destination, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (Directory.Exists(destination) || File.Exists(destination)))
        {
            throw new IOException($"\"{newName}\" already exists in this folder.");
        }

        if (caseOnly)
        {
            // Some file systems ignore a move that only changes case; go through a temporary name.
            var temporary = Path.Combine(parent, $"{newName}.photosift-rename-{Guid.NewGuid():N}");
            Directory.Move(folder, temporary);
            Directory.Move(temporary, destination);
        }
        else
        {
            Directory.Move(folder, destination);
        }

        return destination;
    }

    /// <summary>Counts what a folder contains, so the user knows what a delete moves to the trash.</summary>
    public static FolderContents CountContents(string folder)
    {
        int photos = 0, other = 0, subfolders = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
        {
            if (entry is DirectoryInfo)
            {
                subfolders++;
            }
            else if (MediaFormats.IsSupported(entry.Name))
            {
                photos++;
            }
            else
            {
                other++;
            }
        }

        return new FolderContents(photos, other, subfolders);
    }

    /// <summary>Moves a folder, with everything in it, to the OS trash, where it can be restored.</summary>
    public async Task MoveFolderToTrashAsync(string folder, CancellationToken cancellationToken = default)
    {
        if (trash is null)
        {
            throw new InvalidOperationException("Moving to the trash is not available.");
        }

        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException("The folder no longer exists.");
        }

        await trash.MoveToTrashAsync(folder, cancellationToken).ConfigureAwait(false);
        if (Directory.Exists(folder))
        {
            throw new IOException("The folder is still there; it could not be moved to the trash.");
        }
    }

    /// <summary>Moves files into <paramref name="targetFolder"/>. Files that cannot be moved are reported, the rest are moved.</summary>
    public MoveReport Move(IEnumerable<string> files, string targetFolder, CancellationToken cancellationToken = default)
    {
        var moved = new List<MovedFile>();
        var problems = new List<MoveProblem>();
        if (!Directory.Exists(targetFolder))
        {
            return new MoveReport([], files.Select(f => new MoveProblem(f, "The target folder no longer exists.")).ToList());
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reason = MoveOne(file, targetFolder, out var destination);
            if (reason is null)
            {
                moved.Add(new MovedFile(file, destination!));
            }
            else
            {
                problems.Add(new MoveProblem(file, reason));
            }
        }

        return new MoveReport(moved, problems);
    }

    /// <summary>Moves files back to where they were before <paramref name="report"/>'s move.</summary>
    public MoveReport Undo(MoveReport report)
    {
        var moved = new List<MovedFile>();
        var problems = new List<MoveProblem>();
        foreach (var file in report.Moved)
        {
            try
            {
                if (!File.Exists(file.To))
                {
                    problems.Add(new MoveProblem(file.To, "The file is no longer there."));
                    continue;
                }

                if (File.Exists(file.From) || Directory.Exists(file.From))
                {
                    problems.Add(new MoveProblem(file.To, $"{file.From} is taken by another file now."));
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(file.From)!);
                MoveFile(file.To, file.From);
                moved.Add(new MovedFile(file.To, file.From));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(new MoveProblem(file.To, ex.Message));
            }
        }

        return new MoveReport(moved, problems);
    }

    private string? MoveOne(string file, string targetFolder, out string? destination)
    {
        destination = null;
        try
        {
            if (!File.Exists(file))
            {
                return "The file is no longer there.";
            }

            if (PathUtil.Comparer.Equals(PathUtil.Normalize(Path.GetDirectoryName(file)!), PathUtil.Normalize(targetFolder)))
            {
                return "The file is already in that folder.";
            }

            if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(file)), Path.GetPathRoot(Path.GetFullPath(targetFolder)),
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Files are only moved within the same drive.";
            }

            destination = UniquePath(Path.Combine(targetFolder, Path.GetFileName(file)));
            MoveFile(file, destination);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            destination = null;
            return ex.Message;
        }
    }

    private void MoveFile(string from, string to)
    {
        var info = new FileInfo(from);
        var cached = hashCache?.TryGet(from, info.Length, info.LastWriteTimeUtc);

        // overwrite: false, so a file that appeared at the destination meanwhile is never replaced.
        File.Move(from, to, overwrite: false);

        if (cached is not null)
        {
            try
            {
                hashCache!.Set(to, info.Length, info.LastWriteTimeUtc, cached);
            }
            catch (Exception)
            {
                // The cache is only an optimization.
            }
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(directory, $"{name} ({n}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
