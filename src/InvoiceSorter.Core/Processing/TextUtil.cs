using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace InvoiceSorter.Core.Processing;

public static class TextUtil
{
    /// <summary>Lowercase and remove diacritics (OCR often confuses á/å, ő/ö etc.).</summary>
    public static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Removes diacritics but keeps the case ("Ů" → "U", "á" → "a").</summary>
    public static string RemoveDiacritics(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Fold and keep only letters and digits.</summary>
    public static string Compact(string? s)
    {
        var folded = Fold(s);
        var sb = new StringBuilder(folded.Length);
        foreach (var c in folded)
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    public static int AllowedErrors(int length, double tolerance) => (int)Math.Floor(length * tolerance);

    /// <summary>
    /// Approximate substring search (Sellers' algorithm): true when <paramref name="needle"/> occurs in
    /// <paramref name="haystack"/> with at most <paramref name="maxErrors"/> insertions/deletions/substitutions.
    /// </summary>
    public static bool FuzzyContains(string haystack, string needle, int maxErrors)
    {
        if (needle.Length == 0) return true;
        if (maxErrors <= 0) return haystack.Contains(needle, StringComparison.Ordinal);
        if (haystack.Contains(needle, StringComparison.Ordinal)) return true;

        int m = needle.Length;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (int i = 0; i <= m; i++) prev[i] = i;

        foreach (var c in haystack)
        {
            cur[0] = 0;
            for (int i = 1; i <= m; i++)
            {
                int cost = needle[i - 1] == c ? 0 : 1;
                cur[i] = Math.Min(Math.Min(prev[i - 1] + cost, prev[i] + 1), cur[i - 1] + 1);
            }
            if (cur[m] <= maxErrors) return true;
            (prev, cur) = (cur, prev);
        }
        return prev[m] <= maxErrors;
    }

    /// <summary>
    /// OCR-tolerant "contains". The whole text must be found with the allowed number of errors, and every word of
    /// 4+ characters must also be found on its own with its own (smaller) allowance - otherwise the errors could all
    /// fall on the one distinctive word ("METRO KERESKEDELMI KFT" would match "… ÉS KERESKEDELMI KFT").
    /// </summary>
    public static bool FuzzyContainsText(string compactHaystack, string text, double tolerance)
    {
        var needle = Compact(text);
        if (needle.Length == 0) return false;
        if (!FuzzyContains(compactHaystack, needle, AllowedErrors(needle.Length, tolerance))) return false;

        var words = Regex.Split(text, @"[^\p{L}\p{Nd}]+").Select(Compact).Where(w => w.Length >= 4).ToList();
        if (words.Count <= 1) return true;
        return words.All(w => FuzzyContains(compactHaystack, w, AllowedErrors(w.Length, tolerance)));
    }

    /// <summary>
    /// Replaces letters that OCR confuses with digits when they stand inside a number
    /// (left neighbour is a digit and the right one is a digit or the end), e.g. "COM147B912" → "COM1478912".
    /// </summary>
    public static string FixOcrDigits(string s)
    {
        static char? Map(char c) => c switch
        {
            'O' or 'o' or 'D' or 'Q' => '0',
            'I' or 'l' or '|' or 'i' => '1',
            'Z' or 'z' => '2',
            'S' or 's' => '5',
            'G' => '6',
            'B' => '8',
            _ => null,
        };

        var chars = s.ToCharArray();
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 1; i < chars.Length; i++)
            {
                if (char.IsDigit(chars[i]) || Map(chars[i]) is not char d) continue;
                bool leftDigit = char.IsDigit(chars[i - 1]);
                bool rightOk = i == chars.Length - 1 || char.IsDigit(chars[i + 1]);
                if (leftDigit && rightOk) chars[i] = d;
            }
        }
        return new string(chars);
    }

