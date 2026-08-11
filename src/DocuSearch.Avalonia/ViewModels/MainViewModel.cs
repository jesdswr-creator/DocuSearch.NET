using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocuSearch.Core.Data;
using DocuSearch.Core.Models;
using DocuSearch.Core.Services;

namespace DocuSearch.Avalonia.ViewModels;

/// <summary>
/// Main view model — handles all UI state and commands.
/// Uses CommunityToolkit.Mvvm source generators for INotifyPropertyChanged.
/// All long-running operations use async/await — zero UI freeze.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly Database _db;
    private readonly SearchService _search;
    private readonly IndexingService _indexer;
    private readonly ExtractionService _extractor;
    private readonly SettingsService _settingsService;
    private readonly FileWatcherService _watcher;
    private readonly OcrService _ocr;
    private readonly SemanticSearchService _semantic;

    public ObservableCollection<ResultItemViewModel> SearchResults { get; } = new();

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _indexedCount = "Indexed: 0";
    [ObservableProperty] private string _ocrStatusText = "OCR: Ready";
    [ObservableProperty] private string _themeToggleText = "Dark";
    [ObservableProperty] private string _resultCountText = "(0)";
    [ObservableProperty] private string _previewTitle = "Select a file to preview";
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private int _selectedResultIndex = -1;
    [ObservableProperty] private ObservableCollection<string> _tags = new();
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _selectedFileSize = "-";
    [ObservableProperty] private string _selectedFileDate = "-";
    [ObservableProperty] private string _selectedFileHash = "-";
    [ObservableProperty] private string _selectedFilePath = "-";
    [ObservableProperty] private bool _isSemanticEnabled;
    [ObservableProperty] private string _semanticToggleText = "AI: OFF";

    private bool _isDark = true;
    private AppSettings _settings;
    private long _selectedFileId;

    // Reference to the main window for file dialogs
    public Window? MainWindow { get; set; }

    public MainViewModel()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dbPath = Path.Combine(appData, "DocuSearch", "docusearch.db");
        _db = new Database(dbPath);
        _db.Open();

        _settingsService = new SettingsService(_db);
        _settings = _settingsService.Load();

        _search = new SearchService(_db);
        _indexer = new IndexingService(_db, _settings.HashLargeFiles);
        _extractor = new ExtractionService();
        _watcher = new FileWatcherService(_indexer, _extractor);
        _ocr = new OcrService();
        _semantic = new SemanticSearchService(_db);

        // Wire file watcher events
        _watcher.FileAdded += OnFileAdded;
        _watcher.FileDeleted += OnFileDeleted;

        // Start watching indexed folders
        foreach (var folder in _settings.IndexedDrives)
            _watcher.AddWatch(folder);

        _isDark = _settings.DarkMode;
        ThemeToggleText = _isDark ? "Dark" : "Light";

        // Initialize OCR
        _ocr.Initialize();
        OcrStatusText = _ocr.IsAvailable ? "OCR: Ready" : "OCR: N/A";

        // Initialize semantic search in background
        Task.Run(() =>
        {
            var modelPath = Path.Combine(AppContext.BaseDirectory, "models", "bge-small-en-v1.5", "model.onnx");
            if (File.Exists(modelPath) && _semantic.Initialize(modelPath))
            {
                 global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    SemanticToggleText = "AI: OFF";
                    StatusText = "AI model loaded — click AI: OFF to toggle semantic search";
                });
            }
        });
    }

    public void Initialize()
    {
        UpdateIndexedCount();
        StatusText = _settings.IndexedDrives.Count > 0
            ? $"Ready — watching {_settings.IndexedDrives.Count} folder(s)"
            : "Ready — click 'Add Folder' to index documents";
    }

    // ── Commands ──────────────────────────────────────────────

    [RelayCommand]
    private async Task Search()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            SearchResults.Clear();
            ResultCountText = "(0)";
            return;
        }

        StatusText = "Searching...";
        var hits = await Task.Run(() => _search.Search(SearchQuery, 50));

        // If semantic search is enabled, also run semantic search and merge
        if (IsSemanticEnabled && _semantic.IsReady)
        {
            var queryEmbedding = _semantic.Embed(SearchQuery);
            if (queryEmbedding != null)
            {
                var semanticHits = _semantic.SearchSimilar(queryEmbedding, topK: 20, threshold: 0.3f);
                // Merge: add semantic-only results that aren't already in keyword results
                var existingIds = hits.Select(h => h.FileId).ToHashSet();
                foreach (var (fileId, sim) in semanticHits)
                {
                    if (!existingIds.Contains(fileId))
                    {
                        var file = _search.GetFileById(fileId);
                        if (file != null)
                        {
                            hits.Add(new SearchHit
                            {
                                FileId = file.Id,
                                Filename = file.Filename,
                                Path = file.Path,
                                Extension = file.Extension,
                                Size = file.Size,
                                ModifiedDate = DateTimeOffset.FromUnixTimeSeconds(file.ModifiedDate).DateTime,
                                Snippet = $"[semantic match: {sim:F2}]",
                                Score = sim
                            });
                        }
                    }
                }
            }
        }

        SearchResults.Clear();
        foreach (var hit in hits)
            SearchResults.Add(new ResultItemViewModel(hit));

        ResultCountText = $"({hits.Count})";
        StatusText = hits.Count > 0
            ? $"Found {hits.Count} results in {hits.Count}ms"
            : "No results found";
        UpdateIndexedCount();
    }

    [RelayCommand]
    private async Task Extract()
    {
        var pending = _indexer.GetFilesNeedingExtraction();
        if (pending.Count == 0)
        {
            StatusText = "All files already extracted.";
            return;
        }

        var batch = pending.Take(30).ToList();
        StatusText = $"Extracting {batch.Count} of {pending.Count} files...";

        int done = 0, failed = 0;
        foreach (var file in batch)
        {
            StatusText = $"Extracting: {file.Filename} ({done + failed + 1}/{batch.Count})";

            var result = await Task.Run(() => _extractor.Extract(file.Path, file.Extension));

            if (!string.IsNullOrEmpty(result.Text))
            {
                _indexer.StoreExtractedText(file.Id, result.Text, result.Source);
                done++;
            }
            else if (result.NeedsOcr && _ocr.IsAvailable)
            {
                // Try OCR for scanned documents (images only for now)
                var ext = file.Extension.ToLowerInvariant();
                if (ext is "jpg" or "jpeg" or "png" or "bmp" or "tiff" or "tif")
                {
                    var ocrText = await _ocr.OcrImageAsync(file.Path);
                    if (!string.IsNullOrEmpty(ocrText))
                    {
                        _indexer.StoreExtractedText(file.Id, ocrText, "ocr");
                        done++;
                    }
                    else
                    {
                        _indexer.MarkFileFailed(file.Id);
                        failed++;
                    }
                }
                else
                {
                    _indexer.MarkFileNeedsOcr(file.Id);
                    done++;
                }
            }
            else
            {
                _indexer.MarkFileFailed(file.Id);
                failed++;
            }
        }

        StatusText = $"Extraction: {done} done, {failed} failed" +
                     (pending.Count > 30 ? " (click Extract again for next batch)" : "");
        UpdateIndexedCount();
    }

    [RelayCommand]
    private async Task FindDuplicates()
    {
        StatusText = "Finding duplicates...";
        var dups = await Task.Run(() => _search.FindDuplicates());

        SearchResults.Clear();
        foreach (var hit in dups)
            SearchResults.Add(new ResultItemViewModel(hit));

        ResultCountText = $"({dups.Count})";
        StatusText = $"Found {dups.Count} duplicate files";
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        if (MainWindow == null) return;

        var folders = await MainWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Folder to Index",
            AllowMultiple = false
        });

        if (folders.Count == 0) return;

        var folder = folders[0].Path.LocalPath;
        StatusText = $"Scanning {folder}...";

        // Add to indexed drives
        if (!_settings.IndexedDrives.Contains(folder))
        {
            _settings.IndexedDrives.Add(folder);
            _settingsService.Save(_settings);
            _watcher.AddWatch(folder);
        }

        // Scan the folder
        var count = await _indexer.ScanFolderAsync(folder);
        StatusText = $"Scanned {count} files from {folder}";

        // Auto-start extraction
        await Extract();
        UpdateIndexedCount();
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        _isDark = !_isDark;
        _settings.DarkMode = _isDark;
        _settingsService.Save(_settings);
        ThemeToggleText = _isDark ? "Dark" : "Light";
        StatusText = $"{(_isDark ? "Dark" : "Light")} theme";
    }

    [RelayCommand]
    private void ToggleSemantic()
    {
        if (!_semantic.IsReady)
        {
            StatusText = "AI model not loaded — semantic search unavailable";
            return;
        }
        IsSemanticEnabled = !IsSemanticEnabled;
        SemanticToggleText = IsSemanticEnabled ? "AI: ON" : "AI: OFF";
        StatusText = IsSemanticEnabled ? "Semantic search enabled" : "Semantic search disabled";
    }

    [RelayCommand]
    private async Task GenerateEmbeddings()
    {
        if (!_semantic.IsReady)
        {
            StatusText = "AI model not loaded";
            return;
        }

        StatusText = "Generating embeddings...";
        var count = await _semantic.GenerateAllEmbeddingsAsync();
        StatusText = $"Generated {count} embeddings";
    }

    [RelayCommand]
    private void OpenLocation()
    {
        if (_selectedFilePath == "-" || !File.Exists(_selectedFilePath)) return;
        try
        {
            var dir = Path.GetDirectoryName(_selectedFilePath);
            if (dir != null && Directory.Exists(dir))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = dir,
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (_selectedFileId == 0) return;
        _indexer.ToggleFavorite(_selectedFileId);
        StatusText = "Favorite toggled";
    }

    // ── Event handlers ────────────────────────────────────────

    private void OnFileAdded(string path)
    {
         global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            StatusText = $"New file detected: {Path.GetFileName(path)}";
            UpdateIndexedCount();
        });
    }

    private void OnFileDeleted(string path)
    {
        // Could remove from DB — deferred for simplicity
    }

    partial void OnSelectedResultIndexChanged(int value)
    {
        if (value < 0 || value >= SearchResults.Count)
        {
            PreviewTitle = "Select a file to preview";
            PreviewText = "";
            return;
        }

        var item = SearchResults[value];
        _selectedFileId = item.Hit.FileId;

        var file = _search.GetFileById(_selectedFileId);
        if (file != null)
        {
            SelectedFileSize = FormatSize(file.Size);
            SelectedFileDate = DateTimeOffset.FromUnixTimeSeconds(file.ModifiedDate).DateTime.ToString("dd MMM yyyy");
            SelectedFileHash = file.Hash.Length > 32 ? file.Hash[..32] + "..." : (file.Hash.Length > 0 ? file.Hash : "-");
            SelectedFilePath = file.Path;
            PreviewTitle = file.Filename;
            PreviewText = _search.GetExtractedText(file.Id);

            Tags.Clear();
            foreach (var tag in _indexer.GetTags(file.Id))
                Tags.Add(tag);
            Notes = _indexer.GetNote(file.Id);
        }
    }

    // ── Helpers ───────────────────────────────────────────────

    private void UpdateIndexedCount()
    {
        var (total, _) = _search.GetStats();
        IndexedCount = $"Indexed: {total}";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}

