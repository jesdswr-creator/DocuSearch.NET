using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using DocuSearch.Core.Models;
using DocuSearch.Core.Data;

namespace DocuSearch.Core.Services;

/// <summary>
/// Indexes files by walking directories, computing hashes, and inserting
/// metadata into the database. Runs on a background thread.
/// </summary>
public class IndexingService
{
    private readonly Database _db;
    private readonly bool _hashEnabled;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "doc", "docx", "xls", "xlsx", "xlsm",
        "ppt", "pptx", "txt", "rtf", "csv", "md",
        "jpg", "jpeg", "png", "tif", "tiff", "bmp",
        "gif", "webp", "html", "htm", "xml", "json", "log"
    };

    public IndexingService(Database db, bool hashEnabled = true)
    {
        _db = db;
        _hashEnabled = hashEnabled;
    }

    /// <summary>
    /// Scan a folder and index all supported files. Returns the number of files indexed.
    /// </summary>
    public async Task<int> ScanFolderAsync(string folder, IProgress<IndexingProgress>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder))
            return 0;

        int count = 0;
        long scanned = 0;

        await Task.Run(() =>
        {
            foreach (var file in EnumerateFiles(folder))
            {
                ct.ThrowIfCancellationRequested();

                var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
                if (!SupportedExtensions.Contains(ext))
                    continue;

                var info = new FileInfo(file);
                var path = Path.GetFullPath(file);

                var hash = _hashEnabled ? ComputeHash(path) : "";
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                _db.Execute("""
                    INSERT INTO Files (path, filename, extension, size, created_date, modified_date, hash, indexing_status, ocr_status)
                    VALUES (@path, @filename, @ext, @size, @created, @modified, @hash, 'metadata_only', 'pending')
                    ON CONFLICT(path) DO UPDATE SET
                        filename=excluded.filename, extension=excluded.extension,
                        size=excluded.size, modified_date=excluded.modified_date,
                        hash=excluded.hash;
                """,
                ("@path", path),
                ("@filename", info.Name),
                ("@ext", ext),
                ("@size", info.Length),
                ("@created", new DateTimeOffset(info.CreationTimeUtc).ToUnixTimeSeconds()),
                ("@modified", new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()),
                ("@hash", hash));

                count++;
                scanned++;

                if (scanned % 10 == 0 && progress != null)
                {
                    progress.Report(new IndexingProgress
                    {
                        FilesScanned = scanned,
                        DocumentsIndexed = count
                    });
                }
            }
        }, ct);

        return count;
    }

    /// <summary>
    /// Get all files that need content extraction.
    /// </summary>
    public List<FileRecord> GetFilesNeedingExtraction()
    {
        return _db.QueryFiles("""
            SELECT id, path, filename, extension, size, created_date, modified_date, hash, indexing_status, ocr_status, is_favorite
            FROM Files
            WHERE indexing_status = 'metadata_only'
            AND extension IN ('pdf','doc','docx','xls','xlsx','xlsm','ppt','pptx','txt','csv','md','rtf')
            ORDER BY id;
        """);
    }

    /// <summary>
    /// Store extracted text for a file and update the search index.
    /// </summary>
    public void StoreExtractedText(long fileId, string text, string source = "native")
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _db.Execute("""
            INSERT INTO DocumentText (file_id, extracted_text, text_source, char_count, updated_at)
            VALUES (@id, @text, @source, @chars, @now)
            ON CONFLICT(file_id) DO UPDATE SET
                extracted_text=excluded.extracted_text,
                text_source=excluded.text_source,
                char_count=excluded.char_count,
                updated_at=excluded.updated_at;
        """,
        ("@id", fileId),
        ("@text", text),
        ("@source", source),
        ("@chars", text.Length),
        ("@now", now));

        // Update file status
        _db.Execute("UPDATE Files SET indexing_status='content_done', ocr_status='not_needed' WHERE id=@id;",
            ("@id", fileId));

        // Update FTS5 search index
        var file = GetFileById(fileId);
        if (file != null)
        {
            _db.Execute("DELETE FROM SearchIndex WHERE file_id=@id;", ("@id", fileId));
            _db.Execute("""
                INSERT INTO SearchIndex (filename, content, path, extension, file_id)
                VALUES (@filename, @content, @path, @ext, @id);
            """,
            ("@filename", file.Filename),
            ("@content", text),
            ("@path", file.Path),
            ("@ext", file.Extension),
            ("@id", fileId));
        }
    }

    /// <summary>
    /// Mark a file as failed extraction.
    /// </summary>
    public void MarkFileFailed(long fileId)
    {
        _db.Execute("UPDATE Files SET indexing_status='failed' WHERE id=@id;", ("@id", fileId));
    }

    /// <summary>
    /// Mark a file as needing OCR.
    /// </summary>
    public void MarkFileNeedsOcr(long fileId)
    {
        _db.Execute("UPDATE Files SET indexing_status='needs_ocr' WHERE id=@id;", ("@id", fileId));
    }

    /// <summary>
    /// Get all tags for a file.
    /// </summary>
    public List<string> GetTags(long fileId)
    {
        var tags = new List<string>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT tag FROM Tags WHERE file_id=@id ORDER BY tag;";
        cmd.Parameters.AddWithValue("@id", fileId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tags.Add(reader.GetString(0));
        return tags;
    }

    /// <summary>
    /// Add a tag to a file.
    /// </summary>
    public void AddTag(long fileId, string tag)
    {
        _db.Execute("INSERT OR IGNORE INTO Tags (file_id, tag) VALUES (@id, @tag);",
            ("@id", fileId), ("@tag", tag));
    }

    /// <summary>
    /// Remove a tag from a file.
    /// </summary>
    public void RemoveTag(long fileId, string tag)
    {
        _db.Execute("DELETE FROM Tags WHERE file_id=@id AND tag=@tag;",
            ("@id", fileId), ("@tag", tag));
    }

    /// <summary>
    /// Get the note for a file.
    /// </summary>
    public string GetNote(long fileId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT note FROM Notes WHERE file_id=@id;";
        cmd.Parameters.AddWithValue("@id", fileId);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? "" : (string)result;
    }

    /// <summary>
    /// Set the note for a file.
    /// </summary>
    public void SetNote(long fileId, string note)
    {
        _db.Execute("""
            INSERT INTO Notes (file_id, note, updated_at)
            VALUES (@id, @note, @now)
            ON CONFLICT(file_id) DO UPDATE SET note=excluded.note, updated_at=excluded.updated_at;
        """,
        ("@id", fileId),
        ("@note", note),
        ("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    /// <summary>
    /// Toggle the favorite flag for a file.
    /// </summary>
    public void ToggleFavorite(long fileId)
    {
        _db.Execute("UPDATE Files SET is_favorite = NOT is_favorite WHERE id=@id;", ("@id", fileId));
    }

    // ── Private helpers ──────────────────────────────────────

    private FileRecord? GetFileById(long fileId)
    {
        return _db.QueryFiles("SELECT id, path, filename, extension, size, created_date, modified_date, hash, indexing_status, ocr_status, is_favorite FROM Files WHERE id=@id;",
            ("@id", fileId)).FirstOrDefault();
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            try
            {
                foreach (var dir in Directory.GetDirectories(current))
                    stack.Push(dir);
            }
            catch { }

            string[] files;
            try { files = Directory.GetFiles(current); }
            catch { continue; }

            foreach (var file in files)
                yield return file;
        }
    }

    private static string ComputeHash(string path, long maxBytes = 64 * 1024 * 1024)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            var bytesToRead = Math.Min(stream.Length, maxBytes);
            var buffer = new byte[bytesToRead];
            stream.ReadExactly(buffer, 0, (int)bytesToRead);
            return Convert.ToHexString(sha.ComputeHash(buffer)).ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }
}
