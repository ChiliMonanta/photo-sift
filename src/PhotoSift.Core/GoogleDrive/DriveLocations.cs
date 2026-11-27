namespace PhotoSift.Core.GoogleDrive;

/// <summary>
/// Locations of Google Drive items: <c>gdrive://&lt;permissionId&gt;/file/&lt;fileId&gt;</c> and
/// <c>gdrive://&lt;permissionId&gt;/folder/&lt;folderId&gt;</c>. The account is part of the location,
/// so items from different Google accounts never compare equal.
/// </summary>
public static class DriveLocations
{
    public const string Scheme = "gdrive://";

    /// <summary>Display name of the My Drive root folder.</summary>
    public const string RootName = "My Drive";

    public static string File(string permissionId, string fileId) => $"{Scheme}{permissionId}/file/{fileId}";

    public static string Folder(string permissionId, string folderId) => $"{Scheme}{permissionId}/folder/{folderId}";

    public static bool IsDrive(string location) => location.StartsWith(Scheme, StringComparison.Ordinal);

    public static bool TryParse(string location, out string permissionId, out bool isFolder, out string id)
    {
        permissionId = id = string.Empty;
        isFolder = false;
        if (!IsDrive(location))
        {
            return false;
        }

        var parts = location[Scheme.Length..].Split('/');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[2].Length == 0 || parts[1] is not ("file" or "folder"))
        {
            return false;
        }

        permissionId = parts[0];
        isFolder = parts[1] == "folder";
        id = parts[2];
        return true;
    }
}
