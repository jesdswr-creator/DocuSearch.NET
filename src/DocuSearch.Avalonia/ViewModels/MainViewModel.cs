using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocuSearch.Core.Data;
using DocuSearch.Core.Models;
using DocuSearch.Core.Services;

namespace DocuSearch.Avalonia.ViewModels;

/// <summary>
/// Main view model — handles all UI state and commands.
/// Uses CommunityToolkit.Mvvm source generators for INotifyPropertyChanged.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly Database _db;
    private readonly SearchService _search;
    private readonly IndexingService _indexer;
    private readonly ExtractionService _extractor;

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
    [ObservableProperty] private FileRecord? _selectedFile;
    [ObservableProperty] private string _selectedFileSize = "-";
    [ObservableProperty] private string _selectedFileDate = "-";
    [ObservableProperty] private string _selectedFileHash = "-";
    [ObservableProperty] private string _selectedFilePath = "-";

    private bool _isDark = true;
    private long _selectedFileId;

    public MainViewModel()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dbPath = Path.Combine(appData, "DocuSearch", "docusearch.db");
        _db = new Database(dbPath);
        _db.Open();
        _search = new SearchService(_db);
        _indexer = new IndexingService(_db, hashEnabled: true);
        _extractor = new ExtractionService();
    }

    public void Initialize()
    {
        UpdateIndexedCount();
        StatusText = "Ready — add folders via Settings or Add Folder button";
    }

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

        SearchResults.Clear();
        foreach (var hit in hits)
        {
            SearchResults.Add(new ResultItemViewModel(hit));
        }

        ResultCountText = $"({hits.Count})";
        StatusText = hits.Count > 0
            ? $"Found {hits.Count} results"
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

        StatusText = $"Extracting {Math.Min(pending.Count, 30)} of {pending.Count} files...";

        // Process in batches of 30
        var batch = pending.Take(30).ToList();
        var done = 0;
        var failed = 0;

        foreach (var file in batch)
        {
            try
            {
                StatusText = $"Extracting: {file.Filename} ({done + failed + 1}/{batch.Count})";

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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Extraction failed for {file.Path}: {ex.Message}");
                _indexer.MarkFileFailed(file.Id);
                failed++;
            }
        }

        StatusText = $"Extraction complete: {done} succeeded, {failed} failed" +
                     (pending.Count > 30 ? $" (click Extract again for next batch)" : "");
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
    private void ToggleTheme()
    {
        _isDark = !_isDark;
        ThemeToggleText = _isDark ? "Dark" : "Light";
        StatusText = $"{(_isDark ? "Dark" : "Light")} theme";
    }

    [RelayCommand]
    private async Task OpenLocation()
    {
        if (_selectedFile == null) return;
        try
        {
            // Open the file's folder in Windows Explorer
            var dir = Path.GetDirectoryName(_selectedFile.Path);
            if (dir != null && Directory.Exists(dir))
            {
                System.Diagnostics.Process.Start("explorer.exe", dir);
            }
        }
        catch { }
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

        // Load file details
        var file = _search.GetFileById(_selectedFileId);
        if (file != null)
        {
            SelectedFile = file;
            SelectedFileSize = FormatSize(file.Size);
            SelectedFileDate = DateTimeOffset.FromUnixTimeSeconds(file.ModifiedDate).DateTime.ToString("dd MMM yyyy");
            SelectedFileHash = file.Hash.Length > 32 ? file.Hash[..32] + "..." : file.Hash;
            SelectedFilePath = file.Path;
            PreviewTitle = file.Filename;

            // Load extracted text
            PreviewText = _search.GetExtractedText(file.Id);

            // Load tags + notes
            Tags.Clear();
            foreach (var tag in _indexer.GetTags(file.Id))
                Tags.Add(tag);
            Notes = _indexer.GetNote(file.Id);
        }
    }

    private void UpdateIndexedCount()
    {
        var (total, dbSize) = _search.GetStats();
        IndexedCount = $"Indexed: {total} files";
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
/// View model for a single search result item — provides badge color/label + formatted text.
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
