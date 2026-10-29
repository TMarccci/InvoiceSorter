using System.Text.RegularExpressions;
using InvoiceSorter.Core.Logging;
using InvoiceSorter.Core.Models;

namespace InvoiceSorter.Core.Processing;

/// <summary>Result of reading a field, including where on the page it was found (for the preview overlay).</summary>
/// <param name="FromFallback">The value came from the field's fallback rule, not the main one (less certain).</param>
public sealed record FieldReadResult(string? Value, string SourceText, NormRect? LabelBox, NormRect? ValueBox, bool FromFallback = false)
{
    public static readonly FieldReadResult Empty = new(null, "", null, null);
}

/// <summary>Reads text from page regions and evaluates style rules / field definitions.</summary>
public static class FieldReader
{
    /// <summary>Separator of alternatives in identifier texts and label texts: "Számlaszám | Sorszám".</summary>
    public const string AlternativeSeparator = " | ";

    /// <summary>Prefix that turns an identifier text into a regular expression: "re:[A-Z]\d{8}/\d{4}/\d{5}".</summary>
    public const string RegexPrefix = "re:";

    /// <summary>Prefix of an identifier alternative that only counts on narrow (till receipt shaped) pages.</summary>
    public const string NarrowPrefix = "narrow:";

    /// <summary>A page much taller than wide (a till receipt), or a reading without size information.</summary>
    public static bool IsNarrowPage(PageInfo page) =>
        page.PixelWidth > 0 && page.PixelHeight > 0 && page.PixelWidth < page.PixelHeight * 0.55;

