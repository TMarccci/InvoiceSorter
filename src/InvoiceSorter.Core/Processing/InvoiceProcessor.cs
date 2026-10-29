using InvoiceSorter.Core.Logging;
using InvoiceSorter.Core.Models;

namespace InvoiceSorter.Core.Processing;

/// <summary>Splits the analyzed pages into invoices and reads/validates the invoice data.</summary>
public static class InvoiceProcessor
{
    /// <summary>Full pipeline: render + OCR all pages, then split and read.</summary>
    /// <summary>The settings to use for a job: the buyer VAT number is checked against the job's receiving company only.</summary>
    public static AppSettings SettingsFor(InvoiceJob job, AppSettings settings) =>
        job.Receiver == null ? settings : settings.WithReceivers(new[] { job.Receiver });

    /// <summary>Detects the receiving company of the scan (unless one was chosen) and logs the result.</summary>
    public static void DetectReceiver(InvoiceJob job, AppSettings settings, ReceiverCompany? chosen)
    {
        var d = job.Detection = ReceiverDetector.Detect(job.Pages, settings);
        job.Receiver = chosen ?? d.Match;
        if (chosen != null) AppLog.Info($"Vevő cég (kiválasztva): {chosen.Name} {chosen.VatNumber}");
        else if (d.Match != null) AppLog.Info($"Vevő cég ({d.Pages} oldalból {d.MatchPages} oldalon felismerve): {d.Match.Name} {d.Match.VatNumber}");
        else AppLog.Warn("A beállított vevő cégek egyike sem szerepel ebben a szkennelésben");
        if (d.Suggestion != null)
            AppLog.Warn($"Az oldalak többsége ({d.Pages}-ből {d.SuggestionPages}) ezt a céget említi: {d.Suggestion.Name} {d.Suggestion.VatNumber}, de ez nincs a vevő cégek között");
    }

    /// <param name="receiver">The receiving company of this scan; null = detect it automatically.</param>
    public static async Task<InvoiceJob> RunAsync(string pdfPath, IReadOnlyList<InvoiceStyle> styles, AppSettings settings,
        IProgress<ProgressInfo>? progress, CancellationToken ct, ReceiverCompany? receiver = null)
    {
        var job = new InvoiceJob { SourcePath = pdfPath, WorkDir = AppPaths.CreateJobDir() };
        AppLog.Info($"=== Új feldolgozás: {pdfPath}");
        AppLog.Info($"Munkamappa: {job.WorkDir}");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using (var analyzer = await PageAnalyzer.OpenAsync(pdfPath, settings.OcrLanguage))
        {
            progress?.Report(new ProgressInfo(ProgressInfo.Ocr, 0, analyzer.PageCount));
            await analyzer.AnalyzeAllAsync(job, settings, progress, ct);
        }
        job.AddTiming("OCR", watch.Elapsed);

        watch.Restart();
        using var surya = await StartSuryaAsync(settings, progress, ct);
        if (surya != null && settings.SuryaMode == SuryaMode.AllPages)
            await ReadAllPagesWithSuryaAsync(job, surya, progress, ct);
        if (surya != null) job.AddTiming("Surya", watch.Elapsed);

        watch.Restart();
        progress?.Report(new ProgressInfo(ProgressInfo.Splitting, 0, 1));
        DetectReceiver(job, settings, receiver);
        var jobSettings = SettingsFor(job, settings);
        BuildDocuments(job, styles, jobSettings);
        await LookupIssuerNamesAsync(job, styles, jobSettings, progress, ct);
        job.AddTiming("szétbontás, olvasás és ellenőrzés", watch.Elapsed);

        // If the scan belongs to a company that is not configured, every buyer VAT check fails - re-reading would not help.
        if (job.Receiver == null && job.Detection?.Suggestion != null)
            AppLog.Warn("Az újraolvasás kimaradt: előbb vedd fel ennek a szkennelésnek a vevő cégét, majd futtasd újra a felismerést");
        else
            await RefineAsync(job, styles, jobSettings, progress, ct, surya);
        progress?.Report(new ProgressInfo(ProgressInfo.Done, 1, 1));
        return job;
    }

