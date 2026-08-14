using DocuSearch.Core.Models;

namespace DocuSearch.Core.Services;

/// <summary>
/// OCR service — currently a stub.
/// OCR support can be added later via Tesseract.NET or a separate
/// net8.0-windows target that uses Windows.Media.Ocr.
/// </summary>
public class OcrService
{
    public bool IsAvailable => false;
    public void Initialize() { }
    public Task<string> OcrImageAsync(string imagePath) => Task.FromResult("");
}
