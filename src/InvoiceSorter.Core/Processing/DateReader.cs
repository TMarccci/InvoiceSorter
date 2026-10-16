using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using InvoiceSorter.Core.Models;

namespace InvoiceSorter.Core.Processing;

/// <summary>The three dates of a Hungarian invoice.</summary>
public enum DateKind { Issue, Fulfillment, Due }

/// <summary>A date printed on one reading of a page.</summary>
/// <param name="Repaired">OCR letters were read as digits (O→0, l→1) or a comma as a separator – a little less certain.</param>
/// <param name="InRange">Part of a period ("2026.08.01 - 2026.08.31") – never one of the three dates by itself.</param>
public sealed record DateToken(DateOnly Date, string Text, NormRect Box, int PageNumber, int ReadingIndex, bool Repaired, bool InRange, string LineText)
{
    /// <summary>The words of the date (to keep them out of the label search).</summary>
    internal List<OcrWordBox> Words { get; init; } = new();
}

/// <summary>What one date token is, judged by the label printed before it or above it.</summary>
/// <param name="Kind">The date kind, or null when the token has no date label (or one that excludes it, see <see cref="Excluded"/>).</param>
/// <param name="Candidates">The kinds the labels around the token suggest (more than one: <see cref="Conflict"/>).</param>
public sealed record DateLabelling(DateToken Token, DateKind? Kind, string? Label, NormRect? LabelBox, string How, bool Excluded, bool Conflict,
    IReadOnlyList<DateKind> Candidates)
{
    /// <summary>The date stands in a sentence ("… 2026.12.31-ig tart") – never the single date of a receipt.</summary>
    public bool InText { get; init; }

    /// <summary>The text before the date on its row and the text above it (what the labels were searched in).</summary>
    public string Context { get; init; } = "";
}

/// <summary>The result for one of the three dates.</summary>
/// <param name="Value">Null when the date was not found or could not be decided.</param>
/// <param name="Source">Where it came from, for the user ("„Számla kelte” alatt", "egyetlen dátum a nyugtán" …).</param>
/// <param name="Ambiguous">Several different values were found: left empty on purpose, the user picks one.</param>
/// <param name="Notes">Reasons for less certainty (trust score).</param>
public sealed record DateFinding(DateKind Kind, DateOnly? Value, string Source, DateToken? Token, NormRect? LabelBox, bool Ambiguous,
    IReadOnlyList<string> Notes)
{
    public static DateFinding Missing(DateKind kind) => new(kind, null, "", null, null, false, Array.Empty<string>());
}

/// <summary>All dates found on an invoice and the three that were chosen.</summary>
public sealed class DateReadResult
{
    public required DateFinding Issue { get; init; }
    public required DateFinding Fulfillment { get; init; }
    public required DateFinding Due { get; init; }

    /// <summary>Every date token on the first pages (all readings), with what its label says (for suggestions and the log).</summary>
    public required IReadOnlyList<DateLabelling> Tokens { get; init; }

    /// <summary>The invoice is a till receipt / handwritten / cash invoice, where one printed date is all three.</summary>
    public bool SingleDateDocument { get; init; }

    public DateFinding this[DateKind kind] => kind switch { DateKind.Issue => Issue, DateKind.Fulfillment => Fulfillment, _ => Due };

    public static readonly DateReadResult Empty = new()
    {
        Issue = DateFinding.Missing(DateKind.Issue), Fulfillment = DateFinding.Missing(DateKind.Fulfillment), Due = DateFinding.Missing(DateKind.Due),
        Tokens = Array.Empty<DateLabelling>(),
    };
}

/// <summary>
/// Reads the issue date (kiállítás / számla kelte), the fulfilment date (teljesítés) and the payment due date
/// (fizetési határidő) of an invoice.
///
/// Principle: a date is only filled in when the program is sure. A date belongs to a kind only through its label –
/// printed on the same row right before it ("Számla kelte: 2026.09.02.") or as the column heading above it (the usual
/// "Teljesítés dátuma | Számla kelte | Fizetési határidő" table). Several different values, contradicting labels
/// or readings that disagree leave the field empty (the found dates are offered as suggestions). Only till receipts,
/// handwritten pads and cash / card invoices may print a single date that is then all three.
/// </summary>
public static class DateReader
{
    public static string Name(DateKind kind) => kind switch
    {
        DateKind.Issue => "kiállítás dátuma",
        DateKind.Fulfillment => "teljesítés dátuma",
        _ => "fizetési határidő",
    };

    /// <summary>"2026.09.02." – how the dates are shown, typed and exported.</summary>
    public static string Format(DateOnly date) => date.ToString("yyyy.MM.dd.", CultureInfo.InvariantCulture);

    public static string Format(DateOnly? date) => date is { } d ? Format(d) : "";

    // ------------------------------------------------------------------------------------------ date tokens

    // A digit or a letter OCR reads instead of one (O/o/C → 0, I/l/| → 1, B → 8).
    private const string Dg = "[0-9OoIl|CB]";
    private const string Sep = @"\s{0,2}[.,/\-]\s{0,2}";

    // 2026.09.02. | 2026. 09. 02. | 2026-09-02 | 2026/09/02 | 2026.09,30.   (year first – the Hungarian order).
    // A dash before it is allowed: the end of a period ("2026.07.01-2026.07.31").
    private static readonly Regex YearFirst = new(
        $@"(?<![0-9A-Za-z.,/])(?<y>(?:19|2[0OC])[0-9OC]{{2}}){Sep}(?<m>{Dg}{{1,2}}){Sep}(?<d>{Dg}{{1,2}})(?![0-9])\.?",
        RegexOptions.CultureInvariant);

    // 21.04.2026 | 21/04/2026 | 21-04-2026   (day first – the European order)
    private static readonly Regex DayFirst = new(
        $@"(?<![0-9A-Za-z.,/\-])(?<d>{Dg}{{1,2}}){Sep}(?<m>{Dg}{{1,2}}){Sep}(?<y>(?:19|20)[0-9O]{{2}})(?![0-9])\.?",
        RegexOptions.CultureInvariant);

    // 2026 08 28 – the dots were lost by the OCR (faint print). Only the strict form: two-digit month and day.
    private static readonly Regex YearFirstSpaced = new(
        @"(?<![0-9A-Za-z.,/])(?<y>20\d{2}) {1,2}(?<m>0[1-9]|1[0-2]) {1,2}(?<d>0[1-9]|[12]\d|3[01])(?![0-9.,:/])",
        RegexOptions.CultureInvariant);