    /// <summary>
    /// Starts the Surya worker when Surya is switched on. If it cannot start (not installed, no Python …) the problem
    /// is logged and processing continues with Windows OCR only.
    /// </summary>
    public static async Task<SuryaOcr?> StartSuryaAsync(AppSettings settings, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (settings.SuryaMode == SuryaMode.Off) return null;
        try
        {
            progress?.Report(new ProgressInfo(settings.SuryaLocation == SuryaLocation.Server
                ? ProgressInfo.ConnectingSurya : ProgressInfo.StartingSurya, 0, 1));
            return await SuryaOcr.StartAsync(settings, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"A Surya OCR be van kapcsolva, de nem indult el – csak Windows OCR-rel folytatja. {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// "All pages" mode: Surya reads every page and becomes the main reading; the Windows OCR text stays available as
    /// a fallback reading (it is also what decided the rotation and blank pages).
    /// </summary>
    public static async Task ReadAllPagesWithSuryaAsync(InvoiceJob job, SuryaOcr surya, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var pages = job.Pages.Where(p => !p.IsBlank && p.Error == null && File.Exists(p.ImagePath)).ToList();
        int done = 0;
        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = surya.PagesInFlight };
        await Parallel.ForEachAsync(pages, options, async (page, token) =>
        {
            try
            {
                var lines = await surya.RecognizeAsync(page.ImagePath, token);
                var windows = new PageInfo
                {
                    Index = page.Index, ImagePath = page.ImagePath, PixelWidth = page.PixelWidth, PixelHeight = page.PixelHeight,
                    Rotation = page.Rotation, Engine = page.Engine, Lines = page.Lines,
                };
                page.ExtraReadings = page.ExtraReadings.Prepend(windows).ToList();
                page.Lines = lines;
                page.Engine = "Surya";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn($"{page.Number}. oldal: a Surya OCR nem sikerült, a Windows OCR szövege marad. {ex.Message}");
            }
            progress?.Report(new ProgressInfo(ProgressInfo.SuryaAllPages, Interlocked.Increment(ref done), pages.Count, $"{page.Number}. oldal", page));
        });
        AppLog.Info($"A Surya OCR {done} oldalt olvasott el");
    }

    /// <summary>
    /// Asks the company register for the names of supplier tax numbers not seen before (when switched on) and reads
    /// the invoices again if new names arrived.
    /// </summary>
    public static async Task LookupIssuerNamesAsync(InvoiceJob job, IReadOnlyList<InvoiceStyle> styles, AppSettings settings,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (!settings.LookupIssuerNames) return;
        var known = settings.KnownIssuers.Select(ParseKnownIssuer).Where(k => k.Tax != null).Select(k => k.Tax!.Core).ToHashSet();
        var cores = job.Documents.Where(d => d.Style is { } s && s.Issuer.Mode != FieldMode.Fixed)
            .SelectMany(d => SupplierTaxNumbers(d.Pages, settings)).Select(t => t.Core)
            .Where(c => !known.Contains(c) && !CompanyRegistry.IsCached(c)).Distinct().ToList();
        if (cores.Count == 0) return;
        progress?.Report(new ProgressInfo(ProgressInfo.CompanyLookup, 0, cores.Count));
        try
        {
            if (await CompanyRegistry.LookupAsync(cores, ct) > 0) BuildDocuments(job, styles, settings);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"A cégnév-lekérdezés nem sikerült: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-reads the invoices with problems (magnified, other OCR language, darker / contrast / black &amp; white images) and
    /// splits and reads again - the extra readings are used as fallbacks. Returns the number of re-read invoices.
    /// </summary>
    public static async Task<int> RefineAsync(InvoiceJob job, IReadOnlyList<InvoiceStyle> styles, AppSettings settings,
        IProgress<ProgressInfo>? progress, CancellationToken ct, SuryaOcr? surya = null)
    {
        var documents = PageRefiner.InvoicesNeedingRefinement(job.Documents, styles, settings);
        if (documents.Count == 0) return 0;
        int errorsBefore = job.Documents.Count(d => d.HasErrors);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await PageRefiner.RefineAsync(documents, styles, settings, progress, ct, surya);
        BuildDocuments(job, styles, settings);
        await LookupIssuerNamesAsync(job, styles, settings, progress, ct);
        job.AddTiming("újraolvasás", watch.Elapsed);
        AppLog.Info($"Az újraolvasás {errorsBefore} hibás számlából {errorsBefore - job.Documents.Count(d => d.HasErrors)} számlát javított");
        return documents.Count;
    }

    /// <summary>
    /// Splits the pages into documents. Uses cached OCR results, so it can be re-run quickly after editing styles.
    /// </summary>
    public static void BuildDocuments(InvoiceJob job, IReadOnlyList<InvoiceStyle> styles, AppSettings settings)
    {
        var docs = new List<InvoiceDocument>();
        InvoiceDocument? current = null;
        var fallback = FallbackStyle(styles);
        (int Page, int Of, bool HasTotal)? previousCounter = null;
        var currentCounters = new HashSet<(int Page, int Of)>();   // page counters of the invoice being built

        foreach (var page in job.Pages)
        {
            if (page.IsBlank && settings.RemoveBlankPages)
            {
                AppLog.Debug($"Page {page.Number}: blank, removed");
                continue;
            }
            if (page.Deleted)
            {
                AppLog.Debug($"Page {page.Number}: deleted by the user");
                page.DeletedFrom = null;   // the invoices are rebuilt: a restored page becomes a new invoice
                continue;
            }

            // The normal OCR text decides; the high resolution re-read (if any) is the fallback.
            var style = page.Readings.Select(r => FieldReader.FindStyle(r, styles, settings)).FirstOrDefault(s => s != null);
            var counter = PageCounterOf(page);
            var titleContinuation = current != null && HasContinuationTitle(page, settings);
            var continuation = settings.ContinuationKeywords.Any(k =>
                !string.IsNullOrWhiteSpace(k) && TextUtil.FuzzyContainsText(page.CompactText, k, settings.FuzzyTolerance))
                || titleContinuation
                // "Oldal: 2/3": a later page of a multi-page invoice that repeats the header of its layout (same style),
                // or the page that follows "Oldal: 1/3" of the previous page – "Számla oldal 2" after "Oldal 1" too, when
                // neither has a total (the printed terms on the back of page 1 may stand between them).
                // Not when the invoice has this page already, or the page shows another invoice number: a pile scanned
                // in reverse ("2/2", "1/2" | "2/2", "1/2" of the next invoice).
                || (current != null && counter is { Page: > 1 } later && later.Page <= later.Of && !currentCounters.Contains((later.Page, later.Of)) &&
                    ((style != null && style == current.Style) ||
                     (previousCounter is { } prev && prev.Page == later.Page - 1 &&
                      ((prev.HasTotal && later.HasTotal && prev.Of == later.Of) || (!prev.HasTotal && !later.HasTotal)))) &&
                    !OtherInvoiceNumber(current, page, fallback, settings));
            if (!page.IsBlank && !(counter == null && titleContinuation)) previousCounter = counter;
            var mentionsReceiver = style == null && settings.NewDocumentOnReceiverMatch && page.Readings.Any(r =>
                VatValidator.MentionsReceiver(r.Text, r.CompactText, settings.ReceiverCompanies, settings.FuzzyTolerance));

            var title = style == null && !mentionsReceiver ? page.Readings.Select(r => FindTitleLine(r, settings)).FirstOrDefault(t => t != null) : null;

            bool startsNew = current == null || (!continuation && (style != null || mentionsReceiver || title != null));
            if (startsNew)
            {
                current = new InvoiceDocument
                {
                    Style = style ?? fallback,
                    StartReason = style != null ? StartReason.Style : mentionsReceiver ? StartReason.Receiver
                        : title != null ? StartReason.Title : StartReason.FirstPage,
                };
                docs.Add(current);
                currentCounters.Clear();
                var reason = style != null ? $"style {style.Name}" : mentionsReceiver ? "unknown style, mentions receiver" :
                    title != null ? $"unknown style, title '{title}'" : "unknown style, first page";
                AppLog.Debug($"Page {page.Number}: new invoice ({reason})");
            }
            else
            {
                AppLog.Debug($"Page {page.Number}: continuation of previous invoice{(continuation ? " (continuation keyword)" : "")}");
            }
            current!.Pages.Add(page);
            if (counter is { } c) currentCounters.Add((c.Page, c.Of));
        }

        foreach (var doc in docs) ReadDocument(doc, settings);

        if (settings.MergeSameInvoiceNumber)
        {
            for (int i = docs.Count - 1; i > 0; i--)
            {
                var a = docs[i - 1];
                var b = docs[i];
                if (a.Style != null && a.Style == b.Style && !string.IsNullOrWhiteSpace(a.InvoiceNumber) &&
                    string.Equals(a.InvoiceNumber, b.InvoiceNumber, StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Info($"{b.PageRange}. oldal(ak) összevonva a(z) {a.PageRange}. oldali számlával (azonos számlaszám: {a.InvoiceNumber})");
                    foreach (var p in b.Pages) a.Pages.Add(p);
                    docs.RemoveAt(i);
                    ReadDocument(a, settings);
                }
            }
        }

        job.Documents.Clear();
        foreach (var d in docs) job.Documents.Add(d);
        Renumber(job);
        UpdateDuplicates(job);
        ScoreAll(job, settings);

        var errors = job.Documents.Count(d => d.HasErrors);
        AppLog.Info($"Szétválasztva {job.Documents.Count} számlára: {job.Documents.Count - errors} rendben, {errors} hibás");
        foreach (var d in job.Documents)
        {
            var msg = $"{d.Number}. számla, oldal: {d.PageRange} | megbízhatóság {d.Trust} | sablon: {d.StyleName} | kibocsátó: '{d.Issuer}' | szám: '{d.InvoiceNumber}' | " +
                      $"kelte: {Or(d.IssueDate)}, teljesítés: {Or(d.FulfillmentDate)}, határidő: {Or(d.DueDate)} | adószám: {d.Vat.Message}";
            if (d.HasErrors) AppLog.Warn(msg + " | HIBÁK: " + d.ErrorsText);
            else AppLog.Info(msg);
        }
    }

    private static string Or(string date) => string.IsNullOrEmpty(date) ? "–" : date;

    private static readonly System.Text.RegularExpressions.Regex PageCounter = new(@"^[\s:.;•·-]*([0-9Il|]{1,2})(?:\s*/\s*([0-9Il|]{1,2}))?(?![0-9/.:])");

    /// <summary>
    /// The printed page counter ("Oldal: 2/3", "Page 1/2"), read from the word after an "Oldal"/"Page" label on the
    /// same line (the OCR text order of the two may differ). Null if there is none.
    /// </summary>
    public static (int Page, int Of)? PageOf(PageInfo page) => PageCounterOf(page) is { } c ? (c.Page, c.Of) : null;

    /// <summary>The page counter and whether it has a total ("2/3") or not ("Oldal 2").</summary>
    private static (int Page, int Of, bool HasTotal)? PageCounterOf(PageInfo page)
    {
        var words = page.Lines.SelectMany(l => l.Words).ToList();
        foreach (var label in words)
        {
            var f = TextUtil.Compact(label.Text);
            // "Oldal", "Page", "Oldalszám" (MVM)
            bool isLabel = (f.Length is >= 4 and <= 6 && (TextUtil.Levenshtein(f, "oldal") <= 1 || f == "page")) ||
                           (f.Length is >= 8 and <= 10 && TextUtil.Levenshtein(f, "oldalszam") <= 1);
            if (!isLabel) continue;
            var right = words.Where(w => w.X > label.X + label.W * 0.5 && w.X < label.X + label.W + 0.12 &&
                                         Math.Abs(w.CenterY - label.CenterY) < Math.Max(label.H, 0.008))
                .OrderBy(w => w.X).Select(w => w.Text).Take(3);
            var m = PageCounter.Match(string.Join(" ", right));
            if (!m.Success) continue;
            // "Oldal: 2/3" or just "Oldal: 2" (METRO)
            int k = int.Parse(TextUtil.ToDigits(m.Groups[1].Value));
            int n = m.Groups[2].Success ? int.Parse(TextUtil.ToDigits(m.Groups[2].Value)) : k;
            if (k >= 1 && n >= 1) return (k, n, m.Groups[2].Success);
        }
        return null;
    }

    /// <summary>Computes the trust score of every invoice (after reading and duplicate detection).</summary>
    public static void ScoreAll(InvoiceJob job, AppSettings settings)
    {
        foreach (var d in job.Documents) Score(d, settings);
    }

    /// <summary>Trust score of one invoice; with the low-trust rule on, a low score also counts as an error.</summary>
    public static void Score(InvoiceDocument doc, AppSettings settings)
    {
        doc.LowTrustLimit = settings.LowTrustIsError ? settings.LowTrustThreshold : null;
        TrustScorer.Score(doc, settings);
    }

    /// <summary>The generic style used for invoices that no other style recognizes (null if there is none).</summary>
    public static InvoiceStyle? FallbackStyle(IEnumerable<InvoiceStyle> styles) => styles.FirstOrDefault(s => s.IsFallback && s.Enabled);

    /// <summary>Reads issuer and invoice number using the document's style and checks the buyer VAT number.</summary>
    public static void ReadDocument(InvoiceDocument doc, AppSettings settings)
    {
        doc.Notes.Clear();
        if (doc.Style != null)
        {
            var issuer = doc.Style.Issuer.Mode == FieldMode.Fixed
                ? FixedIssuer(doc, settings)
                : FirstValue(doc, p => ReadFieldWithFallback(doc.Style.Issuer, p, settings).Value) ?? "";
            if (doc.Style.Issuer.Mode != FieldMode.Fixed)
            {
                issuer = NormalizeIssuer(issuer, settings);
                // The supplier's tax number identifies the issuer more reliably than the OCR-read name: its known or
                // registered name wins (also when a bank or the buyer was read as the issuer).
                var byTaxNumber = IssuerByTaxNumber(doc.Pages, issuer, settings);
                if (byTaxNumber != null && byTaxNumber != issuer)
                {
                    doc.Notes.Add(issuer.Length == 0 || SameCompany(issuer, byTaxNumber)
                        ? TrustScorer.IssuerByTaxNumberNote : TrustScorer.IssuerCorrectedByTaxNumberNote);
                    issuer = byTaxNumber;
                }
                else if (byTaxNumber != null) doc.Notes.Add(TrustScorer.IssuerConfirmedByTaxNumberNote);
            }
            doc.Issuer = issuer;
            doc.InvoiceNumber = InvoiceNumberOf(doc, settings);
        }
        else
        {
            doc.Issuer = "";
            doc.InvoiceNumber = "";
            doc.Notes.Add("Az első oldalra egyik sablon sem illeszkedett. Készíts sablont ehhez az elrendezéshez, vagy add meg az adatokat kézzel.");
        }

        doc.Vat = CheckVat(doc.Pages, settings);
        doc.VatApproved = false;
        doc.NonInvoiceTitle = FindNonInvoiceTitle(doc, settings);
        ReadDates(doc, settings);
        Score(doc, settings);
        doc.Refresh();
    }

    /// <summary>
    /// The issuer of a style with a fixed issuer name: the style does not set it by itself. The name counts only when the
    /// first page confirms it – it is printed there (in any reading), or the supplier tax number belongs to it. Otherwise
    /// the issuer is the company of the supplier tax number (a page that matched the style by mistake gets its real
    /// issuer), or stays empty for the user. Adds the matching note to <see cref="InvoiceDocument.Notes"/>.
    /// </summary>
    public static string FixedIssuer(InvoiceDocument doc, AppSettings settings)
    {
        var name = doc.Style?.Issuer.FixedValue?.Trim() ?? "";
        if (doc.Pages.Count == 0) return "";
        var byTax = IssuerByTaxNumber(doc.Pages, name, settings);
        if (name.Length == 0)
        {
            if (byTax != null) doc.Notes.Add(TrustScorer.IssuerByTaxNumberNote);
            return byTax ?? "";
        }
        if (byTax != null && (byTax == name || SameCompany(byTax, name) || SameCompany(name, byTax)))
        {
            doc.Notes.Add(TrustScorer.IssuerConfirmedByTaxNumberNote);
            return name;
        }
        var core = VatValidator.StripCompanyForm(name);
        if (core.Length > 0 && doc.Pages[0].Readings.Any(r => TextUtil.FuzzyContainsText(r.CompactText, core, settings.FuzzyTolerance)))
            return name;

        doc.Notes.Add(TrustScorer.FixedIssuerNotOnPageNote);
        if (byTax != null) doc.Notes.Add(TrustScorer.IssuerByTaxNumberNote);
        return byTax ?? "";
    }

    /// <summary>The three dates; a date the user typed is kept (re-reading after a split, merge or rotation).</summary>
    public static void ReadDates(InvoiceDocument doc, AppSettings settings)
    {
        var dates = DateReader.Read(doc, settings);
        doc.DateReading = dates;
        doc.DatesRequired = settings.DatesRequired;
        foreach (var kind in new[] { DateKind.Issue, DateKind.Fulfillment, DateKind.Due })
            if (!doc.HasEdit(DateEditKey(kind))) doc.SetDate(kind, DateReader.Format(dates[kind].Value));
        if (!doc.HasEdit("dates accepted")) doc.DatesApproved = false;
    }

    /// <summary>The manual-change key of a date ("issue date" …).</summary>
    public static string DateEditKey(DateKind kind) => kind switch
    {
        DateKind.Issue => "issue date",
        DateKind.Fulfillment => "fulfillment date",
        _ => "due date",
    };

    /// <summary>Buyer VAT check over all pages, including their high resolution readings.</summary>
    public static VatCheckResult CheckVat(IEnumerable<PageInfo> pages, AppSettings settings) =>
        VatValidator.Check(string.Join("\n", pages.SelectMany(p => p.Readings).Select(r => r.Text)), settings.ReceiverCompanies, settings.AcceptTaxNumberCore);

    /// <summary>
    /// Reads a field from a page; when the normal OCR text gives nothing, from its other readings (re-reads). The
    /// field's own rule is tried on every reading before its fallback rule is used.
    /// </summary>
    public static FieldReadResult ReadFieldWithFallback(FieldDefinition field, PageInfo page, AppSettings settings)
    {
        FieldReadResult? first = null;
        bool isFallback = false;
        for (var level = field; level != null; level = level.Fallback, isFallback = true)
        {
            foreach (var reading in page.Readings)
            {
                var r = FieldReader.ReadSingleLevel(level, reading, settings);
                if (r.Value != null) return isFallback ? r with { FromFallback = true } : r;
                first ??= r;
            }
        }
        return first!;
    }

    /// <summary>A known issuer entry: "Name" or "Name | 12345678-2-41" (with the issuer's tax number).</summary>
    public static (string Name, VatValidator.TaxNumber? Tax) ParseKnownIssuer(string entry)
    {
        var parts = entry.Split('|', 2, StringSplitOptions.TrimEntries);
        return (parts[0], parts.Length > 1 ? VatValidator.Parse(parts[1]) : null);
    }

    /// <summary>
    /// Cleans an issuer name read from the page: repairs OCR damage and replaces it with the matching
    /// entry of <see cref="AppSettings.KnownIssuers"/> when one is close enough.
    /// </summary>
    public static string NormalizeIssuer(string raw, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var repaired = settings.RepairIssuerNames ? TextUtil.RepairCompanyName(raw) : raw.Trim();
        var compact = TextUtil.Compact(repaired);

        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (var entry in settings.KnownIssuers)
        {
            var known = ParseKnownIssuer(entry).Name;
            var k = TextUtil.Compact(known);
            if (k.Length < 3) continue;
            // Compare the whole name and its beginning (the read text may carry extra words after the name).
            int d = Math.Min(TextUtil.Levenshtein(compact, k), TextUtil.Levenshtein(compact[..Math.Min(compact.Length, k.Length)], k) + 1);
            // The distinctive first word must match too ("Alfa Kereskedelmi és Szolgáltató Kft." is not "Béta Kereskedelmi és Szolgáltató Kft.").
            if (d <= Math.Max(1, (int)(k.Length * 0.2)) && d < bestDistance && SameFirstWord(repaired, known))
            {
                best = known;
                bestDistance = d;
            }
        }
        return best ?? repaired;
    }

    /// <summary>Tax numbers on the pages that are not the receiving company's (valid check digit only).</summary>
    public static List<VatValidator.TaxNumber> SupplierTaxNumbers(IEnumerable<PageInfo> pages, AppSettings settings)
    {
        var receivers = settings.ReceiverCompanies.Select(r => VatValidator.Parse(r.VatNumber)?.Core).OfType<string>().ToHashSet();
        return VatValidator.FindCandidates(string.Join("\n", pages.SelectMany(p => p.Readings).Select(r => r.Text)))
            .Where(t => VatValidator.IsPlausibleFull(t) && !receivers.Contains(t.Core))
            .GroupBy(t => t.Core).Select(g => g.First()).ToList();
    }

    /// <summary>
    /// The issuer name belonging to a supplier tax number printed on the invoice (first page preferred): a known issuer
    /// with that tax number, else the name from the company register cache (see <see cref="CompanyRegistry"/>) - or
    /// the known issuer spelling of that company. With several tax numbers the one matching <paramref name="read"/> wins.
    /// </summary>
    public static string? IssuerByTaxNumber(IReadOnlyList<PageInfo> pages, string read, AppSettings settings)
    {
        var taxes = SupplierTaxNumbers(pages.Take(1), settings);
        // Tax numbers of later pages (attachments, a next receipt) only help when no usable name was read
        // (nothing, or part of a sentence like "Csoportos beszedési megbízás esetén BKM Nonprofit Zrt.").
        bool doubtfulName = read.Length == 0 || read.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Count(w => char.IsLower(w[0]) && w is not ("és" or "es")) >= 3;
        if (taxes.Count == 0 && doubtfulName) taxes = SupplierTaxNumbers(pages, settings);
        var names = taxes.Select(t => (Name: NameForTaxNumber(t, settings), Known: IsKnownTax(t, settings)))
            .Where(n => n.Name != null).DistinctBy(n => n.Name).ToList();
        if (names.Count == 0) return null;
        if (read.Length > 0 && names.FirstOrDefault(n => n.Name == read || SameCompany(read, n.Name!)) is { Name: not null } matching)
            // A name from the known issuers list is the user's spelling; the register's long official name is only used
            // when the page shows something else ("Delta Kft." printed → kept, "OTP Bank Nyrt." read → replaced).
            return matching.Known ? matching.Name : read;
        return names.Count == 1 ? names[0].Name : null;
    }

    private static bool IsKnownTax(VatValidator.TaxNumber tax, AppSettings settings) =>
        settings.KnownIssuers.Any(k => ParseKnownIssuer(k).Tax?.Core == tax.Core);

    /// <summary>Known or registered issuer name of a tax number, or null.</summary>
    public static string? NameForTaxNumber(VatValidator.TaxNumber tax, AppSettings settings)
    {
        var known = settings.KnownIssuers.Select(ParseKnownIssuer).ToList();
        var byTax = known.FirstOrDefault(k => k.Tax?.Core == tax.Core).Name;
        if (byTax != null) return byTax;
        if (!settings.LookupIssuerNames || CompanyRegistry.CachedName(tax.Core) is not { } registered) return null;
        // Prefer the spelling of the known issuers list ("Delta Kft.", "SPAR Magyarország Kereskedelmi Kft.").
        var compact = TextUtil.Compact(registered);
        return known.Where(k => k.Tax == null && (SameCompany(k.Name, registered) || TextUtil.Compact(k.Name) == compact))
                   .OrderByDescending(k => k.Name.Length).Select(k => k.Name).FirstOrDefault()
               ?? registered;
    }

    private static readonly HashSet<string> LegalFormWords = new() { "kft", "zrt", "nyrt", "bt", "kkt", "rt", "ev", "ec" };

    private static bool SameFirstWord(string a, string b)
    {
        static string First(string s) => s.Split(new[] { ' ', ',', '.' }, StringSplitOptions.RemoveEmptyEntries).Select(TextUtil.Compact).FirstOrDefault(w => w.Length > 0) ?? "";
        string x = First(a), y = First(b);
        return x == y || TextUtil.Levenshtein(x, y) <= Math.Max(1, y.Length / 4);
    }

    /// <summary>
    /// True when <paramref name="name"/> is (a shorter form of) <paramref name="registered"/>: same first word and
    /// every other word of it found in the registered name ("Delta Kft." ~ "Delta Kereskedelmi Kft."). Tolerates
    /// small OCR errors.
    /// </summary>
    public static bool SameCompany(string name, string registered)
    {
        static List<string> Words(string s) => s.Split(new[] { ' ', ',', '-', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(TextUtil.Compact).Where(w => w.Length > 0 && !LegalFormWords.Contains(w)).ToList();
        var a = Words(name);
        var b = Words(registered);
        if (a.Count == 0 || b.Count == 0) return false;
        static bool Close(string x, string y) => x == y || (Math.Min(x.Length, y.Length) >= 4 && TextUtil.Levenshtein(x, y) <= Math.Max(1, y.Length / 5));
        if (Close(a[0], b[0]) && a.Skip(1).Where(w => w.Length >= 3).All(w => b.Any(v => Close(w, v)))) return true;
        // Brand names: "TERMOSZIG Kft." ~ "Termoszigetelő Gyártó … Kft.", "AB80 Kft." ~ "AB 80 Kereskedelmi … Kft.",
        // and acronyms: "DRV Zrt." ~ "Dunántúli Regionális Vízmű Zrt.".
        if (a.Count > 1) return false;
        var joined = string.Concat(b);
        if (a[0].Length >= 4 && joined.StartsWith(a[0])) return true;
        var initials = string.Concat(b.Where(w => w is not ("es" or "and")).Select(w => w[0]));
        return a[0].Length is >= 2 and <= 5 && initials.StartsWith(a[0]) && a[0].Length >= Math.Min(3, initials.Length);
    }

    /// <summary>The known issuer whose tax number is printed on the invoice, if any.</summary>
    public static string? IssuerFromTaxNumber(IEnumerable<PageInfo> pages, AppSettings settings)
    {
        var known = settings.KnownIssuers.Select(ParseKnownIssuer).Where(k => k.Tax != null).ToList();
        if (known.Count == 0) return null;
        foreach (var tax in SupplierTaxNumbers(pages, settings))
        {
            var hit = known.FirstOrDefault(k => k.Tax!.Core == tax.Core);
            if (hit.Name != null) return hit.Name;
        }
        return null;
    }

    /// <summary>
    /// Remembers an issuer name (and its tax number, if known): typed corrections are learned, and the tax number lets
    /// the issuer be recognized later even when its name cannot be read.
    /// </summary>
    public static bool LearnIssuer(string name, AppSettings settings, VatValidator.TaxNumber? tax = null)
    {
        name = name.Trim();
        if (name.Length < 3) return false;
        var c = TextUtil.Compact(name);
        var entries = settings.KnownIssuers.Select(ParseKnownIssuer).ToList();
        if (tax != null && entries.Any(e => e.Tax?.Core == tax.Core)) tax = null;          // number already known
        var existing = entries.FirstOrDefault(e => e.Name == name);
        if (existing.Name != null && (tax == null || existing.Tax != null)) return false;   // nothing new
        var keepTax = tax ?? entries.FirstOrDefault(e => TextUtil.Compact(e.Name) == c).Tax;

        // Replace a similar (misspelled) entry instead of keeping both.
        settings.KnownIssuers.RemoveAll(k => TextUtil.Levenshtein(TextUtil.Compact(ParseKnownIssuer(k).Name), c) <= Math.Max(1, c.Length / 10));
        settings.KnownIssuers.Add(keepTax == null ? name : $"{name} | {keepTax.Full}");
        settings.KnownIssuers.Sort(StringComparer.CurrentCultureIgnoreCase);
        return true;
    }

    /// <summary>
    /// The title of a non-invoice document on its first page ("Szállítólevél",
    /// "Számla melléklet", "Átutalási megbízás" …) when the page has no invoice title – such pages were usually
    /// scanned together with an invoice. Null when it looks like an invoice.
    /// </summary>
    public static string? FindNonInvoiceTitle(InvoiceDocument doc, AppSettings settings)
    {
        // Also for layout styles: invoicing programs print delivery notes etc. with the same layout (Diamond Stúdió).
        if (doc.Pages.Count == 0) return null;
        var keywords = settings.NonInvoiceKeywords.Where(k => TextUtil.Compact(k).Length >= 4).ToList();
        if (keywords.Count == 0) return null;
        var readings = doc.Pages[0].Readings.ToList();
        if (readings.Any(r => FindTitleLine(r, settings) != null || r.Lines.Any(l => IsInvoiceTitleLine(l.Text, settings)))) return null;
        foreach (var reading in readings)
        foreach (var line in reading.Lines)
        {
            var text = TextUtil.Compact(line.Text);
            if (text.Length < 4) continue;
            foreach (var keyword in keywords)
            {
                var k = TextUtil.Compact(keyword);
                if (Math.Abs(text.Length - k.Length) > 3) continue;
                if (TextUtil.Levenshtein(text, k) <= TextUtil.AllowedErrors(k.Length, settings.FuzzyTolerance)) return keyword;
            }
        }
        return null;
    }

    // Words that may follow an invoice title: "Számla (INVOICE)", "SZÁMLA / INVOICE", "Számla EREDETI", "E-SZÁMLA 1 E-INVOICE".
    private static readonly System.Text.RegularExpressions.Regex TitleExtras = new(
        @"\((?:[^)]*)\)|\b(?:e-?invoice|invoice|eredeti|masolat|copy|original|peldany|\d+\.?)\b|[/|:.,-]",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>An invoice title line, also with an English translation or "EREDETI / MÁSOLAT" after it.</summary>
    public static bool IsInvoiceTitleLine(string line, AppSettings settings)
    {
        var folded = TextUtil.Fold(line);
        if (folded.Length > 60) return false;
        var core = TextUtil.Compact(TitleExtras.Replace(folded, " "));
        if (core.Length < 4) return TextUtil.Compact(folded) is "invoice" or "einvoice";
        return settings.InvoiceTitleKeywords.Select(TextUtil.Compact).Where(k => k.Length >= 4)
            .Any(k => Math.Abs(core.Length - k.Length) <= 2 && TextUtil.Levenshtein(core, k) <= TextUtil.AllowedErrors(k.Length, settings.FuzzyTolerance));
    }

    /// <summary>A value found on the page for the review screen to offer ("2026/208982" – "payment note").</summary>
    public sealed record ValueSuggestion(string Value, string Source)
    {
        public override string ToString() => Value;
    }

    // Invoice number labels with a tolerant value pattern: also "26/00 595" or "2026/20 982" with an OCR space.
    private static FieldDefinition LooseNumberField(AppSettings settings, InvoiceStyle? generic) => new()
    {
        Mode = FieldMode.Label,
        LabelText = (generic?.InvoiceNumber.LabelText is { Length: > 0 } labels ? labels : "Számlaszám | Számla sorszáma | Sorszám") + " | Számla/Invoice | Invoice No",
        LabelDirection = LabelDirection.Auto,
        Regex = @"(?<![0-9A-Za-z/._-])(?![A-Z0-9/._-]*@)(?<value>(?=(?:[^\s\d]*\d){3})[A-Z0-9][A-Z0-9/._-]{2,}(?:(?<=/[0-9]{0,4}) [0-9]{2,6}(?![0-9A-Za-z]))?)",
    };

    /// <summary>
    /// Other invoice numbers found on the first pages of the invoice: the style's rule and its fallback, the generic
    /// rules and the text next to invoice number labels, in every reading (normal and re-reads). Not the current value.
    /// </summary>
    public static List<ValueSuggestion> InvoiceNumberSuggestions(InvoiceDocument doc, IReadOnlyList<InvoiceStyle> styles, AppSettings settings, int max = 8)
    {
        var generic = FallbackStyle(styles);
        var fields = new List<(FieldDefinition Field, string Source)>();
        for (var f = doc.Style?.InvoiceNumber; f != null; f = f.Fallback)
            fields.Add((f, f == doc.Style!.InvoiceNumber ? $"„{doc.Style.Name}” sablon" : "a sablon tartalék szabálya"));
        // "001603 2026" next to the label: OCR often drops the slash of "001603/2026".
        fields.Add((new FieldDefinition
        {
            Mode = FieldMode.Label,
            LabelText = LooseNumberField(settings, generic).LabelText,
            LabelDirection = LabelDirection.Auto,
            Regex = @"(?<![0-9A-Za-z/._-])(?<a>[A-Z0-9][A-Z0-9._-]*\d{3,})\s+(?<y>(?:19|20)\d{2})(?![0-9A-Za-z/])",
            Format = "${a}/${y}",
        }, "számlaszám-címke mellett – a perjelet valószínűleg elhagyta az OCR"));
        if (generic != null && generic != doc.Style)
            for (var f = generic.InvoiceNumber; f != null; f = f.Fallback) fields.Add((f, "általános szabályok"));
        fields.Add((LooseNumberField(settings, generic), "számlaszám-címke mellett"));
        return Suggestions(doc, fields, doc.InvoiceNumber, v => TextUtil.Compact(v).Count(char.IsDigit) >= 3 ? v : null, settings, max);
    }

    /// <summary>
    /// Other issuer names: the style's and the generic rules in every reading, and the known or registered names of
    /// the supplier tax numbers printed on the invoice. Not the current value, never the receiving company.
    /// </summary>
    public static List<ValueSuggestion> IssuerSuggestions(InvoiceDocument doc, IReadOnlyList<InvoiceStyle> styles, AppSettings settings, int max = 6)
    {
        var result = new List<ValueSuggestion>();
        foreach (var tax in SupplierTaxNumbers(doc.Pages.Take(1), settings).Concat(SupplierTaxNumbers(doc.Pages, settings)).DistinctBy(t => t.Core))
        {
            var name = NameForTaxNumber(tax, settings);
            if (name != null) result.Add(new(name, $"{tax.Full} adószám"));
            if (settings.LookupIssuerNames && CompanyRegistry.CachedName(tax.Core) is { } registered && registered != name)
                result.Add(new(registered, $"cégnyilvántartás, {tax.Full} adószám"));
        }
        var generic = FallbackStyle(styles);
        var fields = new List<(FieldDefinition Field, string Source)>();
        for (var f = doc.Style?.Issuer; f != null; f = f.Fallback)
        {
            if (f.Mode != FieldMode.Fixed) fields.Add((f, $"„{doc.Style!.Name}” sablon"));
            else if (!string.IsNullOrWhiteSpace(f.FixedValue)) result.Add(new(f.FixedValue.Trim(), $"„{doc.Style!.Name}” sablon (a lapon nem szerepel)"));
        }
        if (generic != null && generic != doc.Style)
            for (var f = generic.Issuer; f != null; f = f.Fallback) fields.Add((f, "általános szabályok"));
        // Not banks of the seller block and not parts of sentences ("Ezúton értesítjük, hogy a … Nyrt.").
        result.AddRange(Suggestions(doc, fields, doc.Issuer, v =>
            NormalizeIssuer(v, settings) is { Length: >= 3 } n && !System.Text.RegularExpressions.Regex.IsMatch(n, @"\bBank\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
            n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(w => char.IsLower(w[0]) && w is not ("és" or "es")) < 3 ? n : null, settings, max));
        var current = TextUtil.Compact(doc.Issuer);
        return result.Where(s => TextUtil.Compact(s.Value) != current).DistinctBy(s => TextUtil.Compact(s.Value)).Take(max).ToList();
    }

    private static List<ValueSuggestion> Suggestions(InvoiceDocument doc, List<(FieldDefinition Field, string Source)> fields, string current,
        Func<string, string?> accept, AppSettings settings, int max)
    {
        var seen = new HashSet<string> { TextUtil.Compact(current) };
        var result = new List<ValueSuggestion>();
        foreach (var (field, source) in fields)
        foreach (var page in doc.Pages.Take(2))
        {
            var readings = page.Readings.ToList();
            for (int i = 0; i < readings.Count; i++)
            {
                IEnumerable<string> values;
                try { values = FieldReader.AllValues(field, readings[i], settings).Take(6).ToList(); }
                catch (Exception ex) { AppLog.Debug($"Suggestions: {ex.Message}"); continue; }
                foreach (var raw in values)
                {
                    if (accept(raw) is not { } value || value.Length > 60) continue;
                    if (!seen.Add(TextUtil.Compact(value))) continue;
                    result.Add(new(value, i == 0 ? source : $"{source} (újraolvasás)"));
                    if (result.Count >= max) return result;
                }
            }
        }
        return result;
    }

    /// <summary>A line in the top quarter of the page starts with one of <see cref="AppSettings.ContinuationTitles"/> ("Általános szerződési feltételek …").</summary>
    public static bool HasContinuationTitle(PageInfo page, AppSettings settings)
    {
        var titles = settings.ContinuationTitles.Select(TextUtil.Compact).Where(t => t.Length >= 4).ToList();
        if (titles.Count == 0) return false;
        foreach (var line in page.Lines.Where(l => l.Words.Count > 0 && l.Words[0].Y < 0.25))
        {
            var text = TextUtil.Compact(line.Text);
            foreach (var t in titles)
                if (text.Length >= t.Length && TextUtil.Levenshtein(text[..t.Length], t) <= TextUtil.AllowedErrors(t.Length, settings.FuzzyTolerance))
                    return true;
        }
        return false;
    }

    /// <summary>Returns the first text line that is an invoice title (e.g. "Egyszerűsített számla"), or null.</summary>
    public static string? FindTitleLine(PageInfo page, AppSettings settings)
    {
        var keywords = settings.InvoiceTitleKeywords.Select(TextUtil.Compact).Where(k => k.Length > 0).ToList();
        if (keywords.Count == 0) return null;
        foreach (var line in page.Lines)
        {
            var text = TextUtil.Compact(line.Text);
            foreach (var k in keywords)
            {
                if (Math.Abs(text.Length - k.Length) > 3) continue;
                if (TextUtil.Levenshtein(text, k) <= TextUtil.AllowedErrors(k.Length, settings.FuzzyTolerance))
                    return line.Text;
            }
        }
        return null;
    }

    /// <summary>
    /// The page shows an invoice number and the invoice being built another one (read with the same rule; a partly read
    /// number that is contained in the other counts as the same). Nothing readable on either side: no contradiction.
    /// </summary>
    private static bool OtherInvoiceNumber(InvoiceDocument doc, PageInfo page, InvoiceStyle? fallback, AppSettings settings)
    {
        if ((doc.Style ?? fallback) is not { } style) return false;
        string? Number(PageInfo p)
        {
            try { return TextUtil.Compact(ReadFieldWithFallback(style.InvoiceNumber, p, settings).Value); }
            catch (Exception ex) { AppLog.Debug($"Invoice number check: {ex.Message}"); return null; }
        }
        var mine = Number(page);
        if (string.IsNullOrEmpty(mine)) return false;
        foreach (var p in doc.Pages)
        {
            var theirs = Number(p);
            if (string.IsNullOrEmpty(theirs)) continue;
            return !(mine == theirs || mine.Contains(theirs, StringComparison.Ordinal) || theirs.Contains(mine, StringComparison.Ordinal));
        }
        return false;
    }

    /// <summary>
    /// The invoice number of the first page that has one. A later page of the same invoice that repeats it in full
    /// ("Számla oldal 2 számlaszám 412345678") wins over a partly read one on the first page ("45678" – the number
    /// crossed by a pen stroke).
    /// </summary>
    private static string InvoiceNumberOf(InvoiceDocument doc, AppSettings settings)
    {
        var values = doc.Pages.Select(p => ReadFieldWithFallback(doc.Style!.InvoiceNumber, p, settings).Value)
            .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
        if (values.Count == 0) return "";
        var first = values[0];
        var compact = TextUtil.Compact(first);
        var fuller = values.Skip(1)
            .Where(v => TextUtil.Compact(v) is { } c && c.Length > compact.Length && compact.Length > 0 && c.Contains(compact, StringComparison.Ordinal))
            .GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        if (fuller != null) AppLog.Debug($"Invoice number '{first}' of the first page is part of '{fuller}' on a later page: the later one is used");
        return fuller ?? first;
    }

    private static string? FirstValue(InvoiceDocument doc, Func<PageInfo, string?> read)
    {
        foreach (var page in doc.Pages)
        {
            var v = read(page);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    public static void Renumber(InvoiceJob job)
    {
        for (int i = 0; i < job.Documents.Count; i++) job.Documents[i].Number = i + 1;
    }

    public static void UpdateDuplicates(InvoiceJob job)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in job.Documents)
        {
            int? dup = null;
            // An excluded copy (e.g. the same paper scanned twice) does not make the other one a duplicate.
            if (!d.Excluded && !string.IsNullOrWhiteSpace(d.Issuer) && !string.IsNullOrWhiteSpace(d.InvoiceNumber))
            {
                var key = TextUtil.Compact(d.Issuer) + "|" + TextUtil.Compact(d.InvoiceNumber);
                if (seen.TryGetValue(key, out var first)) dup = first;
                else seen[key] = d.Number;
            }
            d.DuplicateOf = dup;
        }
    }

    /// <summary>Moves the pages from the given page onwards into a new document right after this one.</summary>
    public static InvoiceDocument SplitAt(InvoiceJob job, InvoiceDocument doc, PageInfo page, IReadOnlyList<InvoiceStyle> styles, AppSettings settings)
    {
        int idx = doc.Pages.IndexOf(page);
        if (idx <= 0) throw new InvalidOperationException("A szétvágáshoz ne az első oldalt válaszd.");

        var moved = doc.Pages.Skip(idx).ToList();
        foreach (var p in moved) doc.Pages.Remove(p);

        var newDoc = new InvoiceDocument { Style = FieldReader.FindStyle(moved[0], styles, settings) ?? FallbackStyle(styles), StartReason = StartReason.Manual };
        foreach (var p in moved) newDoc.Pages.Add(p);
        job.Documents.Insert(job.Documents.IndexOf(doc) + 1, newDoc);

        ReadDocument(doc, settings);
        ReadDocument(newDoc, settings);
        Renumber(job);
        UpdateDuplicates(job);
        ScoreAll(job, settings);
        AppLog.Info($"Kézi szétvágás: a(z) {doc.Number}. számla oldalai {doc.PageRange}, az új {newDoc.Number}. számla oldalai {newDoc.PageRange}");
        return newDoc;
    }

    /// <summary>Appends the document's pages to the previous document.</summary>
    public static InvoiceDocument MergeWithPrevious(InvoiceJob job, InvoiceDocument doc, AppSettings settings)
    {
        int idx = job.Documents.IndexOf(doc);
        if (idx <= 0) throw new InvalidOperationException("Az első számla előtt nincs számla, amellyel összevonható lenne.");
        return MergeInto(job, doc, job.Documents[idx - 1], settings);
    }

    /// <summary>
    /// Appends the pages of <paramref name="doc"/> to any other invoice <paramref name="target"/> (e.g. a page of an
    /// invoice that was scanned somewhere else in the pile) and removes <paramref name="doc"/>. The target keeps its
    /// values (also manual edits); the buyer VAT check covers the new pages too.
    /// </summary>
    public static InvoiceDocument MergeInto(InvoiceJob job, InvoiceDocument doc, InvoiceDocument target, AppSettings settings)
    {
        if (doc == target) throw new InvalidOperationException("Egy számla nem vonható össze önmagával.");
        if (!job.Documents.Contains(doc) || !job.Documents.Contains(target)) throw new InvalidOperationException("Mindkét számlának ehhez a feldolgozáshoz kell tartoznia.");

        var (issuer, number, approved) = (target.Issuer, target.InvoiceNumber, target.VatApproved);
        foreach (var p in doc.Pages) target.Pages.Add(p);
        job.Documents.Remove(doc);

        target.Vat = CheckVat(target.Pages, settings);
        target.Issuer = issuer;
        target.InvoiceNumber = number;
        target.VatApproved = approved;
        target.Refresh();

        Renumber(job);
        UpdateDuplicates(job);
        ScoreAll(job, settings);
        AppLog.Info($"Kézi összevonás: a(z) {target.Number}. számla oldalai most {target.PageRange}");
        return target;
    }

    /// <summary>
    /// Moves a page of an invoice to another position (the order of the pages in the saved PDF). When the first page
    /// changes, the invoice is read again from its new first page unless its values were edited by hand.
    /// </summary>
    public static void MovePage(InvoiceJob job, InvoiceDocument doc, PageInfo page, int newIndex, AppSettings settings)
    {
        int old = doc.Pages.IndexOf(page);
        if (old < 0) throw new InvalidOperationException("Az oldal nem ehhez a számlához tartozik.");
        newIndex = Math.Clamp(newIndex, 0, doc.Pages.Count - 1);
        if (old == newIndex) return;
        var first = doc.Pages[0];
        doc.Pages.RemoveAt(old);
        doc.Pages.Insert(newIndex, page);

        if (doc.Pages[0] != first && !doc.HasEdit("issuer") && !doc.HasEdit("invoice number"))
            ReadDocument(doc, settings);
        else
        {
            doc.Vat = CheckVat(doc.Pages, settings);
            ReadDates(doc, settings);
            doc.Refresh();
        }
        UpdateDuplicates(job);
        ScoreAll(job, settings);
        AppLog.Info($"{doc.Number}. számla: a(z) {page.Number}. oldal a(z) {newIndex + 1}. helyre került (oldalak: {doc.PageRange})");
    }

    /// <summary>
    /// Deletes a page that belongs to no invoice (advert, a sheet scanned twice, an empty page that has some marks). It
    /// is not saved and stays deleted when the invoices are detected again; the source PDF is not changed. An invoice
    /// that loses its only page is removed. Returns the invoice that keeps the other pages, or null when it was removed.
    /// </summary>
    public static InvoiceDocument? DeletePage(InvoiceJob job, InvoiceDocument doc, PageInfo page, IReadOnlyList<InvoiceStyle> styles, AppSettings settings)
    {
        int idx = doc.Pages.IndexOf(page);
        if (idx < 0 || !job.Documents.Contains(doc)) throw new InvalidOperationException("Az oldal nem ehhez a számlához tartozik.");
        doc.Pages.RemoveAt(idx);
        page.Deleted = true;
        page.DeletedFrom = doc;
        page.DeletedIndex = idx;

        if (doc.Pages.Count == 0)
        {
            int number = doc.Number;
            job.Documents.Remove(doc);
            Renumber(job);
            UpdateDuplicates(job);
            ScoreAll(job, settings);
            AppLog.Info($"A(z) {page.Number}. oldal törölve; ez volt a(z) {number}. számla egyetlen oldala, a számla is törölve");
            return null;
        }

        ReadAfterPageChange(doc, idx == 0, styles, settings);
        UpdateDuplicates(job);
        ScoreAll(job, settings);
        AppLog.Info($"A(z) {page.Number}. oldal törölve a(z) {doc.Number}. számlából (oldalak most: {doc.PageRange})");
        return doc;
    }

    /// <summary>
    /// Brings a deleted page back: to its place in the invoice it was deleted from, or – when it was the only page of
    /// that invoice – the invoice comes back with its values. After the invoices were detected again it becomes a new
    /// invoice at its place in the page order. Returns the invoice that holds the page.
    /// </summary>
    public static InvoiceDocument RestorePage(InvoiceJob job, PageInfo page, IReadOnlyList<InvoiceStyle> styles, AppSettings settings)
    {
        if (!page.Deleted || !job.Pages.Contains(page)) throw new InvalidOperationException("Ez az oldal nincs törölve.");
        var doc = page.DeletedFrom;
        page.Deleted = false;
        page.DeletedFrom = null;

        if (doc != null && job.Documents.Contains(doc))
        {
            int idx = Math.Clamp(page.DeletedIndex, 0, doc.Pages.Count);
            doc.Pages.Insert(idx, page);
            ReadAfterPageChange(doc, idx == 0, styles, settings);
        }
        else
        {
            bool sameInvoice = doc != null && doc.Pages.Count == 0;
            if (!sameInvoice)
                doc = new InvoiceDocument { Style = FieldReader.FindStyle(page, styles, settings) ?? FallbackStyle(styles), StartReason = StartReason.Manual };
            doc!.Pages.Add(page);
            int at = job.Documents.ToList().FindIndex(d => d.Pages.Count > 0 && d.Pages.Min(p => p.Number) > page.Number);
            job.Documents.Insert(at < 0 ? job.Documents.Count : at, doc);
            if (sameInvoice) doc.Refresh();
            else ReadDocument(doc, settings);
        }

        Renumber(job);
        UpdateDuplicates(job);
        ScoreAll(job, settings);
        AppLog.Info($"A(z) {page.Number}. oldal visszaállítva a(z) {doc.Number}. számlába (oldalak: {doc.PageRange})");
        return doc;
    }

    /// <summary>
    /// An invoice lost or got back a page: read again (a new first page may also match another style) unless the
    /// issuer or invoice number was typed by hand – then only the buyer VAT number and the dates are checked again.
    /// The user's "VAT accepted" decision is kept.
    /// </summary>
    private static void ReadAfterPageChange(InvoiceDocument doc, bool firstPageChanged, IReadOnlyList<InvoiceStyle> styles, AppSettings settings)
    {
        if (!doc.HasEdit("issuer") && !doc.HasEdit("invoice number"))
        {
            if (firstPageChanged && !doc.HasEdit("style") && FieldReader.FindStyle(doc.Pages[0], styles, settings) is { } style) doc.Style = style;
            bool approved = doc.VatApproved;
            ReadDocument(doc, settings);
            doc.VatApproved = approved;
        }
        else
        {
            doc.Vat = CheckVat(doc.Pages, settings);
            doc.NonInvoiceTitle = FindNonInvoiceTitle(doc, settings);
            ReadDates(doc, settings);
            doc.Refresh();
        }
    }
}
