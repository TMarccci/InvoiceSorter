using System.Text;
using InvoiceSorter.Core;
using InvoiceSorter.Core.Logging;
using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;
using InvoiceSorter.Core.Storage;

// Command line front-end, useful for batch processing and for testing styles.
//
//   InvoiceSorter.Cli process <source.pdf> [--out <dir>] [--styles <styles.json>] [--settings <settings.json>]
//   InvoiceSorter.Cli reprocess <workDir> [--out <dir>] [--styles ...]     (re-uses OCR results of an earlier run)
//   InvoiceSorter.Cli dump <workDir> <pageNumber>                          (prints OCR words with coordinates)

Console.OutputEncoding = Encoding.UTF8;
if (args.Length < 2)
{
    Console.WriteLine("Usage:\n  process <source.pdf> [--out dir] [--styles file] [--settings file]\n  reprocess <workDir> [--out dir] [--styles file] [--settings file]\n  dump <workDir> <page>");
    return 1;
}

string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

var verbose = args.Contains("--verbose");
AppLog.EntryAdded += e => { if (verbose || e.Level >= LogLevel.Warning || e.Message.StartsWith("===")) Console.WriteLine(e); };

if (Opt("--registry") is { } registryPath) CompanyRegistry.CachePath = registryPath;
var styles = args[0] == "dump" ? new List<InvoiceStyle>() : JsonStore.LoadStyles(Opt("--styles"));
var settings = JsonStore.LoadSettings(Opt("--settings"));

