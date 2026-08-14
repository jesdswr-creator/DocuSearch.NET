using System.Collections.ObjectModel;
using System.IO;
using global::Avalonia.Controls;
using global::Avalonia.Platform.Storage;
using global::Avalonia.Media;
using global::Avalonia.Layout;
using global::Avalonia.Threading;
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
    private SemanticSearchService? _semantic;
    private AppSettings _settings = new();

    public ObservableCollection<ResultItemViewModel> SearchResults { get; } = new();
    public ObservableCollection<string> IndexedFolders { get; } = new();

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _statusText = "Ready — click '+ Add Folder' or 'Add Drive' to start";
    [ObservableProperty] private string _indexedCount = "0 files";
    [ObservableProperty] private string _resultCountText = "";
    [ObservableProperty] private string _previewTitle = "No file selected";
    [ObservableProperty] private string _previewText = "Select a file from the results to preview its content.";
    [ObservableProperty] private int _selectedResultIndex = -1;
    [ObservableProperty] private ObservableCollection<string> _tags = new();
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _selectedFileSize = "—";
    [ObservableProperty] private string _selectedFileDate = "—";
    [ObservableProperty] private string _selectedFileHash = "—";
    [ObservableProperty] private string _selectedFilePath = "—";
    [ObservableProperty] private string _semanticToggleText = "AI";

    private long _selectedFileId;
    public Window? MainWindow { get; set; }

    public MainViewModel()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dbDir = Path.Combine(appData, "DocuSearch");
            Directory.CreateDirectory(dbDir);
            var dbPath = Path.Combine(dbDir, "docusearch.db");

            _db = new Database(dbPath);
            _db.Open();
            _settingsService = new SettingsService(_db);
            _settings = _settingsService.Load();
            _search = new SearchService(_db);
            _indexer = new IndexingService(_db, _settings.HashLargeFiles);
            _extractor = new ExtractionService();
            _watcher = new FileWatcherService(_indexer, _extractor);
            _semantic = new SemanticSearchService(_db);

            _watcher.FileAdded += (p) => global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"New file: {Path.GetFileName(p)}";
                UpdateIndexedCount();
            });

            foreach (var f in _settings.IndexedDrives)
            {
                _watcher.AddWatch(f);
                IndexedFolders.Add(f);
            }

            // Background BGE init
            Task.Run(() =>
            {
                try
                {
                    var exeDir = AppContext.BaseDirectory;
                    var candidates = new[] {
                        Path.Combine(exeDir, "models", "bge-small-en-v1.5", "model.onnx"),
                        Path.Combine(exeDir, "bge-small-en-v1.5", "model.onnx"),
                        Path.Combine(dbDir, "models", "bge-small-en-v1.5", "model.onnx"),
                    };
                    foreach (var p in candidates)
                    {
                        if (File.Exists(p) && _semantic!.Initialize(p))
                        {
                            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                SemanticToggleText = "AI";
                                StatusText = "AI model loaded — click AI to enable";
                            });
                            break;
                        }
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
        try { UpdateIndexedCount(); } catch { }
    }

    // ═══ Search ═══════════════════════════════════════════════

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
            foreach (var h in hits) SearchResults.Add(new ResultItemViewModel(h));
            ResultCountText = $"{hits.Count} results";
            StatusText = hits.Count > 0 ? $"Found {hits.Count}" : "No results";
            UpdateIndexedCount();
        }
        catch (Exception ex) { StatusText = $"Search error: {ex.Message}"; }
    }

    // ═══ Extract ══════════════════════════════════════════════

    [RelayCommand]
    private async Task Extract()
    {
        if (_indexer == null || _extractor == null) return;
        var pending = _indexer.GetFilesNeedingExtraction();
        if (pending.Count == 0) { StatusText = "All files extracted."; return; }
        var batch = pending.Take(30).ToList();
        StatusText = $"Extracting {batch.Count}/{pending.Count}…";
        int done = 0, failed = 0;
        foreach (var f in batch)
        {
            StatusText = $"Extracting: {f.Filename} ({done + failed + 1}/{batch.Count})";
            try
            {
                var r = await Task.Run(() => _extractor.Extract(f.Path, f.Extension));
                if (!string.IsNullOrEmpty(r.Text)) { _indexer.StoreExtractedText(f.Id, r.Text, r.Source); done++; }
                else if (r.NeedsOcr) { _indexer.MarkFileNeedsOcr(f.Id); done++; }
                else { _indexer.MarkFileFailed(f.Id); failed++; }
            }
            catch { _indexer.MarkFileFailed(f.Id); failed++; }
        }
        StatusText = $"Extraction: {done} done, {failed} failed" + (pending.Count > 30 ? " — click Extract again" : "");
        UpdateIndexedCount();
    }

    // ═══ Duplicates ═══════════════════════════════════════════

    [RelayCommand]
    private async Task FindDuplicates()
    {
        if (_search == null) return;
        StatusText = "Finding duplicates…";
        var dups = await Task.Run(() => _search.FindDuplicates());
        SearchResults.Clear();
        foreach (var h in dups) SearchResults.Add(new ResultItemViewModel(h));
        ResultCountText = $"{dups.Count} duplicates";
        StatusText = $"Found {dups.Count} duplicates";
    }

    // ═══ Add Folder (folder picker) ══════════════════════════

    [RelayCommand]
    private async Task AddFolder()
    {
        if (MainWindow == null || _indexer == null) return;
        try
        {
            var folders = await MainWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Folder to Index",
                AllowMultiple = false
            });
            if (folders.Count == 0) return;
            var folder = folders[0].Path.LocalPath;
            await ScanAndIndex(folder);
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
    }

    // ═══ Add Drive (text input dialog) ═══════════════════════

    [RelayCommand]
    private async Task AddDrive()
    {
        if (MainWindow == null || _indexer == null) return;
        try
        {
            // Build a simple input dialog
            var inputBox = new TextBox
            {
                Watermark = "e.g. D:\\ or D:\\MyFolder",
                Margin = new global::Avalonia.Thickness(16),
                FontSize = 14,
                Width = 340
            };
            var okBtn = new Button
            {
                Content = "Add Drive",
                Classes = { "primary" },
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new global::Avalonia.Thickness(16, 0, 16, 16)
            };
            var label = new TextBlock
            {
                Text = "Enter a drive or folder path to index:",
                Margin = new global::Avalonia.Thickness(16, 16, 16, 4),
                FontSize = 13,
                Foreground = global::Avalonia.Media.Brushes.Black
            };
            var panel = new StackPanel();
            panel.Children.Add(label);
            panel.Children.Add(inputBox);
            panel.Children.Add(okBtn);

            var dialog = new Window
            {
                Title = "Add Drive or Folder",
                Width = 420,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = global::Avalonia.Media.Brushes.White,
                Content = panel
            };

            string? drivePath = null;
            okBtn.Click += (s, e) =>
            {
                drivePath = inputBox.Text?.Trim();
                dialog.Close();
            };

            await dialog.ShowDialog(MainWindow);

            if (string.IsNullOrEmpty(drivePath)) return;
            if (!Directory.Exists(drivePath))
            {
                StatusText = $"Path not found: {drivePath}";
                return;
            }

            await ScanAndIndex(drivePath);
        }
        catch (Exception ex) { StatusText = $"Add drive error: {ex.Message}"; }
    }

    private async Task ScanAndIndex(string folder)
    {
        if (_indexer == null) return;
        StatusText = $"Scanning {Path.GetFileName(folder.TrimEnd('\\', '/'))}…";
        if (!_settings.IndexedDrives.Contains(folder))
        {
            _settings.IndexedDrives.Add(folder);
            _settingsService?.Save(_settings);
            _watcher?.AddWatch(folder);
            IndexedFolders.Add(folder);
        }
        var count = await _indexer.ScanFolderAsync(folder);
        StatusText = $"Found {count} files. Extracting…";
        await Extract();
    }

    // ═══ AI Toggle ═══════════════════════════════════════════

    [RelayCommand]
    private void ToggleSemantic()
    {
        if (_semantic == null || !_semantic.IsReady) { StatusText = "AI model not loaded"; return; }
        SemanticToggleText = SemanticToggleText == "AI" ? "AI ✓" : "AI";
        StatusText = SemanticToggleText == "AI ✓" ? "Semantic search ON" : "Semantic search OFF";
    }

    // ═══ Open Location ═══════════════════════════════════════

    [RelayCommand]
    private void OpenLocation()
    {
        if (SelectedFilePath == "—" || !File.Exists(SelectedFilePath)) return;
        try
        {
            var dir = Path.GetDirectoryName(SelectedFilePath);
            if (dir != null) System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch { }
    }

    // ═══ Menu commands ═══════════════════════════════════════

    [RelayCommand]
    private void ShowSearch()
    {
        StatusText = "Type your search query and press Enter or click Search";
    }

    [RelayCommand]
    private void ShowStats()
    {
        if (_search == null) return;
        var (total, dbSize) = _search.GetStats();
        StatusText = $"Stats: {total} files indexed, DB: {FormatSize(dbSize)}, Folders: {_settings.IndexedDrives.Count}";
    }

    [RelayCommand]
    private void ShowSettings()
    {
        var folders = string.Join(", ", _settings.IndexedDrives);
        StatusText = $"Indexed locations: {(_settings.IndexedDrives.Count > 0 ? folders : "none — use Add Folder or Add Drive")}";
    }

    [RelayCommand]
    private void ShowHelp()
    {
        StatusText = "Help: 1) Add Folder/Drive  2) Click Extract  3) Search  4) Click AI for semantic search";
    }

    [RelayCommand]
    private void ShowAbout()
    {
        StatusText = "DocuSearch.NET v2.0 — C# / .NET 8 / Avalonia UI — Offline Document Search";
    }

    // ═══ Selection changed ═══════════════════════════════════

    partial void OnSelectedResultIndexChanged(int value)
    {
        if (_search == null) return;
        if (value < 0 || value >= SearchResults.Count)
        {
            PreviewTitle = "No file selected";
            PreviewText = "Select a file from results.";
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
            var text = _search.GetExtractedText(file.Id);
            PreviewText = !string.IsNullOrEmpty(text) ? text : "(no text extracted — click Extract)";
            Tags.Clear();
            if (_indexer != null)
            {
                foreach (var tag in _indexer.GetTags(file.Id)) Tags.Add(tag);
                Notes = _indexer.GetNote(file.Id);
            }
        }
    }

    private void UpdateIndexedCount()
    {
        if (_search == null) return;
        var (total, _) = _search.GetStats();
        IndexedCount = $"{total} files";
    }

    private static string FormatSize(long b) =>
        b < 1024 ? $"{b} B" :
        b < 1048576 ? $"{b / 1024.0:F1} KB" :
        b < 1073741824 ? $"{b / 1048576.0:F1} MB" :
        $"{b / 1073741824.0:F1} GB";
}

