using System.Runtime.Versioning;
using DocuSearch.Core.Models;

namespace DocuSearch.Core.Services;

/// <summary>
/// OCR service using Windows.Media.Ocr (built into Windows 10+).
/// Supports 50+ languages via Windows language packs.
/// No DLL to bundle, no API key needed — truly unlimited.
///
/// Note: This requires the "Windows SDK" target framework on Windows.
/// On non-Windows platforms, OCR is unavailable (returns empty).
/// </summary>
[SupportedOSPlatform("windows")]
public class OcrService
{
    private bool _available;

    public bool IsAvailable => _available;

    public void Initialize()
    {
        try
        {
            // Check if Windows.Media.Ocr is available
            _available = OperatingSystem.IsWindowsVersionAtLeast(10);
        }
        catch
        {
            _available = false;
        }
    }

    /// <summary>
    /// OCR an image file. Returns extracted text or empty string.
    /// </summary>
    public async Task<string> OcrImageAsync(string imagePath)
    {
        if (!_available || !OperatingSystem.IsWindowsVersionAtLeast(10))
            return "";

        try
        {
            return await Task.Run(() => OcrImageWindows(imagePath));
        }
        catch
        {
            return "";
        }
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private string OcrImageWindows(string imagePath)
    {
        // Windows.Media.Ocr requires WinRT APIs.
        // Since we're targeting net8.0 (not net8.0-windows10.0.x),
        // we use a subprocess approach: call a helper that runs
        // PowerShell with the Windows.Media.Ocr API.
        //
        // This avoids the complexity of multi-targeting and keeps
        // the build simple. The PowerShell script is small and fast.

        var script = $@"
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | ? {{ $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' }})[0]
function Await($WinRtTask, $ResultType) {{
    $asTask = $asTaskGeneric.MakeGenericMethod($ResultType)
    $netTask = $asTask.Invoke($null, @($WinRtTask))
    $netTask.Wait(-1) | Out-Null
    $netTask.Result
}}

[Windows.Storage.StorageFile, Windows.Storage.StorageFile, ContentType=WindowsRuntime] | Out-Null
[Windows.Media.Ocr.OcrEngine, Windows.Media.Ocr.OcrEngine, ContentType=WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging.BitmapDecoder, ContentType=WindowsRuntime] | Out-Null

$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync('{imagePath.Replace("'", "''")}')) ([Windows.Storage.StorageFile])
$stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
$decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
$result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
Write-Output $result.Text
";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -Command \"{script.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi);
        if (process == null) return "";

        process.WaitForExit(30000); // 30s timeout
        return process.StandardOutput.ReadToEnd().Trim();
    }

    /// <summary>
    /// Get list of available OCR languages.
    /// </summary>
    public List<string> GetAvailableLanguages()
    {
        if (!_available) return new List<string> { "auto" };
        return new List<string> { "auto", "en", "zh", "ja", "ko" };
    }
}
