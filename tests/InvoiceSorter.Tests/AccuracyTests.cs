using InvoiceSorter.Core;
using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;
using InvoiceSorter.Core.Storage;

namespace InvoiceSorter.Tests;

/// <summary>Style recognition, fixed issuers, page counters and key–value blocks (cases from real scans, with made-up data).</summary>
public class AccuracyTests
{
    private static PageInfo Page(int index, params (string Text, double X, double Y)[] words)
    {
        var page = new PageInfo { Index = index };
        foreach (var g in words.GroupBy(w => w.Y))
        {
            var line = new OcrTextLine();
            foreach (var w in g.OrderBy(w => w.X))
                line.Words.Add(new OcrWordBox { Text = w.Text, X = w.X, Y = w.Y, W = 0.06, H = 0.012 });
            page.Lines.Add(line);
        }
        return page;
    }

    private static AppSettings Settings()
    {
        var s = AppSettings.CreateDefault();
        s.LookupIssuerNames = false;
        s.DatesRequired = false;
        s.KnownIssuers = new() { "Delta Kereskedelmi Kft. | 99991056-2-44" };
        s.ReceiverCompanies = new() { new ReceiverCompany { Name = "Példa Kávéház Kft.", VatNumber = "99991025-2-43" } };
        return s;
    }

    private static readonly List<InvoiceStyle> Styles = JsonStore.LoadDefaultStyles();
    private static InvoiceStyle Generic => Styles.Single(s => s.IsFallback);

    [Theory]
    // The errors of the whole phrase all fell on "METRO": "Vasalatkereskedelmi Kft" is another company.
    [InlineData("Delta Vasalatkereskedelmi Kft Intemet to www.delta-minta.hu", "METRO KERESKEDELMI KFT", false)]
    [InlineData("METRO KERESKEDELMI KFT. Budaörs", "METRO KERESKEDELMI KFT", true)]
    [InlineData("MFTRO KERESKEDELM1 KFT.", "METRO KERESKEDELMI KFT", true)]
    // Real OCR errors stay accepted: m → łn, two wrong letters in a long word, one in a short one.
    [InlineData("Készült a www.szałnlazz.hu rendszerében", "szamlazz.hu", true)]
    [InlineData("NAV Ellerdrző kód: A12345678", "NAV Ellenőrző kód", true)]
    [InlineData("ancFR-SOET Kft. szoftverével", "FR-SOFT", true)]
    public void FuzzyMatchKeepsEachWordInsideThePhrase(string pageText, string text, bool expected) =>
        Assert.Equal(expected, TextUtil.FuzzyContainsText(TextUtil.Compact(pageText), text, 0.2));

    private static InvoiceDocument FixedIssuerDoc(PageInfo page)
    {
        var style = new InvoiceStyle
        {
            Name = "Áruház",
            IdentifierRules = { new MatchRule { Text = "Kereskedelmi" } },
            Issuer = new FieldDefinition { Mode = FieldMode.Fixed, FixedValue = "METRO Kereskedelmi Kft." },
            InvoiceNumber = new FieldDefinition { Mode = FieldMode.Label, LabelText = "Számlaszám", Regex = @"(\d{6,})" },
        };
        var doc = new InvoiceDocument { Style = style, StartReason = StartReason.Style };
        doc.Pages.Add(page);
        InvoiceProcessor.ReadDocument(doc, Settings());
        return doc;
    }

