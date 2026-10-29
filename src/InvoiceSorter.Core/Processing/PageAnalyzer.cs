using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using InvoiceSorter.Core.Logging;
using InvoiceSorter.Core.Models;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace InvoiceSorter.Core.Processing;

/// <param name="Page">The page that was just processed (shown as live preview), if any.</param>
public sealed record ProgressInfo(string Stage, int Current, int Total, string? Detail = null, PageInfo? Page = null)
{
    // The stage names (shown in the window; the window also uses them to switch the wizard step).
    public const string Ocr = "Oldalak olvasása (OCR)";
    public const string SuryaAllPages = "Oldalak olvasása Surya OCR-rel";
    public const string StartingSurya = "Surya OCR indítása (a modell betöltése)";
    public const string ConnectingSurya = "Csatlakozás a Surya-szerverhez";
    public const string Splitting = "Számlák szétválasztása és olvasása";
    public const string CompanyLookup = "Cégnevek lekérdezése adószám alapján";
    public const string Refine = "Bizonytalan számlák újraolvasása";
    public const string Saving = "Mentés";
    public const string Done = "Kész";
}

/// <summary>
/// Renders PDF pages to images (Windows.Data.Pdf) and runs the built-in Windows OCR engine on them.
/// Both are part of Windows - no cloud service, no AI credits.
/// </summary>
public sealed class PageAnalyzer : IDisposable
{
    private readonly PdfDocument _pdf;
    private readonly SemaphoreSlim _renderLock = new(1, 1);
    private readonly ConcurrentBag<OcrEngine> _engines = new();
    private readonly Language _language;

    public int PageCount => (int)_pdf.PageCount;
    public string OcrLanguageTag => _language.LanguageTag;

    private PageAnalyzer(PdfDocument pdf, Language language)
    {
        _pdf = pdf;
        _language = language;
    }

    public static IReadOnlyList<string> AvailableOcrLanguages() =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    /// <summary>The OCR language to use: the configured one, otherwise Hungarian, otherwise English.</summary>
    public static Language SelectLanguage(string? preferredLanguage)
    {
        var languages = OcrEngine.AvailableRecognizerLanguages;
        if (languages.Count == 0)
            throw new InvalidOperationException("Nincs telepítve Windows OCR nyelv. Adj hozzá OCR-t támogató nyelvet: Windows Gépház › Idő és nyelv › Nyelv.");

        return languages.FirstOrDefault(l => !string.IsNullOrEmpty(preferredLanguage) && l.LanguageTag.Equals(preferredLanguage, StringComparison.OrdinalIgnoreCase))
               ?? languages.FirstOrDefault(l => l.LanguageTag.StartsWith("hu", StringComparison.OrdinalIgnoreCase))
               ?? languages.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
               ?? languages[0];
    }

    /// <summary>Converts a Windows OCR result to normalized lines (coordinates relative to the given image size).</summary>
    internal static List<OcrTextLine> ToNormalizedLines(OcrResult result, double width, double height) => ToLines(result, (int)width, (int)height);

