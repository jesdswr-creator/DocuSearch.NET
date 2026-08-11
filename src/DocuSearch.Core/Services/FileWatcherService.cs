using System.IO;
using DocuSearch.Core.Data;
using DocuSearch.Core.Models;

namespace DocuSearch.Core.Services;

/// <summary>
/// Watches indexed folders for file changes using FileSystemWatcher.
/// Debounces events to avoid processing rapid add+modify sequences.
/// </summary>
public class FileWatcherService : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly IndexingService _indexer;
    private readonly ExtractionService _extractor;
    private readonly Timer _debounceTimer;
    private readonly HashSet<string> _pendingPaths = new();
    private readonly object _lock = new();

    public event Action<string>? FileAdded;
    public event Action<string>? FileDeleted;
    public event Action<string, string>? FileRenamed;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "doc", "docx", "xls", "xlsx", "xlsm",
        "ppt", "pptx", "txt", "rtf", "csv", "md",
        "jpg", "jpeg", "png", "tif", "tiff", "bmp",
        "gif", "webp", "html", "htm", "xml", "json", "log"
    };

    public FileWatcherService(IndexingService indexer, ExtractionService extractor)
    {
        _indexer = indexer;
        _extractor = extractor;
        _debounceTimer = new Timer(OnDebounceTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Start watching a folder. Multiple folders can be watched.
    /// </summary>
    public void AddWatch(string folder)
    {
        if (!Directory.Exists(folder)) return;

        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                              NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            watcher.Created += OnFileCreated;
            watcher.Deleted += OnFileDeleted;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnWatcherError;

            _watchers.Add(watcher);
        }
        catch { }
    }

    /// <summary>
    /// Stop all watchers and clear.
    /// </summary>
    public void ClearWatches()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        var ext = Path.GetExtension(e.FullPath).TrimStart('.').ToLowerInvariant();
        if (!SupportedExtensions.Contains(ext)) return;

        lock (_lock)
        {
            _pendingPaths.Add(e.FullPath);
        }
        _debounceTimer.Change(500, Timeout.Infinite);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        FileDeleted?.Invoke(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        FileRenamed?.Invoke(e.OldFullPath, e.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Watcher buffer overflow — re-add the watch
        // (triggers full rescan on next startup)
    }

    private void OnDebounceTick(object? state)
    {
        List<string> paths;
        lock (_lock)
        {
            paths = _pendingPaths.ToList();
            _pendingPaths.Clear();
        }

        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;

                var info = new FileInfo(path);
                var ext = info.Extension.TrimStart('.').ToLowerInvariant();

                // Index the file
                _indexer.ScanFolderAsync(Path.GetDirectoryName(path)!).Wait();

                // Auto-extract if supported
                if (SupportedExtensions.Contains(ext))
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            var result = await Task.Run(() => _extractor.Extract(path, ext));
                            if (!string.IsNullOrEmpty(result.Text))
                            {
                                // Find the file ID and store text
                                // (simplified — in production would look up by path)
                            }
                        }
                        catch { }
                    });
                }

                FileAdded?.Invoke(path);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        ClearWatches();
        _debounceTimer.Dispose();
    }
}
