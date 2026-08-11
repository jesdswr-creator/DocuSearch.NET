using System.IO.Compression;
using System.Xml;
using DocuSearch.Core.Models;

namespace DocuSearch.Core.Services;

/// <summary>
/// Extracts text from PDF, DOCX, XLSX, PPTX, and plain text files.
/// Pure C# — no native dependencies (no Poppler, no minizip).
/// Uses System.IO.Compression for Office formats and basic PDF text extraction.
/// </summary>
public class ExtractionService
{
    /// <summary>
    /// Extract text from a file based on its extension.
    /// </summary>
    public ExtractionResult Extract(string path, string extension)
    {
        try
        {
            return extension.ToLowerInvariant() switch
            {
                "txt" or "csv" or "md" or "rtf" or "log" or "json" or "xml" or "html" or "htm"
                    => ExtractText(path),
                "pdf"
                    => ExtractPdf(path),
                "docx"
                    => ExtractDocx(path),
                "xlsx" or "xlsm"
                    => ExtractXlsx(path),
                "pptx"
                    => ExtractPptx(path),
                "doc"
                    => new ExtractionResult { Text = "", NeedsOcr = true, ErrorMessage = "Legacy .doc format not supported — convert to .docx" },
                "xls"
                    => new ExtractionResult { Text = "", NeedsOcr = true, ErrorMessage = "Legacy .xls format not supported — convert to .xlsx" },
                "ppt"
                    => new ExtractionResult { Text = "", NeedsOcr = true, ErrorMessage = "Legacy .ppt format not supported — convert to .pptx" },
                _
                    => new ExtractionResult { Text = "", ErrorMessage = $"Unsupported extension: {extension}" }
            };
        }
        catch (Exception ex)
        {
            return new ExtractionResult { Text = "", ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Extract text from a plain text file.
    /// </summary>
    private ExtractionResult ExtractText(string path)
    {
        var text = File.ReadAllText(path);
        // Truncate to 500KB for memory safety
        if (text.Length > 500_000)
            text = text[..500_000] + "\n\n[... text truncated ...]";
        return new ExtractionResult { Text = text, Source = "native" };
    }

    /// <summary>
    /// Extract text from a PDF file. Uses basic PDF text extraction
    /// (reads text from content streams). For complex PDFs, may need OCR.
    /// </summary>
    private ExtractionResult ExtractPdf(string path)
    {
        try
        {
            var text = ExtractPdfText(path);
            if (string.IsNullOrWhiteSpace(text) || text.Length < 10)
            {
                return new ExtractionResult
                {
                    Text = "",
                    NeedsOcr = true,
                    ErrorMessage = "PDF appears to be scanned — needs OCR"
                };
            }
            // Truncate for memory
            if (text.Length > 500_000)
                text = text[..500_000] + "\n\n[... text truncated ...]";
            return new ExtractionResult { Text = text, Source = "native" };
        }
        catch (Exception ex)
        {
            return new ExtractionResult
            {
                Text = "",
                NeedsOcr = true,
                ErrorMessage = $"PDF extraction failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Basic PDF text extraction — reads text operators from content streams.
    /// This is a simplified parser that works for text-based PDFs.
    /// For complex/encrypted PDFs, returns empty (triggers OCR).
    /// </summary>
    private static string ExtractPdfText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs);

        // Read the entire PDF into memory (cap at 50MB)
        var maxRead = Math.Min(fs.Length, 50 * 1024 * 1024);
        var data = reader.ReadBytes((int)maxRead);
        var text = System.Text.Encoding.ASCII.GetString(data);

        var extracted = new System.Text.StringBuilder();
        var i = 0;

        // Find all "stream...endstream" blocks and extract text operators
        while (i < text.Length)
        {
            var streamStart = text.IndexOf("stream", i);
            if (streamStart < 0) break;

            // Skip the newline after "stream"
            streamStart += 6;
            while (streamStart < text.Length && (text[streamStart] == '\r' || text[streamStart] == '\n'))
                streamStart++;

            var streamEnd = text.IndexOf("endstream", streamStart);
            if (streamEnd < 0) break;

            var content = text.AsSpan(streamStart, streamEnd - streamStart);

            // Extract text from Tj and TJ operators
            ExtractTextFromContent(content, extracted);

            i = streamEnd + 9;
        }

        return extracted.ToString();
    }

    private static void ExtractTextFromContent(ReadOnlySpan<char> content, System.Text.StringBuilder output)
    {
        var i = 0;
        while (i < content.Length)
        {
            // Look for (text) Tj or [(text)] TJ
            if (content[i] == '(')
            {
                i++;
                var start = i;
                while (i < content.Length && content[i] != ')')
                {
                    if (content[i] == '\\' && i + 1 < content.Length)
                        i++; // skip escaped char
                    i++;
                }
                if (i < content.Length)
                {
                    var text = content.Slice(start, i - start).ToString();
                    output.Append(System.Text.RegularExpressions.Regex.Unescape(text));
                    output.Append(' ');
                }
            }
            else if (content[i] == '<' && i + 1 < content.Length && content[i + 1] != '<')
            {
                // Hex string <...>
                i++;
                var start = i;
                while (i < content.Length && content[i] != '>')
                    i++;
                if (i < content.Length)
                {
                    var hex = content.Slice(start, i - start).ToString();
                    try
                    {
                        var bytes = Convert.FromHexString(hex.Replace(" ", ""));
                        output.Append(System.Text.Encoding.UTF8.GetString(bytes));
                        output.Append(' ');
                    }
                    catch { }
                }
            }
            i++;
        }
    }

    /// <summary>
    /// Extract text from a .docx file (Office Open XML format).
    /// Uses System.IO.Compression to read the document.xml inside the .docx.
    /// </summary>
    private ExtractionResult ExtractDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var docEntry = zip.GetEntry("word/document.xml");
        if (docEntry == null)
            return new ExtractionResult { Text = "", ErrorMessage = "Not a valid .docx file" };

        using var stream = docEntry.Open();
        var doc = new XmlDocument { PreserveWhitespace = false };
        doc.Load(stream);

        // Extract all text nodes from <w:t> elements
        var nsmgr = new XmlNamespaceManager(doc.NameTable);
        nsmgr.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");

        var nodes = doc.SelectNodes("//w:t", nsmgr);
        var text = new System.Text.StringBuilder();
        foreach (XmlNode node in nodes!)
        {
            text.Append(node.InnerText);
        }

        // Insert paragraph breaks
        var paragraphs = doc.SelectNodes("//w:p", nsmgr);
        var result = text.ToString();

        if (result.Length > 500_000)
            result = result[..500_000] + "\n\n[... text truncated ...]";

        return new ExtractionResult { Text = result, Source = "native" };
    }

    /// <summary>
    /// Extract text from an .xlsx file.
    /// Reads sharedStrings.xml + sheet data.
    /// </summary>
    private ExtractionResult ExtractXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        // Read shared strings
        var sharedStrings = new List<string>();
        var sharedEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (sharedEntry != null)
        {
            using var stream = sharedEntry.Open();
            var doc = new XmlDocument();
            doc.Load(stream);
            var nsmgr = new XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("s", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            var nodes = doc.SelectNodes("//s:si", nsmgr);
            if (nodes != null)
            {
                foreach (XmlNode node in nodes)
                {
                    var textNodes = node.SelectNodes(".//s:t", nsmgr);
                    var sb = new System.Text.StringBuilder();
                    foreach (XmlNode t in textNodes!)
                        sb.Append(t.InnerText);
                    sharedStrings.Add(sb.ToString());
                }
            }
        }

        // Read sheets
        var result = new System.Text.StringBuilder();
        var sheetEntries = zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet") && e.FullName.EndsWith(".xml"))
            .OrderBy(e => e.FullName);

        foreach (var sheetEntry in sheetEntries)
        {
            using var stream = sheetEntry.Open();
            var doc = new XmlDocument();
            doc.Load(stream);
            var nsmgr = new XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("s", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");

            var rows = doc.SelectNodes("//s:sheetData/s:row", nsmgr);
            if (rows == null) continue;

            foreach (XmlNode row in rows)
            {
                var cells = row.SelectNodes("s:c", nsmgr);
                if (cells == null) continue;

                var rowText = new System.Text.StringBuilder();
                foreach (XmlNode cell in cells)
                {
                    var type = cell.Attributes?["t"]?.Value;
                    var valueNode = cell.SelectSingleNode("s:v", nsmgr);

                    if (valueNode != null)
                    {
                        if (type == "s" && int.TryParse(valueNode.InnerText, out var idx) && idx < sharedStrings.Count)
                            rowText.Append(sharedStrings[idx]);
                        else
                            rowText.Append(valueNode.InnerText);
                        rowText.Append('\t');
                    }
                }
                if (rowText.Length > 0)
                {
                    result.AppendLine(rowText.ToString().TrimEnd('\t'));
                }
            }
        }

        var text = result.ToString();
        if (text.Length > 500_000)
            text = text[..500_000] + "\n\n[... text truncated ...]";

        return new ExtractionResult { Text = text, Source = "native" };
    }

    /// <summary>
    /// Extract text from a .pptx file.
    /// Reads slide XML files for text content.
    /// </summary>
    private ExtractionResult ExtractPptx(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        var result = new System.Text.StringBuilder();
        var slideEntries = zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide") && e.FullName.EndsWith(".xml"))
            .OrderBy(e => e.FullName, new NaturalStringComparer());

        foreach (var slideEntry in slideEntries)
        {
            using var stream = slideEntry.Open();
            var doc = new XmlDocument();
            doc.Load(stream);
            var nsmgr = new XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("a", "http://schemas.openxmlformats.org/drawingml/2006/main");

            var nodes = doc.SelectNodes("//a:t", nsmgr);
            if (nodes != null)
            {
                foreach (XmlNode node in nodes)
                    result.Append(node.InnerText + " ");
                result.AppendLine();
            }
        }

        var text = result.ToString();
        if (text.Length > 500_000)
            text = text[..500_000] + "\n\n[... text truncated ...]";

        return new ExtractionResult { Text = text, Source = "native" };
    }

    /// <summary>
    /// Natural string comparer for sorting slide1, slide2, ..., slide10 correctly.
    /// </summary>
    private class NaturalStringComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (x == null || y == null) return 0;
            // Extract the number from slideN.xml
            var xNum = ExtractNumber(x);
            var yNum = ExtractNumber(y);
            return xNum.CompareTo(yNum);
        }

        private static int ExtractNumber(string s)
        {
            var match = System.Text.RegularExpressions.Regex.Match(s, @"slide(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? int.Parse(match.Groups[1].Value) : 0;
        }
    }
}
