using System.IO.Compression;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using DocuSearch.Core.Models;

namespace DocuSearch.Core.Services;

public class ExtractionService
{
    public ExtractionResult Extract(string path, string extension)
    {
        try
        {
            return extension.ToLowerInvariant() switch
            {
                "txt" or "csv" or "md" or "rtf" or "log" or "json" or "xml" or "html" or "htm"
                    => ExtractText(path),
                "pdf" => ExtractPdf(path),
                "docx" => ExtractDocx(path),
                "xlsx" or "xlsm" => ExtractXlsx(path),
                "pptx" => ExtractPptx(path),
                "doc" or "xls" or "ppt"
                    => new ExtractionResult { Text = "", NeedsOcr = true, ErrorMessage = "Legacy format — convert to .docx/.xlsx/.pptx" },
                _ => new ExtractionResult { Text = "", ErrorMessage = $"Unsupported: {extension}" }
            };
        }
        catch (Exception ex)
        {
            return new ExtractionResult { Text = "", ErrorMessage = ex.Message };
        }
    }

    private ExtractionResult ExtractText(string path)
    {
        var text = File.ReadAllText(path);
        if (text.Length > 500_000) text = text[..500_000] + "\n[truncated]";
        return new ExtractionResult { Text = text, Source = "native" };
    }

    private ExtractionResult ExtractPdf(string path)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            using var doc = PdfDocument.Open(path);

            int pageCount = 0;
            foreach (var page in doc.GetPages())
            {
                if (pageCount >= 200) break; // cap at 200 pages
                sb.AppendLine(page.Text);
                pageCount++;
            }

            var text = sb.ToString().Trim();
            if (string.IsNullOrEmpty(text) || text.Length < 10)
                return new ExtractionResult { Text = "", NeedsOcr = true, ErrorMessage = "Scanned PDF — needs OCR" };

            if (text.Length > 500_000) text = text[..500_000] + "\n[truncated]";
            return new ExtractionResult { Text = text, Source = "native" };
        }
        catch (Exception ex)
        {
            return new ExtractionResult { Text = "", NeedsOcr = true, ErrorMessage = $"PDF: {ex.Message}" };
        }
    }

    private ExtractionResult ExtractDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml");
        if (entry == null) return new ExtractionResult { Text = "", ErrorMessage = "Invalid .docx" };

        using var stream = entry.Open();
        var doc = new XmlDocument();
        doc.Load(stream);
        var nsmgr = new XmlNamespaceManager(doc.NameTable);
        nsmgr.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");

        var sb = new System.Text.StringBuilder();
        var paragraphs = doc.SelectNodes("//w:p", nsmgr);
        if (paragraphs != null)
        {
            foreach (XmlNode p in paragraphs)
            {
                var texts = p.SelectNodes(".//w:t", nsmgr);
                if (texts != null)
                {
                    foreach (XmlNode t in texts) sb.Append(t.InnerText);
                    sb.AppendLine();
                }
            }
        }

        var text = sb.ToString();
        if (text.Length > 500_000) text = text[..500_000] + "\n[truncated]";
        return new ExtractionResult { Text = text, Source = "native" };
    }

    private ExtractionResult ExtractXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var sharedStrings = new List<string>();
        var sharedEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (sharedEntry != null)
        {
            using var s = sharedEntry.Open();
            var doc = new XmlDocument(); doc.Load(s);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("s", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            var nodes = doc.SelectNodes("//s:si", ns);
            if (nodes != null)
                foreach (XmlNode n in nodes)
                {
                    var ts = n.SelectNodes(".//s:t", ns);
                    var sb = new System.Text.StringBuilder();
                    if (ts != null) foreach (XmlNode t in ts) sb.Append(t.InnerText);
                    sharedStrings.Add(sb.ToString());
                }
        }

        var result = new System.Text.StringBuilder();
        var sheets = zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet") && e.FullName.EndsWith(".xml"))
            .OrderBy(e => e.FullName);
        foreach (var sheet in sheets)
        {
            using var s = sheet.Open();
            var doc = new XmlDocument(); doc.Load(s);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("s", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            var rows = doc.SelectNodes("//s:sheetData/s:row", ns);
            if (rows == null) continue;
            foreach (XmlNode row in rows)
            {
                var cells = row.SelectNodes("s:c", ns);
                if (cells == null) continue;
                var rowSb = new System.Text.StringBuilder();
                foreach (XmlNode cell in cells)
                {
                    var type = cell.Attributes?["t"]?.Value;
                    var val = cell.SelectSingleNode("s:v", ns);
                    if (val != null)
                    {
                        if (type == "s" && int.TryParse(val.InnerText, out var idx) && idx < sharedStrings.Count)
                            rowSb.Append(sharedStrings[idx]);
                        else rowSb.Append(val.InnerText);
                        rowSb.Append('\t');
                    }
                }
                if (rowSb.Length > 0) result.AppendLine(rowSb.ToString().TrimEnd('\t'));
            }
        }

        var text = result.ToString();
        if (text.Length > 500_000) text = text[..500_000] + "\n[truncated]";
        return new ExtractionResult { Text = text, Source = "native" };
    }

    private ExtractionResult ExtractPptx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var result = new System.Text.StringBuilder();
        var slides = zip.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide") && e.FullName.EndsWith(".xml"))
            .OrderBy(e => ExtractSlideNumber(e.FullName));
        foreach (var slide in slides)
        {
            using var s = slide.Open();
            var doc = new XmlDocument(); doc.Load(s);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("a", "http://schemas.openxmlformats.org/drawingml/2006/main");
            var nodes = doc.SelectNodes("//a:t", ns);
            if (nodes != null) { foreach (XmlNode n in nodes) result.Append(n.InnerText + " "); result.AppendLine(); }
        }
        var text = result.ToString();
        if (text.Length > 500_000) text = text[..500_000] + "\n[truncated]";
        return new ExtractionResult { Text = text, Source = "native" };
    }

    private static int ExtractSlideNumber(string path)
    {
        var match = System.Text.RegularExpressions.Regex.Match(path, @"slide(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }
}