    // 2026. szeptember 2. | 2026. szept. 02.   (matched on the accent-free, lower-case text)
    private static readonly Regex YearMonthName = new(
        @"(?<![0-9a-z])(?<y>(?:19|20)\d{2})\.?\s{0,2}(?<mn>januar|februar|marcius|aprilis|majus|junius|julius|augusztus|szeptember|oktober|november|december|jan|febr?|marc?|apr|maj|jun|jul|aug|szept?|okt|nov|dec)\.?\s{0,2}(?<d>\d{1,2})(?![0-9])\.?",
        RegexOptions.CultureInvariant);

    // 02 Sep 2026 | 2 September 2026
    private static readonly Regex DayMonthNameYear = new(
        @"(?<![0-9a-z])(?<d>\d{1,2})\.?\s{1,2}(?<mn>january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sept?|oct|nov|dec)\.?\s{1,2}(?<y>(?:19|20)\d{2})(?![0-9])",
        RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, int> MonthNames = new()
    {
        ["januar"] = 1, ["jan"] = 1, ["januari"] = 1, ["january"] = 1,
        ["februar"] = 2, ["febr"] = 2, ["feb"] = 2, ["february"] = 2,
        ["marcius"] = 3, ["marc"] = 3, ["mar"] = 3, ["march"] = 3,
        ["aprilis"] = 4, ["apr"] = 4, ["april"] = 4,
        ["majus"] = 5, ["maj"] = 5, ["may"] = 5,
        ["junius"] = 6, ["jun"] = 6, ["june"] = 6,
        ["julius"] = 7, ["jul"] = 7, ["july"] = 7,
        ["augusztus"] = 8, ["aug"] = 8, ["august"] = 8,
        ["szeptember"] = 9, ["szept"] = 9, ["szep"] = 9, ["sep"] = 9, ["sept"] = 9, ["september"] = 9,
        ["oktober"] = 10, ["okt"] = 10, ["oct"] = 10, ["october"] = 10,
        ["november"] = 11, ["nov"] = 11,
        ["december"] = 12, ["dec"] = 12,
    };

    // A period: "2026.08.01 - 2026.08.31", "2026.08.01-től 2026.08.31-ig".
    private static readonly Regex RangeDash = new(@"^\s{0,3}(?:[-–—~]|tol|től|-tol|-től)", RegexOptions.CultureInvariant);

    /// <summary>The digits of an OCR-damaged number part (O → 0, l/I/| → 1).</summary>
    private static string Digits(string s, ref bool repaired)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            char d = c switch { 'O' or 'o' or 'C' => '0', 'I' or 'l' or '|' => '1', 'B' => '8', _ => c };
            if (d != c) repaired = true;
            sb.Append(d);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Month and day as printed: two digits each, or a one-digit month ("2026. 9. 12."). A one-digit day after a
    /// two-digit month is a damaged date ("2026.08.1" – the "9" of "19" covered by a tick mark) and is not used; nor is
    /// a part that consists only of letters read as digits.
    /// </summary>
    private static bool WellFormed(string rawMonth, string rawDay, string month, string day)
    {
        if (month.Length == 2 && day.Length == 1) return false;
        // A single letter as the month or the day is the scrap of a damaged number ("2026.08. I"); "IO" for 10 is fine.
        static bool Scrap(string raw) => raw.Length == 1 && !char.IsDigit(raw[0]);
        return !Scrap(rawMonth) && !Scrap(rawDay);
    }

    /// <summary>Earliest and latest year accepted at all (a year outside is OCR noise or not a date).</summary>
    private static bool PlausibleYear(int year) => year >= 2000 && year <= DateTime.Today.Year + 2;

    /// <summary>Every date printed on one reading of a page, with its position.</summary>
    public static List<DateToken> FindDates(PageInfo reading, int pageNumber, int readingIndex)
    {
        var result = new List<DateToken>();
        foreach (var line in reading.Lines)
        {
            if (line.Words.Count == 0) continue;
            // Line text with the character range of every word.
            var sb = new StringBuilder();
            var spans = new List<(int Start, int End, OcrWordBox Word)>();
            foreach (var w in line.Words)
            {
                if (sb.Length > 0) sb.Append(' ');
                int start = sb.Length;
                sb.Append(w.Text);
                spans.Add((start, sb.Length, w));
            }
            var text = sb.ToString();
            var folded = TextUtil.Fold(text);
            bool sameLength = folded.Length == text.Length;

            var found = new List<(int Start, int End, DateOnly Date, bool Repaired)>();
            void Add(Match m, int y, int mo, int d, bool repaired)
            {
                if (!PlausibleYear(y) || mo < 1 || mo > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo)) return;
                if (found.Any(f => m.Index < f.End && f.Start < m.Index + m.Length)) return;   // already found by another pattern
                found.Add((m.Index, m.Index + m.Length, new DateOnly(y, mo, d), repaired));
            }

            foreach (var regex in new[] { YearFirst, DayFirst })
            foreach (Match m in regex.Matches(text))
            {
                bool repaired = m.Value.Contains(',');
                var y = Digits(m.Groups["y"].Value, ref repaired);
                var mo = Digits(m.Groups["m"].Value, ref repaired);
                var d = Digits(m.Groups["d"].Value, ref repaired);
                if (regex == DayFirst && d.Length == 1 && mo.Length == 2) continue;   // "1.08.2026": damaged
                if (regex == YearFirst && !WellFormed(m.Groups["m"].Value, m.Groups["d"].Value, mo, d)) continue;
                Add(m, int.Parse(y), int.Parse(mo), int.Parse(d), repaired);
            }
            foreach (Match m in YearFirstSpaced.Matches(text))
                Add(m, int.Parse(m.Groups["y"].Value), int.Parse(m.Groups["m"].Value), int.Parse(m.Groups["d"].Value), true);
            if (sameLength)
            {
                foreach (var regex in new[] { YearMonthName, DayMonthNameYear })
                foreach (Match m in regex.Matches(folded))
                    if (MonthNames.TryGetValue(m.Groups["mn"].Value, out var month))
                        Add(m, int.Parse(m.Groups["y"].Value), month, int.Parse(m.Groups["d"].Value), false);
            }

            found.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (int i = 0; i < found.Count; i++)
            {
                var f = found[i];
                var words = spans.Where(s => s.Start < f.End && f.Start < s.End).Select(s => s.Word).ToList();
                if (words.Count == 0) continue;
                // A period: a dash right after this date and another date, or a dash between the previous date and this one.
                bool rangeStart = i + 1 < found.Count && RangeDash.IsMatch(folded[f.End..found[i + 1].Start]);
                bool rangeEnd = i > 0 && RangeDash.IsMatch(folded[found[i - 1].End..f.Start]);
                bool openRange = RangeDash.IsMatch(folded[f.End..]) && folded[f.End..].TrimStart(' ', '-', '–', '—').Length == 0;
                result.Add(new DateToken(f.Date, text[f.Start..f.End].Trim(), Union(words), pageNumber, readingIndex, f.Repaired,
                    rangeStart || rangeEnd || openRange, text) { Words = words });
            }
        }
        return result;
    }

