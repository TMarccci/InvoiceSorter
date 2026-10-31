using System.IO.Compression;
using System.Xml.Linq;
using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;

namespace InvoiceSorter.Tests;

/// <summary>The report of a scan: CSV and Excel with the read values and the OCR text.</summary>
public class ExportTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static PageInfo Reading(int index, string engine, params string[] lines)
    {
        var page = new PageInfo { Index = index, Engine = engine };
        for (int i = 0; i < lines.Length; i++)
        {
            var line = new OcrTextLine();
            line.Words.Add(new OcrWordBox { Text = lines[i], X = 0.1, Y = 0.1 + i * 0.03, W = 0.3, H = 0.012 });
            page.Lines.Add(line);
        }
        return page;
    }

    /// <summary>Two pages: the first was re-read with Surya too, the second only by Windows OCR.</summary>
    private static InvoiceDocument Invoice()
    {
        var first = Reading(0, "Windows", "Alfa Kft.", "Számlaszám: A-100");
        first.ExtraReadings.Add(Reading(0, "Windows", "Alfa Kft.", "Számlaszám: A-1OO"));
        first.ExtraReadings.Add(Reading(0, "Surya", "Alfa Kft.", "Számlaszám: A-100"));
        var doc = new InvoiceDocument { Number = 1 };
        doc.Pages.Add(first);
        doc.Pages.Add(Reading(1, "Windows", "Fizetendő: 12 700 Ft"));
        doc.Issuer = "Alfa Kft.";
        doc.InvoiceNumber = "A-100";
        doc.IssueDate = "2026.09.02.";
        return doc;
    }

    [Theory]
    [InlineData(0, 1, "A1")]
    [InlineData(25, 1, "Z1")]
    [InlineData(26, 3, "AA3")]
    [InlineData(27, 3, "AB3")]
    [InlineData(701, 1, "ZZ1")]
    [InlineData(702, 1, "AAA1")]
    public void CellNames(int column, int row, string expected) => Assert.Equal(expected, XlsxWriter.CellName(column, row));

    [Fact]
    public void CellTextIsCleanedAndCutToTheExcelLimit()
    {
        Assert.Equal("a\nb\nc", XlsxWriter.CellText("a\r\nb\rc\u0001"));
        var cut = XlsxWriter.CellText(new string('x', XlsxWriter.MaxCellText + 10));
        Assert.Equal(XlsxWriter.MaxCellText, cut.Length);
        Assert.EndsWith("…", cut);
    }

    [Fact]
    public void ReportHasTheTextOfEachOcrEngine()
    {
        var doc = Invoice();
        var windows = InvoiceReport.OcrText(doc, InvoiceReport.WindowsEngine);
        Assert.Contains("--- 1. oldal ---", windows);
        Assert.Contains("--- 2. oldal ---", windows);
        Assert.Contains("Számlaszám: A-100", windows);
        Assert.DoesNotContain("A-1OO", windows);   // the normal reading, not the re-read

        var surya = InvoiceReport.OcrText(doc, InvoiceReport.SuryaEngine);
        Assert.Contains("--- 1. oldal ---", surya);
        Assert.DoesNotContain("--- 2. oldal ---", surya);   // Surya did not read page 2
    }

    [Fact]
    public void CsvReportKeepsMultiLineOcrTextInOneCell()
    {
        var path = Path.Combine(Path.GetTempPath(), $"report-test-{Guid.NewGuid():N}.csv");
        try
        {
            InvoiceReport.WriteCsv(path, new[] { (Invoice(), "A-100_Alfa Kft.pdf") });
            var text = File.ReadAllText(path);
            var header = text[..text.IndexOf('\n')].TrimEnd('\r').Split(';');
            Assert.Equal(InvoiceReport.Columns, header);
            Assert.Contains(";2026.09.02.;", text);
            Assert.Contains("\"--- 1. oldal ---\nAlfa Kft.", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExcelReportHasInvoicesAndEveryReading()
    {
        var path = Path.Combine(Path.GetTempPath(), $"report-test-{Guid.NewGuid():N}.xlsx");
        try
        {
            InvoiceReport.WriteXlsx(path, new[] { (Invoice(), "A-100_Alfa Kft.pdf") });
            using var zip = ZipFile.OpenRead(path);
            foreach (var part in new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml" })
                Assert.NotNull(zip.GetEntry(part));

            XDocument Sheet(int n) { using var s = zip.GetEntry($"xl/worksheets/sheet{n}.xml")!.Open(); return XDocument.Load(s); }
            var invoices = Sheet(1).Descendants(Main + "row").ToList();
            Assert.Equal(2, invoices.Count);
            Assert.Equal(InvoiceReport.Columns.Length, invoices[0].Elements(Main + "c").Count());
            var date = invoices[1].Elements(Main + "c").Single(c => (string?)c.Attribute("r") == "H2");
            Assert.Equal("2", (string?)date.Attribute("s"));                                    // date style
            Assert.Equal(new DateTime(2026, 9, 2).ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture), date.Element(Main + "v")!.Value);

            // OCR sheet: header + 3 readings of page 1 + 1 reading of page 2.
            var readings = Sheet(2).Descendants(Main + "row").ToList();
            Assert.Equal(5, readings.Count);
            Assert.Contains("Surya", readings[3].Value);
            Assert.Contains("2. újraolvasás", readings[3].Value);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
