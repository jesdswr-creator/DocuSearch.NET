namespace DocuSearch.Core.Models;

/// <summary>
/// A file record stored in the database.
/// </summary>
public class FileRecord
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public string Filename { get; set; } = "";
    public string Extension { get; set; } = "";
    public long Size { get; set; }
    public long CreatedDate { get; set; }
    public long ModifiedDate { get; set; }
    public string Hash { get; set; } = "";
    public string IndexingStatus { get; set; } = "metadata_only"; // metadata_only, content_done, needs_ocr, failed, skipped
    public string OcrStatus { get; set; } = "pending"; // pending, done, not_needed, failed
    public bool IsFavorite { get; set; }
}

/// <summary>
/// A search hit returned by the search engine.
/// </summary>
public class SearchHit
{
    public long FileId { get; set; }
    public string Filename { get; set; } = "";
    public string Path { get; set; } = "";
    public string Extension { get; set; } = "";
    public long Size { get; set; }
    public DateTime ModifiedDate { get; set; }
    public string Snippet { get; set; } = "";
    public double Score { get; set; }
    public bool IsFavorite { get; set; }
}

/// <summary>
/// Result of a text extraction operation.
/// </summary>
public class ExtractionResult
{
    public string Text { get; set; } = "";
    public string Source { get; set; } = "native";
    public bool NeedsOcr { get; set; }
    public string ErrorMessage { get; set; } = "";
}

/// <summary>
/// App settings persisted to the database.
/// </summary>
public class AppSettings
{
    public List<string> IndexedDrives { get; set; } = new();
    public List<string> ExcludedFolders { get; set; } = new();
    public bool DarkMode { get; set; } = true;
    public bool HashLargeFiles { get; set; } = true;
    public int MaxWorkerThreads { get; set; } = 2;
}

/// <summary>
/// Indexing progress update.
/// </summary>
public class IndexingProgress
{
    public long FilesScanned { get; set; }
    public long DocumentsIndexed { get; set; }
    public long QueueRemaining { get; set; }
    public long ErrorsCount { get; set; }
}
