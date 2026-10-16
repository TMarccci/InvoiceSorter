using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;

namespace InvoiceSorter.Tests;

/// <summary>
/// The three invoice dates (issue, fulfilment, due). The layouts are those of real Hungarian invoices, with invented
/// companies and dates near today (the plausibility checks compare with today's date).
/// </summary>
public class DateReaderTests
{
    // ------------------------------------------------------------------ helpers

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);
    private static string D(int daysAgo) => DateReader.Format(Today.AddDays(-daysAgo)).TrimEnd('.');
    private static string Hu(int daysAgo) => DateReader.Format(Today.AddDays(-daysAgo));

    /// <summary>A word: text at (x, y); its width follows the text length.</summary>
    private static (string Text, double X, double Y) W(string text, double x, double y) => (text, x, y);

    /// <summary>A page whose words on the same y form one OCR line (an A4 page unless narrow).</summary>
    private static PageInfo Page(bool narrow, params (string Text, double X, double Y)[] words)
    {
        var page = new PageInfo { Index = 0, PixelWidth = narrow ? 600 : 1654, PixelHeight = narrow ? 3000 : 2339 };
        double h = narrow ? 0.004 : 0.010;
        foreach (var g in words.GroupBy(w => w.Y).OrderBy(g => g.Key))
        {
            var line = new OcrTextLine();
            foreach (var w in g.OrderBy(w => w.X))
                line.Words.Add(new OcrWordBox { Text = w.Text, X = w.X, Y = w.Y, W = w.Text.Length * (narrow ? 0.03 : 0.0075), H = h });
            page.Lines.Add(line);
        }
        return page;
    }

    private static PageInfo Page(params (string Text, double X, double Y)[] words) => Page(false, words);

    private static AppSettings Settings() => AppSettings.CreateDefault();

    private static InvoiceDocument Doc(PageInfo page, InvoiceStyle? style = null, AppSettings? settings = null)
    {
        var doc = new InvoiceDocument { Style = style ?? new InvoiceStyle { IsFallback = true } };
        doc.Pages.Add(page);
        InvoiceProcessor.ReadDates(doc, settings ?? Settings());
        return doc;
    }

    // ------------------------------------------------------------------ parsing typed / printed dates

    [Theory]
    [InlineData("2026.09.02.", 2026, 9, 2)]
    [InlineData("2026. 09. 02.", 2026, 9, 2)]
    [InlineData("2026-09-02", 2026, 9, 2)]
    [InlineData("2026/09/02", 2026, 9, 2)]
    [InlineData("20260902", 2026, 9, 2)]
    [InlineData("02.09.2026", 2026, 9, 2)]
    [InlineData("2026. szeptember 2.", 2026, 9, 2)]
    [InlineData("2026. szept. 02.", 2026, 9, 2)]
    [InlineData("2026. 9. 2.", 2026, 9, 2)]
    [InlineData("2026.09,30.", 2026, 9, 30)]
    public void TypedDatesAreUnderstood(string text, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), DateReader.Parse(text));

    [Theory]
    [InlineData("2026.13.02")]          // no 13th month
    [InlineData("2026.02.30")]          // no 30 February
    [InlineData("2026.08.1")]           // a damaged "2026.08.19" (tick mark over the 9)
    [InlineData("2026.09.02 abc")]      // more than a date
    [InlineData("1899.01.01")]          // not a plausible year
    [InlineData("hétfő")]
    public void NonDatesAreRejected(string text) => Assert.Null(DateReader.Parse(text));

    [Fact]
    public void TypedValuesAreNormalized()
    {
        var doc = new InvoiceDocument();
        doc.IssueDate = "2026-09-02";
        Assert.Equal("2026.09.02.", doc.IssueDate);
        doc.DueDate = "2026.13.02";                 // not a date: kept as typed, the review shows the error
        Assert.Equal("2026.13.02", doc.DueDate);
        Assert.Contains(DateReader.Problems(doc), p => p.Contains("Érvénytelen dátum"));
    }

    [Fact]
    public void OcrDamageIsRepairedOnlyWhereItIsSafe()
    {
        var page = Page(W("2026.O9.O2.", 0.1, 0.1), W("2C26.0B.04", 0.1, 0.2), W("2026.", 0.1, 0.3), W("IO.", 0.15, 0.3), W("24.", 0.18, 0.3),
            W("2026.08.", 0.1, 0.4), W("I", 0.17, 0.4), W("2026", 0.1, 0.5), W("08", 0.15, 0.5), W("28", 0.18, 0.5));
        var dates = DateReader.FindDates(page, 1, 0);
        Assert.Contains(dates, t => t.Date == new DateOnly(2026, 9, 2) && t.Repaired);
        Assert.Contains(dates, t => t.Date == new DateOnly(2026, 8, 4) && t.Repaired);     // C → 0, B → 8
        Assert.Contains(dates, t => t.Date == new DateOnly(2026, 10, 24) && t.Repaired);   // "IO" → 10
        Assert.Contains(dates, t => t.Date == new DateOnly(2026, 8, 28) && t.Repaired);    // the dots lost by the OCR
        Assert.DoesNotContain(dates, t => t.Date == new DateOnly(2026, 8, 1));             // "2026.08. I" is a scrap
    }

    [Fact]
    public void PeriodsAreRecognized()
    {
        var page = Page(W("2026.07.01-2026.07.31", 0.1, 0.1), W("2026.08.01.", 0.1, 0.2), W("-", 0.2, 0.2), W("2026.08.31.", 0.22, 0.2));
        var dates = DateReader.FindDates(page, 1, 0);
        Assert.Equal(4, dates.Count);
        Assert.All(dates, t => Assert.True(t.InRange));
    }

    // ------------------------------------------------------------------ labels

    [Fact]
    public void LabelsBeforeTheDatesOnTheSameRow()
    {
        var doc = Doc(Page(
            W("Fizetési", 0.1, 0.20), W("mód:", 0.17, 0.20), W("átutalás", 0.6, 0.20),
            W("Teljesítés", 0.1, 0.22), W("dátuma:", 0.18, 0.22), W(D(12), 0.6, 0.22),
            W("Kiállítás", 0.1, 0.24), W("dátuma:", 0.17, 0.24), W(D(10), 0.6, 0.24),
            W("Fizetési", 0.1, 0.26), W("határidő:", 0.17, 0.26), W(D(2), 0.6, 0.26)));
        Assert.Equal(Hu(10), doc.IssueDate);
        Assert.Equal(Hu(12), doc.FulfillmentDate);
        Assert.Equal(Hu(2), doc.DueDate);
        Assert.Empty(DateReader.Problems(doc));
        Assert.Contains("Kiállítás dátuma", doc.IssueDateSource);
    }

    [Fact]
    public void ColumnHeadingsAboveTheDates()
    {
        // "Fizetési mód | Teljesítés dátuma | Számla kelte | Fizetési határidő" with the values in the row below;
        // the heading "Számla kelte" is split over two OCR lines.
        var doc = Doc(Page(
            W("Fizetés", 0.05, 0.300), W("módja", 0.11, 0.300), W("Teljesítés", 0.30, 0.300), W("dátuma", 0.38, 0.300),
            W("Számla", 0.55, 0.296), W("kelte", 0.56, 0.306), W("Fizetési", 0.75, 0.300), W("határidő", 0.81, 0.300),
            W("Átutalás", 0.05, 0.320), W(D(20), 0.31, 0.320), W(D(18), 0.53, 0.320), W(D(10), 0.76, 0.320)));
        Assert.Equal(Hu(18), doc.IssueDate);
        Assert.Equal(Hu(20), doc.FulfillmentDate);
        Assert.Equal(Hu(10), doc.DueDate);
    }

    [Fact]
    public void ColonHeadingsOfATableStayHeadings()
    {
        // "A teljesítés időpontja: | A számla kelte: | Fizetési határidő:" – the next heading follows each colon.
        var doc = Doc(Page(
            W("A", 0.31, 0.23), W("teljesítés", 0.33, 0.23), W("időpontja:", 0.40, 0.23),
            W("A", 0.52, 0.23), W("számla", 0.54, 0.23), W("kelte:", 0.60, 0.23),
            W("Fizetési", 0.68, 0.23), W("határidő:", 0.74, 0.23),
            W(D(9), 0.33, 0.25), W(D(9), 0.52, 0.25), W(D(1), 0.68, 0.25)));
        Assert.Equal(Hu(9), doc.IssueDate);
        Assert.Equal(Hu(9), doc.FulfillmentDate);
        Assert.Equal(Hu(1), doc.DueDate);
    }

    [Fact]
    public void ALabelValueRowIsNotTheHeadingOfTheRowBelow()
    {
        // "Teljesítés kelte: 2026.08.1✓" (the tick mark ruined the date) above "Számla kelte: 2026.07.31".
        var doc = Doc(Page(
            W("Teljesítés", 0.57, 0.124), W("kelte:", 0.63, 0.124), W("I", 0.69, 0.124),
            W("Számla", 0.57, 0.137), W("kelte:", 0.615, 0.137), W(D(30), 0.67, 0.137)));
        Assert.Equal(Hu(30), doc.IssueDate);
        Assert.Equal("", doc.FulfillmentDate);                 // not guessed
        Assert.False(doc.DateReading!.Fulfillment.Ambiguous);
        Assert.Contains(DateReader.Problems(doc), p => p.Contains("Hiányzó dátum: teljesítés dátuma"));
    }

    [Fact]
    public void BilingualLabelsGluedByASlash()
    {
        var doc = Doc(Page(
            W("Számla", 0.06, 0.30), W("kelte/lnvoice", 0.11, 0.30), W("date:", 0.22, 0.30), W(D(8), 0.34, 0.30),
            W("Telj.", 0.06, 0.32), W("kelte/Date", 0.10, 0.32), W("of", 0.19, 0.32), W("service:", 0.21, 0.32), W(D(4), 0.34, 0.32),
            W("Fiz.", 0.06, 0.34), W("határidő/Due", 0.09, 0.34), W("date:", 0.20, 0.34), W(D(4), 0.34, 0.34)));
        Assert.Equal(Hu(8), doc.IssueDate);
        Assert.Equal(Hu(4), doc.FulfillmentDate);
        Assert.Equal(Hu(4), doc.DueDate);
    }

    [Fact]
    public void PeriodsBalancesAndContractsAreNotInvoiceDates()
    {
        var doc = Doc(Page(
            W("Teljesítési", 0.05, 0.20), W("időszak:", 0.13, 0.20), W(D(60), 0.25, 0.20), W("-", 0.34, 0.20), W(D(30), 0.36, 0.20),
            W("Nyitóegyenleg:", 0.05, 0.22), W(D(61), 0.25, 0.22),
            W("Szerződés", 0.05, 0.24), W("kelte:", 0.13, 0.24), W(D(300), 0.25, 0.24),
            W("Számla", 0.6, 0.20), W("kelte:", 0.66, 0.20), W(D(25), 0.75, 0.20)));
        Assert.Equal(Hu(25), doc.IssueDate);
        Assert.Equal("", doc.FulfillmentDate);
        Assert.Equal("", doc.DueDate);
        // The excluded dates are still offered (marked), the period is labelled as such.
        var suggestions = DateReader.Suggestions(doc, DateKind.Fulfillment);
        Assert.Contains(suggestions, s => s.Source.Contains("időszak") || s.Source.Contains("nem számladátum"));
    }

    [Fact]
    public void TheHeadingOfTheNeighbouringColumnIsNotUsed()
    {
        // The "Teljesítés" heading was not read; "Fiz. határidő" stands over the next column only.
        var doc = Doc(Page(
            W("Fiz.", 0.47, 0.30), W("határidő", 0.50, 0.30), W("Kiállítás", 0.60, 0.30),
            W(D(20), 0.37, 0.32), W(D(10), 0.47, 0.32), W(D(14), 0.60, 0.32)));
        Assert.Equal(Hu(14), doc.IssueDate);
        Assert.Equal(Hu(10), doc.DueDate);
        Assert.Equal("", doc.FulfillmentDate);
    }

    [Fact]
    public void ADatesInASentenceDoNotTakeTheHeadingAbove()
    {
        var doc = Doc(Page(
            W("Fizetési", 0.05, 0.30), W("határidő", 0.12, 0.30),
            W(D(5), 0.05, 0.32),
            W("a", 0.05, 0.34), W(D(40) + "-ig", 0.07, 0.34), W("beérkezett", 0.17, 0.34), W("befizetések", 0.27, 0.34)));
        Assert.Equal(Hu(5), doc.DueDate);
        Assert.False(doc.DateReading!.Due.Ambiguous);
    }

    // ------------------------------------------------------------------ undecidable: left empty, with suggestions

    [Fact]
    public void TwoDifferentDatesWithTheSameLabelAreNotGuessed()
    {
        // A collective invoice: every delivery has its own "Teljesítés:" date.
        var doc = Doc(Page(
            W("Kelt", 0.30, 0.20), W(D(10), 0.30, 0.22),
            W("Teljesítés:", 0.05, 0.40), W(D(15), 0.15, 0.40),
            W("Teljesítés:", 0.05, 0.50), W(D(12), 0.15, 0.50)));
        Assert.Equal(Hu(10), doc.IssueDate);
        Assert.Equal("", doc.FulfillmentDate);
        Assert.True(doc.DateReading!.Fulfillment.Ambiguous);
        Assert.Contains(DateReader.Problems(doc), p => p.Contains("Nem dönthető el"));
        var suggestions = DateReader.Suggestions(doc, DateKind.Fulfillment);
        Assert.Equal(new[] { Hu(12), Hu(15) }.OrderBy(v => v), suggestions.Take(2).Select(s => s.Value).OrderBy(v => v));
    }

    [Fact]
    public void ReadingsThatDisagreeAreNotGuessed()
    {
        var normal = Page(W("Számla", 0.1, 0.2), W("kelte:", 0.16, 0.2), W(D(10), 0.3, 0.2));
        normal.ExtraReadings.Add(Page(W("Számla", 0.1, 0.2), W("kelte:", 0.16, 0.2), W(D(16), 0.3, 0.2)));
        var doc = Doc(normal);
        Assert.Equal("", doc.IssueDate);
        Assert.True(doc.DateReading!.Issue.Ambiguous);
    }

    [Fact]
    public void ADateOnlyInOneReReadIsNotTrusted()
    {
        var normal = Page(W("Költ.:", 0.1, 0.2), W("x", 0.3, 0.2));
        normal.ExtraReadings.Add(Page(W("Kelt:", 0.1, 0.2), W(D(10), 0.3, 0.2)));
        Assert.Equal("", Doc(normal).IssueDate);

        normal.ExtraReadings.Add(Page(W("Kelt:", 0.1, 0.2), W(D(10), 0.3, 0.2)));   // two re-reads agree
        var doc = Doc(normal);
        Assert.Equal(Hu(10), doc.IssueDate);
        Assert.Contains(doc.DateReading!.Issue.Notes, n => n.Contains("újraolvasás"));
    }

    // ------------------------------------------------------------------ single-date invoices

    [Fact]
    public void ATillReceiptHasOneDateForAllThree()
    {
        var doc = Doc(Page(true,
            W("EGYSZERŰSÍTETT", 0.1, 0.10), W("SZÁMLA", 0.6, 0.10),
            W("A", 0.1, 0.60), W("pontok", 0.15, 0.60), W(D(400 - 390) + "-ig", 0.4, 0.60), W("érvényesek", 0.7, 0.60),
            W("BIZONYLATSZÁM:", 0.1, 0.90), W(D(3) + ".", 0.1, 0.91), W("14:18", 0.7, 0.91)));
        Assert.True(doc.DateReading!.SingleDateDocument);
        Assert.Equal(Hu(3), doc.IssueDate);
        Assert.Equal(Hu(3), doc.FulfillmentDate);
        Assert.Equal(Hu(3), doc.DueDate);
        Assert.Empty(DateReader.Problems(doc));
    }

    [Fact]
    public void ACashInvoiceWithOneLabelledDate()
    {
        var doc = Doc(Page(
            W("Készpénzes", 0.05, 0.10), W("számla", 0.17, 0.10),
            W("Kibocsátás", 0.40, 0.08), W("dátuma", 0.49, 0.08), W(D(6), 0.40, 0.10)));
        Assert.Equal(Hu(6), doc.IssueDate);
        Assert.Equal(Hu(6), doc.FulfillmentDate);
        Assert.Equal(Hu(6), doc.DueDate);
        Assert.Contains("készpénzes", doc.DueDateSource);
    }

    [Fact]
    public void AMentionOfCashInTheSmallPrintIsNotACashPayment()
    {
        // A transfer invoice with a postal cheque ("Készpénzátutalási megbízás") and "készpénzes befizetés esetén …".
        var doc = Doc(Page(
            W("Számla", 0.05, 0.10), W("kelte:", 0.11, 0.10), W(D(6), 0.25, 0.10),
            W("Készpénzátutalási", 0.05, 0.80), W("megbízás", 0.18, 0.80),
            W("Készpénzes", 0.05, 0.85), W("befizetés", 0.15, 0.85), W("esetén", 0.25, 0.85)));
        Assert.False(doc.DateReading!.SingleDateDocument);
        Assert.Equal(Hu(6), doc.IssueDate);
        Assert.Equal("", doc.FulfillmentDate);
        Assert.Equal("", doc.DueDate);
    }

    [Fact]
    public void PaymentTermsAfterTheLabelCount()
    {
        var doc = Doc(Page(
            W("Számla", 0.6, 0.05), W("dátuma:", 0.66, 0.05), W(D(4), 0.75, 0.05),
            W("TELJESÍTÉS", 0.05, 0.90), W("DÁTUMA:", 0.15, 0.90), W(D(4), 0.25, 0.90),
            W("Fizetés:", 0.05, 0.92), W("Azonnal", 0.12, 0.92)));
        Assert.True(doc.DateReading!.SingleDateDocument);
        Assert.Equal(Hu(4), doc.DueDate);
    }

    // ------------------------------------------------------------------ checks

    [Fact]
    public void ADueDateBeforeTheIssueDateIsAnError()
    {
        var doc = new InvoiceDocument { IssueDate = Hu(5), FulfillmentDate = Hu(5), DueDate = Hu(9) };
        Assert.Contains(DateReader.Problems(doc), p => p.Contains("korábbi, mint a kiállítás"));
        doc.DatesApproved = true;
        Assert.Empty(DateReader.Problems(doc));
    }

    [Fact]
    public void AWrongYearDigitIsCaught()
    {
        // A faint "2026" read as "2020": far from the other dates and from today.
        var doc = new InvoiceDocument { IssueDate = Hu(10), FulfillmentDate = DateReader.Format(Today.AddYears(-6)), DueDate = Hu(2) };
        var problems = DateReader.Problems(doc);
        Assert.Contains(problems, p => p.Contains("Valószínűtlen dátum (teljesítés dátuma)"));
        Assert.Contains(problems, p => p.Contains("több mint egy év"));
    }

    [Fact]
    public void ThePrintedPaymentTermIsCheckedAndOffered()
    {
        var page = Page(
            W("Átutalás", 0.05, 0.30), W("(8", 0.12, 0.30), W("nap)", 0.15, 0.30),
            W("Számla", 0.40, 0.28), W("kelte", 0.46, 0.28), W(D(20), 0.40, 0.30));
        var doc = Doc(page);
        Assert.Equal(8, DateReader.PaymentTermDays(doc));
        // No due date printed: the computed one is offered first, but not filled in.
        Assert.Equal("", doc.DueDate);
        Assert.Equal(Hu(12), DateReader.Suggestions(doc, DateKind.Due)[0].Value);
        // A due date that contradicts the term costs trust points.
        doc.DueDate = Hu(5);
        Assert.Contains(DateReader.Doubts(doc), x => x.Reason.Contains("8 napos fizetési feltétel"));
        doc.DueDate = Hu(12);
        Assert.DoesNotContain(DateReader.Doubts(doc), x => x.Reason.Contains("fizetési feltétel"));
    }

    [Fact]
    public void MissingDatesAreAnErrorOnlyWhenRequired()
    {
        var doc = new InvoiceDocument { IssueDate = Hu(1) };
        Assert.Contains(DateReader.Problems(doc), p => p.StartsWith("Hiányzó dátum: teljesítés dátuma, fizetési határidő"));
        doc.DatesRequired = false;
        Assert.Empty(DateReader.Problems(doc));
    }

    [Fact]
    public void ManualDatesSurviveReReading()
    {
        var page = Page(W("Számla", 0.1, 0.2), W("kelte:", 0.16, 0.2), W(D(10), 0.3, 0.2));
        var doc = Doc(page);
        doc.DueDate = Hu(1);
        doc.MarkEdited("due date");
        InvoiceProcessor.ReadDates(doc, Settings());
        Assert.Equal(Hu(1), doc.DueDate);
        Assert.Equal(Hu(10), doc.IssueDate);
        Assert.Equal("kézzel megadva", doc.DueDateSource);
    }

    [Fact]
    public void AStyleRuleReadsALayoutTheGenericLabelsMiss()
    {
        // "Dátum/Teljesítés ideje 2026.09.04" is the issue and the fulfilment date at once.
        var style = new InvoiceStyle
        {
            IssueDate = new FieldDefinition { Mode = FieldMode.Label, LabelText = "Dátum/Teljesítés ideje", LabelDirection = LabelDirection.Right },
        };
        var doc = Doc(Page(W("Dátum/Teljesítés", 0.51, 0.18), W("ideje", 0.59, 0.18), W(D(7), 0.63, 0.18)), style);
        Assert.Equal(Hu(7), doc.IssueDate);
        Assert.Equal(Hu(7), doc.FulfillmentDate);
        Assert.Contains("sablon", doc.IssueDateSource);
    }

    [Fact]
    public void OldSettingsGetTheDefaultDateLabels()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"SuryaMode\":\"Off\"}", InvoiceSorter.Core.Storage.JsonStore.Options)!;
        Assert.Contains("Számla kelte", old.IssueDateLabels);
        Assert.Contains("Teljesítési időszak", old.NotDateLabels);
        Assert.True(old.DatesRequired);
    }
}
