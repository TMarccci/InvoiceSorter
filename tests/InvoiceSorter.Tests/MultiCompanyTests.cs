using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;

namespace InvoiceSorter.Tests;

/// <summary>Behaviour learned from the second batch of samples (several receiving companies, many unknown layouts).</summary>
public class MultiCompanyTests
{
    private static PageInfo Page(int index, params (string Text, double X, double Y)[] words)
    {
        var page = new PageInfo { Index = index };
        foreach (var g in words.GroupBy(w => w.Y))
        {
            var line = new OcrTextLine();
            double x = g.Min(w => w.X);
            foreach (var w in g.OrderBy(w => w.X))
                line.Words.Add(new OcrWordBox { Text = w.Text, X = w.X, Y = w.Y, W = 0.08, H = 0.012 });
            page.Lines.Add(line);
        }
        return page;
    }

    private static AppSettings Settings(params (string Name, string Vat)[] receivers)
    {
        var s = AppSettings.CreateDefault();
        s.ReceiverCompanies = receivers.Select(r => new ReceiverCompany { Name = r.Name, VatNumber = r.Vat }).ToList();
        return s;
    }

    [Fact]
    public void IdentifierNeedsEveryWord()
    {
        // "METRO KERESKEDELMI KFT" must not match another company's "… ÉS KERESKEDELMI KFT".
        var compact = TextUtil.Compact("SIGNAL TELEFONTECHNIKA SZOLGÁLTATÓ ÉS KERESKEDELMI KFT");
        Assert.False(TextUtil.FuzzyContainsText(compact, "METRO KERESKEDELMI KFT", 0.2));
        Assert.True(TextUtil.FuzzyContainsText(TextUtil.Compact("METRO KERESKEDELMI KFT."), "METRO KERESKEDELMI KFT", 0.2));
        Assert.True(TextUtil.FuzzyContainsText(TextUtil.Compact("METR0 KERESKEDELMl KFT"), "METRO KERESKEDELMI KFT", 0.2));
    }

    [Fact]
    public void DetectsUnknownReceiverAndIgnoresFrequentSupplier()
    {
        // Buyer Teszt Szoftver on every page; supplier "Alfa Kft." also frequent, but in the seller block.
        var pages = Enumerable.Range(0, 6).Select(i => Page(i,
            ("Szállító:", 0.05, 0.10), ("Alfa", 0.05, 0.12), ("Kft.", 0.10, 0.12), ("Adószám:", 0.05, 0.14), ("12345676-2-41", 0.15, 0.14),
            ("Vevő:", 0.55, 0.101), ("Teszt", 0.55, 0.121), ("Szoftver", 0.6, 0.121), ("Kft.", 0.67, 0.121), ("Adószám:", 0.55, 0.141), ("99991032-2-41", 0.65, 0.141))).ToList();
        var settings = Settings(("Minta Vevő 1926 Kft.", "99991018-2-41"));

        var d = ReceiverDetector.Detect(pages, settings);
        Assert.Null(d.Match);
        Assert.NotNull(d.Suggestion);
        Assert.Equal("99991032-2-41", d.Suggestion!.VatNumber);
        Assert.Equal("Teszt Szoftver Kft.", d.Suggestion.Name);

        settings.ReceiverCompanies.Add(new ReceiverCompany { Name = "Teszt Szoftver Kft.", VatNumber = "99991032-2-41" });
        d = ReceiverDetector.Detect(pages, settings);
        Assert.Equal("99991032-2-41", d.Match?.VatNumber);
        Assert.Null(d.Suggestion);
    }

    [Fact]
    public void TaxNumberCoreAcceptedOnlyWhenEnabled()
    {
        var receivers = new List<ReceiverCompany> { new() { Name = "Teszt Szoftver Kft.", VatNumber = "99991032-2-41" } };
        Assert.Equal(VatStatus.Valid, VatValidator.Check("Vevő adószáma: 99991032", receivers, acceptCore: true).Status);
        Assert.Equal(VatStatus.Mismatch, VatValidator.Check("Vevő adószáma: 99991032", receivers, acceptCore: false).Status);
        Assert.Equal(VatStatus.Mismatch, VatValidator.Check("Vevő adószáma: 99991032-2-42", receivers, acceptCore: true).Status);
    }

    [Fact]
    public void FallbackStyleNeverRecognizesPages()
    {
        var page = Page(0, ("Számla", 0.4, 0.05));
        var generic = new InvoiceStyle { IsFallback = true, IdentifierRules = { new MatchRule { Text = "Számla" } } };
        Assert.Equal(0, FieldReader.MatchScore(generic, page, AppSettings.CreateDefault()));
    }

    [Fact]
    public void IssuerSkipsReceiverNameAndUsesFallbackRule()
    {
        var settings = Settings(("Próba Sarok Kft.", "99991049-2-42"));
        var page = Page(0,
            ("Pr0ba", 0.10, 0.04), ("Sar0k", 0.18, 0.04), ("Kii.", 0.25, 0.04),           // receiver, OCR-damaged
            ("Alfa", 0.10, 0.08), ("Gasztro", 0.18, 0.08), ("Hungary", 0.26, 0.08), ("Kft.", 0.34, 0.08));
        var company = @"^(?<value>(?-i:[A-Z])[^\n:,]{1,60}?\b(?:K\s?[fti1l]\s?[ti1l]|Z\s?[rt]\s?t|B\s?t)\b\.?)";
        var field = new FieldDefinition
        {
            Mode = FieldMode.Label, LabelText = "Eladó", LabelDirection = LabelDirection.Auto, Regex = company, ExcludeReceiver = true,
            Fallback = new FieldDefinition { Mode = FieldMode.Region, Region = new NormRect(0, 0, 1, 0.5), Regex = company, ExcludeReceiver = true },
        };
        Assert.Equal("Alfa Gasztro Hungary Kft.", FieldReader.ReadField(field, page, settings));
    }

    [Fact]
    public void AutoDirectionFindsValueBelowLabel()
    {
        var page = Page(0, ("Számlaszám", 0.80, 0.20), ("AL2614974VBS", 0.80, 0.21));
        var field = new FieldDefinition { Mode = FieldMode.Label, LabelText = "Számlaszám", LabelDirection = LabelDirection.Auto, Regex = @"(\S*\d\S*)" };
        Assert.Equal("AL2614974VBS", FieldReader.ReadField(field, page, AppSettings.CreateDefault()));
    }

    [Fact]
    public void GenericNumberRejectsDecreeReferenceAndYear()
    {
        var builtIn = InvoiceSorter.Core.Storage.JsonStore.LoadDefaultStyles().Single(s => s.IsFallback);
        var page = Page(0, ("Számla", 0.1, 0.1), ("száma:", 0.18, 0.1), ("151/2003.", 0.3, 0.1), ("(IX.22.)", 0.4, 0.1));
        Assert.Null(FieldReader.ReadField(builtIn.InvoiceNumber, page, AppSettings.CreateDefault()));
        page = Page(0, ("Számla", 0.1, 0.1), ("száma:", 0.18, 0.1), ("2026/1159GY", 0.3, 0.1));
        Assert.Equal("2026/1159GY", FieldReader.ReadField(builtIn.InvoiceNumber, page, AppSettings.CreateDefault()));
    }
}
