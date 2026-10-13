using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;
using InvoiceSorter.Core.Storage;

namespace InvoiceSorter.Tests;

public class ReviewOperationTests
{
    private static PageInfo Page(int index, params (string Text, double X, double Y)[] words)
    {
        var page = new PageInfo { Index = index };
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
        s.LookupIssuerNames = false;
        s.ReceiverCompanies = new() { new ReceiverCompany { Name = "Példa Kávéház Kft.", VatNumber = "99991025-2-43" } };
        return s;
    }

    private static readonly InvoiceStyle Alfa = new()
    {
        Name = "Alfa",
        IdentifierRules = { new MatchRule { Text = "Alfa" } },
        Issuer = new FieldDefinition { Mode = FieldMode.Fixed, FixedValue = "Alfa Kft." },
        InvoiceNumber = new FieldDefinition { Mode = FieldMode.Label, LabelText = "Számlaszám", Regex = @"(A-\d+)" },
    };

    private static PageInfo FirstPage(int index, string number) => Page(index,
        ("Alfa", 0.05, 0.05), ("Kft.", 0.12, 0.05), ("Számlaszám:", 0.6, 0.05), (number, 0.75, 0.05),
        ("Vevő", 0.05, 0.2), ("99991025-2-43", 0.25, 0.2));

    private static InvoiceJob Job(params InvoiceDocument[] docs)
    {
        var job = new InvoiceJob { SourcePath = "x.pdf", WorkDir = Path.GetTempPath() };
        foreach (var d in docs)
        {
            foreach (var p in d.Pages) job.Pages.Add(p);
            InvoiceProcessor.ReadDocument(d, Settings());
            job.Documents.Add(d);
        }
        return job;
    }

    private static InvoiceDocument Doc(params PageInfo[] pages)
    {
        var d = new InvoiceDocument { Style = Alfa, StartReason = StartReason.Style };
        foreach (var p in pages) d.Pages.Add(p);
        return d;
    }

    [Fact]
    public void MergeIntoAnyInvoiceKeepsTheTargetsValues()
    {
        var a = Doc(FirstPage(0, "A-1"));
        var b = Doc(FirstPage(1, "A-2"));
        var stray = Doc(Page(2, ("melléklet", 0.1, 0.1)));
        var job = Job(a, b, stray);
        a.InvoiceNumber = "A-1 (checked)";

        var result = InvoiceProcessor.MergeInto(job, stray, a, Settings());
        Assert.Same(a, result);
        Assert.Equal(new[] { 1, 3 }, a.Pages.Select(p => p.Number));
        Assert.Equal("A-1 (checked)", a.InvoiceNumber);
        Assert.Equal(2, job.Documents.Count);
        Assert.Equal(new[] { 1, 2 }, job.Documents.Select(d => d.Number));
        Assert.Throws<InvalidOperationException>(() => InvoiceProcessor.MergeInto(job, a, a, Settings()));
    }

    [Fact]
    public void MovingPagesChangesTheOrderAndRereadsANewFirstPage()
    {
        var copy = FirstPage(0, "A-6");          // e.g. a page of another invoice that was scanned on top
        var invoice = FirstPage(1, "A-7");
        var doc = Doc(copy, invoice);
        var job = Job(doc);
        Assert.Equal("A-6", doc.InvoiceNumber);

        InvoiceProcessor.MovePage(job, doc, invoice, 0, Settings());
        Assert.Equal(new[] { 2, 1 }, doc.Pages.Select(p => p.Number));
        Assert.Equal("A-7", doc.InvoiceNumber);
        Assert.Equal("2, 1", doc.PageRange);

        // Out of range positions are clamped.
        InvoiceProcessor.MovePage(job, doc, invoice, 10, Settings());
        Assert.Equal(new[] { 1, 2 }, doc.Pages.Select(p => p.Number));
    }

    [Fact]
    public void DefaultFileNameStartsWithTheInvoiceNumber()
    {
        var settings = Settings();
        var doc = Job(Doc(FirstPage(0, "A-12"))).Documents[0];
        Assert.Equal("A-12_Alfa Kft.pdf", PdfExporter.BuildFileName(doc, settings));
        Assert.EndsWith("_p1_A-12_Alfa Kft.pdf", PdfExporter.BuildErrorFileName(doc));
    }

