using InvoiceSorter.Core.Models;

namespace InvoiceSorter.Core.Processing;

/// <summary>
/// Computes the trust score (0-100) of an invoice: how sure the program is that the issuer, the invoice number,
/// the buyer VAT check and the page split are right. It starts at 100 and deducts points for every sign of doubt;
/// each deduction is recorded as a reason that is shown to the user.
/// </summary>
public static class TrustScorer
{
    public const int HighThreshold = 85;
    public const int MediumThreshold = 60;

    public static void Score(InvoiceDocument doc, AppSettings settings)
    {
        var reasons = new List<string>();
        int score = 100;
        void Deduct(int points, string reason)
        {
            score -= points;
            reasons.Add($"−{points} {reason}");
        }

        var style = doc.Style;
        if (style == null || doc.Pages.Count == 0)
        {
            doc.Trust = 0;
            doc.TrustReasons = new[] { "nincs sablon – automatikusan semmi sem olvasható" };
            return;
        }

        // ---- layout
        if (style.IsFallback) Deduct(20, "nem illeszkedett sablon – az általános szabályokkal olvasva");

        // ---- issuer
        if (doc.HasEdit("issuer")) { }
        else if (style.Issuer.Mode == FieldMode.Fixed) { }
        else if (!string.IsNullOrWhiteSpace(doc.Issuer))
        {
            var known = settings.KnownIssuers.Select(k => InvoiceProcessor.ParseKnownIssuer(k).Name).Contains(doc.Issuer) ||
                        doc.Notes.Contains(IssuerConfirmedByTaxNumberNote) || doc.Notes.Contains(IssuerByTaxNumberNote) ||
                        doc.Notes.Contains(IssuerCorrectedByTaxNumberNote);
            if (doc.Notes.Contains(IssuerCorrectedByTaxNumberNote)) Deduct(5, "a lapon olvasott kibocsátónév eltér – a név a szállító adószáma alapján");
            else if (doc.Notes.Contains(IssuerByTaxNumberNote)) Deduct(2, "a kibocsátónév az adószám alapján kiegészítve");
            else if (!known) Deduct(style.IsFallback ? 12 : 8, "a kibocsátó a lapról olvasva, nincs az ismert kibocsátók között");

            // "Csoportos beszedési megbízás esetén BKM Nonprofit Zrt." - company names have few lowercase words.
            if (!known && doc.Issuer.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Count(w => char.IsLower(w[0]) && !LowercaseNameWords.Contains(w.TrimEnd('.', ','))) >= 3)
                Deduct(10, "a kibocsátó egy mondat részletének tűnik");
            // Garbled OCR ("Üáiľ;róoft Kft."): unusual characters in the name.
            if (!known && doc.Issuer.Any(c => !char.IsLetterOrDigit(c) && " .,-&'()/+\"„”".IndexOf(c) < 0))
                Deduct(8, "a kibocsátónévben szokatlan karakterek vannak (OCR-zaj?)");

            var raw = FirstValue(doc, p => InvoiceProcessor.ReadFieldWithFallback(style.Issuer, p, settings).Value);
            if (raw != null && !known && TextUtil.Levenshtein(TextUtil.Compact(raw), TextUtil.Compact(doc.Issuer)) > 3)
                Deduct(5, "a kibocsátónevet OCR-javítás után sikerült olvasni");
        }

        // ---- invoice number
        if (!doc.HasEdit("invoice number") && !string.IsNullOrWhiteSpace(doc.InvoiceNumber) && style.InvoiceNumber.Mode != FieldMode.Fixed)
        {
            var field = style.InvoiceNumber;
            if (string.IsNullOrWhiteSpace(field.Regex)) Deduct(10, "a számlaszámot nem ellenőrzi minta (regex)");

            // Which readings of the page give the number, and do they agree?
            int agree = 0, disagree = 0;
            bool fromNormalReading = false, fromFallbackRule = false;
            foreach (var page in doc.Pages.Take(2))
            {
                var readings = page.Readings.ToList();
                for (int i = 0; i < readings.Count; i++)
                {
                    var r = FieldReader.ReadFieldDetailed(field, readings[i], settings);
                    if (r.Value == null) continue;
                    if (string.Equals(r.Value, doc.InvoiceNumber, StringComparison.OrdinalIgnoreCase))
                    {
                        agree++;
                        if (i == 0) fromNormalReading = true;
                        if (r.FromFallback) fromFallbackRule = true;
                    }
                    else disagree++;
                }
            }
            if (fromFallbackRule) Deduct(8, "a számlaszámot a sablon tartalék szabálya találta");
            if (!fromNormalReading && agree > 0) Deduct(8, "a számlaszám csak újraolvasás után volt olvasható");
            if (disagree > 0) Deduct(Math.Min(20, 10 + 5 * (disagree - 1)), $"az OCR-olvasatok eltérnek a számlaszámban ({disagree} eltérő)");
            else if (agree >= 3) score = Math.Min(100, score + 5);

            if (field.FixOcrDigits)
            {
                var unrepaired = new FieldDefinition
                {
                    Mode = field.Mode, Region = field.Region, LabelText = field.LabelText, LabelDirection = field.LabelDirection,
                    Regex = field.Regex, Format = field.Format, RemoveSpaces = field.RemoveSpaces, FixOcrDigits = false,
                    ExcludeReceiver = field.ExcludeReceiver, Fallback = field.Fallback,
                };
                var plain = FirstValue(doc, p => InvoiceProcessor.ReadFieldWithFallback(unrepaired, p, settings).Value);
                if (plain != null && !string.Equals(plain, doc.InvoiceNumber, StringComparison.OrdinalIgnoreCase))
                    Deduct(5, "a számlaszámban betűket számjegyre javított");
            }
        }

        // ---- buyer VAT
        if (!doc.VatApproved && doc.Vat.Status == VatStatus.Valid && doc.Vat.Message.Contains(VatValidator.CoreOnlyNote))
            Deduct(5, "a vevő adószámából csak az első 8 számjegy szerepel");
        if (!doc.VatApproved && doc.Vat.Status == VatStatus.Valid && doc.Vat.Message.Contains(VatValidator.OcrDigitNote))
            Deduct(10, "a vevő adószáma egy hibás számjeggyel olvasva");

        // ---- page split
        if (!doc.HasEdit("split") && !doc.HasEdit("merged"))
        {
            switch (doc.StartReason)
            {
                case StartReason.Title: Deduct(5, "az első oldal csak a címe alapján felismerve"); break;
                case StartReason.Receiver: Deduct(3, "az első oldal csak a vevő cég neve alapján felismerve"); break;
                case StartReason.FirstPage when doc.Number > 1: Deduct(5, "az első oldal nincs felismerve"); break;
            }
            if (doc.Pages.Count > 6) Deduct(5, $"{doc.Pages.Count} oldal – több számlát is tartalmazhat");
        }
        if (doc.DuplicateOf != null && doc.DuplicateAccepted) Deduct(5, "duplikált számlaszám elfogadva");

        // ---- dates (a date typed by the user counts as confirmed)
        if (!doc.DatesApproved && doc.DateReading is { } dates)
        {
            var seen = new HashSet<string>();
            foreach (var kind in new[] { DateKind.Issue, DateKind.Fulfillment, DateKind.Due })
            {
                if (doc.HasEdit(InvoiceProcessor.DateEditKey(kind))) continue;
                var finding = dates[kind];
                if (finding.Value == null || DateReader.Format(finding.Value) != doc.GetDate(kind)) continue;
                foreach (var note in finding.Notes)
                {
                    // The same doubt for several dates (e.g. one re-read page) counts once, more when it concerns more dates.
                    int points = note.StartsWith("az OCR-olvasatok eltérnek") ? 8 : note.StartsWith("egydátumos") ? 2 : 5;
                    if (seen.Add(note)) Deduct(points, note);
                    else score -= 1;
                }
            }
            foreach (var (points, reason) in DateReader.Doubts(doc)) Deduct(points, reason);
        }

        // An invoice with errors is never "highly trusted", whatever was read.
        if (doc.BaseErrors.Count > 0) score = Math.Min(score, MediumThreshold - 1);

        doc.Trust = Math.Clamp(score, 0, 100);
        doc.TrustReasons = reasons;
    }

    public const string IssuerByTaxNumberNote = "A kibocsátó az adószáma alapján felismerve.";
    public const string IssuerConfirmedByTaxNumberNote = "A kibocsátót az adószáma megerősíti.";
    public const string IssuerCorrectedByTaxNumberNote = "A kibocsátónév a szállító adószámához tartozó névre cserélve.";

    /// <summary>Lowercase words that are normal in Hungarian company names.</summary>
    private static readonly HashSet<string> LowercaseNameWords = new(StringComparer.Ordinal)
    {
        "és", "es", "kereskedelmi", "szolgáltató", "termelő", "fejlesztő", "tanácsadó", "építőipari", "ipari",
        "korlátolt", "felelősségű", "társaság", "nyilvánosan", "működő", "részvénytársaság", "zártkörűen", "kft", "zrt", "bt", "nyrt", "kkt",
    };

    private static string? FirstValue(InvoiceDocument doc, Func<PageInfo, string?> read)
    {
        foreach (var p in doc.Pages)
            if (read(p) is { Length: > 0 } v) return v;
        return null;
    }
}
