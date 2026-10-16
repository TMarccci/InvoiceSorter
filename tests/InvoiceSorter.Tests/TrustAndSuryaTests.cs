using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;

namespace InvoiceSorter.Tests;

public class TrustAndSuryaTests
{
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

    private static AppSettings Settings()
    {
        var s = AppSettings.CreateDefault();
        s.KnownIssuers = new() { "Alfa Kft." };
        s.DatesRequired = false;   // the sample pages have no dates (DateReaderTests covers them)
        s.ReceiverCompanies = new() { new ReceiverCompany { Name = "Minta Vevő 1926 Kft.", VatNumber = "99991018-2-41" } };
        return s;
    }

    private static InvoiceDocument Read(InvoiceStyle style, PageInfo page, AppSettings settings)
    {
        var doc = new InvoiceDocument { Style = style, StartReason = StartReason.Style };
        doc.Pages.Add(page);
        InvoiceProcessor.ReadDocument(doc, settings);
        return doc;
    }

    private static readonly PageInfo SamplePage = Page(
        ("Alfa", 0.05, 0.05), ("Kft.", 0.12, 0.05),
        ("Számlaszám:", 0.6, 0.05), ("SZ-2026-0042", 0.75, 0.05),
        ("Vevő", 0.05, 0.2), ("adószám:", 0.12, 0.2), ("99991018-2-41", 0.25, 0.2));

    [Fact]
    public void SpecificStyleWithKnownIssuerIsHighlyTrusted()
    {
        var style = new InvoiceStyle
        {
            IdentifierRules = { new MatchRule { Text = "Alfa" } },
            Issuer = new FieldDefinition { Mode = FieldMode.Fixed, FixedValue = "Alfa Kft." },
            InvoiceNumber = new FieldDefinition { Mode = FieldMode.Label, LabelText = "Számlaszám", Regex = @"(SZ-\d{4}-\d{4})" },
        };
        var doc = Read(style, SamplePage, Settings());
        Assert.False(doc.HasErrors);
        Assert.True(doc.Trust >= TrustScorer.HighThreshold, doc.TrustReasonsText);
        Assert.Equal("Magas", doc.TrustLevel);
    }

    [Fact]
    public void GenericStyleWithUnknownIssuerIsLessTrusted()
    {
        var generic = InvoiceSorter.Core.Storage.JsonStore.LoadDefaultStyles().Single(s => s.IsFallback);
        var settings = Settings();
        settings.KnownIssuers.Clear();
        var doc = Read(generic, SamplePage, settings);
        Assert.Equal("Alfa Kft.", doc.Issuer);
        Assert.Equal("SZ-2026-0042", doc.InvoiceNumber);
        Assert.True(doc.Trust < TrustScorer.HighThreshold, doc.TrustReasonsText);
        Assert.Contains(doc.TrustReasons, r => r.Contains("általános szabályokkal"));
        Assert.Contains(doc.TrustReasons, r => r.Contains("nincs az ismert kibocsátók között"));
    }

    [Fact]
    public void ErrorsCapTheScoreAndLowTrustCanBeAnError()
    {
        var style = new InvoiceStyle
        {
            IdentifierRules = { new MatchRule { Text = "Alfa" } },
            Issuer = new FieldDefinition { Mode = FieldMode.Fixed, FixedValue = "Alfa Kft." },
            InvoiceNumber = new FieldDefinition { Mode = FieldMode.Label, LabelText = "Sorszám", Regex = @"(\d+)" },   // not on the page
        };
        var doc = Read(style, SamplePage, Settings());
        Assert.True(doc.HasErrors);
        Assert.True(doc.Trust < TrustScorer.MediumThreshold);

        var generic = InvoiceSorter.Core.Storage.JsonStore.LoadDefaultStyles().Single(s => s.IsFallback);
        var settings = Settings();
        settings.KnownIssuers.Clear();
        settings.LowTrustIsError = true;
        settings.LowTrustThreshold = 95;
        var lowTrust = Read(generic, SamplePage, settings);
        Assert.Contains(lowTrust.Errors, e => e.Contains("Alacsony megbízhatóság"));
    }

