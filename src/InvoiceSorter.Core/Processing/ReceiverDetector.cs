using System.Text.RegularExpressions;
using InvoiceSorter.Core.Models;

namespace InvoiceSorter.Core.Processing;

/// <summary>What a scan is addressed to: the configured receiving company found most, and a possibly better unknown one.</summary>
/// <param name="Match">The configured receiving company mentioned on most pages (null if none is mentioned).</param>
/// <param name="MatchPages">Number of pages mentioning <paramref name="Match"/>.</param>
/// <param name="Suggestion">A tax number that is on many more pages than any configured receiver (a company missing from the settings).</param>
/// <param name="SuggestionPages">Number of pages mentioning <paramref name="Suggestion"/>.</param>
public sealed record ReceiverDetection(ReceiverCompany? Match, int MatchPages, ReceiverCompany? Suggestion, int SuggestionPages, int Pages);

/// <summary>
/// Finds out which company a scan belongs to. A scan usually contains invoices of one receiving company (e.g. an
/// accounting office scans each client separately): its tax number is the one printed on most pages.
/// </summary>
public static class ReceiverDetector
{
    private static readonly Regex LegalForm = new(@"\b(?:K\s?f\s?t|Z\s?r\s?t|N\s?y\s?r\s?t|B\s?t|K\s?k\s?t)\b", RegexOptions.IgnoreCase);

    public static ReceiverDetection Detect(IReadOnlyList<PageInfo> pages, AppSettings settings)
    {
        var content = pages.Where(p => !p.IsBlank).ToList();
        var configured = settings.ReceiverCompanies
            .Select(r => (Company: r, Tax: VatValidator.Parse(r.VatNumber)))
            .Where(x => x.Tax != null).ToList();

        // Pages per tax number core (each page counted once per number; only numbers with a valid check digit).
        var pagesPerCore = new Dictionary<string, int>();
        var fullPerCore = new Dictionary<string, Dictionary<string, int>>();
        foreach (var page in content)
        {
            var text = string.Join("\n", page.Readings.Select(r => r.Text));
            foreach (var group in VatValidator.FindCandidates(text).Where(c => VatValidator.IsChecksumValid(c.Core)).GroupBy(c => c.Core))
            {
                pagesPerCore[group.Key] = pagesPerCore.GetValueOrDefault(group.Key) + 1;
                var full = group.FirstOrDefault(c => c.Vat != null);
                if (full == null) continue;
                var counts = fullPerCore.TryGetValue(group.Key, out var d) ? d : fullPerCore[group.Key] = new();
                counts[full.Full] = counts.GetValueOrDefault(full.Full) + 1;
            }
        }

        ReceiverCompany? match = null;
        int matchPages = 0;
        foreach (var (company, tax) in configured)
        {
            int n = pagesPerCore.GetValueOrDefault(tax!.Core);
            if (n > matchPages) { match = company; matchPages = n; }
        }

        // A number on at least a fifth of the pages (and on 3 pages, or every page of a short scan), clearly more often
        // than the best configured receiver. A supplier that sent many invoices also has a frequent tax number - those
        // are printed in the seller block. On 1-2 pages the count proves nothing: the number must be under a buyer label.
        ReceiverCompany? suggestion = null;
        int suggestionPages = 0;
        var top = pagesPerCore.Where(kv => configured.All(c => c.Tax!.Core != kv.Key))
                              .OrderByDescending(kv => kv.Value)
                              .Take(5)
                              .FirstOrDefault(kv => Block(content, kv.Key) is var (buyer, seller) && (kv.Value >= 3 ? seller <= buyer : buyer > seller));
        if (top.Key != null && top.Value >= Math.Max(Math.Min(3, content.Count), content.Count / 5) && top.Value > matchPages * 2)
        {
            var full = fullPerCore.TryGetValue(top.Key, out var counts) ? counts.MaxBy(kv => kv.Value).Key : top.Key;
            suggestion = new ReceiverCompany { Name = CompanyRegistry.CachedName(top.Key) ?? GuessName(content, top.Key) ?? "", VatNumber = full };
            suggestionPages = top.Value;
        }
        return new ReceiverDetection(match, matchPages, suggestion, suggestionPages, content.Count);
    }

    private static readonly Regex BuyerLabel = new(@"vev|v[aá]s[aá]rl|megrendel|buyer|customer|el[oő]fizet|fizet[oő]", RegexOptions.IgnoreCase);
    private static readonly Regex SellerLabel = new(@"elad|sz[aá]ll[ií]t|kibocs|szolg[aá]ltat|seller|supplier", RegexOptions.IgnoreCase);

    /// <summary>How often the tax number is printed under a buyer label (Vevő …) and under a seller label (Eladó, Szállító …).</summary>
    private static (int Buyer, int Seller) Block(IEnumerable<PageInfo> pages, string core)
    {
        int buyer = 0, seller = 0;
        foreach (var page in pages)
        foreach (var line in page.Lines.Where(l => l.Words.Count > 0))
        {
            if (!Regex.Replace(line.Text, @"\D", "").Contains(core[..6])) continue;
            double x = line.Words[0].X, y = line.Words.Min(w => w.Y);
            var context = page.Lines.Where(l => l.Words.Count > 0 && Math.Abs(l.Words[0].X - x) < 0.15 && y - l.Words.Min(w => w.Y) is >= 0 and < 0.15)
                                    .Select(l => l.Text).ToList();
            if (context.Any(BuyerLabel.IsMatch)) buyer++;
            else if (context.Any(SellerLabel.IsMatch)) seller++;
        }
        return (buyer, seller);
    }

    /// <summary>The company name printed above the tax number most often (a line with a legal form such as Kft.).</summary>
    private static string? GuessName(IEnumerable<PageInfo> pages, string core)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        foreach (var line in page.Lines.Where(l => l.Words.Count > 0))
        {
            if (!Regex.Replace(line.Text, @"\D", "").Contains(core[..6])) continue;
            var x = line.Words[0].X;
            var y = line.Words.Min(w => w.Y);
            foreach (var above in page.Lines.Where(l => l.Words.Count > 0 && Math.Abs(l.Words[0].X - x) < 0.12 && y - l.Words.Min(w => w.Y) is > 0 and < 0.09))
            {
                var m = Regex.Match(above.Text, @"^(.{2,}?" + LegalForm + @"\.?)", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var name = TextUtil.RepairCompanyName(m.Groups[1].Value);
                counts[name] = counts.GetValueOrDefault(name) + 1;
            }
        }
        return counts.Count == 0 ? null : counts.MaxBy(kv => kv.Value).Key;
    }
}
