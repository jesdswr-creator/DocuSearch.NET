using Microsoft.Data.Sqlite;
using DocuSearch.Core.Models;

namespace DocuSearch.Core.Data;

/// <summary>
/// SQLite database wrapper — handles connection, schema creation, and migrations.
/// Uses WAL mode for concurrent read/write access.
/// </summary>
public class Database : IDisposable
{
    private SqliteConnection? _connection;
    private readonly string _dbPath;
    private readonly object _lock = new();

    public Database(string dbPath)
    {
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? ".");
    }

    public void Open()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();

        // WAL mode for concurrent reads + single writer
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("PRAGMA busy_timeout=30000;");
        Execute("PRAGMA foreign_keys=ON;");

        InitializeSchema();
    }

    public SqliteConnection Connection
    {
        get
        {
            if (_connection == null)
                throw new InvalidOperationException("Database not opened.");
            return _connection;
        }
    }

    private void InitializeSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS Files (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                path            TEXT UNIQUE NOT NULL,
                filename        TEXT NOT NULL,
                extension       TEXT NOT NULL DEFAULT '',
                size            INTEGER NOT NULL DEFAULT 0,
                created_date    INTEGER NOT NULL DEFAULT 0,
                modified_date   INTEGER NOT NULL DEFAULT 0,
                hash            TEXT    DEFAULT '',
                indexing_status TEXT    DEFAULT 'metadata_only',
                ocr_status      TEXT    DEFAULT 'pending',
                is_favorite     INTEGER DEFAULT 0
            );
        """);

        Execute("CREATE INDEX IF NOT EXISTS idx_files_filename ON Files(filename);");
        Execute("CREATE INDEX IF NOT EXISTS idx_files_extension ON Files(extension);");
        Execute("CREATE INDEX IF NOT EXISTS idx_files_modified ON Files(modified_date);");
        Execute("CREATE INDEX IF NOT EXISTS idx_files_status ON Files(indexing_status);");
        Execute("CREATE INDEX IF NOT EXISTS idx_files_hash ON Files(hash);");
        Execute("CREATE INDEX IF NOT EXISTS idx_files_favorite ON Files(is_favorite);");

        Execute("""
            CREATE TABLE IF NOT EXISTS DocumentText (
                file_id         INTEGER PRIMARY KEY REFERENCES Files(id) ON DELETE CASCADE,
                extracted_text  TEXT,
                text_source     TEXT DEFAULT 'native',
                char_count      INTEGER DEFAULT 0,
                updated_at      INTEGER DEFAULT 0
            );
        """);

        Execute("""
            CREATE VIRTUAL TABLE IF NOT EXISTS SearchIndex USING fts5(
                filename, content, path, extension, file_id UNINDEXED,
                tokenize='trigram'
            );
        """);

        Execute("""
            CREATE TABLE IF NOT EXISTS Tags (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id INTEGER NOT NULL REFERENCES Files(id) ON DELETE CASCADE,
                tag     TEXT NOT NULL,
                UNIQUE(file_id, tag)
            );
        """);
        Execute("CREATE INDEX IF NOT EXISTS idx_tags_file ON Tags(file_id);");
        Execute("CREATE INDEX IF NOT EXISTS idx_tags_tag ON Tags(tag);");

        Execute("""
            CREATE TABLE IF NOT EXISTS Notes (
                file_id INTEGER PRIMARY KEY REFERENCES Files(id) ON DELETE CASCADE,
                note    TEXT DEFAULT '',
                updated_at INTEGER DEFAULT 0
            );
        """);

        Execute("""
            CREATE TABLE IF NOT EXISTS SavedSearches (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                name    TEXT NOT NULL,
                query   TEXT NOT NULL,
                created_at INTEGER DEFAULT 0
            );
        """);

        Execute("""
            CREATE TABLE IF NOT EXISTS Settings (
                key   TEXT PRIMARY KEY,
                value TEXT
            );
        """);
    }

    public int Execute(string sql, params (string, object?)[] parameters)
    {
        lock (_lock)
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
            return cmd.ExecuteNonQuery();
        }
    }

    public T? ExecuteScalar<T>(string sql, params (string, object?)[] parameters) where T : struct
    {
        lock (_lock)
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            var result = cmd.ExecuteScalar();
            return result is DBNull or null ? null : (T)Convert.ChangeType(result, typeof(T));
        }
    }

    public List<FileRecord> QueryFiles(string sql, params (string, object?)[] parameters)
    {
        lock (_lock)
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            using var reader = cmd.ExecuteReader();
            var results = new List<FileRecord>();
            while (reader.Read())
            {
                results.Add(new FileRecord
                {
                    Id = reader.GetInt64(0),
                    Path = reader.GetString(1),
                    Filename = reader.GetString(2),
                    Extension = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Size = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                    CreatedDate = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                    ModifiedDate = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                    Hash = reader.IsDBNull(7) ? "" : reader.GetString(7),
                    IndexingStatus = reader.IsDBNull(8) ? "metadata_only" : reader.GetString(8),
                    OcrStatus = reader.IsDBNull(9) ? "pending" : reader.GetString(9),
                    IsFavorite = !reader.IsDBNull(10) && reader.GetBoolean(10)
                });
            }
            return results;
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