    [Fact]
    public void FixedIssuerIsOnlyGivenWhenThePageShowsIt()
    {
        // The name is printed: the style's spelling.
        var metro = FixedIssuerDoc(Page(0, ("METRO", 0.05, 0.05), ("Kereskedelmi", 0.12, 0.05), ("Kft.", 0.2, 0.05)));
        Assert.Equal("METRO Kereskedelmi Kft.", metro.Issuer);
        Assert.DoesNotContain(TrustScorer.FixedIssuerNotOnPageNote, metro.Notes);

        // Another supplier's page that matched the style by mistake: the issuer of its tax number, with a warning.
        var other = FixedIssuerDoc(Page(0, ("Delta", 0.05, 0.05), ("Vasalatkereskedelmi", 0.12, 0.05), ("Kft", 0.2, 0.05),
            ("Adószám", 0.07, 0.157), ("99991056-2-44", 0.24, 0.157), ("Adószám", 0.52, 0.184), ("99991025-2-43", 0.71, 0.184)));
        Assert.Equal("Delta Kereskedelmi Kft.", other.Issuer);
        Assert.Contains(TrustScorer.FixedIssuerNotOnPageNote, other.Notes);
        Assert.Contains(other.TrustReasons, r => r.Contains("rossz sablon"));

        // Nothing confirms it: left empty for the user, the style's name is offered as a suggestion.
        var unknown = FixedIssuerDoc(Page(0, ("Vasalatkereskedelmi", 0.12, 0.05), ("Kft", 0.2, 0.05)));
        Assert.Equal("", unknown.Issuer);
        Assert.Contains(unknown.Errors, e => e.Contains("Kibocsátó nem található"));
        Assert.Contains(InvoiceProcessor.IssuerSuggestions(unknown, Styles, Settings()), s => s.Value == "METRO Kereskedelmi Kft.");
    }

    private static InvoiceJob Build(params PageInfo[] pages)
    {
        var job = new InvoiceJob { SourcePath = "x.pdf", WorkDir = Path.GetTempPath() };
        job.Pages.AddRange(pages);
        InvoiceProcessor.BuildDocuments(job, Styles, Settings());
        return job;
    }

    [Fact]
    public void PageCounterWithoutTotalContinuesTheInvoiceAcrossTheTermsPage()
    {
        // "Oldal 1" – terms printed on the back – "Számla oldal 2 számlaszám …": every page names the buyer.
        var first = Page(0, ("SZÁMLA", 0.3, 0.05), ("Számlaszám", 0.5, 0.1), ("123456789", 0.65, 0.1),
            ("Példa", 0.1, 0.2), ("Kávéház", 0.17, 0.2), ("Kft.", 0.24, 0.2), ("Oldal", 0.8, 0.3), ("1", 0.87, 0.3));
        var terms = Page(1, ("ALFA", 0.18, 0.05), ("Általános", 0.18, 0.07), ("szerződési", 0.25, 0.07), ("feltételek", 0.33, 0.07), ("szállítási", 0.4, 0.07));
        var second = Page(2, ("Számla", 0.1, 0.14), ("oldal", 0.17, 0.14), ("2", 0.24, 0.14), ("számlaszám", 0.27, 0.14), ("123456789,", 0.34, 0.14),
            ("Példa", 0.1, 0.16), ("Kávéház", 0.17, 0.16), ("Kft.", 0.24, 0.16));
        var job = Build(first, terms, second);
        Assert.Single(job.Documents);
        Assert.Equal("1-3", job.Documents[0].PageRange);
    }

    [Fact]
    public void ReversedPilesAreNotJoinedByTheirPageCounters()
    {
        // Each invoice scanned back to front: "2/2", "1/2" | "2/2", "1/2" of the next invoice.
        PageInfo P(int i, string counter, string number) => Page(i, ("Oldalszám:", 0.6, 0.03), (counter, 0.68, 0.03),
            ("Számlaszám:", 0.1, 0.1), (number, 0.25, 0.1), ("Példa", 0.1, 0.2), ("Kávéház", 0.17, 0.2), ("Kft.", 0.24, 0.2));
        var job = Build(P(0, "2/2", "A-100"), P(1, "1/2", "A-100"), P(2, "2/2", "A-200"), P(3, "1/2", "A-200"));
        Assert.Equal(new[] { "1-2", "3-4" }, job.Documents.Select(d => d.PageRange));
        Assert.Equal((1, 4), InvoiceProcessor.PageOf(Page(0, ("Oldalszám", 0.6, 0.05), ("1/4", 0.68, 0.05))));
    }

