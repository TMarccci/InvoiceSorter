using System.Text;
using System.Text.RegularExpressions;
using InvoiceSorter.Core.Models;

namespace InvoiceSorter.Core.Processing;

/// <summary>
/// Finds and validates the buyer's (receiver company's) Hungarian tax number on an invoice.
/// Tolerates typical OCR noise: missing/extra dashes and spaces, O/0, l/1, S/5, B/8 confusion.
/// </summary>
public static class VatValidator
{
    // 8 digit core, optional "-d-dd" suffix. Letters that OCR confuses with digits are allowed and mapped later.
    private static readonly Regex Candidate = new(
        @"(?<![0-9A-Za-z])(?<hu>HU\s?)?(?<core>(?:[0-9OoIlSB|] ?){7}[0-9OoIlSB|])(?:\s{0,2}[-–—.~_]?\s{0,2}(?<v>[0-9OoIl|])\s{0,2}[-–—.~_]?\s{0,2}(?<c>[0-9OoIlSB|]{2}))?(?![0-9])",
        RegexOptions.Compiled);

    public sealed record TaxNumber(string Core, string? Vat, string? County, bool IsEu)
    {
        public string Full => Vat != null && County != null ? $"{Core}-{Vat}-{County}" : IsEu ? $"HU{Core}" : Core;
        public override string ToString() => Full;
    }

