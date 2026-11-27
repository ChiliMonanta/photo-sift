namespace PhotoSift.Core.Deletion;

/// <summary>
/// Moves a file to the operating system's trash. Implementations must never delete
/// permanently, and must throw if the file could not be moved to the trash.
/// </summary>
public interface ITrashService
{
    Task MoveToTrashAsync(string path, CancellationToken cancellationToken = default);
}