    public static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Maps every letter that OCR confuses with a digit to that digit (for regex groups named "digits").</summary>
    public static string ToDigits(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = chars[i] switch
            {
                'O' or 'o' or 'D' or 'Q' or 'U' or 'u' => '0',
                'I' or 'l' or '|' or 'i' or 'L' or '!' => '1',
                'Z' or 'z' => '2',
                'S' or 's' => '5',
                'G' or 'b' => '6',
                'B' => '8',
                'g' or 'q' => '9',
                'A' => '4',
                _ => chars[i],
            };
        }
        return new string(chars);
    }

    /// <summary>Maps digits that OCR produces instead of letters back to letters (for regex groups named "letters").</summary>
    public static string ToLetters(string s)
    {
        bool lower = s.Count(char.IsLower) >= s.Count(char.IsUpper);
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char? l = chars[i] switch { '0' => 'o', '1' => 'i', '5' => 's', '8' => 'b', '2' => 'z', _ => null };
            if (l is char c) chars[i] = lower ? c : char.ToUpperInvariant(c);
        }
        return new string(chars);
    }

    private static readonly (Regex Pattern, string Replacement)[] LegalForms =
    {
        (new Regex(@"\bN\s?y\s?r\s?t\b(?:\s?\.)?", RegexOptions.IgnoreCase), "Nyrt."),
        (new Regex(@"\b[ZI2]\s?[rt]\s?t\b(?:\s?\.)?", RegexOptions.IgnoreCase), "Zrt."),
        (new Regex(@"\bK\s?[fti1l]\s?[ti1l]\b(?:\s?\.)?", RegexOptions.IgnoreCase), "Kft."),
        (new Regex(@"\bK\s?k\s?t\b(?:\s?\.)?", RegexOptions.IgnoreCase), "Kkt."),
        (new Regex(@"\bB\s?t\b(?:\s?\.)?", RegexOptions.IgnoreCase), "Bt."),
    };

    /// <summary>
    /// Repairs typical OCR damage in a company name read from a Hungarian invoice:
    /// accents of other languages (å, õ, û…), "6"/"0" read instead of "ó"/"o" inside words,
    /// broken legal forms ("Ktt", "K ft .", "ZRT") and stray punctuation.
    /// </summary>
    public static string RepairCompanyName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                'å' or 'ã' or 'â' or 'à' or 'ä' => 'á',
                'Å' or 'Ã' or 'Â' or 'À' or 'Ä' => 'Á',
                'õ' or 'ô' or 'ò' => 'ő',
                'Õ' or 'Ô' or 'Ò' => 'Ő',
                'û' or 'ù' => 'ű',
                'Û' or 'Ù' => 'Ű',
                'ì' or 'î' or 'ï' => 'í',
                'è' or 'ê' or 'ë' => 'é',
                _ => c,
            });
        }
        var r = sb.ToString();
        r = Regex.Replace(r, @"(?<=\p{L}{2})6(?=\p{L}|\b)", "ó");   // Szolgáltat6 → Szolgáltató
        r = Regex.Replace(r, @"(?<=\p{L})0(?=\p{L})", "o");
        r = Regex.Replace(r, @"[""„”“]", "");                       // "NAPRAFORGÓ" Kft → NAPRAFORGÓ Kft
        foreach (var (pattern, replacement) in LegalForms) r = pattern.Replace(r, replacement);
        r = Regex.Replace(r, @"\s+([.,])", "$1");
        return CollapseWhitespace(r).Trim(' ', ',', ':', ';', '-');
    }

    public static string CollapseWhitespace(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static readonly char[] InvalidFileChars = Path.GetInvalidFileNameChars();

    /// <summary>Makes a string safe for use as (part of) a Windows file name.</summary>
    public static string SanitizeFileName(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '"' or '„' or '”' or '“') continue;
            sb.Append(InvalidFileChars.Contains(c) || c == '/' || c == '\\' ? '-' : c);
        }
        var result = CollapseWhitespace(sb.ToString()).Trim(' ', '.');
        if (result.Length > 150) result = result[..150].Trim(' ', '.');
        return result;
    }
}
