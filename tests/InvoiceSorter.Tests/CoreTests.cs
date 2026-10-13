using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;

namespace InvoiceSorter.Tests;

public class VatValidatorTests
{
    private static readonly List<ReceiverCompany> Receiver = new() { new ReceiverCompany { Name = "Minta Vevő 1926 Kft.", VatNumber = "99991018-2-41" } };

    [Theory]
    [InlineData("99991018", true)]
    [InlineData("10828663", true)]  // METRO
    [InlineData("99991019", false)]
    [InlineData("9999101", false)]
    public void Checksum(string core, bool valid) => Assert.Equal(valid, VatValidator.IsChecksumValid(core));

    [Theory]
    [InlineData("Adószám: 99991018-2-41")]
    [InlineData("Elad6 Adåszåm: 99991018-241")]          // dash lost by OCR
    [InlineData("Adószám: 999910 18 - 2 - 41")]
    [InlineData("Adöszåm: 9999l018-2-4l")]               // l read instead of 1
    [InlineData("Közösségi adószám: HU99991018")]
    public void FindsBuyerVat(string text) => Assert.Equal(VatStatus.Valid, VatValidator.Check(text, Receiver).Status);

    [Fact]
    public void WrongSuffixIsMismatch()
    {
        var r = VatValidator.Check("Vevő adószáma: 99991018-2-42", Receiver);
        Assert.Equal(VatStatus.Mismatch, r.Status);
        Assert.Equal("99991018-2-42", r.Found);
    }

    [Fact]
    public void MissingIsNotFound() =>
        Assert.Equal(VatStatus.NotFound, VatValidator.Check("Eladó adószáma: 10828663-2-44", Receiver).Status);

    [Fact]
    public void NoReceiverConfigured() =>
        Assert.Equal(VatStatus.NotConfigured, VatValidator.Check("99991018-2-41", new List<ReceiverCompany>()).Status);
}

public class TextUtilTests
{
    [Theory]
    [InlineData("COM147B912", "COM1478912")]
    [InlineData("COM149S743", "COM1495743")]
    [InlineData("0008/0O0225", "0008/000225")]
    [InlineData("METRO", "METRO")]
    [InlineData("SZ001932", "SZ001932")]   // letters of a prefix stay letters
    public void FixOcrDigits(string input, string expected) => Assert.Equal(expected, TextUtil.FixOcrDigits(input));

    [Fact]
    public void CompactRemovesAccentsAndPunctuation() =>
        Assert.Equal("szamlasorszam", TextUtil.Compact("Számla sorszám:"));

    [Theory]
    [InlineData("ezaszamlaszamsz001932", "szamlaszam", 1, true)]
    [InlineData("ezaszamiaszamsz001932", "szamlaszam", 1, true)]   // l read as i
    [InlineData("ezabankszamla", "szamlaszam", 1, false)]
    public void FuzzyContains(string hay, string needle, int errors, bool expected) =>
        Assert.Equal(expected, TextUtil.FuzzyContains(hay, needle, errors));

    [Theory]
    [InlineData("51405/26-BV", "51405-26-BV")]
    [InlineData("A: b?c*", "A- b-c-")]
    public void SanitizeFileName(string input, string expected) => Assert.Equal(expected, TextUtil.SanitizeFileName(input));
}

public class IssuerNameTests
{
    [Theory]
    [InlineData("Omega-Hungåria Kereskedelmi és Szolgåltat6 Zrt.", "Omega-Hungária Kereskedelmi és Szolgáltató Zrt.")]
    [InlineData("MH Magyarország Ktt.", "MH Magyarország Kft.")]
    [InlineData("Pepc:o Hungary K ft .", "Pepc:o Hungary Kft.")]
    [InlineData("\"NAPRAFORGÓ\" Kft", "NAPRAFORGÓ Kft.")]
    [InlineData("TESCO-GLOBAL ZRT.", "TESCO-GLOBAL Zrt.")]
    [InlineData("Minta Vevő 1926 Kft", "Minta Vevő 1926 Kft.")]
    public void RepairCompanyName(string raw, string expected) => Assert.Equal(expected, TextUtil.RepairCompanyName(raw));

    [Theory]
    [InlineData("SZIKRÅ MÜVEK KFT", "Szikra Művek Kft.")]
    [InlineData("Pepc:o Hungary K ft .", "Pepco Hungary Kft.")]
    [InlineData("Omnipack Magyarorszåg Kft.", "Omnipack Magyarország Kft.")]
    [InlineData("Teljesen Új Partner Kft", "Teljesen Új Partner Kft.")]   // unknown names are only repaired
    public void NormalizeToKnownIssuer(string raw, string expected) =>
        Assert.Equal(expected, InvoiceProcessor.NormalizeIssuer(raw, new AppSettings
        {
            KnownIssuers = new() { "Szikra Művek Kft.", "Pepco Hungary Kft.", "Omnipack Magyarország Kft." },
        }));

    [Fact]
    public void LearnIssuerReplacesMisspelledEntry()
    {
        var s = AppSettings.CreateDefault();
        s.KnownIssuers = new() { "Kiss Bt" };
        Assert.True(InvoiceProcessor.LearnIssuer("Kiss Bt.", s));      // only punctuation differs -> treated as same
        Assert.False(InvoiceProcessor.LearnIssuer("Kiss Bt.", s));
        Assert.Contains("Kiss Bt.", s.KnownIssuers);
    }
}

public class FieldReaderTests
{
    private static readonly AppSettings Settings = AppSettings.CreateDefault();