    // ------------------------------------------------------------------------------------------ labels

    private sealed record LabelMatch(DateKind? Kind, string Label, List<OcrWordBox> Words, int Distance)
    {
        public NormRect Box => Union(Words);
        public bool Excludes => Kind == null;
    }

    private static List<(DateKind? Kind, string Label, string Compact)> Labels(AppSettings settings)
    {
        var list = new List<(DateKind?, string, string)>();
        void AddAll(IEnumerable<string> labels, DateKind? kind)
        {
            foreach (var l in labels)
            {
                var c = TextUtil.Compact(l);
                if (c.Length >= 3) list.Add((kind, l.Trim(), c));
            }
        }
        AddAll(settings.IssueDateLabels, DateKind.Issue);
        AddAll(settings.FulfillmentDateLabels, DateKind.Fulfillment);
        AddAll(settings.DueDateLabels, DateKind.Due);
        AddAll(settings.NotDateLabels, null);
        return list;
    }

    /// <summary>
    /// Every label in a run of words (reading order). A label may be split over several words or OCR lines
    /// ("Számla" / "kelte"); where labels overlap, the longest wins ("Teljesítési időszak" over "Teljesítés").
    /// </summary>
    private static List<LabelMatch> MatchLabels(IReadOnlyList<OcrWordBox> input, List<(DateKind? Kind, string Label, string Compact)> labels, AppSettings settings)
    {
        // Bilingual labels are often glued by a slash into one OCR word ("kelte/Invoice"): split them.
        // The match refers to the original words (they are compared with other word lists by reference).
        var words = new List<OcrWordBox>();
        var origin = new List<OcrWordBox>();
        foreach (var w in input)
        {
            var parts = w.Text.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) { words.Add(w); origin.Add(w); continue; }
            foreach (var part in parts) { words.Add(new OcrWordBox { Text = part, X = w.X, Y = w.Y, W = w.W, H = w.H }); origin.Add(w); }
        }
        List<OcrWordBox> Originals(int from, int to) => origin.Skip(from).Take(to - from + 1).Distinct().ToList();
        var compact = words.Select(w => TextUtil.Compact(w.Text)).ToList();
        var candidates = new List<(LabelMatch Match, int From, int To, int Length)>();
        for (int i = 0; i < words.Count; i++)
        {
            if (compact[i].Length == 0) continue;
            var acc = "";
            for (int j = i; j < words.Count && j < i + 6; j++)
            {
                acc += compact[j];
                // A value glued to the label ("Kelt:2026.09.02") does not count.
                var cmp = acc.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                if (cmp.Length == 0) break;
                foreach (var (kind, label, target) in labels)
                {
                    // OCR damages a label by a character or two; more would allow other words ("napja" for "dátuma").
                    int allowed = Math.Min(target.Length >= 15 ? 3 : 2, TextUtil.AllowedErrors(target.Length, settings.FuzzyTolerance));
                    if (Math.Abs(cmp.Length - target.Length) > allowed) continue;
                    int d = TextUtil.Levenshtein(cmp, target);
                    if (d > allowed) continue;
                    // Exact matches beat longer fuzzy ones ("Fizetési határidő" over "Befizetési határidő"), longer
                    // exact ones beat shorter ones ("Teljesítési időszak" over "Teljesítés").
                    candidates.Add((new LabelMatch(kind, label, Originals(i, j), d), i, j, target.Length - 2 * d));
                }
                if (acc.Length > 40) break;
            }
        }
        // Longest label first; then fewest errors. Overlapping shorter matches are dropped.
        var chosen = new List<(LabelMatch Match, int From, int To)>();
        foreach (var c in candidates.OrderByDescending(c => c.Length).ThenBy(c => c.Match.Distance).ThenByDescending(c => c.To - c.From))
            if (!chosen.Any(x => c.From <= x.To && x.From <= c.To)) chosen.Add((c.Match, c.From, c.To));
        return chosen.OrderBy(c => c.From).Select(c => c.Match).ToList();
    }

    private static double RowTolerance(OcrWordBox w) => Math.Max(w.H, 0.002) * 0.6;

    /// <summary>
    /// The label printed on the same row right before the date: the words between the previous date of the row
    /// (or the start of the row) and this date; the label must be the last thing before the date (at most three
    /// short words after it, e.g. "/ Invoice date:").
    /// </summary>
    private static LabelMatch? LabelLeft(DateToken token, List<OcrWordBox> all, List<DateToken> rowDates, List<(DateKind?, string, string)> labels, AppSettings settings,
        out string context)
    {
        context = "";
        var first = token.Words.MinBy(w => w.X)!;
        double cy = token.Box.Y + token.Box.H / 2, tol = RowTolerance(first);
        double limitX = rowDates.Where(d => d != token && d.Box.X + d.Box.W <= token.Box.X + 0.002 && Math.Abs(d.Box.Y + d.Box.H / 2 - cy) < tol)
                                .Select(d => d.Box.X + d.Box.W).DefaultIfEmpty(-1).Max();
        var left = all.Where(w => !token.Words.Contains(w) && Math.Abs(w.CenterY - cy) < tol && w.X + w.W <= token.Box.X + 0.004 && w.X >= limitX - 0.001)
                      .OrderBy(w => w.X).ToList();
        if (left.Count == 0) return null;
        context = string.Join(" ", left.Select(w => w.Text));
        var matches = MatchLabels(left, labels, settings);
        if (matches.Count == 0) return null;
        var last = matches[^1];
        int lastIndex = left.IndexOf(last.Words[^1]);
        var after = left.Skip(lastIndex + 1).ToList();
        if (after.Count > 3 || after.Any(w => w.Text.Count(char.IsDigit) >= 2)) return null;
        return last;
    }

    /// <summary>
    /// The column heading above the date: the closest rows above that overlap the date horizontally, up to the
    /// previous row of values (a row with a date or a number). The heading nearest to the date's centre wins.
    /// </summary>
    /// <returns>The nearest heading and every date kind of the headings about as near (more than one: undecidable).</returns>
    private static (LabelMatch? Match, List<DateKind> Kinds) LabelAbove(DateToken token, List<OcrWordBox> all, List<DateToken> pageDates,
        List<(DateKind?, string, string)> labels, AppSettings settings, double aspect, out string context)
    {
        context = "";
        double h = Math.Max(token.Words.Average(w => w.H), 0.002);
        double x0 = token.Box.X - 0.03, x1 = token.Box.X + token.Box.W + 0.03, cx = token.Box.X + token.Box.W / 2;
        double top = token.Box.Y - h * 6;
        var dateWords = pageDates.SelectMany(d => d.Words).ToHashSet();

        var above = all.Where(w => w.Y + w.H <= token.Box.Y + h * 0.2 && w.CenterY >= top).ToList();
        // Rows above, nearest first.
        var rows = new List<List<OcrWordBox>>();
        foreach (var w in above.OrderByDescending(w => w.CenterY))
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r[0].CenterY - w.CenterY) < RowTolerance(w));
            if (row != null) row.Add(w); else rows.Add(new() { w });
        }
        var heading = new List<OcrWordBox>();
        int used = 0;
        foreach (var row in rows)
        {
            var inColumn = row.Where(w => w.X < x1 && w.X + w.W > x0).ToList();
            if (inColumn.Count == 0) { if (heading.Count > 0) break; continue; }
            // A row of values (another date, an amount, an account number) ends the heading.
            if (inColumn.Any(w => dateWords.Contains(w) || w.Text.Count(char.IsDigit) >= 3)) break;
            // The heading cell may be wider than the date: neighbouring words of the same cell are added.
            var cell = row.OrderBy(w => w.X).ToList();
            int lo = cell.IndexOf(inColumn.MinBy(w => w.X)!), hi = cell.IndexOf(inColumn.MaxBy(w => w.X)!);
            double gap = h * aspect * 1.6;
            while (lo > 0 && cell[lo].X - (cell[lo - 1].X + cell[lo - 1].W) < gap) lo--;
            while (hi < cell.Count - 1 && cell[hi + 1].X - (cell[hi].X + cell[hi].W) < gap) hi++;
            heading.InsertRange(0, cell.GetRange(lo, hi - lo + 1));
            if (++used >= 3) break;
        }
        if (heading.Count == 0) return (null, new());
        // Reading order: rows top to bottom, words left to right.
        var ordered = heading.OrderBy(w => Math.Round(w.CenterY / Math.Max(h * 0.8, 0.001))).ThenBy(w => w.X).ToList();
        context = string.Join(" ", ordered.Select(w => w.Text));
        // "Teljesítés kelte: 2026.08.19" – a label with a colon followed by a value on its own row labels that value
        // (a list of "label: value" rows); it is not the heading of the column below. A colon heading followed by
        // the next heading ("Számla kelte:  Teljesítés:  Fizetési határidő:") stays a heading.
        bool KeyValue(LabelMatch m)
        {
            var last = m.Words[^1];
            if (!last.Text.TrimEnd().EndsWith(':')) return false;
            var next = all.Where(w => w.X >= last.X + last.W - 0.002 && Math.Abs(w.CenterY - last.CenterY) < RowTolerance(last) && w.X - (last.X + last.W) < 0.2)
                          .MinBy(w => w.X);
            // A value: digits, or a short scrap of one ("I" – the rest covered by a tick mark); not the article of
            // the next heading ("A számla kelte:").
            return next != null && (next.Text.Any(char.IsDigit) ||
                                    (next.Text.Count(char.IsLetter) <= 2 && next.Text.Trim('.', ',') is not ("A" or "Az" or "a" or "az")));
        }
        // The heading must stand over the date: a heading of the neighbouring column (the date's own heading not read
        // by the OCR) must not label it.
        bool Over(LabelMatch m)
        {
            var b = m.Box;
            double overlap = Math.Min(b.X + b.W, token.Box.X + token.Box.W) - Math.Max(b.X, token.Box.X);
            return overlap >= Math.Min(b.W, token.Box.W) * 0.25;
        }
        var matches = MatchLabels(ordered, labels, settings).Where(m => !KeyValue(m) && Over(m)).ToList();
        if (matches.Count == 0) return (null, new());
        var byDistance = matches.Select(m => (m, d: Math.Abs(m.Box.X + m.Box.W / 2 - cx))).OrderBy(x => x.d).ToList();
        // Headings about as close as the nearest one all count (two different ones: the date cannot be assigned).
        var close = byDistance.Where(x => x.d - byDistance[0].d < 0.03).Select(x => x.m).ToList();
        return (close[0], close.Select(m => m.Kind).OfType<DateKind>().Distinct().ToList());
    }

    /// <summary>What the labels say about every date token of a reading.</summary>
    /// <summary>Height / width of the page image: converts a line height (y units) to x units.</summary>
    private static double Aspect(PageInfo reading) =>
        reading.PixelWidth > 0 && reading.PixelHeight > 0 ? reading.PixelHeight / (double)reading.PixelWidth : Math.Sqrt(2);

    private static readonly Regex TextSuffix = new(@"\d\.?\s*-?\s*(?:ig|tol|től|tól|án|én|jén|ján|ától|étől|napjával|napjáig|-i|-ei|-ai)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The date stands in running text ("… a 2026.08.31-ig beérkezett befizetéseket …"), not in a table cell or after
    /// a label: a lower-case word right next to it, or a suffix glued to it.
    /// </summary>
    private static bool InRunningText(DateToken token, List<OcrWordBox> all, double aspect)
    {
        if (TextSuffix.IsMatch(token.Text) || TextSuffix.IsMatch(string.Join(" ", token.Words.Select(w => w.Text)))) return true;
        double h = Math.Max(token.Words.Average(w => w.H), 0.002), hx = h * aspect;
        double cy = token.Box.Y + token.Box.H / 2, tol = RowTolerance(token.Words[0]);
        var row = all.Where(w => !token.Words.Contains(w) && Math.Abs(w.CenterY - cy) < tol).ToList();
        var leftN = row.Where(w => w.X + w.W <= token.Box.X + 0.002 && token.Box.X - (w.X + w.W) < hx * 1.2).MaxBy(w => w.X);
        var rightN = row.Where(w => w.X >= token.Box.X + token.Box.W - 0.002 && w.X - (token.Box.X + token.Box.W) < hx * 1.2).MinBy(w => w.X);
        static bool Lower(OcrWordBox? w) => w != null && w.Text.Count(char.IsLetter) >= 2 && char.IsLower(w.Text.First(char.IsLetter))
                                            && !w.Text.TrimEnd().EndsWith(':');
        return Lower(leftN) || Lower(rightN);
    }

    private static List<DateLabelling> Label(PageInfo reading, List<DateToken> tokens, List<(DateKind?, string, string)> labels, AppSettings settings)
    {
        var all = reading.Lines.SelectMany(l => l.Words).ToList();
        double aspect = Aspect(reading);
        var result = new List<DateLabelling>();
        foreach (var t in tokens)
        {
            var left = LabelLeft(t, all, tokens, labels, settings, out var leftText);
            string aboveText = "";
            // A column heading belongs to the values of a table, not to a date in a sentence below it.
            bool inText = InRunningText(t, all, aspect);
            var (above, aboveKinds) = inText ? (null, new List<DateKind>()) : LabelAbove(t, all, tokens, labels, settings, aspect, out aboveText);
            int before = result.Count;
            // A label that excludes the date ("Teljesítési időszak", "Nyitóegyenleg", "Szerződés" …).
            if ((left != null && left.Excludes) || (left == null && above != null && above.Excludes && aboveKinds.Count == 0))
            {
                var ex = left ?? above!;
                result.Add(new DateLabelling(t, null, ex.Label, ex.Box, left != null ? "sor" : "oszlop", true, false, Array.Empty<DateKind>())
                    { InText = inText, Context = $"{leftText} ⟂ {aboveText}" });
                continue;
            }
            // Every kind the label before the date and the heading(s) above it suggest; exactly one is needed.
            var kinds = new List<DateKind>();
            if (left?.Kind is { } lk) kinds.Add(lk);
            kinds.AddRange(aboveKinds);
            kinds = kinds.Distinct().ToList();
            if (kinds.Count == 1)
            {
                var from = left?.Kind != null ? left : above!;
                result.Add(new DateLabelling(t, kinds[0], from.Label, from.Box, from == left ? "sor" : "oszlop", false, false, kinds));
            }
            else if (kinds.Count > 1)
            {
                var from = left ?? above;
                result.Add(new DateLabelling(t, null, from?.Label, from?.Box, from == left ? "sor" : "oszlop", false, true, kinds));
            }
            else
            {
                result.Add(new DateLabelling(t, null, null, null, "", false, false, kinds));
            }
            if (result.Count > before) result[^1] = result[^1] with { InText = inText, Context = $"{leftText} ⟂ {aboveText}" };
        }
        return result;
    }

    // ------------------------------------------------------------------------------------------ the invoice

    /// <summary>Reads the three dates of an invoice from all its readings.</summary>
    public static DateReadResult Read(InvoiceDocument doc, AppSettings settings)
    {
        if (doc.Pages.Count == 0) return DateReadResult.Empty;
        var labels = Labels(settings);
        var tokens = new List<DateLabelling>();
        // The dates belong to the first page; later pages are read too (some layouts print the payment terms on the last page).
        var pages = doc.Pages.Where(p => !p.IsBlank).Take(4).ToList();
        foreach (var page in pages)
        {
            var readings = page.Readings.ToList();
            for (int r = 0; r < readings.Count; r++)
            {
                var found = FindDates(readings[r], page.Number, r);
                tokens.AddRange(Label(readings[r], found, labels, settings));
            }
        }

        var single = IsSingleDateDocument(doc, settings);
        var findings = new Dictionary<DateKind, DateFinding>();
        foreach (var kind in new[] { DateKind.Issue, DateKind.Fulfillment, DateKind.Due })
            findings[kind] = Decide(kind, doc, tokens, settings);

        // Till receipts, handwritten pads, cash / card invoices: one printed date is all three.
        if (single)
        {
            var labelled = findings.Values.Where(f => f.Value != null).Select(f => f.Value!.Value).Distinct().ToList();
            bool anyAmbiguous = findings.Values.Any(f => f.Ambiguous);
            if (!anyAmbiguous && labelled.Count == 1)
            {
                // e.g. "Kelt: 2026.09.02." on a cash invoice: the missing ones are the same day.
                var date = labelled[0];
                var from = findings.Values.First(f => f.Value != null);
                foreach (var kind in findings.Keys.ToList().Where(k => findings[k].Value == null))
                    findings[kind] = new DateFinding(kind, date, $"azonos a(z) {Name(from.Kind)} értékével ({SingleDateReason(doc, settings)})",
                        from.Token, from.LabelBox, false, new[] { "egydátumos számla: a hiányzó dátum azonos a kiírttal" });
            }
            else if (!anyAmbiguous && labelled.Count == 0)
            {
                var candidates = tokens.Where(t => t.Token.PageNumber == pages[0].Number && !t.Excluded && !t.Token.InRange && !t.InText).ToList();
                var distinct = candidates.Select(t => t.Token.Date).Distinct().ToList();
                if (distinct.Count == 1)
                {
                    var t = candidates.OrderBy(c => c.Token.ReadingIndex).First();
                    var notes = new List<string> { "egydátumos számla: az egyetlen kiírt dátum mindhárom" };
                    if (t.Token.Repaired) notes.Add("a dátumban OCR-javítás történt (O→0, l→1 vagy vessző)");
                    if (candidates.All(c => c.Token.ReadingIndex > 0)) notes.Add("a dátum csak újraolvasás után volt olvasható");
                    foreach (var kind in findings.Keys.ToList())
                        findings[kind] = new DateFinding(kind, t.Token.Date, $"egyetlen dátum ({SingleDateReason(doc, settings)})", t.Token, null, false, notes);
                }
                else if (distinct.Count > 1)
                {
                    foreach (var kind in findings.Keys.ToList())
                        findings[kind] = new DateFinding(kind, null, "", null, null, true, Array.Empty<string>());
                }
            }
        }

        return new DateReadResult
        {
            Issue = findings[DateKind.Issue], Fulfillment = findings[DateKind.Fulfillment], Due = findings[DateKind.Due],
            Tokens = tokens, SingleDateDocument = single,
        };
    }

    /// <summary>One kind: the labelled tokens of the first page that has any, checked for agreement across the readings.</summary>
    private static DateFinding Decide(DateKind kind, InvoiceDocument doc, List<DateLabelling> tokens, AppSettings settings)
    {
        // A date the style reads explicitly wins (layouts where the generic labels fail).
        if (StyleField(doc.Style, kind) is { } field && StyleValue(doc, field, settings) is { } styled)
            return new DateFinding(kind, styled.Date, $"„{doc.Style!.Name}” sablon", styled, null, false,
                styled.Repaired ? new[] { "a dátumban OCR-javítás történt (O→0, l→1 vagy vessző)" } : Array.Empty<string>());

        var mine = tokens.Where(t => t.Kind == kind && !t.Token.InRange).ToList();
        var conflicting = tokens.Where(t => t.Conflict && !t.Token.InRange && t.Candidates.Contains(kind)).ToList();
        if (mine.Count == 0) return conflicting.Count > 0 ? new DateFinding(kind, null, "", null, null, true, Array.Empty<string>()) : DateFinding.Missing(kind);

        int firstPage = mine.Min(t => t.Token.PageNumber);
        var onPage = mine.Where(t => t.Token.PageNumber == firstPage).ToList();
        var notes = new List<string>();
        if (firstPage != doc.Pages[0].Number) notes.Add($"a dátum csak a(z) {firstPage}. oldalon szerepel");

        // The normal reading decides; re-reads must not contradict it. Without a normal reading the re-reads must agree.
        var normal = onPage.Where(t => t.Token.ReadingIndex == 0).Select(t => t.Token.Date).Distinct().ToList();
        var others = onPage.Where(t => t.Token.ReadingIndex > 0).Select(t => t.Token.Date).ToList();
        DateOnly value;
        if (normal.Count > 1)
            return new DateFinding(kind, null, "", null, null, true, Array.Empty<string>());   // the label is printed twice with different dates
        if (normal.Count == 1)
        {
            value = normal[0];
            int against = others.Count(d => d != value), with = others.Count(d => d == value);
            if (against > 0 && against >= with + 1)
                return new DateFinding(kind, null, "", null, null, true, Array.Empty<string>());   // the readings disagree
            if (against > 0) notes.Add("az OCR-olvasatok eltérnek a dátumban (a többség egyezik)");
        }
        else
        {
            var groups = others.GroupBy(d => d).OrderByDescending(g => g.Count()).ToList();
            if (groups.Count > 1 && groups[0].Count() == groups[1].Count())
                return new DateFinding(kind, null, "", null, null, true, Array.Empty<string>());
            value = groups[0].Key;
            // Not in the normal reading: at least two re-reads must show this date with this label (one damaged
            // re-read turned "Költ.:" into "Kelt").
            var v = value;
            if (onPage.Where(t => t.Token.ReadingIndex > 0 && t.Token.Date == v).Select(t => t.Token.ReadingIndex).Distinct().Count() < 2)
                return DateFinding.Missing(kind);
            notes.Add("a dátum csak újraolvasás után volt olvasható");
            if (groups.Count > 1) notes.Add("az OCR-olvasatok eltérnek a dátumban (a többség egyezik)");
        }

        // A date whose labels contradict each other and that may be this one, with another value: the user decides.
        if (conflicting.Any(t => t.Token.PageNumber == firstPage && t.Token.Date != value))
            return new DateFinding(kind, null, "", null, null, true, Array.Empty<string>());

        var chosen = onPage.Where(t => t.Token.Date == value).OrderBy(t => t.Token.ReadingIndex).First();
        if (onPage.Where(t => t.Token.Date == value).All(t => t.Token.Repaired)) notes.Add("a dátumban OCR-javítás történt (O→0, l→1 vagy vessző)");
        var where = chosen.How == "oszlop" ? "alatt" : "mellett";
        return new DateFinding(kind, value, $"„{chosen.Label}” {where}", chosen.Token, chosen.LabelBox, false, notes);
    }

    public static FieldDefinition? StyleField(InvoiceStyle? style, DateKind kind) => style == null ? null : kind switch
    {
        DateKind.Issue => style.IssueDate,
        DateKind.Fulfillment => style.FulfillmentDate,
        _ => style.DueDate,
    };

    /// <summary>The date a style's own field rule reads (the first date in the value it finds).</summary>
    private static DateToken? StyleValue(InvoiceDocument doc, FieldDefinition field, AppSettings settings)
    {
        foreach (var page in doc.Pages.Take(2))
        {
            var r = InvoiceProcessor.ReadFieldWithFallback(field, page, settings);
            if (r.Value == null) continue;
            var fake = new PageInfo { Index = page.Index, Lines = new() { new OcrTextLine { Words = new() { new OcrWordBox { Text = r.Value, W = 0.1, H = 0.01 } } } } };
            var date = FindDates(fake, page.Number, 0).FirstOrDefault();
            if (date != null) return date with { Box = r.ValueBox ?? date.Box };
        }
        return null;
    }

    /// <summary>
    /// Till receipt, handwritten invoice pad, or paid on the spot (cash, bank card): such invoices may print a single
    /// date, which is then the issue, fulfilment and due date at once.
    /// </summary>
    public static bool IsSingleDateDocument(InvoiceDocument doc, AppSettings settings) => SingleDateReason(doc, settings) != null;

    /// <summary>Payment method / terms labels (compact form), longest first.</summary>
    private static readonly string[] PaymentLabels =
        { "fizetesifeltetelek", "fizetesifeltetel", "fizetesimodja", "fizetesmodja", "fizetesimod", "fizetesmod", "paymentmethod", "fizetes" };

    /// <summary>Why the invoice counts as a single-date invoice (null: it does not) – for the log and the review.</summary>
    public static string? SingleDateWhy(InvoiceDocument doc, AppSettings settings) => SingleDateReason(doc, settings);

    private static string? SingleDateReason(InvoiceDocument doc, AppSettings settings)
    {
        if (doc.Pages.Count == 0) return null;
        var first = doc.Pages[0];
        if (doc.Style?.Id is "nav-penztargep") return "pénztárgépes nyugta";
        if (doc.Style?.Id is "fugea") return "kézi számlatömb";
        if (FieldReader.IsNarrowPage(first)) return "nyugta";
        // The payment method – not any mention ("készpénzes befizetés esetén …" in the small print of a transfer
        // invoice): a line that starts with the keyword (the payment method cell of a table, a title such as
        // "Készpénzfizetési számla"), or a payment method label followed by it.
        var keywords = settings.SingleDateKeywords.Select(k => (Text: k.Trim(), Compact: TextUtil.Compact(k))).Where(k => k.Compact.Length >= 2).ToList();
        foreach (var reading in first.Readings)
        foreach (var line in reading.Lines)
        {
            if (line.Words.Count == 0) continue;
            var compactLine = TextUtil.Compact(line.Text);
            foreach (var (text, k) in keywords)
            {
                // The first word(s) of the line must be the keyword – whole words ("Készpénzátutalási megbízás", the
                // postal cheque of a transfer invoice, is not "Készpénz").
                int words = Math.Max(1, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
                var start = TextUtil.Compact(string.Concat(line.Words.Take(words).Select(w => w.Text)));
                int allowed = Math.Min(2, TextUtil.AllowedErrors(k.Length, settings.FuzzyTolerance));
                // …and not the start of a sentence ("Készpénzes befizetés esetén …"): no lower-case word right after it.
                bool sentence = line.Words.Skip(words).FirstOrDefault() is { } next &&
                                next.Text.Count(char.IsLetter) >= 3 && char.IsLower(next.Text.First(char.IsLetter));
                bool starts = !sentence && start.Length > 0 && TextUtil.Levenshtein(start, k) <= allowed;
                bool afterLabel = false;
                if (k.Length >= 5)
                {
                    // "Fizetés módja: Készpénz", "Fizetés: Azonnal", "Fizetési feltétel: utánvét" – right after the label.
                    foreach (var label in PaymentLabels)
                    {
                        int i = compactLine.IndexOf(label, StringComparison.Ordinal);
                        if (i >= 0 && compactLine.Length >= i + label.Length + k.Length &&
                            TextUtil.Levenshtein(compactLine.Substring(i + label.Length, k.Length), k) <= allowed)
                            afterLabel = true;
                    }
                }
                if (starts || afterLabel) return text.ToLowerInvariant();
            }
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------ typed values

    /// <summary>
    /// A date typed by the user or read by the program: "2026.09.02.", "2026. 09. 02.", "2026-09-02", "20260902",
    /// "02.09.2026", "2026. szeptember 2." … Null when it is not a valid date.
    /// </summary>
    public static DateOnly? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (Regex.IsMatch(t, @"^\d{8}$") && DateOnly.TryParseExact(t, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var compact))
            return PlausibleYear(compact.Year) ? compact : null;
        var page = new PageInfo { Lines = new() { new OcrTextLine { Words = new() { new OcrWordBox { Text = t, W = 0.1, H = 0.01 } } } } };
        var found = FindDates(page, 1, 0);
        // The whole text must be the date (not "2026.09.02 abc").
        if (found.Count != 1) return null;
        var rest = t.Replace(found[0].Text, "").Trim(' ', '.');
        return rest.Length == 0 ? found[0].Date : null;
    }

    /// <summary>A typed or read value in the normal form "2026.09.02."; unchanged when it is not a date (the error shows it).</summary>
    public static string Normalize(string? text) => Parse(text) is { } d ? Format(d) : (text ?? "").Trim();

    // ------------------------------------------------------------------------------------------ checks

    /// <summary>
    /// Problems with the dates of an invoice (errors in the review): missing, not decidable, not a date, a due date
    /// before the issue date, an issue date in the future.
    /// </summary>
    public static List<string> Problems(InvoiceDocument doc)
    {
        var problems = new List<string>();
        if (doc.Excluded || doc.DatesApproved) return problems;
        var missing = new List<string>();
        var ambiguous = new List<string>();
        foreach (var kind in new[] { DateKind.Issue, DateKind.Fulfillment, DateKind.Due })
        {
            var text = doc.GetDate(kind);
            if (string.IsNullOrWhiteSpace(text))
            {
                if (doc.DateReading?[kind].Ambiguous == true) ambiguous.Add(Name(kind));
                else missing.Add(Name(kind));
            }
            else if (Parse(text) == null) problems.Add($"Érvénytelen dátum ({Name(kind)}): „{text}”");
        }
        if (ambiguous.Count > 0)
            problems.Add($"Nem dönthető el egyértelműen: {string.Join(", ", ambiguous)} – több eltérő dátum szerepel, válassz a javaslatok közül");
        if (missing.Count > 0 && doc.DatesRequired)
            problems.Add($"Hiányzó dátum: {string.Join(", ", missing)} – írd be, vagy válassz a javaslatok közül");

        var issue = Parse(doc.IssueDate);
        var due = Parse(doc.DueDate);
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (issue is { } i && i > today.AddDays(3))
            problems.Add($"A kiállítás dátuma ({Format(i)}) a jövőben van – ellenőrizd");
        // A wrong digit in the year ("2020" read for a faint "2026") gives a date far from the others or from today.
        var all = new[] { DateKind.Issue, DateKind.Fulfillment, DateKind.Due }
            .Select(k => (Kind: k, Date: Parse(doc.GetDate(k)))).Where(x => x.Date != null).Select(x => (x.Kind, Date: x.Date!.Value)).ToList();
        foreach (var (kind, date) in all)
            if (date < today.AddYears(-2) || date > today.AddYears(1))
                problems.Add($"Valószínűtlen dátum ({Name(kind)}): {Format(date)} – túl régi vagy túl késői, lehet OCR-hiba; ellenőrizd");
        if (all.Count >= 2 && all.Max(x => x.Date.DayNumber) - all.Min(x => x.Date.DayNumber) > 400)
            problems.Add($"A dátumok között több mint egy év van ({string.Join(", ", all.Select(x => Format(x.Date)))}) – valószínűleg OCR-hiba; ellenőrizd");
        if (issue is { } i2 && due is { } d2 && d2 < i2)
            problems.Add($"A fizetési határidő ({Format(d2)}) korábbi, mint a kiállítás dátuma ({Format(i2)}) – ellenőrizd (ha így van: Dátumok elfogadva)");
        return problems;
    }

    // "Átutalás (8 nap)", "Átutalás 8 nap", "Fizetési feltétel: 15 nap" – the number right after the payment label.
    // Not "… számított 45 napon belül …" (legal text) and not "102 nap múlva" (loyalty periods).
    private static readonly Regex PaymentTerm = new(
        @"(?:^|\s)(?:átutalás|atutalas|fizetési\s+feltétel(?:ek)?|fizetési\s+határidő)\s*[:(]?\s*\(?\s*(?<n>\d{1,3})\s*nap(?:os)?\b(?!\s*(?:múlva|belül|napon))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The payment term printed on the first page in days (null: none, or several different ones).</summary>
    public static int? PaymentTermDays(InvoiceDocument doc)
    {
        if (doc.Pages.Count == 0) return null;
        var found = new HashSet<int>();
        foreach (var line in doc.Pages[0].Lines)
            foreach (Match m in PaymentTerm.Matches(line.Text))
                if (int.TryParse(m.Groups["n"].Value, out var n) && n >= 1 && n <= 120) found.Add(n);
        return found.Count == 1 ? found.First() : null;
    }

    /// <summary>Unusual but possible dates (trust score deductions): long payment terms, far apart issue and fulfilment, old invoices.</summary>
    public static List<(int Points, string Reason)> Doubts(InvoiceDocument doc)
    {
        var doubts = new List<(int, string)>();
        if (doc.Excluded || doc.DatesApproved) return doubts;
        var issue = Parse(doc.IssueDate);
        var fulfil = Parse(doc.FulfillmentDate);
        var due = Parse(doc.DueDate);
        // The printed payment term must fit (a day of tolerance: some programs count from the fulfilment date).
        if (issue is { } it && due is { } dt && PaymentTermDays(doc) is { } term)
        {
            int days = dt.DayNumber - it.DayNumber;
            int fromFulfil = fulfil is { } ft ? dt.DayNumber - ft.DayNumber : days;
            if (Math.Abs(days - term) > 1 && Math.Abs(fromFulfil - term) > 1)
                doubts.Add((10, $"a számlán {term} napos fizetési feltétel áll, a határidő mégis {days} nappal a kiállítás után van"));
        }
        if (issue is { } i && due is { } d && d.DayNumber - i.DayNumber > 120)
            doubts.Add((8, $"a fizetési határidő {d.DayNumber - i.DayNumber} nappal a kiállítás után van – szokatlanul hosszú"));
        if (issue is { } i2 && fulfil is { } f && Math.Abs(f.DayNumber - i2.DayNumber) > 45)
            doubts.Add((5, $"a teljesítés és a kiállítás között {Math.Abs(f.DayNumber - i2.DayNumber)} nap van"));
        if (issue is { } i3 && i3 < DateOnly.FromDateTime(DateTime.Today).AddDays(-200))
            doubts.Add((5, $"a kiállítás dátuma ({Format(i3)}) több mint fél éve volt"));
        return doubts;
    }

    // ------------------------------------------------------------------------------------------ suggestions

    /// <summary>
    /// The dates found on the invoice, for the chips under a date field: those labelled as this kind first, then
    /// the unlabelled ones, then the others. Not the current value, not dates of a period.
    /// </summary>
    public static List<InvoiceProcessor.ValueSuggestion> Suggestions(InvoiceDocument doc, DateKind kind, int max = 6)
    {
        var reading = doc.DateReading;
        if (reading == null) return new();
        var current = Parse(doc.GetDate(kind));
        var result = new List<InvoiceProcessor.ValueSuggestion>();
        var seen = new HashSet<DateOnly>();
        if (current is { } c) seen.Add(c);
        // The due date computed from the printed payment term ("Átutalás (8 nap)"): offered, never filled in by itself.
        if (kind == DateKind.Due && Parse(doc.IssueDate) is { } issue && PaymentTermDays(doc) is { } term && seen.Add(issue.AddDays(term)))
            result.Add(new(Format(issue.AddDays(term)), $"kiszámolva: kiállítás + {term} nap (a számlán szereplő fizetési feltétel)"));
        var ordered = reading.Tokens
            .OrderBy(t => t.Kind == kind ? 0 : t.Kind == null && !t.Excluded && !t.Token.InRange ? 1 : t.Kind != null ? 2 : 3)
            .ThenBy(t => t.Token.PageNumber).ThenBy(t => t.Token.ReadingIndex).ThenBy(t => t.Token.Box.Y).ThenBy(t => t.Token.Box.X);
        foreach (var t in ordered)
        {
            if (!seen.Add(t.Token.Date)) continue;
            var page = t.Token.PageNumber == doc.Pages[0].Number ? "" : $"{t.Token.PageNumber}. oldal, ";
            var source = t.Kind is { } k && t.Label != null ? $"{page}„{t.Label}” {(t.How == "oszlop" ? "alatt" : "mellett")} ({Name(k)})"
                : t.Excluded ? $"{page}„{t.Label}” – nem számladátum"
                : t.Token.InRange ? $"{page}időszak része: {Shorten(t.Token.LineText)}"
                : $"{page}{Shorten(t.Token.LineText)}";
            if (t.Token.ReadingIndex > 0) source += " (újraolvasás)";
            result.Add(new(Format(t.Token.Date), source));
            if (result.Count >= max) break;
        }
        return result;
    }

    private static string Shorten(string s) => s.Length <= 70 ? s : s[..69] + "…";

    private static NormRect Union(IReadOnlyCollection<OcrWordBox> words)
    {
        double x0 = words.Min(w => w.X), y0 = words.Min(w => w.Y);
        double x1 = words.Max(w => w.X + w.W), y1 = words.Max(w => w.Y + w.H);
        return new NormRect(x0, y0, x1 - x0, y1 - y0);
    }
}
