using System.Security.Cryptography;

namespace PhotoSift.Core.Hashing;

public sealed class Md5FileHasher : IFileHasher
{
    private const int BufferSize = 1024 * 1024;

    public async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hash = await MD5.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