/// <summary>
/// View model for a single search result item.
/// </summary>
public class ResultItemViewModel
{
    public SearchHit Hit { get; }

    public string Filename => Hit.Filename;
    public string Snippet => Hit.Snippet.Length > 120 ? Hit.Snippet[..120] + "..." : Hit.Snippet;
    public string Path => Hit.Path;
    public string BadgeLabel => GetBadgeLabel(Hit.Extension);
    public string BadgeColor => GetBadgeColor(Hit.Extension);
    public string MetaLine => $"{FormatSize(Hit.Size)} • {Hit.ModifiedDate:dd MMM yyyy}";
    public bool HasScore => Hit.Score > 0;
    public string ScoreText => Hit.Score.ToString("F2");

    public ResultItemViewModel(SearchHit hit) { Hit = hit; }

    private static string GetBadgeLabel(string ext) => ext.ToLowerInvariant() switch
    {
        "pdf" => "PDF",
        "doc" or "docx" => "DOC",
        "xls" or "xlsx" or "xlsm" => "XLS",
        "ppt" or "pptx" => "PPT",
        _ when ext.Length <= 3 => ext.ToUpper(),
        _ => ext[..3].ToUpper()
    };

    private static string GetBadgeColor(string ext) => ext.ToLowerInvariant() switch
    {
        "pdf" => "#EF4444",
        "doc" or "docx" => "#2563EB",
        "xls" or "xlsx" or "xlsm" => "#16A34A",
        "ppt" or "pptx" => "#EA580C",
        "txt" or "csv" or "md" => "#64748B",
        "jpg" or "jpeg" or "png" or "tiff" or "bmp" or "gif" => "#A855F7",
        _ => "#62718A"
    };

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
