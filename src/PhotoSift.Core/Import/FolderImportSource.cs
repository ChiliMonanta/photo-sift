using System.Runtime.CompilerServices;

namespace PhotoSift.Core.Import;

/// <summary>
/// Imports from a folder, e.g. a camera's memory card, or a phone that the OS shows as a folder
/// (such as Android on Linux through gvfs). Symbolic links are not followed.
/// </summary>
public sealed class FolderImportSource : IImportSource
{
    public FolderImportSource(string folder)
    {
        Folder = PathUtil.Normalize(folder);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder));
        DisplayName = string.IsNullOrEmpty(name) ? Folder.TrimEnd(Path.DirectorySeparatorChar, ':') : name;
    }

    public string Folder { get; }

    public string Id => "folder:" + Folder;

    public string DisplayName { get; }

    public async IAsyncEnumerable<ImportItem> EnumerateAsync(IProgress<int>? found, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Folder))
        {
            throw new DirectoryNotFoundException($"The folder {Folder} does not exist or is not accessible.");
        }

        var count = 0;
        var pending = new Stack<string>([Folder]);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (entry.LinkTarget is not null)
                {
                    continue;
                }

                if (entry is DirectoryInfo subdirectory)
                {
                    pending.Push(subdirectory.FullName);
                }
                else if (entry is FileInfo file && MediaFormats.IsSupported(file.Name))
                {
                    found?.Report(++count);
                    yield return new ImportItem(
                        file.FullName,
                        Path.GetRelativePath(Folder, file.FullName).Replace(Path.DirectorySeparatorChar, '/'),
                        file.Length,
                        file.LastWriteTimeUtc);
                }
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task<Stream> OpenReadAsync(ImportItem item, CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(new FileStream(item.Key, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous));

    public void Dispose()
    {
    }
}
