using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using InvoiceSorter.Core.Logging;
using InvoiceSorter.Core.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace InvoiceSorter.Core.Processing;

/// <summary>One extra way of reading a page: OCR engine / language, magnification and image adjustment.</summary>
public sealed record RefinePass(Language Language, double Scale, Enhancement Enhancement, bool Surya = false)
{
    public string Name => Surya ? "Surya MI-OCR"
        : $"{Language.LanguageTag} {Scale:0.#}×" + (Enhancement == Enhancement.None ? "" : ", " + ImageEnhancer.Describe(Enhancement));
}

/// <summary>
/// Second OCR pass for invoices that still have a problem (missing issuer / invoice number / buyer VAT, unknown style).
/// Small print is often misread at scanner resolution but read correctly when enlarged; OCR languages make
/// different mistakes; faded or grey prints become readable after darkening, contrast stretching or turning
/// them black &amp; white. The passes are tried one after another and stop for an invoice as soon as all of its
/// details can be read. Each extra reading is kept in <see cref="PageInfo.ExtraReadings"/>.
/// </summary>
public static class PageRefiner
{
    public const double Scale = 2.0;
    private const int MaxPagesPerInvoice = 2;

    /// <summary>The passes, cheapest / most promising first.</summary>
    public static List<RefinePass> Passes(AppSettings settings)
    {
        var primary = PageAnalyzer.SelectLanguage(settings.OcrLanguage);
        var other = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag != primary.LanguageTag);
        var passes = new List<RefinePass>();
        // Surya (if switched on for unclear invoices) is the most accurate reader, so it goes first.
        if (settings.SuryaMode == SuryaMode.UnclearOnly) passes.Add(new(primary, 1, Enhancement.None, Surya: true));
        passes.Add(new(primary, Scale, Enhancement.None));
        if (other != null) passes.Add(new(other, Scale, Enhancement.None));
        passes.Add(new(primary, Scale, Enhancement.Darker));
        passes.Add(new(primary, Scale, Enhancement.Contrast));
        passes.Add(new(primary, Scale, Enhancement.BlackWhite));
        if (other != null) passes.Add(new(other, Scale, Enhancement.Darker));
        passes.Add(new(primary, Scale, Enhancement.Lighter));
        return passes;
    }

    /// <summary>Invoices worth re-reading: they have a problem that better OCR could solve and passes are left.</summary>
    public static List<InvoiceDocument> InvoicesNeedingRefinement(IEnumerable<InvoiceDocument> documents, IReadOnlyList<InvoiceStyle> styles, AppSettings settings)
    {
        int passCount = Passes(settings).Count;
        return documents.Where(d => d.HasErrors && d.Pages.Count > 0
                                    && d.Pages.Take(MaxPagesPerInvoice).Any(p => p.RefinePassesDone < passCount && !p.IsBlank && File.Exists(p.ImagePath))
                                    && !IsResolved(d, styles, settings)).ToList();
    }

    /// <summary>True when style, issuer, invoice number and buyer VAT can all be read (from any reading).</summary>
    public static bool IsResolved(InvoiceDocument doc, IReadOnlyList<InvoiceStyle> styles, AppSettings settings)
    {
        var first = doc.Pages[0];
        var style = doc.Style ?? first.Readings.Select(r => FieldReader.FindStyle(r, styles, settings)).FirstOrDefault(s => s != null);
        if (style == null) return false;
        bool Readable(FieldDefinition f) => f.Mode == FieldMode.Fixed
            ? !string.IsNullOrWhiteSpace(f.FixedValue)
            : doc.Pages.Any(p => InvoiceProcessor.ReadFieldWithFallback(f, p, settings).Value != null);
        return Readable(style.Issuer) && Readable(style.InvoiceNumber)
               && (doc.VatApproved || InvoiceProcessor.CheckVat(doc.Pages, settings).Status == VatStatus.Valid);
    }

    /// <summary>
    /// Re-reads the first pages of the given invoices with the extra passes until each invoice is resolved.
    /// Only reads pages and adds readings - the invoices themselves are not changed (safe on a background thread).
    /// Returns the number of invoices that became readable.
    /// </summary>
    /// <param name="surya">The running Surya worker for the Surya pass (null: the pass is skipped).</param>
    public static async Task<int> RefineAsync(IReadOnlyList<InvoiceDocument> documents, IReadOnlyList<InvoiceStyle> styles, AppSettings settings,
        IProgress<ProgressInfo>? progress, CancellationToken ct, SuryaOcr? surya = null)
    {
        if (documents.Count == 0) return 0;
        var passes = Passes(settings);
        AppLog.Info($"{documents.Count} hibás számla újraolvasása. Menetek: {string.Join("; ", passes.Select(p => p.Name))}");

        var engines = new ConcurrentDictionary<string, ConcurrentBag<OcrEngine>>();
        int done = 0, fixedCount = 0;
        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };

        await Parallel.ForEachAsync(documents, options, async (doc, token) =>
        {
            var pages = doc.Pages.Take(MaxPagesPerInvoice).Where(p => !p.IsBlank && File.Exists(p.ImagePath)).ToList();
            var images = new Dictionary<PageInfo, (byte[] Pixels, int Width, int Height)>();
            bool resolved = false;

            for (int passIndex = 0; passIndex < passes.Count && !resolved; passIndex++)
            {
                var pass = passes[passIndex];
                foreach (var page in pages.Where(p => p.RefinePassesDone == passIndex))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        PageInfo reading;
                        if (pass.Surya)
                        {
                            if (surya == null) { page.RefinePassesDone = passIndex + 1; continue; }
                            reading = new PageInfo
                            {
                                Index = page.Index, ImagePath = page.ImagePath, PixelWidth = page.PixelWidth, PixelHeight = page.PixelHeight,
                                Rotation = page.Rotation, Engine = "Surya", Lines = await surya.RecognizeAsync(page.ImagePath, token),
                            };
                        }
                        else
                        {
                            if (!images.TryGetValue(page, out var image)) images[page] = image = await LoadScaledAsync(page.ImagePath, pass.Scale);
                            reading = await ReadAsync(page, image, pass, engines);
                        }
                        page.ExtraReadings = page.ExtraReadings.Append(reading).ToList();
                        progress?.Report(new ProgressInfo(ProgressInfo.Refine, Volatile.Read(ref done), documents.Count,
                            $"A(z) {doc.Pages[0].Number}. oldali számla: {page.Number}. oldal, {pass.Name}", reading));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"{page.Number}. oldal: az újraolvasás ({pass.Name}) nem sikerült: {ex.Message}");
                    }
                    page.RefinePassesDone = passIndex + 1;
                }
                resolved = IsResolved(doc, styles, settings);
                if (resolved)
                {
                    Interlocked.Increment(ref fixedCount);
                    AppLog.Info($"A(z) {doc.PageRange}. oldali számla újraolvasás után olvasható ({pass.Name})");
                }
            }
            if (!resolved) AppLog.Debug($"Invoice on page(s) {doc.PageRange}: still not readable after {passes.Count} re-read passes");
            Interlocked.Increment(ref done);
        });
        return fixedCount;
    }

    private static async Task<(byte[] Pixels, int Width, int Height)> LoadScaledAsync(string path, double requestedScale)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);

        // Windows OCR accepts images up to OcrEngine.MaxImageDimension pixels.
        double scale = Math.Min(requestedScale, PageAnalyzer.MaxOcrPixels / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        uint w = (uint)(decoder.PixelWidth * scale), h = (uint)(decoder.PixelHeight * scale);
        var transform = new BitmapTransform { ScaledWidth = w, ScaledHeight = h, InterpolationMode = BitmapInterpolationMode.Fant };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var pixels = new byte[w * h * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        return (pixels, (int)w, (int)h);
    }

    private static async Task<PageInfo> ReadAsync(PageInfo page, (byte[] Pixels, int Width, int Height) image, RefinePass pass,
        ConcurrentDictionary<string, ConcurrentBag<OcrEngine>> engines)
    {
        var bag = engines.GetOrAdd(pass.Language.LanguageTag, _ => new ConcurrentBag<OcrEngine>());
        if (!bag.TryTake(out var engine))
            engine = OcrEngine.TryCreateFromLanguage(pass.Language) ?? throw new InvalidOperationException($"Nem hozható létre OCR-motor ehhez a nyelvhez: {pass.Language.LanguageTag}");
        try
        {
            var pixels = ImageEnhancer.Apply(image.Pixels, pass.Enhancement);
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);
            var reading = new PageInfo
            {
                Index = page.Index, ImagePath = page.ImagePath, PixelWidth = image.Width, PixelHeight = image.Height, Rotation = page.Rotation,
                Lines = PageAnalyzer.ToNormalizedLines(result, image.Width, image.Height),
            };
            AppLog.Debug($"Page {page.Number}: re-read ({pass.Name}): {reading.WordCount} words (normal reading: {page.WordCount})");
            return reading;
        }
        finally
        {
            bag.Add(engine);
        }
    }
}
