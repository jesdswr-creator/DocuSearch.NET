using Microsoft.Data.Sqlite;
using DocuSearch.Core.Models;
using DocuSearch.Core.Data;

namespace DocuSearch.Core.Services;

/// <summary>
/// Handles full-text search via SQLite FTS5.
/// Supports keyword (BM25) search with snippet generation.
/// </summary>
public class SearchService
{
    private readonly Database _db;

    public SearchService(Database db)
    {
        _db = db;
    }

    /// <summary>
    /// Search for files by keyword. Returns top N results sorted by BM25 score.
    /// </summary>
    public List<SearchHit> Search(string query, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<SearchHit>();

        var hits = new List<SearchHit>();

        // Try FTS5 content search first
        try
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = """
                SELECT f.id, f.filename, f.path, f.extension, f.size, f.modified_date,
                       f.is_favorite, snippet(SearchIndex, 1, '<<', '>>', '...', 20) as snippet,
                       bm25(SearchIndex) as score
                FROM SearchIndex
                JOIN Files f ON f.id = SearchIndex.file_id
                WHERE SearchIndex MATCH @query
                ORDER BY score
                LIMIT @limit;
            """;
            cmd.Parameters.AddWithValue("@query", query);
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                hits.Add(new SearchHit
                {
                    FileId = reader.GetInt64(0),
                    Filename = reader.GetString(1),
                    Path = reader.GetString(2),
                    Extension = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Size = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                    ModifiedDate = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)).DateTime,
                    IsFavorite = !reader.IsDBNull(6) && reader.GetBoolean(6),
                    Snippet = reader.IsDBNull(7) ? "" : reader.GetString(7),
                    Score = reader.IsDBNull(8) ? 0 : -reader.GetDouble(8) // FTS5 bm25 is negative (smaller = more relevant)
                });
            }
        }
        catch
        {
            // FTS5 query parse error — fall back to filename search
        }

        // If FTS5 returned nothing, try filename LIKE search
        if (hits.Count == 0)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, filename, path, extension, size, modified_date, is_favorite
                FROM Files
                WHERE filename LIKE @pattern
                ORDER BY modified_date DESC
                LIMIT @limit;
            """;
            cmd.Parameters.AddWithValue("@pattern", $"%{query}%");
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                hits.Add(new SearchHit
                {
                    FileId = reader.GetInt64(0),
                    Filename = reader.GetString(1),
                    Path = reader.GetString(2),
                    Extension = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Size = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                    ModifiedDate = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)).DateTime,
                    IsFavorite = !reader.IsDBNull(6) && reader.GetBoolean(6),
                    Snippet = "",
                    Score = 0
                });
            }
        }

        return hits;
    }

    /// <summary>
    /// Find duplicate files by SHA-256 hash.
    /// </summary>
    public List<SearchHit> FindDuplicates()
    {
        var hits = new List<SearchHit>();

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.id, f.path, f.filename, f.extension, f.size, f.modified_date, f.hash
            FROM Files f
            WHERE f.hash != '' AND f.hash IN (
                SELECT hash FROM Files WHERE hash != ''
                GROUP BY hash HAVING COUNT(*) > 1
            )
            ORDER BY f.hash, f.filename;
        """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(new SearchHit
            {
                FileId = reader.GetInt64(0),
                Path = reader.GetString(1),
                Filename = reader.GetString(2),
                Extension = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Size = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                ModifiedDate = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)).DateTime,
            });
        }

        return hits;
    }

    /// <summary>
    /// Get the extracted text for a file.
    /// </summary>
    public string GetExtractedText(long fileId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT extracted_text FROM DocumentText WHERE file_id = @id;";
        cmd.Parameters.AddWithValue("@id", fileId);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? "" : (string)result;
    }

    /// <summary>
    /// Get file metadata by ID.
    /// </summary>
    public FileRecord? GetFileById(long fileId)
    {
        var files = _db.QueryFiles(
            "SELECT id, path, filename, extension, size, created_date, modified_date, hash, indexing_status, ocr_status, is_favorite FROM Files WHERE id = @id;",
            ("@id", fileId));
        return files.FirstOrDefault();
    }

    /// <summary>
    /// Get total file count and database size.
    /// </summary>
    public (long totalFiles, long dbSize) GetStats()
    {
        var count = _db.ExecuteScalar<long>("SELECT COUNT(*) FROM Files;") ?? 0;
        var fileInfo = new System.IO.FileInfo(_db.Connection.DataSource);
        return (count, fileInfo.Length);
    }
}