switch (args[0])
{
    case "process":
    {
        var progress = new Progress<ProgressInfo>(p => { if (p.Current % 20 == 0) Console.WriteLine($"{p.Stage} {p.Current}/{p.Total}"); });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var job = await InvoiceProcessor.RunAsync(args[1], styles, settings, progress, CancellationToken.None);
        JobCache.Save(job);
        Console.WriteLine($"Processed in {sw.Elapsed.TotalSeconds:0.0}s. Work dir: {job.WorkDir}");
        Finish(job);
        break;
    }
    case "reprocess":
    {
        var job = JobCache.Load(args[1]);
        if (args.Contains("--fresh-refine"))
        {
            // Forget earlier re-reads (e.g. to compare re-read settings on the same OCR baseline).
            foreach (var p in job.Pages) { p.ExtraReadings = new(); p.RefinePassesDone = 0; }
        }
        InvoiceProcessor.DetectReceiver(job, settings, null);
        var jobSettings = InvoiceProcessor.SettingsFor(job, settings);
        InvoiceProcessor.BuildDocuments(job, styles, jobSettings);
        await InvoiceProcessor.LookupIssuerNamesAsync(job, styles, jobSettings, null, CancellationToken.None);
        if (!args.Contains("--no-refine"))
        {
            using var surya = await InvoiceProcessor.StartSuryaAsync(settings, null, CancellationToken.None);
            if (await InvoiceProcessor.RefineAsync(job, styles, jobSettings, null, CancellationToken.None, surya) > 0 && !args.Contains("--fresh-refine"))
                JobCache.Save(job);
        }
        Console.WriteLine($"Receiving company: {job.Receiver?.Name ?? "-"} {job.Receiver?.VatNumber}" +
                          (job.Detection?.Suggestion is { } s ? $" | suggestion: {s.Name} {s.VatNumber} ({job.Detection.SuggestionPages}/{job.Detection.Pages} pages)" : ""));
        Finish(job);
        break;
    }
    case "dates":
    {
        // dates <workDir> [--tokens]: the three dates of every invoice with their source and problems (no re-reading).
        var job = JobCache.Load(args[1]);
        InvoiceProcessor.DetectReceiver(job, settings, null);
        var jobSettings = InvoiceProcessor.SettingsFor(job, settings);
        InvoiceProcessor.BuildDocuments(job, styles, jobSettings);
        int complete = 0, partial = 0, none = 0, ambiguous = 0;
        foreach (var d in job.Documents)
        {
            var r = d.DateReading!;
            int n = new[] { d.IssueDate, d.FulfillmentDate, d.DueDate }.Count(x => x.Length > 0);
            if (n == 3) complete++; else if (n > 0) partial++; else none++;
            if (r.Issue.Ambiguous || r.Fulfillment.Ambiguous || r.Due.Ambiguous) ambiguous++;
            Console.WriteLine($"#{d.Number} p{d.PageRange} [{d.StyleName}] {d.Issuer} | {d.InvoiceNumber}{(r.SingleDateDocument ? " | EGYDÁTUMOS: " + DateReader.SingleDateWhy(d, jobSettings) : "")}");
            foreach (var kind in new[] { DateKind.Issue, DateKind.Fulfillment, DateKind.Due })
            {
                var f = r[kind];
                Console.WriteLine($"     {DateReader.Name(kind),-18} {(f.Value is { } v ? DateReader.Format(v) : f.Ambiguous ? "?? (nem dönthető el)" : "–"),-22} {f.Source}" +
                                  (f.Notes.Count > 0 ? "  {" + string.Join("; ", f.Notes) + "}" : ""));
            }
            foreach (var p in DateReader.Problems(d)) Console.WriteLine($"     ! {p}");
            foreach (var (points, reason) in DateReader.Doubts(d)) Console.WriteLine($"     ~ -{points} {reason}");
            if (args.Contains("--tokens"))
                foreach (var t in r.Tokens.Where(t => t.Token.ReadingIndex == 0))
                    Console.WriteLine($"       · p{t.Token.PageNumber} {DateReader.Format(t.Token.Date)} '{t.Token.Text}' " +
                                      $"{(t.Kind is { } k ? DateReader.Name(k) : t.Excluded ? "KIZÁRT" : t.Conflict ? "ÜTKÖZÉS " + string.Join("/", t.Candidates) : "-")} " +
                                      $"{(t.Label != null ? $"[{t.How}: {t.Label}]" : "")}{(t.Token.InRange ? " (időszak)" : "")}{(t.Token.Repaired ? " (javított)" : "")}{(t.InText ? " (szövegben)" : "")} | {t.Token.LineText}" +
                                      (args.Contains("--why") ? $"   ⟨{t.Context}⟩" : ""));
        }
        Console.WriteLine($"ÖSSZESEN {job.Documents.Count} számla: mindhárom dátum {complete}, részben {partial}, egy sem {none}, nem eldönthető {ambiguous}");
        if (Opt("--json") is { } jsonPath)
        {
            // For checking the values against the page images: every found date with its box and label box.
            var rows = job.Documents.Select(d => new
            {
                d.Number, d.PageRange, d.Issuer, d.InvoiceNumber, Style = d.StyleName,
                Dates = new[] { DateKind.Issue, DateKind.Fulfillment, DateKind.Due }.Select(k =>
                {
                    var f = d.DateReading![k];
                    var page = f.Token != null ? d.Pages.FirstOrDefault(p => p.Number == f.Token.PageNumber) : null;
                    return new { Kind = k.ToString(), Value = DateReader.Format(f.Value), f.Source, f.Ambiguous, Image = page?.ImagePath, Box = f.Token?.Box, f.LabelBox };
                }).ToList(),
            }).ToList();
            File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
        }
        break;
    }
    case "upgrade":
    {
        // upgrade --styles file --settings file: applies the built-in style upgrade (as the app does at start-up).
        if (JsonStore.UpgradeBuiltInStyles(styles, settings, Opt("--styles")))
        {
            JsonStore.SaveStyles(styles, Opt("--styles"));
            JsonStore.SaveSettings(settings, Opt("--settings"));
            Console.WriteLine($"Upgraded to built-in styles version {JsonStore.BuiltInStylesVersion}");
        }
        else Console.WriteLine("Already up to date");
        break;
    }
    case "learn":
    {
        // learn <workDir>... --settings file: remembers "issuer | tax number" of every correctly read invoice whose
        // first page shows exactly one supplier tax number, and saves them as known issuers into the settings file.
        int learned = 0;
        foreach (var workDir in args.Skip(1).TakeWhile(a => !a.StartsWith("--")))
        {
            var job = JobCache.Load(workDir);
            InvoiceProcessor.DetectReceiver(job, settings, null);
            var jobSettings = InvoiceProcessor.SettingsFor(job, settings);
            InvoiceProcessor.BuildDocuments(job, styles, jobSettings);
            foreach (var d in job.Documents.Where(d => !d.HasErrors && d.Style is { } s && s.Issuer.Mode != FieldMode.Fixed))
            {
                var taxes = InvoiceProcessor.SupplierTaxNumbers(d.Pages.Take(1), jobSettings);
                if (taxes.Count != 1) continue;
                if (InvoiceProcessor.LearnIssuer(d.Issuer, settings, taxes[0]))
                {
                    learned++;
                    Console.WriteLine($"learned: {d.Issuer} | {taxes[0].Full}   ({Path.GetFileName(workDir)} p{d.PageRange}, {d.StyleName})");
                }
            }
        }
        if (Opt("--settings") is { } settingsPath) JsonStore.SaveSettings(settings, settingsPath);
        Console.WriteLine($"{learned} issuer(s) learned");
        break;
    }
    case "test":
    {
        // test <workDir> <page>: how every style sees one page.
        var job = JobCache.Load(args[1]);
        var page = job.Pages[int.Parse(args[2]) - 1];
        foreach (var style in styles)
        {
            var score = FieldReader.MatchScore(style, page, settings);
            var rules = string.Join(", ", style.IdentifierRules.Select(r => $"{FieldReader.RuleScore(r, page, settings)}"));
            if (score == 0 && !args.Contains("--all")) continue;
            Console.WriteLine($"{style.Name}: score {score} (rules {rules})");
            foreach (var (name, field) in new[] { ("issuer", style.Issuer), ("number", style.InvoiceNumber) })
            {
                var r = FieldReader.ReadFieldDetailed(field, page, settings);
                Console.WriteLine($"   {name}: '{r.Value}'  label={r.LabelBox}  source='{r.SourceText.Replace("\n", " ⏎ ")}'");
            }
        }
        Console.WriteLine($"best: {FieldReader.FindStyle(page, styles, settings)?.Name ?? "-"}");
        break;
    }
    case "reread":
    {
        // reread <workDir> <page> [--dpi 400] [--pdf source.pdf]: the page read again from the PDF (as the re-read passes
        // do, turned like the page) next to its normal reading.
        var job = JobCache.Load(args[1]);
        var page = job.Pages[int.Parse(args[2]) - 1];
        using var analyzer = await PageAnalyzer.OpenAsync(Opt("--pdf") ?? job.SourcePath, settings.OcrLanguage);
        var reading = await analyzer.ReadRenderedAsync(page, int.Parse(Opt("--dpi") ?? "400"));
        Console.WriteLine($"Page {page.Number}, rotation {page.Rotation}°: normal {page.WordCount} words, from the PDF {reading.WordCount} words ({reading.PixelWidth}×{reading.PixelHeight} px)");
        foreach (var (a, b) in page.Lines.Take(12).Zip(reading.Lines.Take(12)))
            Console.WriteLine($"   {a.Text,-60} | {b.Text}");
        break;
    }
    case "rerender-test":
    {
        // rerender-test <workDir> [--dpi 300,400,600] [--max N] [--pdf source.pdf]: how one re-read reads the invoices that need one –
        // from the saved page image enlarged 2× (the current first pass) or from the PDF rendered again.
        var job = JobCache.Load(args[1]);
        foreach (var p in job.Pages) { p.ExtraReadings = new(); p.RefinePassesDone = 0; }
        InvoiceProcessor.DetectReceiver(job, settings, null);
        var jobSettings = InvoiceProcessor.SettingsFor(job, settings);
        InvoiceProcessor.BuildDocuments(job, styles, jobSettings);
        var candidates = PageRefiner.InvoicesNeedingRefinement(job.Documents, styles, jobSettings).Take(int.Parse(Opt("--max") ?? "100000")).ToList();
        using var analyzer = await PageAnalyzer.OpenAsync(Opt("--pdf") ?? job.SourcePath, settings.OcrLanguage);
        var firstPass = PageRefiner.Passes(jobSettings).First(p => !p.Surya);
        var variants = new List<(string Name, Func<PageInfo, Task<PageInfo>> Read)> { ("JPEG 2x", p => PageRefiner.ReadSavedImageAsync(p, firstPass)) };
        foreach (var dpi in (Opt("--dpi") ?? "300,400,600").Split(',').Select(int.Parse))
            variants.Add(($"PDF {dpi}", p => analyzer.ReadRenderedAsync(p, dpi)));
        var totals = variants.ToDictionary(v => v.Name, _ => new int[6]);
        var times = variants.ToDictionary(v => v.Name, _ => TimeSpan.Zero);
        string Brief(InvoiceDocument d) => $"{d.Issuer} | {d.InvoiceNumber} | {(d.Vat.Status == VatStatus.Valid || d.VatApproved ? "ÁFA ok" : "ÁFA -")} | {Or(d.IssueDate)} {Or(d.FulfillmentDate)} {Or(d.DueDate)}";
        static string Or(string s) => string.IsNullOrEmpty(s) ? "–" : s;
        foreach (var doc in candidates)
        {
            var pages = doc.Pages.Take(2).Where(p => !p.IsBlank && File.Exists(p.ImagePath)).ToList();
            Console.WriteLine($"#{doc.Number} p{doc.PageRange} {doc.StyleName}\n   alap:     {Brief(doc)}  [{doc.Errors.Count} hiba]");
            foreach (var (name, read) in variants)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var p in pages) p.ExtraReadings = new() { await read(p) };
                times[name] += sw.Elapsed;
                InvoiceProcessor.ReadDocument(doc, jobSettings);
                var t = totals[name];
                bool resolved = PageRefiner.IsResolved(doc, styles, jobSettings);
                if (resolved) t[0]++;
                if (!string.IsNullOrWhiteSpace(doc.Issuer)) t[1]++;
                if (!string.IsNullOrWhiteSpace(doc.InvoiceNumber)) t[2]++;
                if (doc.Vat.Status == VatStatus.Valid) t[3]++;
                if (doc.IssueDate.Length > 0 && doc.FulfillmentDate.Length > 0 && doc.DueDate.Length > 0) t[4]++;
                t[5] += doc.Errors.Count;
                Console.WriteLine($"   {name,-9} {Brief(doc)}  [{doc.Errors.Count} hiba{(resolved ? ", megoldva" : "")}]");
            }
            foreach (var p in pages) p.ExtraReadings = new();
            InvoiceProcessor.ReadDocument(doc, jobSettings);
        }
        Console.WriteLine($"ÖSSZESEN {candidates.Count} újraolvasandó számla");
        foreach (var (name, _) in variants)
        {
            var t = totals[name];
            Console.WriteLine($"   {name,-9} megoldva {t[0]}, kibocsátó {t[1]}, számlaszám {t[2]}, adószám {t[3]}, dátumok {t[4]}, hibák {t[5]}, idő {times[name].TotalSeconds:0} mp");
        }
        break;
    }
    case "suggest":
    {
        // suggest <workDir> [--no-refine ...]: issuer / invoice number suggestions of every invoice with problems.
        var job = JobCache.Load(args[1]);
        InvoiceProcessor.DetectReceiver(job, settings, null);
        var jobSettings = InvoiceProcessor.SettingsFor(job, settings);
        InvoiceProcessor.BuildDocuments(job, styles, jobSettings);
        foreach (var d in job.Documents.Where(d => d.HasErrors || d.IsGeneric || args.Contains("--all")))
        {
            Console.WriteLine($"#{d.Number} p{d.PageRange} {d.StyleName} | '{d.Issuer}' | '{d.InvoiceNumber}' | {d.ErrorsText}");
            foreach (var s in InvoiceProcessor.InvoiceNumberSuggestions(d, styles, jobSettings)) Console.WriteLine($"     number: {s.Value,-28} ({s.Source})");
            foreach (var s in InvoiceProcessor.IssuerSuggestions(d, styles, jobSettings)) Console.WriteLine($"     issuer: {s.Value,-40} ({s.Source})");
        }
        break;
    }
    case "surya-test":
    {
        // surya-test <image> [--settings file] [--server URL] [--api-key KEY]: checks local Surya or a Surya server.
        if (Opt("--server") is { } server) { settings.SuryaLocation = SuryaLocation.Server; settings.SuryaServerUrl = server; }
        if (Opt("--api-key") is { } key) settings.SuryaApiKey = key;
        try { Console.WriteLine(await SuryaOcr.TestAsync(settings, args[1])); }
        catch (Exception ex) { Console.WriteLine("FAILED: " + ex.Message); return 2; }
        break;
    }
    case "rotate":
    {
        // rotate <workDir> <page> <degrees>: turns a page image clockwise and reads it again (saved in the work dir).
        var job = JobCache.Load(args[1]);
        var page = job.Pages[int.Parse(args[2]) - 1];
        int before = page.WordCount;
        await PageAnalyzer.RotatePageAsync(page, int.Parse(args[3]), settings.OcrLanguage);
        JobCache.Save(job);
        Console.WriteLine($"Page {page.Number}: rotation {page.Rotation}°, {before} → {page.WordCount} words, image {page.ImagePath}");
        break;
    }
    case "dump":
    {
        var job = JobCache.Load(args[1]);
        var page = job.Pages[int.Parse(args[2]) - 1];
        Console.WriteLine($"Page {page.Number} rot={page.Rotation} blank={page.IsBlank} ink={page.InkRatio:P3} words={page.WordCount}");
        foreach (var line in page.Lines)
        {
            var f = line.Words[0];
            Console.WriteLine($"[{f.X:0.000},{f.Y:0.000}] {string.Join(" ", line.Words.Select(w => w.Text))}");
        }
        break;
    }
}
return 0;

void Finish(InvoiceJob job)
{
    foreach (var d in job.Documents)
    {
        Console.WriteLine($"#{d.Number,3} p{d.PageRange,-10} {d.StatusText,-5} T{d.Trust,3} {d.StyleName,-22} {d.Issuer,-28} {d.InvoiceNumber,-22} {d.VatText,-20} {d.ErrorsText}");
        if (args.Contains("--why"))   // the trust deductions
            foreach (var reason in d.TrustReasons) Console.WriteLine($"       - {reason}");
    }
    Console.WriteLine($"{job.Documents.Count} invoices, {job.Documents.Count(d => !d.HasErrors)} OK");
    var outDir = Opt("--out");
    if (outDir != null)
    {
        var r = PdfExporter.Export(job, outDir, InvoiceProcessor.SettingsFor(job, settings));
        Console.WriteLine($"Saved {r.Saved}, errors {r.SavedWithErrors}, skipped {r.Skipped}");
    }
}
