using InvoiceSorter.Core.Models;

namespace InvoiceSorter.Core.Processing;

/// <summary>
/// Display order of the invoice styles: the generic rules first, then the invoicing programs from the most common in
/// Hungary (cash registers, NAV Online Számlázó, Billingo, Számlázz.hu …) to the rare ones, then the companies' own
/// systems alphabetically. A style the user made for a program (e.g. "Billingo") is placed with that program.
/// </summary>
public static class StyleOrder
{
    // Id prefix or name keyword → rank (lower = more popular).
    private static readonly (string Key, int Rank)[] Ranks =
    {
        ("generic", 0),
        ("nav-penztargep", 10), ("cash register", 10), ("pénztárgép", 10),
        ("nav-online-szamlazo", 20), ("nav online", 20),
        ("billingo", 30),
        ("szamlazzhu", 40), ("számlázz", 40), ("szamlazz", 40),
        ("naturasoft", 50), ("okos-ugyvitel", 55), ("okos", 55), ("kulcs", 56),
        ("sap-business-one", 60), ("dynamics", 61), ("rlb60", 62), ("rlb-60", 62), ("diamond", 63), ("infocentrum", 64),
        ("hugir", 65), ("vectory", 66), ("juta", 67), ("fr-soft", 68), ("mvm-libra", 69), ("webshop", 70), ("fugea", 71),
    };

    public static int Rank(InvoiceStyle style)
    {
        if (style.IsFallback) return 0;
        var id = style.Id.ToLowerInvariant();
        var name = style.Name.ToLowerInvariant();
        foreach (var (key, rank) in Ranks)
            if (id.StartsWith(key) || name.Contains(key)) return rank;
        return 100;   // the companies' own systems (METRO, Telekom …) and other user styles
    }

    public static List<InvoiceStyle> Sort(IEnumerable<InvoiceStyle> styles) =>
        styles.OrderBy(Rank).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
}
