namespace PhotoSift.Core.Caching;

/// <summary>
/// Caches file hashes between scans. An entry is only valid while the file's
/// size and last write time are unchanged. Implementations must be thread-safe.
/// </summary>
public interface IHashCache
{
    string? TryGet(string path, long size, DateTime lastWriteUtc);

    void Set(string path, long size, DateTime lastWriteUtc, string hash);
}

public sealed class NullHashCache : IHashCache
{
    public static NullHashCache Instance { get; } = new();

    public string? TryGet(string path, long size, DateTime lastWriteUtc) => null;

    public void Set(string path, long size, DateTime lastWriteUtc, string hash)
    {
    }
}
