using System.Text;
using InvoiceSorter.Core.Models;

namespace InvoiceSorter.Core.Processing;

/// <summary>
/// The report of a scan: one row per invoice with the read values and the text the OCR engines read (CSV next to the
/// saved PDFs, and Excel with a second sheet holding every reading of every page).
/// </summary>
public static class InvoiceReport
{
    public static readonly string[] Columns =
    {
        "Számla", "Oldalak", "Állapot", "Megbízhatóság", "Sablon", "Kibocsátó", "Számlaszám", "Kiállítás dátuma", "Teljesítés dátuma",
        "Fizetési határidő", "Vevő adószáma", "Fájl", "Hibák", "Kézi módosítás", "Levonások", "Dátumok forrása",
        "Windows OCR szövege", "Surya szövege",
    };

    private static readonly double[] Widths = { 7, 9, 9, 8, 22, 30, 20, 12, 12, 12, 22, 40, 50, 24, 50, 40, 60, 60 };

    public const string WindowsEngine = "Windows", SuryaEngine = "Surya";

    /// <summary>The values of an invoice (dates as DateTime when they are complete dates, for Excel).</summary>
    /// <param name="file">The saved file relative to the output folder ("" when not saved).</param>
    public static object?[] Row(InvoiceDocument doc, string file) => new object?[]
    {
        doc.Number, doc.PageRange, doc.StatusText, doc.Trust, doc.StyleName, doc.Issuer, doc.InvoiceNumber,
        Date(doc.IssueDate), Date(doc.FulfillmentDate), Date(doc.DueDate),
        doc.VatText, file, doc.ErrorsText, doc.EditsText, doc.TrustReasonsText,
        $"kelte: {doc.IssueDateSource}; teljesítés: {doc.FulfillmentDateSource}; határidő: {doc.DueDateSource}",
        OcrText(doc, WindowsEngine), OcrText(doc, SuryaEngine),
    };

    private static object Date(string text) => DateReader.Parse(text) is { } d ? d.ToDateTime(TimeOnly.MinValue) : text;

    /// <summary>
    /// The text an OCR engine read from the pages of the invoice: of each page the first reading of that engine (the
    /// normal reading, or the first re-read). Pages are headed "--- 2. oldal ---"; "" when the engine read no page.
    /// </summary>
    public static string OcrText(InvoiceDocument doc, string engine)
    {
        var sb = new StringBuilder();
        foreach (var page in doc.Pages)
        {
            var reading = page.Readings.FirstOrDefault(r => r.Engine == engine);
            if (reading == null) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append($"--- {page.Number}. oldal ---\n").Append(FieldReader.WordsToText(reading.Lines.SelectMany(l => l.Words)));
        }
        return sb.ToString();
    }

    /// <summary>"Olvasás", "1. újraolvasás" … – the name of a reading of a page, as in the OCR text window.</summary>
    public static string ReadingName(int index) => index == 0 ? "Olvasás" : $"{index}. újraolvasás";

    public static string EngineName(string engine) => engine == SuryaEngine ? "Surya" : "Windows OCR";

    public static void WriteCsv(string path, IEnumerable<(InvoiceDocument Doc, string File)> invoices)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(";", Columns));
        foreach (var (doc, file) in invoices)
            sb.AppendLine(string.Join(";", Row(doc, file).Select(v => Csv(v switch
            {
                DateTime d => DateReader.Format(DateOnly.FromDateTime(d)),
                null => "",
                _ => v.ToString() ?? "",
            }))));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Sheet "Számlák" (one row per invoice) and sheet "OCR-szöveg" (every reading of every page).</summary>
    public static void WriteXlsx(string path, IReadOnlyList<(InvoiceDocument Doc, string File)> invoices)
    {
        var rows = invoices.Select(x => Row(x.Doc, x.File)).ToList();
        var readings = new List<object?[]>();
        foreach (var (doc, _) in invoices)
        foreach (var page in doc.Pages)
        {
            int i = 0;
            foreach (var reading in page.Readings)
            {
                var text = FieldReader.WordsToText(reading.Lines.SelectMany(l => l.Words));
                readings.Add(new object?[] { doc.Number, page.Number, ReadingName(i++), EngineName(reading.Engine), reading.WordCount, text });
            }
        }
        XlsxWriter.Write(path, new[]
        {
            new XlsxSheet("Számlák", Columns, rows, Widths),
            new XlsxSheet("OCR-szöveg", new[] { "Számla", "Oldal", "Olvasat", "OCR-motor", "Szavak", "Szöveg" }, readings, new double[] { 7, 7, 15, 13, 8, 120 }),
        });
    }

    private static string Csv(string s) =>
        s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