    [Fact]
    public void UpgradeSwitchesTheOldDefaultFileNamePatternOnly()
    {
        string path = Path.Combine(Path.GetTempPath(), $"styles-{Guid.NewGuid():N}.json");
        try
        {
            var old = AppSettings.CreateDefault();
            old.BuiltInStylesVersion = 5;
            old.FileNameTemplate = AppSettings.OldDefaultFileNameTemplate;
            JsonStore.UpgradeBuiltInStyles(JsonStore.LoadDefaultStyles(), old, path);
            Assert.Equal("{InvoiceNumber}_{Issuer}", old.FileNameTemplate);

            var custom = AppSettings.CreateDefault();
            custom.BuiltInStylesVersion = 5;
            custom.FileNameTemplate = "{Number}_{Issuer}";
            JsonStore.UpgradeBuiltInStyles(JsonStore.LoadDefaultStyles(), custom, path);
            Assert.Equal("{Number}_{Issuer}", custom.FileNameTemplate);
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), Path.GetFileNameWithoutExtension(path) + "*")) File.Delete(f);
        }
    }

    [Fact]
    public void DeliveryNoteWithoutInvoiceTitleIsFlagged()
    {
        var generic = JsonStore.LoadDefaultStyles().Single(st => st.IsFallback);
        var note = new InvoiceDocument { Style = generic, StartReason = StartReason.Title };
        note.Pages.Add(Page(0, ("Szállítólevél", 0.4, 0.05), ("Profil", 0.05, 0.1), ("Markt", 0.12, 0.1), ("Kft.", 0.2, 0.1),
            ("Vevő", 0.05, 0.2), ("99991025-2-43", 0.25, 0.2)));
        InvoiceProcessor.ReadDocument(note, Settings());
        Assert.Equal("Szállítólevél", note.NonInvoiceTitle);
        Assert.Contains(note.Errors, e => e.Contains("Nem számla?"));
        note.IsInvoiceConfirmed = true;
        Assert.DoesNotContain(note.Errors, e => e.Contains("Nem számla?"));

        var invoice = new InvoiceDocument { Style = generic, StartReason = StartReason.Title };
        invoice.Pages.Add(Page(0, ("SZÁMLA", 0.4, 0.05), ("Szállítólevél", 0.4, 0.5)));
        InvoiceProcessor.ReadDocument(invoice, Settings());
        Assert.Null(invoice.NonInvoiceTitle);
    }

    [Fact]
    public void GenericRulesKeepNumbersThatOnlyLookLikeDatesAndSuggestAlternatives()
    {
        var generic = JsonStore.LoadDefaultStyles().Single(st => st.IsFallback);
        var page = Page(0, ("Számlaszám", 0.69, 0.07), ("2026/41/1100/0538", 0.81, 0.07),
            ("Kelt:", 0.05, 0.3), ("2026.09.12", 0.15, 0.3), ("siometrik2019@gmail.com", 0.05, 0.4));
        Assert.Equal("2026/41/1100/0538", FieldReader.ReadField(generic.InvoiceNumber, page, Settings()));

        var doc = new InvoiceDocument { Style = generic, StartReason = StartReason.FirstPage };
        doc.Pages.Add(Page(0, ("Számla", 0.05, 0.05), ("száma:", 0.12, 0.05), ("26/00", 0.3, 0.05), ("595", 0.36, 0.05)));
        InvoiceProcessor.ReadDocument(doc, Settings());
        var suggestions = InvoiceProcessor.InvoiceNumberSuggestions(doc, JsonStore.LoadDefaultStyles(), Settings());
        Assert.Contains(suggestions, x => x.Value == "26/00 595");
    }

    [Fact]
    public void GeneralTermsPageStaysWithItsInvoice()
    {
        var settings = Settings();
        Assert.True(InvoiceProcessor.HasContinuationTitle(Page(0, ("ALFA", 0.18, 0.05), ("Általános", 0.18, 0.07), ("szerződési", 0.25, 0.07), ("feltételek", 0.33, 0.07), ("szállítási", 0.4, 0.07)), settings));
        Assert.False(InvoiceProcessor.HasContinuationTitle(Page(0, ("Számla", 0.18, 0.05), ("Általános", 0.18, 0.8), ("szerződési", 0.25, 0.8), ("feltételek", 0.33, 0.8)), settings));
    }
}
