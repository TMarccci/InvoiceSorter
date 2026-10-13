using System.Text.Json;
using InvoiceSorter.Core.Models;

namespace InvoiceSorter.Core.Storage;

/// <summary>Saves/loads the OCR results of a job so styles can be re-tested without re-running OCR.</summary>
public static class JobCache
{
    private sealed class CacheFile
    {
        public string SourcePath { get; set; } = "";
        public List<CachedPage> Pages { get; set; } = new();
    }

    private sealed class CachedPage
    {
        public int Index { get; set; }
        public string ImagePath { get; set; } = "";
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
        public int Rotation { get; set; }
        public bool IsBlank { get; set; }
        public double InkRatio { get; set; }
        public string? Error { get; set; }
        public List<OcrTextLine> Lines { get; set; } = new();
        public List<List<OcrTextLine>>? ExtraReadings { get; set; }
        public int RefinePassesDone { get; set; }
    }

    public static string CachePath(string workDir) => Path.Combine(workDir, "pages.json");

    public static void Save(InvoiceJob job)
    {
        var file = new CacheFile
        {
            SourcePath = job.SourcePath,
            Pages = job.Pages.Select(p => new CachedPage
            {
                Index = p.Index, ImagePath = p.ImagePath, PixelWidth = p.PixelWidth, PixelHeight = p.PixelHeight,
                Rotation = p.Rotation, IsBlank = p.IsBlank, InkRatio = p.InkRatio, Error = p.Error, Lines = p.Lines,
                ExtraReadings = p.ExtraReadings.Count == 0 ? null : p.ExtraReadings.Select(r => r.Lines).ToList(),
                RefinePassesDone = p.RefinePassesDone,
            }).ToList(),
        };
        File.WriteAllText(CachePath(job.WorkDir), JsonSerializer.Serialize(file, JsonStore.Options));
    }

    public static InvoiceJob Load(string workDir)
    {
        var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath(workDir)), JsonStore.Options)!;
        var job = new InvoiceJob { SourcePath = file.SourcePath, WorkDir = workDir };
        job.Pages.AddRange(file.Pages.Select(p => new PageInfo
        {
            Index = p.Index, ImagePath = p.ImagePath, PixelWidth = p.PixelWidth, PixelHeight = p.PixelHeight,
            Rotation = p.Rotation, IsBlank = p.IsBlank, InkRatio = p.InkRatio, Error = p.Error, Lines = p.Lines,
            ExtraReadings = (p.ExtraReadings ?? new()).Select(lines => new PageInfo { Index = p.Index, ImagePath = p.ImagePath, Rotation = p.Rotation, Lines = lines }).ToList(),
            RefinePassesDone = p.RefinePassesDone,
        }));
        return job;
    }
}
