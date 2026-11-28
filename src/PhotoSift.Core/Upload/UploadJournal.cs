using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PhotoSift.Core.Upload;

public enum UploadStatus
{
    /// <summary>An upload session was started and has not finished; it can be resumed.</summary>
    Uploading = 1,

    Done = 2,
}

/// <summary>A file the upload journal knows about, for one local folder and Drive folder.</summary>
/// <param name="ModifiedUtc">The local file's last write time when it was uploaded; a changed file is checked again.</param>
/// <param name="Session">While uploading: the resumable upload session.</param>
/// <param name="DriveName">While uploading: the name the file gets in Drive.</param>
/// <param name="ParentId">While uploading: the Drive folder it goes in.</param>
public sealed record UploadJournalEntry(
    string RelativePath,
    long Size,
    DateTime ModifiedUtc,
    UploadStatus Status,
    string? Md5,
    Uri? Session,
    string? DriveName,
    string? ParentId,
    string? DriveFileId);

/// <summary>
/// Remembers which files were uploaded from a local folder to a Drive folder, and which upload is
/// in progress, so an upload can continue after it is canceled, the app closes, or the network drops.
/// </summary>
/// <remarks>
/// Upload session addresses are stored here while an upload runs. They are only usable with the
/// user's access token for about a week, and the database is in the user's own app data folder.
/// </remarks>
public sealed class UploadJournal : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Lock _lock = new();

    public UploadJournal(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _connection.Open();

        Execute(
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            CREATE TABLE IF NOT EXISTS uploaded_files (
                target_id TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                size INTEGER NOT NULL,
                modified_ticks INTEGER NOT NULL,
                status INTEGER NOT NULL,
                md5 TEXT,
                session_uri TEXT,
                drive_name TEXT,
                parent_id TEXT,
                drive_file_id TEXT,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (target_id, relative_path)
            );
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
    }

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "PhotoSift",
            "upload-journal.db");

    public IReadOnlyDictionary<string, UploadJournalEntry> GetEntries(string targetId)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT relative_path, size, modified_ticks, status, md5, session_uri, drive_name, parent_id, drive_file_id
                FROM uploaded_files WHERE target_id = $target
                """;
            command.Parameters.AddWithValue("$target", targetId);
            using var reader = command.ExecuteReader();
            var entries = new Dictionary<string, UploadJournalEntry>(StringComparer.Ordinal);
            while (reader.Read())
            {
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                var session = Text(5);
                var entry = new UploadJournalEntry(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    new DateTime(reader.GetInt64(2), DateTimeKind.Utc),
                    (UploadStatus)reader.GetInt32(3),
                    Text(4),
                    session is null ? null : new Uri(session),
                    Text(6),
                    Text(7),
                    Text(8));
                entries[entry.RelativePath] = entry;
            }

            return entries;
        }
    }

    /// <summary>Records a started upload session before the first byte is sent, so it can be resumed.</summary>
    public void MarkUploading(string targetId, UploadItem item, Uri session, string driveName, string parentId) =>
        Upsert(targetId, item, UploadStatus.Uploading, session, driveName, parentId, null);

    public void MarkDone(string targetId, UploadItem item, string driveFileId) =>
        Upsert(targetId, item, UploadStatus.Done, null, null, null, driveFileId);

    /// <summary>Forgets a file; it is checked again next time.</summary>
    public void Remove(string targetId, string relativePath)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM uploaded_files WHERE target_id = $target AND relative_path = $path";
            command.Parameters.AddWithValue("$target", targetId);
            command.Parameters.AddWithValue("$path", relativePath);
            command.ExecuteNonQuery();
        }
    }

    public string? GetSetting(string key)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    public void Dispose() => _connection.Dispose();

    private void Upsert(
        string targetId, UploadItem item, UploadStatus status, Uri? session, string? driveName, string? parentId, string? fileId)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO uploaded_files (target_id, relative_path, size, modified_ticks, status, md5, session_uri, drive_name,
                                            parent_id, drive_file_id, updated_utc)
                VALUES ($target, $path, $size, $modified, $status, $md5, $session, $name, $parent, $file, $updated)
                ON CONFLICT(target_id, relative_path) DO UPDATE SET
                    size = excluded.size,
                    modified_ticks = excluded.modified_ticks,
                    status = excluded.status,
                    md5 = excluded.md5,
                    session_uri = excluded.session_uri,
                    drive_name = excluded.drive_name,
                    parent_id = excluded.parent_id,
                    drive_file_id = excluded.drive_file_id,
                    updated_utc = excluded.updated_utc
                """;
            command.Parameters.AddWithValue("$target", targetId);
            command.Parameters.AddWithValue("$path", item.File.RelativePath);
            command.Parameters.AddWithValue("$size", item.File.Size);
            command.Parameters.AddWithValue("$modified", (item.File.ModifiedUtc ?? DateTime.UnixEpoch).Ticks);
            command.Parameters.AddWithValue("$status", (int)status);
            command.Parameters.AddWithValue("$md5", item.Md5);
            command.Parameters.AddWithValue("$session", (object?)session?.AbsoluteUri ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", (object?)driveName ?? DBNull.Value);
            command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
            command.Parameters.AddWithValue("$file", (object?)fileId ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
