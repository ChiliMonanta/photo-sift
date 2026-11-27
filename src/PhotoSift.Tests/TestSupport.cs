using System.Collections.Concurrent;
using PhotoSift.Core.Deletion;
using PhotoSift.Core.Hashing;

namespace PhotoSift.Tests;

/// <summary>A temporary folder with generated test images. Deleted on dispose.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "PhotoSift.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(string relativePath) => Path.GetFullPath(Path.Combine(Root, relativePath));

    /// <summary>Writes a generated fake image whose contents are determined by <paramref name="seed"/>.</summary>
    public string WriteImage(string relativePath, int seed, int length = 4096)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        // Fake JPEG start-of-image marker; content is otherwise random.
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        return WriteBytes(relativePath, bytes);
    }

    public string WriteBytes(string relativePath, byte[] bytes)
    {
        var path = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp folder.
        }
    }
}

public sealed class CountingHasher(IFileHasher inner) : IFileHasher
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return inner.ComputeHashAsync(path, cancellationToken);
    }
}

/// <summary>Simulates unreadable files by throwing for the given paths.</summary>
public sealed class FailingHasher(IFileHasher inner, params string[] failingPaths) : IFileHasher
{
    public Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken = default) =>
        failingPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
            ? throw new UnauthorizedAccessException("Access denied (simulated).")
            : inner.ComputeHashAsync(path, cancellationToken);
}

/// <summary>Records trashed paths and removes the temp file to simulate a move to the trash.</summary>
public sealed class FakeTrashService : ITrashService
{
    public ConcurrentQueue<string> Trashed { get; } = new();

    public HashSet<string> FailFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task MoveToTrashAsync(string path, CancellationToken cancellationToken = default)
    {
        if (FailFor.Contains(path))
        {
            throw new IOException("Trash is not available (simulated).");
        }

        File.Delete(path);
        Trashed.Enqueue(path);
        return Task.CompletedTask;
    }
}