    public static async Task<PageAnalyzer> OpenAsync(string pdfPath, string? preferredLanguage)
    {
        var lang = SelectLanguage(preferredLanguage);
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath));
        var pdf = await PdfDocument.LoadFromFileAsync(file);
        return new PageAnalyzer(pdf, lang);
    }

    /// <summary>Renders, rotates (if needed), OCRs all pages and stores upright JPEGs in the job folder.</summary>
    public async Task AnalyzeAllAsync(InvoiceJob job, AppSettings settings, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        int total = PageCount;
        int done = 0;
        var pages = new PageInfo[total];
        AppLog.Info($"{total} oldal olvasása Windows OCR-rel ({_language.LanguageTag}), {settings.RenderDpi} DPI, legnagyobb képméret {OcrEngine.MaxImageDimension} px");

        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, total), options, async (i, token) =>
        {
            var page = new PageInfo { Index = i };
            try
            {
                await AnalyzePageAsync(page, job.WorkDir, settings, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                page.Error = ex.Message;
                AppLog.Error($"{i + 1}. oldal: a feldolgozás nem sikerült", ex);
            }
            pages[i] = page;
            var n = Interlocked.Increment(ref done);
            progress?.Report(new ProgressInfo(ProgressInfo.Ocr, n, total, $"{i + 1}. oldal", page));
        });

        job.Pages.Clear();
        job.Pages.AddRange(pages);

        var blank = pages.Count(p => p.IsBlank);
        var rotated = pages.Count(p => p.Rotation != 0);
        AppLog.Info($"OCR kész: {total} oldal, {blank} üres, {rotated} automatikusan elforgatva, {pages.Count(p => p.Error != null)} sikertelen");
    }

    /// <summary>Analyzes a single page (used by the style editor to load sample pages on demand).</summary>
    public async Task<PageInfo> AnalyzePageAsync(int index, string workDir, AppSettings settings, CancellationToken ct = default)
    {
        var page = new PageInfo { Index = index };
        await AnalyzePageAsync(page, workDir, settings, ct);
        return page;
    }

    private async Task AnalyzePageAsync(PageInfo page, string workDir, AppSettings settings, CancellationToken ct)
    {
        using var rendered = await RenderAsync(page.Index, settings.RenderDpi);
        var decoder = await BitmapDecoder.CreateAsync(rendered);

        var upright = await DecodeAsync(decoder, BitmapRotation.None);
        page.InkRatio = ComputeInkRatio(upright);
        page.IsBlank = page.InkRatio < settings.BlankInkThreshold;

        if (page.IsBlank)
        {
            AppLog.Debug($"Page {page.Number}: blank (ink {page.InkRatio:P3})");
        }
        else
        {
            ct.ThrowIfCancellationRequested();
            var engine = RentEngine();
            try
            {
                var result = await RecognizeFittedAsync(engine, upright);
                int rotation = 0;
                int score = QualityScore(result.Result);

                // Pages scanned sideways/upside down produce mostly garbage words: try the other orientations.
                if (settings.AutoRotate && score < 40)
                {
                    foreach (var (rot, deg) in new[] { (BitmapRotation.Clockwise90Degrees, 90), (BitmapRotation.Clockwise270Degrees, 270), (BitmapRotation.Clockwise180Degrees, 180) })
                    {
                        ct.ThrowIfCancellationRequested();
                        var candidate = await DecodeAsync(decoder, rot);
                        var r = await RecognizeFittedAsync(engine, candidate);
                        var s = QualityScore(r.Result);
                        if (s > score * 1.5 && s >= 10)
                        {
                            upright.Dispose();
                            upright = candidate;
                            result = r;
                            score = s;
                            rotation = deg;
                        }
                        else
                        {
                            candidate.Dispose();
                        }
                    }
                    if (rotation != 0) AppLog.Info($"{page.Number}. oldal: automatikusan elforgatva {rotation}°-kal");
                }

                page.Rotation = rotation;
                page.Lines = result.Lines;
            }
            finally
            {
                _engines.Add(engine);
            }
        }

        page.PixelWidth = upright.PixelWidth;
        page.PixelHeight = upright.PixelHeight;
        page.ImagePath = Path.Combine(workDir, $"page_{page.Number:0000}.jpg");
        await SaveJpegAsync(upright, page.ImagePath);
        upright.Dispose();

        AppLog.Debug($"Page {page.Number}: {page.WordCount} words, ink {page.InkRatio:P2}");
    }

    /// <summary>
    /// Reads a page again from the PDF, rendered at <paramref name="dpi"/> and turned upright like the page
    /// (<see cref="PageInfo.Rotation"/>), optionally with an image adjustment. Unlike the saved page image (JPEG) this
    /// has no second compression; above the scan's own resolution the PDF renderer only interpolates.
    /// </summary>
    public async Task<PageInfo> ReadRenderedAsync(PageInfo page, int dpi, Enhancement enhancement = Enhancement.None, Language? language = null)
    {
        using var upright = await RenderUprightAsync(page, dpi);
        SoftwareBitmap input = upright;
        SoftwareBitmap? adjusted = null;
        if (enhancement != Enhancement.None)
        {
            var pixels = new byte[upright.PixelWidth * upright.PixelHeight * 4];
            upright.CopyToBuffer(pixels.AsBuffer());
            adjusted = SoftwareBitmap.CreateCopyFromBuffer(ImageEnhancer.Apply(pixels, enhancement).AsBuffer(), BitmapPixelFormat.Bgra8,
                upright.PixelWidth, upright.PixelHeight, BitmapAlphaMode.Premultiplied);
            input = adjusted;
        }
        var engine = language == null || language.LanguageTag == _language.LanguageTag ? RentEngine()
            : OcrEngine.TryCreateFromLanguage(language) ?? throw new InvalidOperationException($"Nem hozható létre OCR-motor ehhez a nyelvhez: {language.LanguageTag}");
        try
        {
            var result = await RecognizeFittedAsync(engine, input);
            return new PageInfo
            {
                Index = page.Index, ImagePath = page.ImagePath, PixelWidth = input.PixelWidth, PixelHeight = input.PixelHeight,
                Rotation = page.Rotation, Lines = result.Lines,
            };
        }
        finally
        {
            adjusted?.Dispose();
            if (language == null || language.LanguageTag == _language.LanguageTag) _engines.Add(engine);
        }
    }

    /// <summary>Renders a page and turns it like the page image (automatic or manual rotation). Neither side exceeds the OCR limit.</summary>
    private async Task<SoftwareBitmap> RenderUprightAsync(PageInfo page, int dpi)
    {
        using var rendered = await RenderAsync(page.Index, dpi);
        var decoder = await BitmapDecoder.CreateAsync(rendered);
        var rotation = (((page.Rotation % 360) + 360) % 360) switch
        {
            90 => BitmapRotation.Clockwise90Degrees,
            180 => BitmapRotation.Clockwise180Degrees,
            270 => BitmapRotation.Clockwise270Degrees,
            _ => BitmapRotation.None,
        };
        return await DecodeAsync(decoder, rotation);
    }

    private OcrEngine RentEngine()
    {
        if (_engines.TryTake(out var engine)) return engine;
        return OcrEngine.TryCreateFromLanguage(_language)
               ?? throw new InvalidOperationException($"Nem hozható létre OCR-motor ehhez a nyelvhez: {_language.LanguageTag}");
    }

    private async Task<InMemoryRandomAccessStream> RenderAsync(int index, int dpi)
    {
        await _renderLock.WaitAsync();
        try
        {
            using var pdfPage = _pdf.GetPage((uint)index);
            var stream = new InMemoryRandomAccessStream();
            double w = pdfPage.Size.Width * dpi / 96.0;
            // Till receipts are narrow pages with small print: render them at least ~1300 px wide (as long as the
            // height stays within what the OCR engine accepts), otherwise whole lines get lost.
            double aspect = pdfPage.Size.Height / Math.Max(1, pdfPage.Size.Width);
            if (pdfPage.Size.Width > 0 && pdfPage.Size.Width < 4.5 * 96)
                w = Math.Max(w, Math.Min(1300, MaxOcrPixels / aspect));
            // Neither side may exceed what the OCR engine of this Windows accepts (Windows 10 allows much smaller
            // images than Windows 11): large pages are rendered at a lower resolution instead of failing.
            w = Math.Min(w, Math.Min(MaxOcrPixels, MaxOcrPixels / aspect));
            var width = (uint)Math.Clamp(Math.Round(w), 200, MaxOcrPixels);
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = width,
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                BitmapEncoderId = BitmapEncoder.BmpEncoderId,
            };
            await pdfPage.RenderToStreamAsync(stream, options);
            stream.Seek(0);
            return stream;
        }
        finally
        {
            _renderLock.Release();
        }
    }

    /// <summary>
    /// The largest image side the Windows OCR engine of this computer accepts (a little margin kept). For testing,
    /// INVOICESORTER_OCR_MAX_PIXELS sets a smaller limit (e.g. to try the behaviour of an older Windows).
    /// </summary>
    internal static int MaxOcrPixels { get; } = Math.Min(
        (int)Math.Max(1000, OcrEngine.MaxImageDimension - 2),
        int.TryParse(Environment.GetEnvironmentVariable("INVOICESORTER_OCR_MAX_PIXELS"), out var limit) && limit >= 1000 ? limit : int.MaxValue);

    /// <summary>A Windows OCR result and the size of the image that was actually read (word boxes are in its pixels).</summary>
    internal sealed record OcrRun(OcrResult Result, int Width, int Height)
    {
        public List<OcrTextLine> Lines => ToLines(Result, Width, Height);
    }

    /// <summary>
    /// Windows OCR of a bitmap; an image larger than <see cref="OcrEngine.MaxImageDimension"/> is scaled down first
    /// (the normalized coordinates of <see cref="OcrRun.Lines"/> fit the original image as well).
    /// </summary>
    internal static async Task<OcrRun> RecognizeFittedAsync(OcrEngine engine, SoftwareBitmap bitmap)
    {
        int longest = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        if (longest <= MaxOcrPixels) return new OcrRun(await engine.RecognizeAsync(bitmap), bitmap.PixelWidth, bitmap.PixelHeight);
        using var scaled = await ScaleAsync(bitmap, MaxOcrPixels / (double)longest);
        return new OcrRun(await engine.RecognizeAsync(scaled), scaled.PixelWidth, scaled.PixelHeight);
    }

    private static async Task<SoftwareBitmap> ScaleAsync(SoftwareBitmap bitmap, double scale)
    {
        using var opaque = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        using var mem = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, mem);
        encoder.SetSoftwareBitmap(opaque);
        encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, Math.Floor(bitmap.PixelWidth * scale));
        encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, Math.Floor(bitmap.PixelHeight * scale));
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        await encoder.FlushAsync();
        mem.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(mem);
        return await DecodeAsync(decoder, BitmapRotation.None);
    }

    private static async Task<SoftwareBitmap> DecodeAsync(BitmapDecoder decoder, BitmapRotation rotation)
    {
        var transform = new BitmapTransform { Rotation = rotation };
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
    }

    /// <summary>Share of dark pixels, ignoring a margin (scanner edges, punch holes).</summary>
    private static double ComputeInkRatio(SoftwareBitmap bmp)
    {
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var buffer = new byte[w * h * 4];
        bmp.CopyToBuffer(buffer.AsBuffer());

        int mx = (int)(w * 0.06), my = (int)(h * 0.04);
        long dark = 0, total = 0;
        for (int y = my; y < h - my; y += 2)
        {
            int row = y * w * 4;
            for (int x = mx; x < w - mx; x += 2)
            {
                int o = row + x * 4;
                int lum = (buffer[o + 2] * 299 + buffer[o + 1] * 587 + buffer[o] * 114) / 1000;
                if (lum < 130) dark++;
                total++;
            }
        }
        return total == 0 ? 0 : (double)dark / total;
    }

    /// <summary>Number of "real looking" words: at least 3 characters, mostly letters/digits.</summary>
    private static int QualityScore(OcrResult result)
    {
        int score = 0;
        foreach (var line in result.Lines)
        foreach (var word in line.Words)
        {
            var t = word.Text;
            if (t.Length >= 3 && t.Count(char.IsLetterOrDigit) >= t.Length * 0.8) score++;
        }
        return score;
    }

    private static List<OcrTextLine> ToLines(OcrResult result, int width, int height)
    {
        var lines = new List<OcrTextLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var l = new OcrTextLine();
            foreach (var word in line.Words)
            {
                Rect r = word.BoundingRect;
                l.Words.Add(new OcrWordBox
                {
                    Text = word.Text,
                    X = r.X / width,
                    Y = r.Y / height,
                    W = r.Width / width,
                    H = r.Height / height,
                });
            }
            lines.Add(l);
        }
        return lines;
    }

    private static async Task SaveJpegAsync(SoftwareBitmap bmp, string path)
    {
        using var converted = SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        using var mem = new InMemoryRandomAccessStream();
        var props = new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(0.8, PropertyType.Single) } };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, mem, props);
        encoder.SetSoftwareBitmap(converted);
        await encoder.FlushAsync();

        mem.Seek(0);
        using var file = File.Create(path);
        await mem.AsStreamForRead().CopyToAsync(file);
    }

    /// <summary>
    /// Turns a page by 90/180/270 degrees clockwise (manual correction in the review): the page image is rotated, read
    /// again with Windows OCR and the rotation is applied to the saved PDF. Earlier re-reads are dropped (they belong
    /// to the old orientation).
    /// </summary>
    public static async Task RotatePageAsync(PageInfo page, int degreesClockwise, string? ocrLanguage)
    {
        degreesClockwise = ((degreesClockwise % 360) + 360) % 360;
        if (degreesClockwise == 0 || !File.Exists(page.ImagePath)) return;
        var rotation = degreesClockwise switch
        {
            90 => BitmapRotation.Clockwise90Degrees,
            180 => BitmapRotation.Clockwise180Degrees,
            _ => BitmapRotation.Clockwise270Degrees,
        };

        SoftwareBitmap rotated;
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(page.ImagePath));
        using (var stream = await file.OpenReadAsync())
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            rotated = await DecodeAsync(decoder, rotation);
        }
        using (rotated)
        {
            var engine = OcrEngine.TryCreateFromLanguage(SelectLanguage(ocrLanguage))
                         ?? throw new InvalidOperationException("A Windows OCR-motor nem hozható létre");
            var result = await RecognizeFittedAsync(engine, rotated);

            page.Rotation = (page.Rotation + degreesClockwise) % 360;
            // A new file name: the old image may still be cached by the viewer.
            var dir = Path.GetDirectoryName(page.ImagePath)!;
            var newPath = Path.Combine(dir, $"page_{page.Number:0000}_r{page.Rotation}.jpg");
            await SaveJpegAsync(rotated, newPath);
            page.ImagePath = newPath;
            page.PixelWidth = rotated.PixelWidth;
            page.PixelHeight = rotated.PixelHeight;
            page.ExtraReadings = new();
            page.RefinePassesDone = 0;
            page.Engine = "Windows";
            page.Lines = result.Lines;
        }
        AppLog.Info($"{page.Number}. oldal: kézzel elforgatva {degreesClockwise}°-kal (most {page.Rotation}°), {page.WordCount} szó olvasva");
    }

    public void Dispose() => _renderLock.Dispose();
}
