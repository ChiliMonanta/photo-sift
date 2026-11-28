using Microsoft.Data.Sqlite;

namespace PhotoSift.Core.Import;

/// <summary>A file the journal knows about.</summary>
/// <param name="PartialPath">While copying: the temporary file being written.</param>
public sealed record JournalEntry(
    string RelativePath,
    long Size,
    JournalStatus Status,
    string? DestinationPath,
    string? PartialPath,
    string? Md5);

public enum JournalStatus
{
    /// <summary>The copy was started and has not finished. After a crash, its partial file is incomplete.</summary>
    Copying = 1,

    Done = 2,
}

/// <summary>
/// Remembers, per source, which files are imported and which copy is in progress, so an import
/// can continue after it is canceled, the app closes, or the phone is disconnected. Every change
/// is written to disk before the next step of the copy.
/// </summary>
public sealed class ImportJournal : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Lock _lock = new();

    public ImportJournal(string databasePath)
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
            CREATE TABLE IF NOT EXISTS imported_files (
                source_id TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                size INTEGER NOT NULL,
                status INTEGER NOT NULL,
                destination_path TEXT,
                partial_path TEXT,
                md5 TEXT,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (source_id, relative_path)
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
            "import-journal.db");

    public IReadOnlyDictionary<string, JournalEntry> GetEntries(string sourceId)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT relative_path, size, status, destination_path, partial_path, md5 FROM imported_files WHERE source_id = $source";
            command.Parameters.AddWithValue("$source", sourceId);
            using var reader = command.ExecuteReader();
            var entries = new Dictionary<string, JournalEntry>(StringComparer.Ordinal);
            while (reader.Read())
            {
                var entry = new JournalEntry(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    (JournalStatus)reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5));
                entries[entry.RelativePath] = entry;
            }

            return entries;
        }
    }

    /// <summary>Records that a copy is starting, before the first byte is written.</summary>
    public void MarkCopying(string sourceId, ImportItem item, string partialPath) =>
        Upsert(sourceId, item.RelativePath, item.Size, JournalStatus.Copying, null, partialPath, null);

    public void MarkDone(string sourceId, ImportItem item, string destinationPath, string md5) =>
        Upsert(sourceId, item.RelativePath, item.Size, JournalStatus.Done, destinationPath, null, md5);

    /// <summary>Forgets a file, e.g. after a failed copy whose partial file was removed; it is copied next time.</summary>
    public void Remove(string sourceId, string relativePath)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM imported_files WHERE source_id = $source AND relative_path = $path";
            command.Parameters.AddWithValue("$source", sourceId);
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
        string sourceId, string relativePath, long size, JournalStatus status, string? destination, string? partial, string? md5)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO imported_files (source_id, relative_path, size, status, destination_path, partial_path, md5, updated_utc)
                VALUES ($source, $path, $size, $status, $destination, $partial, $md5, $updated)
                ON CONFLICT(source_id, relative_path) DO UPDATE SET
                    size = excluded.size,
                    status = excluded.status,
                    destination_path = excluded.destination_path,
                    partial_path = excluded.partial_path,
                    md5 = excluded.md5,
                    updated_utc = excluded.updated_utc
                """;
            command.Parameters.AddWithValue("$source", sourceId);
            command.Parameters.AddWithValue("$path", relativePath);
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$status", (int)status);
            command.Parameters.AddWithValue("$destination", (object?)destination ?? DBNull.Value);
            command.Parameters.AddWithValue("$partial", (object?)partial ?? DBNull.Value);
            command.Parameters.AddWithValue("$md5", (object?)md5 ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
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
