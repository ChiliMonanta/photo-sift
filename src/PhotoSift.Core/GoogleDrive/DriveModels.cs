namespace PhotoSift.Core.GoogleDrive;

/// <summary>A file or folder in Google Drive, with the metadata PhotoSift uses.</summary>
/// <param name="Md5Checksum">Drive's MD5 of the contents; missing for Google Docs and some other files.</param>
/// <param name="Parents">Ids of the parent folders; empty for the My Drive root.</param>
public sealed record DriveItem(
    string Id,
    string Name,
    string MimeType,
    long? Size,
    string? Md5Checksum,
    DateTime ModifiedUtc,
    IReadOnlyList<string> Parents,
    bool Trashed = false)
{
    public const string FolderMimeType = "application/vnd.google-apps.folder";
    public const string ShortcutMimeType = "application/vnd.google-apps.shortcut";

    public bool IsFolder => MimeType == FolderMimeType;

    public bool IsShortcut => MimeType == ShortcutMimeType;
}

/// <summary>The signed-in Google account.</summary>
/// <param name="PermissionId">Google's stable id for the account; used in locations.</param>
public sealed record DriveAccount(string PermissionId, string Email);

/// <summary>Read-only access to the signed-in user's Google Drive.</summary>
public interface IDriveApi
{
    Task<DriveAccount> GetAccountAsync(CancellationToken cancellationToken);

    /// <summary>Gets one item. Use "root" for the My Drive root folder. Throws <see cref="DriveApiException"/> (404) if it does not exist.</summary>
    Task<DriveItem> GetItemAsync(string id, CancellationToken cancellationToken);

    /// <summary>Lists the items in a folder that are not in the trash, sorted by name.</summary>
    Task<IReadOnlyList<DriveItem>> ListChildrenAsync(string folderId, bool foldersOnly, CancellationToken cancellationToken);
}

/// <summary>The signed-in Drive account and its API, or nulls when not connected.</summary>
public interface IDriveSession
{
    DriveAccount? Account { get; }

    IDriveApi? Api { get; }
}

/// <summary>An error response from the Google Drive API.</summary>
public sealed class DriveApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public bool IsNotFound => StatusCode == 404;
}

/// <summary>Thrown when the user must connect Google Drive again (no account, or the sign-in expired or was revoked).</summary>
public sealed class DriveSignInRequiredException(string message) : Exception(message);