    public static IEnumerable<string> Alternatives(string text) =>
        text.Split(AlternativeSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>All OCR words whose center lies in the region, as text lines ordered top to bottom, left to right.</summary>
    public static string GetRegionText(PageInfo page, NormRect region, double padding) =>
        WordsToText(page.Lines.SelectMany(l => l.Words).Where(w => region.Contains(w.CenterX, w.CenterY, padding)));

    /// <summary>Joins words into text lines (words with similar vertical position form one line).</summary>
    public static string WordsToText(IEnumerable<OcrWordBox> words)
    {
        var lines = new List<List<OcrWordBox>>();
        foreach (var w in words.OrderBy(w => w.CenterY))
        {
            var last = lines.LastOrDefault();
            if (last != null && Math.Abs(last.Average(x => x.CenterY) - w.CenterY) < Math.Max(0.004, w.H * 0.5)) last.Add(w);
            else lines.Add(new() { w });
        }
        return string.Join("\n", lines.Select(l => string.Join(" ", l.OrderBy(w => w.X).Select(w => w.Text))));
    }

    public static bool RuleMatches(MatchRule rule, PageInfo page, AppSettings settings) => RuleScore(rule, page, settings) > 0;

    /// <summary>
    /// 0 when the rule does not match. Otherwise a score that grows with the length of the matched text, so that
    /// the most specific style wins. Alternatives are separated by " | "; "re:" makes an alternative a regex.
    /// </summary>
    public static int RuleScore(MatchRule rule, PageInfo page, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(rule.Text)) return 1;
        string? rawText = null, compact = null;
        string Raw() => rawText ??= rule.Region == null ? page.Text : GetRegionText(page, rule.Region, settings.RegionPadding + 0.01);
        string Compact() => compact ??= rule.Region == null ? page.CompactText : TextUtil.Compact(Raw());

        int regionBonus = rule.Region != null ? 5 : 0;
        int best = 0;
        foreach (var alternative in Alternatives(rule.Text))
        {
            var alt = alternative;
            // "narrow:…" counts only on a narrow, till-receipt shaped page (e.g. the cash register code "A30500107",
            // which also appears as a customer number on A4 invoices).
            if (alt.StartsWith(NarrowPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsNarrowPage(page)) continue;
                alt = alt[NarrowPrefix.Length..].Trim();
            }
            if (alt.StartsWith(RegexPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var pattern = alt[RegexPrefix.Length..].Trim();
                if (pattern.Length > 0 && (SafeMatch(Raw(), pattern) ?? SafeMatch(TextUtil.Fold(Raw()), pattern)) != null)
                    best = Math.Max(best, 12 + regionBonus);
            }
            else if (TextUtil.FuzzyContainsText(Compact(), alt, settings.FuzzyTolerance))
            {
                best = Math.Max(best, TextUtil.Compact(alt).Length + regionBonus);
            }
        }
        return best;
    }

    /// <summary>Returns a score > 0 when every identifier rule of the style matches the page.</summary>
    public static int MatchScore(InvoiceStyle style, PageInfo page, AppSettings settings)
    {
        var rules = style.IdentifierRules.Where(r => !string.IsNullOrWhiteSpace(r.Text)).ToList();
        if (!style.Enabled || style.IsFallback || rules.Count == 0) return 0;
        int score = 0;
        foreach (var rule in rules)
        {
            var s = RuleScore(rule, page, settings);
            if (s == 0) return 0;
            score += s;
        }
        return score;
    }

    public static InvoiceStyle? FindStyle(PageInfo page, IEnumerable<InvoiceStyle> styles, AppSettings settings)
    {
        InvoiceStyle? best = null;
        int bestScore = 0;
        foreach (var style in styles)
        {
            var score = MatchScore(style, page, settings);
            if (score > bestScore) { best = style; bestScore = score; }
        }
        return best;
    }

    public static string? ReadField(FieldDefinition field, PageInfo page, AppSettings settings) =>
        ReadFieldDetailed(field, page, settings).Value;

    /// <summary>Reads a field from a page (then with its fallback definition, if any). Value is null if nothing could be read.</summary>
    public static FieldReadResult ReadFieldDetailed(FieldDefinition field, PageInfo page, AppSettings settings)
    {
        var result = ReadSingle(field, page, settings);
        if (result.Value != null || field.Fallback == null) return result;
        var fallback = ReadFieldDetailed(field.Fallback, page, settings);
        return fallback.Value != null ? fallback with { FromFallback = true } : result;
    }

    /// <summary>
    /// Every value the field's own rule finds on the page (all label occurrences and directions, all regex matches),
    /// cleaned like the value that is read. Used for suggestions in the review screen.
    /// </summary>
    public static IEnumerable<string> AllValues(FieldDefinition field, PageInfo page, AppSettings settings)
    {
        IEnumerable<string> Clean(string source) => Candidates(field, source).Select(c => CleanValue(field, c, settings)).OfType<string>();

        switch (field.Mode)
        {
            case FieldMode.Fixed:
                if (!string.IsNullOrWhiteSpace(field.FixedValue)) yield return field.FixedValue.Trim();
                yield break;
            case FieldMode.Region:
                if (field.Region == null) yield break;
                foreach (var v in Clean(GetRegionText(page, field.Region, settings.RegionPadding))) yield return v;
                yield break;
            case FieldMode.PageRegex:
                foreach (var v in Clean(page.Text)) yield return v;
                yield break;
            case FieldMode.Label:
                var directions = field.LabelDirection == LabelDirection.Auto
                    ? new[] { LabelDirection.Right, LabelDirection.Below }
                    : new[] { field.LabelDirection };
                foreach (var label in FindLabels(page, field.LabelText, field.Region, settings))
                foreach (var direction in directions)
                {
                    var words = WordsNextToLabel(page, label, direction);
                    var text = WordsToText(words);
                    if (direction == LabelDirection.Right && label.Remainder.Length > 0) text = (label.Remainder + " " + text).Trim();
                    foreach (var v in Clean(text)) yield return v;
                }
                yield break;
        }
    }

    /// <summary>Reads only the field's own rule (not its fallback).</summary>
    public static FieldReadResult ReadSingleLevel(FieldDefinition field, PageInfo page, AppSettings settings) =>
        ReadSingle(field, page, settings);

    private static FieldReadResult ReadSingle(FieldDefinition field, PageInfo page, AppSettings settings)
    {
        string source;
        NormRect? labelBox = null, valueBox = null;
        switch (field.Mode)
        {
            case FieldMode.Fixed:
                return new FieldReadResult(string.IsNullOrWhiteSpace(field.FixedValue) ? null : field.FixedValue.Trim(), field.FixedValue, null, null);

            case FieldMode.Region:
                if (field.Region == null) return FieldReadResult.Empty;
                source = GetRegionText(page, field.Region, settings.RegionPadding);
                valueBox = field.Region;
                break;

            case FieldMode.Label:
            {
                // Many invoices print the label more than once (header and footer). The best matching label is
                // tried first; if its value does not pass the regex filter, the next occurrence is tried.
                var directions = field.LabelDirection == LabelDirection.Auto
                    ? new[] { LabelDirection.Right, LabelDirection.Below }
                    : new[] { field.LabelDirection };
                FieldReadResult? first = null;
                foreach (var label in FindLabels(page, field.LabelText, field.Region, settings))
                foreach (var direction in directions)
                {
                    var words = WordsNextToLabel(page, label, direction);
                    var text = WordsToText(words);
                    // Under a label, a value filtered by a regex may continue on the next line
                    // ("PRINTER SERVICE" / "Számítástechnikai KFT.") - the two lines are read as one.
                    if (direction == LabelDirection.Below && !string.IsNullOrWhiteSpace(field.Regex) && words.Count > 0)
                    {
                        var next = NextLineBelow(page, words, label);
                        if (next.Count > 0)
                        {
                            words = words.Concat(next).ToList();
                            text = text + " " + WordsToText(next);
                        }
                    }
                    // "Számlaszám:A0001210" - the value is glued to the label in the same OCR word.
                    if (direction == LabelDirection.Right && label.Remainder.Length > 0)
                        text = (label.Remainder + " " + text).Trim();
                    var box = Union(words) ?? (label.Remainder.Length > 0 ? label.Box : null);
                    var result = new FieldReadResult(ExtractValue(field, text, settings), text, label.Box, box);
                    if (result.Value != null) return result;
                    first ??= result;
                }
                return first ?? FieldReadResult.Empty;
            }

            case FieldMode.PageRegex:
                source = page.Text;
                break;

            default:
                return FieldReadResult.Empty;
        }

        return new FieldReadResult(ExtractValue(field, source, settings), source, labelBox, valueBox);
    }

    /// <summary>
    /// Applies the regex filter, output format and clean-up options to the text read for a field. Every regex match
    /// is a candidate (top to bottom); with <see cref="FieldDefinition.ExcludeReceiver"/> the receiving company's
    /// own name is skipped.
    /// </summary>
    private static string? ExtractValue(FieldDefinition field, string source, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        foreach (var candidate in Candidates(field, source))
            if (CleanValue(field, candidate, settings) is { } value) return value;
        return null;
    }

    /// <summary>Clean-up of one regex match (spaces, OCR digits); null when nothing is left or it is the receiver's name.</summary>
    private static string? CleanValue(FieldDefinition field, string candidate, AppSettings settings)
    {
        var value = TextUtil.CollapseWhitespace(candidate).Trim(' ', ':', ',', ';', '|', '_', '-', '—', '•');
        value = value.TrimStart('.');
        if (field.RemoveSpaces) value = Regex.Replace(value, @"\s+", "");
        if (field.FixOcrDigits) value = TextUtil.FixOcrDigits(value);
        if (value.Length == 0) return null;
        if (field.ExcludeReceiver && IsReceiverName(value, settings)) return null;
        return value;
    }

    private static IEnumerable<string> Candidates(FieldDefinition field, string source)
    {
        if (string.IsNullOrWhiteSpace(field.Regex))
        {
            // Without a regex the first line is the value (the next lines only if the first is the receiver's name).
            if (field.Mode == FieldMode.PageRegex) { yield return source; yield break; }
            foreach (var line in source.Split('\n'))
            {
                yield return line;
                if (!field.ExcludeReceiver) yield break;
            }
            yield break;
        }

        Regex regex;
        try { regex = new Regex(field.Regex, RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException ex)
        {
            AppLog.Warn($"Hibás reguláris kifejezés: '{field.Regex}': {ex.Message}");
            yield break;
        }

        // The accent-free text keeps the case: "0Ů020" → "0U020" (a lower-case "a30500107" would be a wrong value).
        foreach (var original in new[] { source, TextUtil.RemoveDiacritics(source) })
        {
            bool any = false;
            int start = 0;
            while (start <= original.Length)
            {
                Match m;
                try { m = regex.Match(original, start); }
                catch (RegexMatchTimeoutException) { AppLog.Warn($"A reguláris kifejezés túl sokáig futott: '{field.Regex}'"); yield break; }
                if (!m.Success) break;
                any = true;

                // A group named "digits" must contain only digits (O→0, l→1, S→5 …), one named "letters" only letters (1→i, 0→o …).
                var input = original;
                if (field.FixOcrDigits)
                {
                    // Several groups are possible: "digits", "digits2", "letters1" …
                    var fixes = regex.GetGroupNames()
                        .Select(n => (n, fix: n.StartsWith("digits") ? TextUtil.ToDigits : n.StartsWith("letters") ? (Func<string, string>?)TextUtil.ToLetters : null))
                        .Where(x => x.fix != null).ToList();
                    foreach (var (name, fix) in fixes)
                    {
                        var g = m.Groups[name];
                        if (!g.Success) continue;
                        input = input[..g.Index] + fix!(g.Value) + input[(g.Index + g.Length)..];
                        var again = regex.Match(input, m.Index);
                        if (again.Success && again.Index == m.Index) m = again;
                    }
                }

                yield return !string.IsNullOrWhiteSpace(field.Format) ? m.Result(field.Format)
                    : m.Groups["value"].Success ? m.Groups["value"].Value
                    : m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value
                    : m.Value;
                start = m.Index + Math.Max(1, m.Length);
            }
            if (any) yield break; // the accent-free text is only tried when the original gave no match at all
        }
    }

    // Legal forms including typical OCR damage ("Kii.", "K ft", "ZRT").
    private static readonly Regex LegalFormWord = new(@"\b(?:K\s?[fti1l]\s?[ti1l]|[ZI2]\s?[rt]\s?t|N\s?y\s?r\s?t|B\s?t|K\s?k\s?t)\b\.?", RegexOptions.IgnoreCase);

    /// <summary>True when the value is (close to) the name of a receiving company.</summary>
    private static bool IsReceiverName(string value, AppSettings settings)
    {
        var v = TextUtil.Compact(LegalFormWord.Replace(value, ""));
        if (v.Length < 3) return false;
        foreach (var r in settings.ReceiverCompanies)
        {
            var name = TextUtil.Compact(LegalFormWord.Replace(r.Name, ""));
            if (name.Length < 3) continue;
            int allowed = Math.Max(1, name.Length / 5);
            if (v.Contains(name) || name.Contains(v) || TextUtil.Levenshtein(v, name) <= allowed
                || (v.Length > name.Length && TextUtil.Levenshtein(v[..(name.Length + 1)], name) <= allowed))
                return true;
        }
        return false;
    }

    /// <param name="Remainder">Text glued to the end of the label in the same OCR word ("Számlaszám:A0001210" → "A0001210").</param>
    private sealed record LabelHit(NormRect Box, List<OcrWordBox> Words, int Distance, string Remainder);

    /// <summary>
    /// Finds every place where the label is printed (OCR-tolerant; alternatives separated by " | "),
    /// closest match first, top-most first on ties.
    /// </summary>
    private static List<LabelHit> FindLabels(PageInfo page, string labelText, NormRect? within, AppSettings settings)
    {
        var hits = new List<LabelHit>();
        foreach (var alternative in Alternatives(labelText))
        {
            var target = TextUtil.Compact(alternative);
            if (target.Length == 0) continue;
            int allowed = TextUtil.AllowedErrors(target.Length, settings.FuzzyTolerance);

            foreach (var line in page.Lines)
            {
                var words = line.Words;
                for (int i = 0; i < words.Count; i++)
                {
                    var acc = "";
                    for (int j = i; j < words.Count && acc.Length <= target.Length + allowed; j++)
                    {
                        var before = acc.Length;
                        acc += TextUtil.Compact(words[j].Text);
                        if (acc.Length < target.Length - allowed) continue;

                        // The label may be followed by other text in the same word ("Számlaszám:SZ001") - compare prefixes too.
                        int full = TextUtil.Levenshtein(acc, target);
                        int prefix = TextUtil.Levenshtein(acc[..Math.Min(acc.Length, target.Length)], target);
                        int d = Math.Min(full, prefix);
                        if (d > allowed) continue;

                        var hitWords = words.Skip(i).Take(j - i + 1).ToList();
                        var box = Union(hitWords)!;
                        if (within != null && !within.Contains(box.X + box.W / 2, box.Y + box.H / 2, settings.RegionPadding)) continue;

                        var remainder = prefix < full ? RemainderAfter(words[j].Text, target.Length - before) : "";
                        hits.Add(new LabelHit(box, hitWords, d, remainder));

                        // The label is complete - don't swallow the following words (the value) into it.
                        break;
                    }
                }
            }
        }

        // Best match first; the same words found through several alternatives are kept once.
        var result = new List<LabelHit>();
        foreach (var hit in hits.OrderBy(h => h.Distance).ThenBy(h => Math.Round(h.Box.Y / 0.005)).ThenBy(h => h.Box.X))
            if (!result.Any(r => r.Words.Intersect(hit.Words).Any())) result.Add(hit);
        return result;
    }

    /// <summary>The part of an OCR word after its first <paramref name="labelChars"/> letters/digits (the glued value).</summary>
    private static string RemainderAfter(string word, int labelChars)
    {
        if (labelChars <= 0) return "";
        int seen = 0, i = 0;
        for (; i < word.Length && seen < labelChars; i++)
            if (char.IsLetterOrDigit(word[i])) seen++;
        var rest = word[i..].Trim(' ', ':', '.', ';', '-');
        // A single stray letter is OCR noise of the label itself ("Számla.szárn:" → "n"), not a value.
        return rest.Any(char.IsDigit) || rest.Length >= 4 ? rest : "";
    }

    private static List<OcrWordBox> WordsNextToLabel(PageInfo page, LabelHit label, LabelDirection direction)
    {
        var all = page.Lines.SelectMany(l => l.Words).Where(w => !label.Words.Contains(w)).ToList();
        var lb = label.Box;
        double lineH = Math.Max(lb.H, 0.008);

        if (direction == LabelDirection.Right)
        {
            double cy = lb.Y + lb.H / 2;
            var row = all.Where(w => Math.Abs(w.CenterY - cy) < lineH * 0.7 && w.X >= lb.X + lb.W - 0.002)
                         .OrderBy(w => w.X).ToList();
            // The value is the first word group to the right (it may be far away in a right-aligned column);
            // stop at the next large horizontal gap.
            var result = new List<OcrWordBox>();
            double lastRight = lb.X + lb.W;
            foreach (var w in row)
            {
                double gap = w.X - lastRight;
                // The rest of a bilingual label ("Számlaszám/Invoice No.:  …  26/0123", "Számlaszám / Number:  …"): the
                // value may still be far away.
                bool labelTail = (w.Text.EndsWith(':') && !w.Text.Any(char.IsDigit) && w.Text.Length <= 12) ||
                                 w.Text.Trim('/', '|', '-', '–', ':').Length == 0;
                if (result.Count == 0 && gap <= 0.12 && labelTail)
                {
                    lastRight = w.X + w.W;
                    continue;
                }
                if (result.Count > 0 && gap > 0.12) break;
                if (result.Count == 0 && gap > 0.8) break;
                result.Add(w);
                lastRight = w.X + w.W;
            }
            return result;
        }
        else
        {
            // The first line under the label that has a word overlapping the label column...
            double left = lb.X - 0.05, right = lb.X + lb.W + 0.12;
            double maxY = lb.Y + lb.H + Math.Max(lineH * 4, 0.035);
            var below = all.Where(w => w.CenterY > lb.Y + lb.H && w.CenterY < maxY && w.X + w.W > left && w.X < right).ToList();
            if (below.Count == 0) return below;
            double firstY = below.Min(w => w.CenterY);
            var line = all.Where(w => Math.Abs(w.CenterY - firstY) < lineH * 0.6).OrderBy(w => w.X).ToList();
            var seed = below.Where(w => Math.Abs(w.CenterY - firstY) < lineH * 0.6).ToList();
            if (IsNextKeyValueRow(line, lb)) return new List<OcrWordBox>();

            // ...extended left and right along that line while the words are close together
            // (the value may start left of the label, e.g. a centered column heading).
            int lo = line.IndexOf(seed.MinBy(w => w.X)!), hi = line.IndexOf(seed.MaxBy(w => w.X)!);
            const double maxGap = 0.04;
            while (lo > 0 && line[lo].X - (line[lo - 1].X + line[lo - 1].W) < maxGap) lo--;
            while (hi < line.Count - 1 && line[hi + 1].X - (line[hi].X + line[hi].W) < maxGap) hi++;
            return line.GetRange(lo, hi - lo + 1);
        }
    }

    /// <summary>
    /// The line under a label is the next row of a key–value block, not the label's value: it starts in the label's
    /// column with words without digits (another label, e.g. "Vevőszám" under "Számlaszám" whose own value the OCR
    /// missed) and its value follows after a gap. Taking it would give the customer number as the invoice number.
    /// </summary>
    private static bool IsNextKeyValueRow(List<OcrWordBox> line, NormRect label)
    {
        // The keys of such a block are aligned exactly; a company name under a heading is often indented.
        int i = line.FindIndex(w => Math.Abs(w.X - label.X) < 0.006);
        if (i < 0 || line[i].Text.Any(char.IsDigit) || TextUtil.Compact(line[i].Text).Length < 3) return false;
        while (i + 1 < line.Count && line[i + 1].X - (line[i].X + line[i].W) < 0.025)
        {
            if (line[i + 1].Text.Any(char.IsDigit)) return false;
            i++;
        }
        // "Minta Kft." is a value (a name), not a key.
        var key = line.Where(w => w.X >= label.X - 0.006 && w.X + w.W <= line[i].X + line[i].W + 0.001).Select(w => TextUtil.Compact(w.Text));
        if (key.Any(k => k is "kft" or "zrt" or "nyrt" or "bt" or "kkt" or "ev" or "ltd" or "gmbh")) return false;
        // A key column and a value column: the other label ends about where ours does, its value starts just right of
        // ours. (Not a heading over "Név:  Minta Kft.", where the value starts under the heading, and not a name under
        // a heading with the next column of the page – the buyer block – far to the right.)
        if (i + 1 >= line.Count) return false;
        double labelEnd = label.X + label.W, valueX = line[i + 1].X;
        return line[i].X + line[i].W < labelEnd + 0.03 && valueX - (line[i].X + line[i].W) >= 0.04 &&
               valueX > labelEnd + 0.01 && valueX < labelEnd + 0.15;
    }

    /// <summary>The words of the line directly under <paramref name="firstLine"/>, in the same column.</summary>
    private static List<OcrWordBox> NextLineBelow(PageInfo page, List<OcrWordBox> firstLine, LabelHit label)
    {
        double lineH = Math.Max(firstLine.Max(w => w.H), 0.008);
        double y = firstLine.Average(w => w.CenterY);
        double x0 = firstLine.Min(w => w.X) - 0.02, x1 = firstLine.Max(w => w.X + w.W) + 0.02;
        var candidates = page.Lines.SelectMany(l => l.Words)
            .Where(w => !label.Words.Contains(w) && !firstLine.Contains(w) && w.CenterY > y + lineH * 0.6 && w.CenterY < y + lineH * 2.2
                        && w.X + w.W > x0 && w.X < x1)
            .ToList();
        if (candidates.Count == 0) return candidates;
        double nextY = candidates.Min(w => w.CenterY);
        return candidates.Where(w => Math.Abs(w.CenterY - nextY) < lineH * 0.6).OrderBy(w => w.X).ToList();
    }

    private static NormRect? Union(IReadOnlyCollection<OcrWordBox> words)
    {
        if (words.Count == 0) return null;
        double x0 = words.Min(w => w.X), y0 = words.Min(w => w.Y);
        double x1 = words.Max(w => w.X + w.W), y1 = words.Max(w => w.Y + w.H);
        return new NormRect(x0, y0, x1 - x0, y1 - y0);
    }

    private static Match? SafeMatch(string input, string pattern)
    {
        try
        {
            var m = Regex.Match(input, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromSeconds(1));
            return m.Success ? m : null;
        }
        catch (ArgumentException ex)
        {
            AppLog.Warn($"Hibás reguláris kifejezés: '{pattern}': {ex.Message}");
            return null;
        }
        catch (RegexMatchTimeoutException)
        {
            AppLog.Warn($"A reguláris kifejezés túl sokáig futott: '{pattern}'");
            return null;
        }
    }
}
