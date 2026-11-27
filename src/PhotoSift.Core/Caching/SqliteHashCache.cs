using Microsoft.Data.Sqlite;

namespace PhotoSift.Core.Caching;

/// <summary>
/// SQLite-backed hash cache. Uses a single connection guarded by a lock so it can be
/// shared by the scanner's worker pool.
/// </summary>
public sealed class SqliteHashCache : IHashCache, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Lock _lock = new();

    public SqliteHashCache(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS file_hashes (
                path TEXT NOT NULL PRIMARY KEY,
                size INTEGER NOT NULL,
                last_write_ticks INTEGER NOT NULL,
                md5 TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "PhotoSift",
            "hash-cache.db");

    public string? TryGet(string path, long size, DateTime lastWriteUtc)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT md5 FROM file_hashes WHERE path = $path AND size = $size AND last_write_ticks = $ticks";
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$ticks", lastWriteUtc.ToUniversalTime().Ticks);
            return command.ExecuteScalar() as string;
        }
    }

    public void Set(string path, long size, DateTime lastWriteUtc, string hash)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO file_hashes (path, size, last_write_ticks, md5)
                VALUES ($path, $size, $ticks, $md5)
                ON CONFLICT(path) DO UPDATE SET
                    size = excluded.size,
                    last_write_ticks = excluded.last_write_ticks,
                    md5 = excluded.md5
                """;
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$ticks", lastWriteUtc.ToUniversalTime().Ticks);
            command.Parameters.AddWithValue("$md5", hash);
            command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _connection.Dispose();
        }
    }
}
