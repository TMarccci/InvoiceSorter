using System.Text;
using InvoiceSorter.Core.Logging;
using InvoiceSorter.Core.Models;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace InvoiceSorter.Core.Processing;

public sealed record ExportResult(int Saved, int SavedWithErrors, int Skipped, int Excluded, string ReportPath, string ExcelPath, List<string> Files);

/// <summary>Writes the split invoices as separate PDF files (pages are copied 1:1 from the source PDF).</summary>
public static class PdfExporter
{
    public static string BuildFileName(InvoiceDocument doc, AppSettings settings)
    {
        // "METRO Kft." + "_" reads badly as "METRO Kft._123", so trailing dots of the values are dropped.
        var name = settings.FileNameTemplate
            .Replace("{Issuer}", doc.Issuer.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            .Replace("{InvoiceNumber}", doc.InvoiceNumber.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            .Replace("{Style}", doc.Style?.Name ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{Number}", doc.Number.ToString("000"), StringComparison.OrdinalIgnoreCase);
        name = TextUtil.SanitizeFileName(name);
        return (name.Length == 0 ? $"Invoice_{doc.Number:000}" : name) + ".pdf";
    }

    public static string BuildErrorFileName(InvoiceDocument doc)
    {
        var parts = new List<string> { $"ERROR_{doc.Number:000}_p{doc.PageRange.Replace(", ", "_")}" };
        if (!string.IsNullOrWhiteSpace(doc.InvoiceNumber)) parts.Add(doc.InvoiceNumber);
        if (!string.IsNullOrWhiteSpace(doc.Issuer)) parts.Add(doc.Issuer);
        return TextUtil.SanitizeFileName(string.Join("_", parts)) + ".pdf";
    }

    private static string ErrorFolder(AppSettings settings) => string.IsNullOrWhiteSpace(settings.ErrorFolderName) ? "_Errors" : settings.ErrorFolderName;

    /// <summary>Where the invoice will be saved, relative to the output folder (without the _2 of an existing file); "" when it is not saved.</summary>
    public static string PlannedFile(InvoiceDocument doc, AppSettings settings) =>
        doc.Excluded ? ""
        : !doc.HasErrors ? BuildFileName(doc, settings)
        : settings.SaveErrorDocuments ? Path.Combine(ErrorFolder(settings), BuildErrorFileName(doc))
        : "";

    /// <summary>The report of the scan as an Excel workbook, without saving the invoices (the "Fájl" column shows where they would go).</summary>
    public static void ExportReport(InvoiceJob job, string xlsxPath, AppSettings settings)
    {
        InvoiceReport.WriteXlsx(xlsxPath, job.Documents.Select(d => (d, PlannedFile(d, settings))).ToList());
        AppLog.Info($"Excel-jelentés mentve ({job.Documents.Count} számla): {xlsxPath}");
    }

    public static ExportResult Export(InvoiceJob job, string outputDir, AppSettings settings, IProgress<ProgressInfo>? progress = null)
    {
        Directory.CreateDirectory(outputDir);
        var errorDir = Path.Combine(outputDir, ErrorFolder(settings));
        var files = new List<string>();
        int saved = 0, savedErrors = 0, skipped = 0, excluded = 0;
        var report = new List<(InvoiceDocument Doc, string File)>();

        AppLog.Info($"{job.Documents.Count} számla mentése ide: {outputDir}");
        using var source = PdfReader.Open(job.SourcePath, PdfDocumentOpenMode.Import);

        int n = 0;
        foreach (var doc in job.Documents)
        {
            progress?.Report(new ProgressInfo(ProgressInfo.Saving, ++n, job.Documents.Count));
            string? target = null;
            try
            {
                if (doc.Excluded)
                {
                    excluded++;
                    AppLog.Info($"A(z) {doc.Number}. számla ({doc.PageRange}. oldal) a felhasználó kérésére kimarad a mentésből");
                }
                else if (!doc.HasErrors)
                {
                    target = UniquePath(Path.Combine(outputDir, BuildFileName(doc, settings)));
                    WritePdf(source, doc, target);
                    saved++;
                    AppLog.Info($"Mentve: {doc.Number}. számla -> {Path.GetFileName(target)}" + (doc.IsManuallyEdited ? $" (kézzel módosítva: {doc.EditsText})" : ""));
                }
                else if (settings.SaveErrorDocuments)
                {
                    Directory.CreateDirectory(errorDir);
                    target = UniquePath(Path.Combine(errorDir, BuildErrorFileName(doc)));
                    WritePdf(source, doc, target);
                    savedErrors++;
                    AppLog.Warn($"Hibákkal mentve: {doc.Number}. számla -> {Path.GetFileName(errorDir)}\\{Path.GetFileName(target)}: {doc.ErrorsText}");
                }
                else
                {
                    skipped++;
                    AppLog.Warn($"Kihagyva: {doc.Number}. számla (hibás): {doc.ErrorsText}");
                }
                if (target != null) files.Add(target);
            }
            catch (Exception ex)
            {
                AppLog.Error($"A(z) {doc.Number}. számla nem menthető", ex);
                skipped++;
                target = null;
            }

            report.Add((doc, target == null ? "" : Path.GetRelativePath(outputDir, target)));
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var reportPath = Path.Combine(outputDir, $"_report_{stamp}.csv");
        InvoiceReport.WriteCsv(reportPath, report);
        var excelPath = Path.Combine(outputDir, $"_report_{stamp}.xlsx");
        try
        {
            InvoiceReport.WriteXlsx(excelPath, report);
        }
        catch (Exception ex)
        {
            AppLog.Error("Az Excel-jelentés nem menthető (a CSV-jelentés elkészült)", ex);
            excelPath = "";
        }

        AppLog.Info($"Mentés kész: {saved} mentve, {savedErrors} a hibás mappába, {skipped} kihagyva, {excluded} kizárva. Jelentés: {reportPath}");
        var logPath = Path.Combine(outputDir, $"_log_{stamp}.txt");
        File.WriteAllLines(logPath, AppLog.EntriesSince(job.StartedAt).Select(e => e.ToString()), new UTF8Encoding(true));

        return new ExportResult(saved, savedErrors, skipped, excluded, reportPath, excelPath, files);
    }

    private static void WritePdf(PdfDocument source, InvoiceDocument doc, string path)
    {
        using var output = new PdfDocument();
        output.Info.Title = Path.GetFileNameWithoutExtension(path);
        output.Info.Creator = "Invoice Sorter";
        foreach (var page in doc.Pages)
        {
            var added = output.AddPage(source.Pages[page.Index]);
            if (page.Rotation != 0) added.Rotate = (added.Rotate + page.Rotation) % 360;
        }
        output.Save(path);
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
