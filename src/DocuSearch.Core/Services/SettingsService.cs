using DocuSearch.Core.Data;
using DocuSearch.Core.Models;

namespace DocuSearch.Core.Services;

/// <summary>
/// Manages app settings persistence (stored in SQLite Settings table).
/// </summary>
public class SettingsService
{
    private readonly Database _db;

    public SettingsService(Database db)
    {
        _db = db;
    }

    public AppSettings Load()
    {
        var settings = new AppSettings();

        var indexedDrives = Get("indexedDrives");
        if (!string.IsNullOrEmpty(indexedDrives))
            settings.IndexedDrives = indexedDrives.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();

        var excluded = Get("excludedFolders");
        if (!string.IsNullOrEmpty(excluded))
            settings.ExcludedFolders = excluded.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();

        settings.DarkMode = Get("darkMode") != "false";
        settings.HashLargeFiles = Get("hashLargeFiles") != "false";
        settings.MaxWorkerThreads = int.TryParse(Get("maxWorkerThreads"), out var t) ? t : 2;

        return settings;
    }

    public void Save(AppSettings settings)
    {
        Set("indexedDrives", string.Join("|", settings.IndexedDrives));
        Set("excludedFolders", string.Join("|", settings.ExcludedFolders));
        Set("darkMode", settings.DarkMode.ToString().ToLower());
        Set("hashLargeFiles", settings.HashLargeFiles.ToString().ToLower());
        Set("maxWorkerThreads", settings.MaxWorkerThreads.ToString());
    }

    private string Get(string key)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM Settings WHERE key = @key;";
        cmd.Parameters.AddWithValue("@key", key);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? "" : (string)result;
    }

    private void Set(string key, string value)
    {
        _db.Execute("""
            INSERT INTO Settings (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
        """,
        ("@key", key),
        ("@value", value));
    }
}