    [Fact]
    public void TheNextRowOfAKeyValueBlockIsNotTheValue()
    {
        // The invoice number is crossed by a pen stroke ("41 23 78"); the customer number of the next row must not be taken.
        var page = Page(0, ("Számlaszám", 0.514, 0.145), ("41", 0.628, 0.141), ("23", 0.664, 0.142), ("78", 0.691, 0.141),
            ("Vevőszám", 0.513, 0.161), ("765432", 0.629, 0.160));
        Assert.True(string.IsNullOrEmpty(FieldReader.ReadField(Generic.InvoiceNumber, page, Settings())));

        // A heading with the company name under it (and the buyer's column next to it) still gives the name.
        var issuer = new FieldDefinition { Mode = FieldMode.Label, LabelText = "Számla kiállító adatai", LabelDirection = LabelDirection.Below };
        var seller = Page(0, ("Számla", 0.047, 0.111), ("kiállító", 0.1, 0.111), ("adatai:", 0.15, 0.111),
            ("Minta", 0.051, 0.135), ("Kft.", 0.1, 0.135), ("PÉLDA", 0.2, 0.135), ("KÁVÉHÁZ", 0.27, 0.135));
        Assert.StartsWith("Minta Kft.", FieldReader.ReadField(issuer, seller, Settings()));
    }

    [Fact]
    public void ALaterPageThatRepeatsTheFullNumberWinsOverAPartlyReadOne()
    {
        var doc = new InvoiceDocument { Style = Generic, StartReason = StartReason.FirstPage };
        doc.Pages.Add(Page(0, ("Számlaszám", 0.5, 0.1), ("45678", 0.65, 0.1)));
        doc.Pages.Add(Page(1, ("Számla", 0.1, 0.14), ("oldal", 0.17, 0.14), ("2", 0.24, 0.14), ("számlaszám", 0.27, 0.14), ("412345678,", 0.34, 0.14)));
        InvoiceProcessor.ReadDocument(doc, Settings());
        Assert.Equal("412345678", doc.InvoiceNumber);
    }

    [Fact]
    public void UpdateCheckTakesTheHighestReleaseNotTheLastPublished()
    {
        var releases = UpdateChecker.ParseList("""
            [
              { "tag_name": "1.2.1", "html_url": "https://e/121", "assets": [] },
              { "tag_name": "1.3.0", "html_url": "https://e/130",
                "assets": [ { "name": "InvoiceSorter-Setup-1.3.0.exe", "browser_download_url": "https://e/c.exe", "size": 1 } ] },
              { "tag_name": "v1.4.0", "draft": true, "assets": [] },
              { "tag_name": "1.5.0-beta", "prerelease": true, "assets": [] }
            ]
            """);
        Assert.Equal(2, releases.Count);
        var newest = UpdateChecker.Newest(releases)!;
        Assert.Equal("1.3.0", newest.Version);
        Assert.Equal("https://e/c.exe", newest.Installer(UpdateChecker.ClientInstaller)!.Url);
    }

    private static string? GenericNumberOn(string line)
    {
        var words = line.Split(' ').Select((w, i) => (w, 0.05 + i * 0.07, 0.3)).ToArray();
        var rule = new FieldDefinition { Mode = FieldMode.PageRegex, Regex = Generic.InvoiceNumber.Regex };
        return FieldReader.ReadField(rule, Page(0, words), Settings());
    }

    [Theory]
    // A date read with a comma, or its month.day tail, is not an invoice number.
    [InlineData("határidő 2026,09.18 1. (eredeti)", null)]
    [InlineData("Copy 09.18.", null)]
    // A bank account with a digit lost by the OCR is not one either.
    [InlineData("Bankszámla 1600006-00000000-12345678", null)]
    // Hungarian capitals belong to the number.
    [InlineData("Számlaszám 2026/1234GYŰJTŐ", "2026/1234GYŰJTŐ")]
    [InlineData("Számlaszám 26/0123", "26/0123")]
    [InlineData("Számlaszám SZA00099/2026", "SZA00099/2026")]
    public void GenericInvoiceNumberRule(string line, string? expected) => Assert.Equal(expected, GenericNumberOn(line));

