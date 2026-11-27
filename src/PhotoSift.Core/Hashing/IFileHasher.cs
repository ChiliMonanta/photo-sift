namespace PhotoSift.Core.Hashing;

public interface IFileHasher
{
    /// <summary>Computes the content hash of a file as a lowercase hexadecimal string.</summary>
    Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken = default);
}