    [Fact]
    public void ManualValuesCountAsConfirmed()
    {
        var generic = InvoiceSorter.Core.Storage.JsonStore.LoadDefaultStyles().Single(s => s.IsFallback);
        var settings = Settings();
        settings.KnownIssuers.Clear();
        var doc = Read(generic, SamplePage, settings);
        int before = doc.Trust;
        doc.MarkEdited("issuer");
        InvoiceProcessor.Score(doc, settings);
        Assert.True(doc.Trust > before);
    }

    [Fact]
    public void BuiltInStylesAreCurrentAndMigrationKeepsUserRegexes()
    {
        var builtIn = InvoiceSorter.Core.Storage.JsonStore.LoadDefaultStyles();
        Assert.Equal(0, InvoiceSorter.Core.Storage.JsonStore.MigrateRegexes(builtIn));   // already the newest patterns

        var userStyle = new InvoiceStyle
        {
            Issuer = new FieldDefinition { Mode = FieldMode.Region, Regex = @"^(?<value>.{2,}?\b(?:K\s?[fti1l]\s?[ti1l]|Z\s?[rt]\s?t|N\s?y\s?r\s?t|B\s?t|K\s?k\s?t)\b\.?)" },
            InvoiceNumber = new FieldDefinition { Mode = FieldMode.PageRegex, Regex = @"(?<prefix>FUGEA)\s*(?<digits>\d{7})" },
        };
        Assert.Equal(1, InvoiceSorter.Core.Storage.JsonStore.MigrateRegexes(new[] { userStyle }));
        Assert.Contains("digitally signed by", userStyle.Issuer.Regex);
        Assert.Equal(@"(?<prefix>FUGEA)\s*(?<digits>\d{7})", userStyle.InvoiceNumber.Regex);   // user's own regex untouched
    }

    [Fact]
    public void UnchangedEnglishNotesOfBuiltInStylesBecomeHungarian()
    {
        var builtIn = InvoiceSorter.Core.Storage.JsonStore.LoadDefaultStyles().Single(s => s.Id == "billingo");
        var unchanged = InvoiceSorter.Core.Storage.JsonStore.DeepClone(builtIn);
        unchanged.Notes = "Billingo online invoicing. Invoice number is printed under the 'Számla' title, issuer under 'ELADÓ'.";
        var edited = InvoiceSorter.Core.Storage.JsonStore.DeepClone(builtIn);
        edited.Notes = "Saját megjegyzés";

        InvoiceSorter.Core.Storage.JsonStore.MigrateRegexes(new[] { unchanged, edited });

        Assert.Equal(builtIn.Notes, unchanged.Notes);   // matched by the stored hash of the old text
        Assert.Equal("Saját megjegyzés", edited.Notes);  // the user's own notes stay
    }

    [Fact]
    public void SuryaResultIsConvertedToNormalizedWords()
    {
        var msg = new SuryaOcr.WorkerMessage
        {
            Width = 1000, Height = 2000,
            Lines = new()
            {
                new() { Text = "Számlaszám: A0001210", Bbox = new double[] { 100, 200, 500, 240 } },              // no word boxes
                new() { Text = "Alfa Kft.", Bbox = new double[] { 100, 300, 300, 340 },
                        Words = new() { new() { Text = "Alfa", Bbox = new double[] { 100, 300, 180, 340 } }, new() { Text = "Kft.", Bbox = new double[] { 200, 300, 300, 340 } } } },
            },
        };
        var lines = SuryaOcr.ToLines(msg);
        Assert.Equal(2, lines.Count);
        Assert.Equal(new[] { "Számlaszám:", "A0001210" }, lines[0].Words.Select(w => w.Text));
        Assert.Equal(0.1, lines[0].Words[0].X, 3);
        Assert.Equal(0.1, lines[0].Words[0].Y, 3);
        Assert.True(lines[0].Words[1].X > lines[0].Words[0].X + lines[0].Words[0].W);
        Assert.Equal(0.2, lines[1].Words[1].X, 3);
        Assert.Equal("Alfa Kft.", lines[1].Text);
    }
}
