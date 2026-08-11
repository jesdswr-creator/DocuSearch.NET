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

public partial class MainViewModel : ObservableObject
{
    private Database? _db;
    private SearchService? _search;
    private IndexingService? _indexer;
    private ExtractionService? _extractor;
    private SettingsService? _settingsService;
    private FileWatcherService? _watcher;
    private OcrService? _ocr;
    private SemanticSearchService? _semantic;

    public ObservableCollection<ResultItemViewModel> SearchResults { get; } = new();

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _statusText = "Ready — click 'Add Folder' to start";
    [ObservableProperty] private string _indexedCount = "0 files";
    [ObservableProperty] private string _ocrStatusText = "OCR";
    [ObservableProperty] private string _themeToggleText = "Light";
    [ObservableProperty] private string _resultCountText = "";
    [ObservableProperty] private string _previewTitle = "No file selected";
    [ObservableProperty] private string _previewText = "Select a file from the results to see its content here.";
    [ObservableProperty] private int _selectedResultIndex = -1;
    [ObservableProperty] private ObservableCollection<string> _tags = new();
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _selectedFileSize = "—";
    [ObservableProperty] private string _selectedFileDate = "—";
    [ObservableProperty] private string _selectedFileHash = "—";
    [ObservableProperty] private string _selectedFilePath = "—";
    [ObservableProperty] private bool _isSemanticEnabled;
    [ObservableProperty] private string _semanticToggleText = "AI";

    private AppSettings _settings = new();
    private long _selectedFileId;

    public Window? MainWindow { get; set; }

    public MainViewModel()
    {
        // All initialization is wrapped — if anything fails, the UI still shows
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dbPath = Path.Combine(appData, "DocuSearch", "docusearch.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

            _db = new Database(dbPath);
            _db.Open();

            _settingsService = new SettingsService(_db);
            _settings = _settingsService.Load();

            _search = new SearchService(_db);
            _indexer = new IndexingService(_db, _settings.HashLargeFiles);
            _extractor = new ExtractionService();
            _watcher = new FileWatcherService(_indexer!, _extractor!);
            _ocr = new OcrService();
            _semantic = new SemanticSearchService(_db);

            // Wire file watcher
            _watcher.FileAdded += OnFileAdded;
            _watcher.FileDeleted += OnFileDeleted;
            foreach (var folder in _settings.IndexedDrives)
                _watcher.AddWatch(folder);

            _ocr.Initialize();
            OcrStatusText = _ocr.IsAvailable ? "OCR" : "OCR N/A";

            // Background BGE init
            Task.Run(() =>
            {
                try
                {
                    var modelPath = Path.Combine(AppContext.BaseDirectory, "models", "bge-small-en-v1.5", "model.onnx");
                    if (File.Exists(modelPath) && _semantic!.Initialize(modelPath))
                    {
                        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            SemanticToggleText = "AI";
                            StatusText = "AI model loaded — toggle AI to enable semantic search";
                        });
                    }
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Init error: {ex.Message}";
        }
    }

    public void Initialize()
    {
        try
        {
            UpdateIndexedCount();
            StatusText = _settings.IndexedDrives.Count > 0
                ? $"Watching {_settings.IndexedDrives.Count} folder(s) — search or extract to begin"
                : "Ready — click '+ Folder' to add documents";
        }
        catch { }
    }

    // ═══ Commands ═════════════════════════════════════════════

    [RelayCommand]
    private async Task Search()
    {
        if (_search == null || string.IsNullOrWhiteSpace(SearchQuery))
        {
            SearchResults.Clear();
            ResultCountText = "";
            return;
        }

        StatusText = "Searching…";
        try
        {
            var hits = await Task.Run(() => _search.Search(SearchQuery, 50));

            SearchResults.Clear();
            foreach (var hit in hits)
                SearchResults.Add(new ResultItemViewModel(hit));

            ResultCountText = $"{hits.Count} results";
            StatusText = hits.Count > 0 ? $"Found {hits.Count} results" : "No results";
            UpdateIndexedCount();
        }
        catch (Exception ex)
        {
            StatusText = $"Search error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Extract()
    {
        if (_indexer == null || _extractor == null) return;

        var pending = _indexer.GetFilesNeedingExtraction();
        if (pending.Count == 0)
        {
            StatusText = "All files already extracted.";
            return;
        }

        var batch = pending.Take(30).ToList();
        StatusText = $"Extracting {batch.Count} of {pending.Count} files…";

        int done = 0, failed = 0;
        foreach (var file in batch)
        {
            StatusText = $"Extracting: {file.Filename} ({done + failed + 1}/{batch.Count})";
            try
            {
                var result = await Task.Run(() => _extractor.Extract(file.Path, file.Extension));
                if (!string.IsNullOrEmpty(result.Text))
                {
                    _indexer.StoreExtractedText(file.Id, result.Text, result.Source);
                    done++;
                }
                else if (result.NeedsOcr)
                {
                    _indexer.MarkFileNeedsOcr(file.Id);
                    done++;
                }
                else
                {
                    _indexer.MarkFileFailed(file.Id);
                    failed++;
                }
            }
            catch
            {
                _indexer.MarkFileFailed(file.Id);
                failed++;
            }
        }

        StatusText = $"Extraction: {done} done, {failed} failed" +
                     (pending.Count > 30 ? " — click Extract again for next batch" : "");
        UpdateIndexedCount();
    }

    [RelayCommand]
    private async Task FindDuplicates()
    {
        if (_search == null) return;
        StatusText = "Finding duplicates…";
        var dups = await Task.Run(() => _search.FindDuplicates());

        SearchResults.Clear();
        foreach (var hit in dups)
            SearchResults.Add(new ResultItemViewModel(hit));

        ResultCountText = $"{dups.Count} duplicates";
        StatusText = $"Found {dups.Count} duplicate files";
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        if (MainWindow == null || _indexer == null) return;

        var folders = await MainWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Folder to Index",
            AllowMultiple = false
        });

        if (folders.Count == 0) return;

        var folder = folders[0].Path.LocalPath;
        StatusText = $"Scanning {folder}…";

        if (!_settings.IndexedDrives.Contains(folder))
        {
            _settings.IndexedDrives.Add(folder);
            _settingsService?.Save(_settings);
            _watcher?.AddWatch(folder);
        }

        var count = await _indexer.ScanFolderAsync(folder);
        StatusText = $"Scanned {count} files. Starting extraction…";
        await Extract();
        UpdateIndexedCount();
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        // Placeholder — actual theme switch would change RequestedThemeVariant
        StatusText = "Theme toggle (coming soon — currently light)";
    }

    [RelayCommand]
    private void ToggleSemantic()
    {
        if (_semantic == null || !_semantic.IsReady)
        {
            StatusText = "AI model not loaded — semantic search unavailable";
            return;
        }
        IsSemanticEnabled = !IsSemanticEnabled;
        SemanticToggleText = IsSemanticEnabled ? "AI ✓" : "AI";
        StatusText = IsSemanticEnabled ? "Semantic search enabled" : "Semantic search disabled";
    }

    [RelayCommand]
    private void OpenLocation()
    {
        if (SelectedFilePath == "—" || !File.Exists(SelectedFilePath)) return;
        try
        {
            var dir = Path.GetDirectoryName(SelectedFilePath);
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

    // ═══ Events ═══════════════════════════════════════════════

    private void OnFileAdded(string path)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            StatusText = $"New file: {Path.GetFileName(path)}";
            UpdateIndexedCount();
        });
    }

