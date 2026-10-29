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
}