    [Fact]
    public void TheTailOfABilingualLabelIsSkipped()
    {
        // "Számlaszám/Invoice No.:" and "Számlaszám / Number:" with the value far right.
        var a = Page(0, ("Számlaszám/lnvoice", 0.439, 0.088), ("No.:", 0.666, 0.088), ("26/0123", 0.834, 0.089));
        Assert.Equal("26/0123", FieldReader.ReadField(Generic.InvoiceNumber, a, Settings()));
        var b = Page(0, ("Számlaszám", 0.492, 0.062), ("/", 0.556, 0.062), ("Number:", 0.566, 0.062), ("5400123456", 0.775, 0.062));
        Assert.Equal("5400123456", FieldReader.ReadField(Generic.InvoiceNumber, b, Settings()));
        // "Okirat száma": a document treated as an invoice.
        var c = Page(0, ("Okirat", 0.228, 0.262), ("száma", 0.29, 0.262), ("6050123456", 0.229, 0.275));
        Assert.Equal("6050123456", FieldReader.ReadField(Generic.InvoiceNumber, c, Settings()));
    }

    [Fact]
    public void BillingoNumbersHaveTheirShapeOtherwiseTheGenericLabelsAreUsed()
    {
        var billingo = Styles.Single(s => s.Id == "billingo");
        // Another layout that mentions Billingo: under "Számla…" there is "Számladátum", the number is next to "Számlaszám / Number:".
        var page = Page(0, ("Billingo", 0.05, 0.95),
            ("Számlaszám", 0.492, 0.062), ("/", 0.556, 0.062), ("Number:", 0.566, 0.062), ("5400123456", 0.775, 0.062),
            ("Számladátum", 0.492, 0.079), ("/", 0.556, 0.079), ("Date", 0.566, 0.079), ("2026.07.30", 0.775, 0.079));
        Assert.Equal("5400123456", InvoiceProcessor.ReadFieldWithFallback(billingo.InvoiceNumber, page, Settings()).Value);

        var real = Page(0, ("Számla", 0.6, 0.05), ("2026-000123", 0.6, 0.066));
        Assert.Equal("2026-000123", InvoiceProcessor.ReadFieldWithFallback(billingo.InvoiceNumber, real, Settings()).Value);
    }

    [Theory]
    [InlineData("HÓSZIG Kft.", "Hőszigetelő Minta Kft.", "HŐSZIG Kft.")]
    [InlineData("GAMMA-OKNA Alkatrész Kft.", "Gamma-Okná Alkatrész Kft.", "GAMMA-OKNÁ Alkatrész Kft.")]
    [InlineData("Minta Richard", "Minta Richárd", "Minta Richárd")]
    [InlineData("Hőszig Kft.", "HOSZIGETELO KFT.", "Hőszig Kft.")]   // a register name without accents removes none
    [InlineData("Delta Kft.", "Béta Kft.", "Delta Kft.")]
    public void AccentsComeFromTheRegisteredName(string read, string registered, string expected) =>
        Assert.Equal(expected, TextUtil.AccentsFrom(read, registered));

    [Fact]
    public void AnInvoiceScannedBackToFrontStaysTogether()
    {
        // "2/2" then "1/2" of the same supplier – the page 1 number is crossed by a pen ("A-1OO" read); then another invoice.
        PageInfo P(int i, string counter, string number, string tax) => Page(i, ("Oldal:", 0.85, 0.02), (counter, 0.92, 0.02),
            ("Adószám:", 0.05, 0.13), (tax, 0.15, 0.13), ("Számlaszám:", 0.1, 0.2), (number, 0.25, 0.2),
            ("Példa", 0.6, 0.1), ("Kávéház", 0.67, 0.1), ("Kft.", 0.74, 0.1));
        var job = Build(P(0, "2/2", "A-100", "99991056-2-44"), P(1, "1/2", "A-1OO", "99991056-2-44"),
                        P(2, "2/2", "B-200", "99991063-2-09"), P(3, "1/2", "C-300", "99991056-2-44"));
        Assert.Equal(new[] { "1-2", "3", "4" }, job.Documents.Select(d => d.PageRange));
    }

    [Fact]
    public void ALowercaseLetterInTheInvoiceNumberCostsTrust()
    {
        var doc = new InvoiceDocument { Style = Generic, StartReason = StartReason.FirstPage };
        doc.Pages.Add(Page(0, ("Számlaszám:", 0.5, 0.1), ("301234567u", 0.65, 0.1)));
        InvoiceProcessor.ReadDocument(doc, Settings());
        Assert.Equal("301234567u", doc.InvoiceNumber);
        Assert.Contains(doc.TrustReasons, r => r.Contains("kisbetű"));
    }

