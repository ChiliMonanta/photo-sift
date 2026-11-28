using PhotoSift.Core.Caching;

namespace PhotoSift.Core.Sorting;

/// <summary>A file that was moved.</summary>
public sealed record MovedFile(string From, string To);

/// <summary>A file that was not moved, and why.</summary>
public sealed record MoveProblem(string Path, string Reason);

public sealed record MoveReport(IReadOnlyList<MovedFile> Moved, IReadOnlyList<MoveProblem> Problems);

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
/// </list>
/// </remarks>
public sealed class PhotoSorter(IHashCache? hashCache = null)
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