    private void OnFileDeleted(string path) { }

    partial void OnSelectedResultIndexChanged(int value)
    {
        if (_search == null) return;
        if (value < 0 || value >= SearchResults.Count)
        {
            PreviewTitle = "No file selected";
            PreviewText = "Select a file from the results to see its content here.";
            return;
        }

        var item = SearchResults[value];
        _selectedFileId = item.Hit.FileId;

        var file = _search.GetFileById(_selectedFileId);
        if (file != null)
        {
            SelectedFileSize = FormatSize(file.Size);
            SelectedFileDate = DateTimeOffset.FromUnixTimeSeconds(file.ModifiedDate).DateTime.ToString("dd MMM yyyy");
            SelectedFileHash = file.Hash.Length > 32 ? file.Hash[..32] + "…" : (file.Hash.Length > 0 ? file.Hash : "—");
            SelectedFilePath = file.Path;
            PreviewTitle = file.Filename;
            PreviewText = _search.GetExtractedText(file.Id);

            Tags.Clear();
            if (_indexer != null)
            {
                foreach (var tag in _indexer.GetTags(file.Id))
                    Tags.Add(tag);
                Notes = _indexer.GetNote(file.Id);
            }
        }
    }

    // ═══ Helpers ══════════════════════════════════════════════

    private void UpdateIndexedCount()
    {
        if (_search == null) return;
        var (total, _) = _search.GetStats();
        IndexedCount = $"{total} files";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1048576) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1073741824) return $"{bytes / 1048576.0:F1} MB";
        return $"{bytes / 1073741824.0:F1} GB";
    }
}

public class ResultItemViewModel
{
    public SearchHit Hit { get; }
    public string Filename => Hit.Filename;
    public string Snippet => string.IsNullOrEmpty(Hit.Snippet) ? "" :
        (Hit.Snippet.Length > 100 ? Hit.Snippet[..100] + "…" : Hit.Snippet);
    public string BadgeLabel => GetBadgeLabel(Hit.Extension);
    public string BadgeColor => GetBadgeColor(Hit.Extension);
    public string MetaLine => $"{FormatSize(Hit.Size)} · {Hit.ModifiedDate:dd MMM yyyy}";
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
        _ => ext.Length >= 3 ? ext[..3].ToUpper() : "?"
    };

    private static string GetBadgeColor(string ext) => ext.ToLowerInvariant() switch
    {
        "pdf" => "#EF4444",
        "doc" or "docx" => "#2563EB",
        "xls" or "xlsx" or "xlsm" => "#16A34A",
        "ppt" or "pptx" => "#EA580C",
        "txt" or "csv" or "md" => "#6B7280",
        "jpg" or "jpeg" or "png" or "tiff" or "bmp" or "gif" => "#A855F7",
        _ => "#9B9B9B"
    };

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1048576) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1073741824) return $"{bytes / 1048576.0:F1} MB";
        return $"{bytes / 1073741824.0:F1} GB";
    }
}