    /// <summary>Parses a tax number written by the user (e.g. in settings). Returns null if it is not 8 or 11 digits.</summary>
    public static TaxNumber? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var isEu = s.Trim().StartsWith("HU", StringComparison.OrdinalIgnoreCase);
        var digits = new string(s.Where(char.IsDigit).ToArray());
        return digits.Length switch
        {
            11 => new TaxNumber(digits[..8], digits[8..9], digits[9..], false),
            8 => new TaxNumber(digits, null, null, isEu),
            _ => null,
        };
    }

    /// <summary>
    /// True for a complete, well-formed Hungarian tax number: valid check digit, VAT code 1-5 and a real county code
    /// (02-20, 22-44, 51). Rules out bank account fragments that happen to have a valid check digit.
    /// </summary>
    public static bool IsPlausibleFull(TaxNumber t) =>
        IsChecksumValid(t.Core) && t.Vat is "1" or "2" or "3" or "4" or "5" &&
        int.TryParse(t.County, out var county) && (county is >= 2 and <= 20 or >= 22 and <= 44 or 51);

    /// <summary>Checks the check digit (8th digit) of a Hungarian tax number core.</summary>
    public static bool IsChecksumValid(string core)
    {
        if (core.Length != 8 || !core.All(char.IsDigit)) return false;
        int[] weights = { 9, 7, 3, 1, 9, 7, 3 };
        int sum = 0;
        for (int i = 0; i < 7; i++) sum += (core[i] - '0') * weights[i];
        int check = (10 - sum % 10) % 10;
        return check == core[7] - '0';
    }

    public static IEnumerable<TaxNumber> FindCandidates(string text)
    {
        foreach (Match m in Candidate.Matches(text))
        {
            var rawCore = m.Groups["core"].Value.Replace(" ", "");
            if (rawCore.Count(char.IsDigit) < 6) continue; // mostly letters: a word, not a number
            var core = MapDigits(rawCore);
            if (m.Groups["v"].Success)
                yield return new TaxNumber(core, MapDigits(m.Groups["v"].Value), MapDigits(m.Groups["c"].Value), false);
            else
                yield return new TaxNumber(core, null, null, m.Groups["hu"].Success);
        }
    }

    /// <param name="acceptCore">Accept the 8-digit core (törzsszám) alone when the full number is not printed
    /// (simplified invoices / till receipts only have to show the first 8 digits of the buyer's tax number).</param>
    public static VatCheckResult Check(string text, IReadOnlyList<ReceiverCompany> receivers, bool acceptCore = false)
    {
        var expected = receivers.Select(r => Parse(r.VatNumber)).Where(t => t != null).Cast<TaxNumber>().ToList();
        if (expected.Count == 0)
            return new VatCheckResult(VatStatus.NotConfigured, null, "Nincs megadva vevő adószám");

        var candidates = FindCandidates(text).ToList();

        foreach (var exp in expected)
        {
            // Exact domestic tax number.
            var exact = candidates.FirstOrDefault(c => c.Core == exp.Core && c.Vat != null &&
                                                       (exp.Vat == null || (c.Vat == exp.Vat && c.County == exp.County)));
            if (exact != null) return new VatCheckResult(VatStatus.Valid, exact.Full, $"Megtalálva: {exact.Full}");

            // EU (community) VAT number HUxxxxxxxx.
            var eu = candidates.FirstOrDefault(c => c.Core == exp.Core && c.IsEu);
            if (eu != null) return new VatCheckResult(VatStatus.Valid, eu.Full, $"Közösségi adószám megtalálva: {eu.Full}");
        }

        foreach (var exp in expected)
        {
            var wrongSuffix = candidates.FirstOrDefault(c => c.Core == exp.Core && c.Vat != null);
            if (wrongSuffix != null)
                return new VatCheckResult(VatStatus.Mismatch, wrongSuffix.Full, $"talált: {wrongSuffix.Full}, várt: {exp.Full}");

            var coreOnly = candidates.FirstOrDefault(c => c.Core == exp.Core);
            if (coreOnly != null && acceptCore)
                return new VatCheckResult(VatStatus.Valid, coreOnly.Full, $"Megtalálva: {coreOnly.Full} ({CoreOnlyNote}: {exp.Full})");
            if (coreOnly != null)
                return new VatCheckResult(VatStatus.Mismatch, coreOnly.Full, $"hiányos szám: {coreOnly.Full}, várt: {exp.Full}");

            // A number that differs in one digit: either an OCR error or a typo on the invoice. Let a human decide.
            var similar = candidates.FirstOrDefault(c => c.Vat != null && HammingDistance(c.Core, exp.Core) == 1);
            // ... unless it cannot be a real tax number (bad check digit), the rest of the number matches and the buyer's
            // name is printed too: then it is the receiver's number with one misread digit.
            if (similar != null && !IsChecksumValid(similar.Core) && exp.Vat != null && similar.Vat == exp.Vat && similar.County == exp.County &&
                receivers.FirstOrDefault(r => Parse(r.VatNumber)?.Core == exp.Core) is { Name.Length: > 2 } receiver &&
                TextUtil.FuzzyContainsText(TextUtil.Compact(text), StripCompanyForm(receiver.Name), 0.2))
                return new VatCheckResult(VatStatus.Valid, exp.Full, $"Megtalálva: {similar.Full} = {exp.Full} egy félreolvasott számjeggyel ({OcrDigitNote}; a vevő neve szerepel)");
            if (similar != null)
                return new VatCheckResult(VatStatus.Mismatch, similar.Full,
                    $"talált: {similar.Full}, várt: {exp.Full} (egy számjegy eltér{(IsChecksumValid(similar.Core) ? "" : ", érvénytelen ellenőrzőszám – valószínűleg OCR-hiba")})");
        }

        return new VatCheckResult(VatStatus.NotFound, null, "A vevő adószáma nem található");
    }

    public const string OcrDigitNote = "érvénytelen ellenőrzőszám";

    /// <summary>Marks a buyer tax number of which only the first 8 digits (törzsszám) are printed.</summary>
    public const string CoreOnlyNote = "az első 8 számjegy";

    /// <summary>True when the text mentions one of the receivers (by VAT core or by name).</summary>
    public static bool MentionsReceiver(string text, string compactText, IReadOnlyList<ReceiverCompany> receivers, double tolerance)
    {
        foreach (var r in receivers)
        {
            var tn = Parse(r.VatNumber);
            if (tn != null && FindCandidates(text).Any(c => c.Core == tn.Core)) return true;
            if (!string.IsNullOrWhiteSpace(r.Name) && TextUtil.FuzzyContainsText(compactText, StripCompanyForm(r.Name), tolerance)) return true;
        }
        return false;
    }

    internal static string StripCompanyForm(string name) =>
        Regex.Replace(name, @"\b(kft|zrt|nyrt|bt|kkt|ltd|gmbh)\b\.?", "", RegexOptions.IgnoreCase).Trim();

    private static string MapDigits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                'O' or 'o' => '0',
                'I' or 'l' or '|' => '1',
                'S' => '5',
                'B' => '8',
                _ => c,
            });
        }
        return sb.ToString();
    }

    private static int HammingDistance(string a, string b) =>
        a.Length != b.Length ? int.MaxValue : a.Zip(b).Count(p => p.First != p.Second);
}