public class ResultItemViewModel
{
    public SearchHit Hit { get; }
    public string Filename => Hit.Filename;
    public string Snippet => string.IsNullOrEmpty(Hit.Snippet) ? "" :
        (Hit.Snippet.Length > 80 ? Hit.Snippet[..80] + "…" : Hit.Snippet);
    public string BadgeLabel => Hit.Extension.ToLowerInvariant() switch
    {
        "pdf" => "PDF", "doc" or "docx" => "DOC", "xls" or "xlsx" => "XLS",
        "ppt" or "pptx" => "PPT",
        _ => Hit.Extension.Length >= 3 ? Hit.Extension[..3].ToUpper() : "?"
    };
    public string BadgeColor => Hit.Extension.ToLowerInvariant() switch
    {
        "pdf" => "#EF4444", "doc" or "docx" => "#2563EB", "xls" or "xlsx" => "#16A34A",
        "ppt" or "pptx" => "#EA580C", _ => "#9B9B9B"
    };
    public string MetaLine => $"{FormatSize(Hit.Size)} · {Hit.ModifiedDate:dd MMM yy}";
    public ResultItemViewModel(SearchHit hit) { Hit = hit; }
    private static string FormatSize(long b) =>
        b < 1024 ? $"{b} B" :
        b < 1048576 ? $"{b / 1024.0:F1} KB" :
        b < 1073741824 ? $"{b / 1048576.0:F1} MB" :
        $"{b / 1073741824.0:F1} GB";
}
