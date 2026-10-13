using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;
using InvoiceSorter.Core.Storage;

namespace InvoiceSorter.Tests;

public class IssuerLookupTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), $"registry-test-{Guid.NewGuid():N}.json");

    public IssuerLookupTests() => CompanyRegistry.CachePath = _cache;

    public void Dispose()
    {
        CompanyRegistry.CachePath = Path.Combine(Path.GetTempPath(), $"registry-test-{Guid.NewGuid():N}.json");
        if (File.Exists(_cache)) File.Delete(_cache);
    }

    private static PageInfo Page(params (string Text, double X, double Y)[] words)
    {
        var page = new PageInfo { Index = 0 };
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
        s.KnownIssuers = new() { "Alfa Kft." };
        s.ReceiverCompanies = new() { new ReceiverCompany { Name = "Példa Kávéház Kft.", VatNumber = "99991025-2-43" } };
        return s;
    }

    [Theory]
    [InlineData("METRO KERESKEDELMI KORLÁTOLT FELELŐSSÉGŰ TÁRSASÁG", "Metro Kereskedelmi Kft.")]
    [InlineData("DELTA KERESKEDELMI, KORLÁTOLT FELELŐSSÉGŰ TÁRSASÁG", "Delta Kereskedelmi Kft.")]
    [InlineData("SPAR MAGYARORSZÁG KERESKEDELMI KORLÁTOLT FELELÖSSÉGÜ TÁRSASÁG", "Spar Magyarország Kereskedelmi Kft.")]
    [InlineData("TESCO-GLOBAL ÁRUHÁZAK ZÁRTKÖRŰEN MŰKÖDŐ RÉSZVÉNYTÁRSASÁG", "Tesco-Global Áruházak Zrt.")]
    [InlineData("GAMMA ÉS TÁRSA ZÁRTKÖRŰEN MŰKÖDŐ RÉSZVÉNYTÁRSASÁG", "Gamma és Társa Zrt.")]
    [InlineData("MAGYAR TELEKOM TÁVKÖZLÉSI NYILVÁNOSAN MŰKÖDŐ RÉSZVÉNYTÁRSASÁG", "Magyar Telekom Távközlési Nyrt.")]
    [InlineData("UPS MAGYARORSZÁG SZÁLLÍTMÁNYOZÓ KORLÁTOLT FELELŐSSÉGŰ TÁRSASÁG", "UPS Magyarország Szállítmányozó Kft.")]
    public void RegisteredNamesAreShortened(string registered, string expected) =>
        Assert.Equal(expected, CompanyRegistry.ShortName(registered));

    [Theory]
    [InlineData("Delta Kft.", "Delta Kereskedelmi Kft.", true)]
    [InlineData("SPAR Kereskedelmi Kft.", "Spar Magyarország Kereskedelmi Kft.", true)]
    [InlineData("TERMOSZIG Kft.", "Termoszigetelő Gyártó és Szolgáltató Kft.", true)]
    [InlineData("DRV Zrt.", "Dunántúli Regionális Vízmű Zrt.", true)]
    [InlineData("OTP Bank Nyrt.", "Delta Kereskedelmi Kft.", false)]
    [InlineData("Példa Kávéház Kft.", "Spar Magyarország Kereskedelmi Kft.", false)]
    [InlineData("Magyar Posta Zrt.", "Magyar Államvasutak Zrt.", false)]
    public void SameCompanyRecognizesShortForms(string name, string registered, bool expected) =>
        Assert.Equal(expected, InvoiceProcessor.SameCompany(name, registered));

    [Fact]
    public void BankReadAsIssuerIsReplacedByTheSupplierTaxNumbersName()
    {
        // Delta invoice: "Delta Kfts." could not be read as a company, so the bank in the seller block was taken.
        CompanyRegistry.Remember("99991056", "DELTA KERESKEDELMI, KORLÁTOLT FELELŐSSÉGŰ TÁRSASÁG");
        var page = Page(("Szállító", 0.07, 0.05), ("Delta", 0.07, 0.06), ("Kfts.", 0.13, 0.06),
            ("Bank", 0.07, 0.116), ("OTP", 0.24, 0.116), ("Bank", 0.29, 0.116), ("Nyrt.", 0.34, 0.116),
            ("Adószám", 0.07, 0.157), ("99991056-2-44", 0.24, 0.157),
            ("Adószám", 0.52, 0.184), ("99991025-2-43", 0.71, 0.184),
            ("Sorszáma", 0.76, 0.022), ("617142872", 0.86, 0.022));
        var generic = JsonStore.LoadDefaultStyles().Single(s => s.IsFallback);
        var doc = new InvoiceDocument { Style = generic, StartReason = StartReason.FirstPage };
        doc.Pages.Add(page);
        InvoiceProcessor.ReadDocument(doc, Settings());
        Assert.Equal("Delta Kereskedelmi Kft.", doc.Issuer);
        Assert.Contains(TrustScorer.IssuerCorrectedByTaxNumberNote, doc.Notes);
    }

    [Fact]
    public void KnownIssuerWithTaxNumberWinsAndVatGroupsAreIgnored()
    {
        var settings = Settings();
        settings.KnownIssuers.Add("SPAR Magyarország Kereskedelmi Kft. | 10485824-2-07");
        var page = Page(("SPAR", 0.3, 0.05), ("(erejkedeltli", 0.4, 0.05), ("Kft.", 0.55, 0.05),
            ("ADOSZAM:", 0.27, 0.11), ("10485824-2-07", 0.4, 0.11));
        Assert.Equal("SPAR Magyarország Kereskedelmi Kft.", InvoiceProcessor.IssuerByTaxNumber(new[] { page }, "SPAR (erejkedeltli Kft.", settings));

        CompanyRegistry.Remember("17781774", "ÁFA CSOPORT / VAT GROUP (AZ ÁFA CSOPORT TAGJAI LEKÉRDEZHETŐEK)");
        Assert.Null(CompanyRegistry.CachedName("17781774"));
    }

    [Fact]
    public void LookupCanBeSwitchedOff()
    {
        CompanyRegistry.Remember("99991063", "GAMMA ÉS TÁRSA ZÁRTKÖRŰEN MŰKÖDŐ RÉSZVÉNYTÁRSASÁG");
        var settings = Settings();
        var tax = VatValidator.Parse("99991063-2-09")!;
        Assert.Equal("Gamma és Társa Zrt.", InvoiceProcessor.NameForTaxNumber(tax, settings));
        settings.LookupIssuerNames = false;
        Assert.Null(InvoiceProcessor.NameForTaxNumber(tax, settings));
    }

    [Fact]
    public void PageCounterIsReadNextToTheLabel()
    {
        Assert.Equal((2, 3), InvoiceProcessor.PageOf(Page(("2/3", 0.86, 0.97), ("Oldal:", 0.80, 0.97))));
        Assert.Equal((2, 2), InvoiceProcessor.PageOf(Page(("Oldal", 0.886, 0.102), (":", 0.93, 0.102), ("2", 0.958, 0.102))));   // METRO
        Assert.Null(InvoiceProcessor.PageOf(Page(("Oldal:", 0.80, 0.97), ("2026.09.21.", 0.86, 0.97))));
    }

    [Fact]
    public void BuyerTaxNumberWithOneMisreadDigitIsAcceptedWhenTheNameIsPrinted()
    {
        var receivers = Settings().ReceiverCompanies;
        var ok = VatValidator.Check("VEVŐ: Példa Kávéház Kft.\n99991325-2-43", receivers);
        Assert.Equal(VatStatus.Valid, ok.Status);
        Assert.Contains(VatValidator.OcrDigitNote, ok.Message);
        // Without the buyer's name it stays a mismatch for a human to check.
        Assert.Equal(VatStatus.Mismatch, VatValidator.Check("99991325-2-43", receivers).Status);
    }

    [Fact]
    public void UpgradeMigratesMetroAndReceiptPatterns()
    {
        var builtIn = JsonStore.LoadDefaultStyles();
        var old = JsonStore.DeepClone(builtIn);
        var metro = old.Single(s => s.Id == "metro");
        metro.InvoiceNumber.Regex = @"(\d{4}\s*/\s*\d{6})";
        metro.InvoiceNumber.Format = "";
        var nav = old.Single(s => s.Id == "nav-penztargep");
        nav.InvoiceNumber.Regex = @"\b(?<p>[A-Z])\s?(?<digits>[0-9OoIl|](?:\s?[0-9OoIl|]){7})\s*/\s*(?<z>\d{4})\s*/\s*(?<s>\d{5})\b";
        nav.InvoiceNumber.Format = "${p}${digits}/${z}/${s}";
        nav.InvoiceNumber.Fallback = null;
        nav.IdentifierRules[0].Text = @"NAV Ellenőrző kód | re:\b[A-Z]\s?[0-9OIl](?:\s?[0-9OIl]){7}\s*/\s*\d{4}\s*/\s*\d{5}\b";

        JsonStore.MigrateRegexes(old);
        foreach (var id in new[] { "metro", "nav-penztargep" })
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(builtIn.Single(s => s.Id == id), JsonStore.Options),
                         System.Text.Json.JsonSerializer.Serialize(old.Single(s => s.Id == id), JsonStore.Options));
    }

    [Fact]
    public void MetroAndReceiptNumbersAreReadInFull()
    {
        var styles = JsonStore.LoadDefaultStyles();
        var settings = Settings();
        var metro = styles.Single(s => s.Id == "metro");
        var metroPage = Page(("METRO", 0.01, 0.10), ("KERESKEDELMI", 0.06, 0.10), ("KFT.", 0.13, 0.10),
            ("Számlaszám", 0.015, 0.225), ("2026-0/0", 0.227, 0.224), ("(11)", 0.29, 0.224), ("0068/003434", 0.33, 0.224), ("(068-108727)", 0.42, 0.224));
        Assert.Equal("2026-0/0(11)0068/003434", FieldReader.ReadField(metro.InvoiceNumber, metroPage, settings));

        var nav = styles.Single(s => s.Id == "nav-penztargep");
        var receipt = Page(("SZÁMLASZÁM:", 0.05, 0.18), ("A20A01313/0340/OOOOl", 0.54, 0.18));
        Assert.Equal("A20401313/0340/00001", FieldReader.ReadField(nav.InvoiceNumber, receipt, settings));

        var rlb = styles.Single(s => s.Id == "rlb60");
        var rlbPage = Page(("Számla", 0.815, 0.205), ("sorszám", 0.86, 0.205), ("2026101234", 0.82, 0.22));
        Assert.Equal("2026/01234", FieldReader.ReadField(rlb.InvoiceNumber, rlbPage, settings));
    }
}