    /// <summary>Builds a page from (text, x, y) words; each word is 0.08 wide and 0.012 high.</summary>
    private static PageInfo Page(params (string Text, double X, double Y)[] words)
    {
        var page = new PageInfo { Index = 0 };
        foreach (var g in words.GroupBy(w => w.Y))
        {
            var line = new OcrTextLine();
            foreach (var w in g.OrderBy(w => w.X))
                line.Words.Add(new OcrWordBox { Text = w.Text, X = w.X, Y = w.Y, W = 0.08, H = 0.012 });
            page.Lines.Add(line);
        }
        return page;
    }

    [Fact]
    public void LabelRightReadsValueOnSameLine()
    {
        var page = Page(("Szåm", 0.02, 0.22), ("I", 0.07, 0.22), ("aszåm.", 0.09, 0.22), ("2026-0/0", 0.23, 0.22), ("0008/000225", 0.32, 0.22), ("EREDETI", 0.9, 0.22));
        var field = new FieldDefinition { Mode = FieldMode.Label, LabelText = "Számlaszám", LabelDirection = LabelDirection.Right, Regex = @"(\d{4}\s*/\s*\d{6})" };
        Assert.Equal("0008/000225", FieldReader.ReadField(field, page, Settings));
    }

    [Fact]
    public void LabelBelowReadsValueUnderLabel()
    {
        var page = Page(("Fizetési", 0.66, 0.186), ("Szåmlasorszåm", 0.826, 0.186), ("2026.09.29", 0.673, 0.198), ("47893126-BV", 0.831, 0.198));
        var field = new FieldDefinition
        {
            Mode = FieldMode.Label, LabelText = "Számlasorszám", LabelDirection = LabelDirection.Below,
            Regex = @"(\d{5})\s*[/1lI|]?\s*(\d{2})\s*-\s*([A-Z]{2})", Format = "$1/$2-$3",
        };
        Assert.Equal("47893/26-BV", FieldReader.ReadField(field, page, Settings));
    }

    [Fact]
    public void RegionReadsWordsInside()
    {
        var page = Page(("SZIKRÅ", 0.07, 0.105), ("MÜVEK", 0.16, 0.105), ("KFT", 0.25, 0.105), ("Minta", 0.6, 0.105));
        var field = new FieldDefinition { Mode = FieldMode.Region, Region = new NormRect(0.05, 0.09, 0.32, 0.04) };
        Assert.Equal("SZIKRÅ MÜVEK KFT", FieldReader.ReadField(field, page, Settings));
    }

    [Fact]
    public void ValueGluedToLabelIsRead()
    {
        var page = Page(("Szåm1aszåm:A0001210", 0.73, 0.065), ("VEVO", 0.71, 0.091));
        var field = new FieldDefinition { Mode = FieldMode.Label, LabelText = "Számlaszám", LabelDirection = LabelDirection.Right, Regex = @"(\S*\d\S*)" };
        Assert.Equal("A0001210", FieldReader.ReadField(field, page, Settings));
    }

    [Fact]
    public void LabelAlternatives()
    {
        var page = Page(("Szåmla", 0.09, 0.26), ("szåma", 0.15, 0.26), ("5120260008292378", 0.3, 0.26));
        var field = new FieldDefinition { Mode = FieldMode.Label, LabelText = "Számla sorszám | Számla száma", LabelDirection = LabelDirection.Right };
        Assert.Equal("5120260008292378", FieldReader.ReadField(field, page, Settings));
    }

    [Fact]
    public void DigitsGroupRepairsOcrLetters()
    {
        var page = Page(("MAGYAR", 0.3, 0.02), ("SZÅMLASZÅM:", 0.05, 0.31), ("Al", 0.5, 0.31), ("5701460/1708/00005", 0.53, 0.31));
        var field = new FieldDefinition
        {
            Mode = FieldMode.PageRegex, FixOcrDigits = true, RemoveSpaces = true, Format = "${p}${digits}/${z}/${s}",
            Regex = @"\b(?<p>[A-Z])\s?(?<digits>[0-9OoIl|](?:\s?[0-9OoIl|]){7})\s*/\s*(?<z>\d{4})\s*/\s*(?<s>\d{5})\b",
        };
        Assert.Equal("A15701460/1708/00005", FieldReader.ReadField(field, page, Settings));
    }

    [Fact]
    public void IdentifierAlternativesAndRegex()
    {
        var receipt = Page(("TESCO-GLOBAL", 0.3, 0.05), ("ZRT.", 0.5, 0.05), ("A05703995/1651/00003", 0.5, 0.15));
        var rule = new MatchRule { Text = @"BIZONYLATSZÁM | NAV Ellenőrző kód | re:\b[A-Z]\d{8}/\d{4}/\d{5}\b" };
        Assert.True(FieldReader.RuleMatches(rule, receipt, Settings));
        Assert.False(FieldReader.RuleMatches(new MatchRule { Text = "BIZONYLATSZÁM | NAV Ellenőrző kód" }, receipt, Settings));
    }

    [Fact]
    public void StyleMatchesWithOcrErrorsAndMissingAccents()
    {
        var page = Page(("Omega-Hungåria", 0.04, 0.06), ("Kereskedelmi", 0.15, 0.06), ("és", 0.26, 0.06), ("Szolgåltatö", 0.3, 0.06), ("Zrt.", 0.4, 0.06));
        var style = new InvoiceStyle { IdentifierRules = { new MatchRule { Text = "Omega-Hungária Kereskedelmi és Szolgáltató" } } };
        Assert.True(FieldReader.MatchScore(style, page, Settings) > 0);

        var other = new InvoiceStyle { IdentifierRules = { new MatchRule { Text = "METRO KERESKEDELMI KFT" } } };
        Assert.Equal(0, FieldReader.MatchScore(other, page, Settings));
    }
}