    [Fact]
    public void SupplierTemplatesReadTheirNumbers()
    {
        // HŐSZIG: the OCR loses the slash of "004321/2026".
        var hoszig = Page(0, ("Szállító", 0.068, 0.121), ("HŐSZIG", 0.069, 0.134), ("Kft.", 0.13, 0.134),
            ("Számla", 0.781, 0.302), ("sorszáma:", 0.83, 0.302), ("004321", 0.798, 0.318), ("2026", 0.86, 0.318));
        var style = FieldReader.FindStyle(hoszig, Styles, Settings());
        Assert.Equal("hoszig", style?.Id);
        Assert.Equal("004321/2026", InvoiceProcessor.ReadFieldWithFallback(style!.InvoiceNumber, hoszig, Settings()).Value);

        // Alza.hu: the number is in the title line.
        var alza = Page(0, ("alza.hu", 0.097, 0.031), ("Számla", 0.286, 0.031), ("-", 0.35, 0.031), ("AHUW261234567", 0.386, 0.031),
            ("Jótállási", 0.572, 0.078), ("jegy", 0.64, 0.078), ("és", 0.68, 0.078), ("bizonylat", 0.71, 0.078),
            ("Eladó:", 0.031, 0.095), ("Alza.hu", 0.131, 0.095), ("Kft.", 0.19, 0.095));
        style = FieldReader.FindStyle(alza, Styles, Settings());
        Assert.Equal("alza", style?.Id);
        Assert.Equal("AHUW261234567", InvoiceProcessor.ReadFieldWithFallback(style!.InvoiceNumber, alza, Settings()).Value);
    }

    [Fact]
    public void OlderInstallationsGetTheVersion11Rules()
    {
        string path = Path.Combine(Path.GetTempPath(), $"styles-{Guid.NewGuid():N}.json");
        try
        {
            var builtIn = JsonStore.LoadDefaultStyles();
            var styles = JsonStore.LoadDefaultStyles();
            // The rules as they were in version 10.
            var generic = styles.Single(s => s.IsFallback).InvoiceNumber;
            generic.Regex = generic.Regex
                .Replace(@"(?<![0-9A-Za-zÁÉÍÓÖŐÚÜŰáéíóöőúüű/._-])", @"(?<![0-9A-Za-z/._-])")
                .Replace(@"[A-Z0-9ÁÉÍÓÖŐÚÜŰ][A-Z0-9ÁÉÍÓÖŐÚÜŰ/._-]{3,})", @"[A-Z0-9][A-Z0-9/._-]{3,})")
                .Replace(@"(?!\d{6,8}-\d{7,8})", @"(?!\d{8}-\d{8})")
                .Replace(@"(?<!(?:19|20)\d{2}[.,]\s?)(?!(?:0[1-9]|1[0-2])[.,](?:0[1-9]|[12]\d|3[01])\.?(?![0-9A-Za-z/_-]))", "");
            generic.LabelText = generic.LabelText.Replace(" | Okirat száma", "");
            var billingo = styles.Single(s => s.Id == "billingo").InvoiceNumber;
            billingo.Regex = @"^(\S{3,})";
            billingo.Fallback = null;
            styles.RemoveAll(s => s.Id is "hoszig" or "alza");
            var settings = AppSettings.CreateDefault();
            settings.BuiltInStylesVersion = 10;

            Assert.True(JsonStore.UpgradeBuiltInStyles(styles, settings, path));
            Assert.Equal(builtIn.Single(s => s.IsFallback).InvoiceNumber.Regex, generic.Regex);
            Assert.Equal(builtIn.Single(s => s.IsFallback).InvoiceNumber.LabelText, generic.LabelText);
            Assert.Equal(builtIn.Single(s => s.Id == "billingo").InvoiceNumber.Regex, billingo.Regex);
            Assert.NotNull(billingo.Fallback);
            Assert.Contains(styles, s => s.Id == "hoszig");
            Assert.Contains(styles, s => s.Id == "alza");
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), Path.GetFileNameWithoutExtension(path) + "*")) File.Delete(f);
        }
    }
}
