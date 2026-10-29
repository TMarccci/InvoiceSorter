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

    /// <summary>
    /// The accents of <paramref name="reference"/> on the words of <paramref name="text"/> that are the same apart from
    /// accents (a word may be the beginning of a reference word: "HÓSZIG" + "Hőszigetelő" → "HŐSZIG"). Accents are only
    /// added or corrected, never removed (a register name without accents leaves the text as it is); the case stays.
    /// </summary>
    public static string AccentsFrom(string text, string reference)
    {
        var refs = Regex.Split(reference, @"[^\p{L}\p{Nd}]+").Where(w => w.Length > 0).ToList();
        return Regex.Replace(text, @"\p{L}{3,}", m =>
        {
            var word = m.Value;
            var folded = Fold(word);
            var r = refs.FirstOrDefault(x => Fold(x).StartsWith(folded, StringComparison.Ordinal));
            if (r == null) return word;
            var chars = word.ToCharArray();
            for (int i = 0; i < chars.Length && i < r.Length; i++)
            {
                char c = r[i];
                if (c == chars[i] || char.ToLowerInvariant(c) == char.ToLowerInvariant(chars[i])) continue;
                if (Fold(c.ToString()) == c.ToString().ToLowerInvariant()) continue;   // the reference letter has no accent
                chars[i] = char.IsUpper(chars[i]) ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c);
            }
            return new string(chars);
        });
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
    /// OCR-tolerant "contains". The whole text must be found with the allowed number of errors, and inside that match
    /// every word of 4+ characters must keep to its own (smaller) allowance – otherwise the errors could all fall on
    /// the one distinctive word: "METRO KERESKEDELMI KFT" matched "Vasalatkereskedelmi Kft" (4 errors, all on
    /// "METRO") when the word was looked for anywhere else on the page ("Internet to" → "metto").
    /// </summary>
    public static bool FuzzyContainsText(string compactHaystack, string text, double tolerance)
    {
        var needle = Compact(text);
        if (needle.Length == 0) return false;
        if (compactHaystack.Contains(needle, StringComparison.Ordinal)) return true;
        int maxErrors = AllowedErrors(needle.Length, tolerance);
        if (!FuzzyContains(compactHaystack, needle, maxErrors)) return false;

        // Where each word of the text lies in the compact needle.
        var words = new List<(int Start, int Length)>();
        int at = 0;
        foreach (var w in Regex.Split(text, @"[^\p{L}\p{Nd}]+").Select(Compact).Where(w => w.Length > 0))
        {
            words.Add((at, w.Length));
            at += w.Length;
        }
        if (at != needle.Length) return true;   // unusual characters: the whole-text check has to do
        var checkedWords = words.Where(w => w.Length >= 4).ToList();
        if (words.Count <= 1 || checkedWords.Count == 0) return true;

        foreach (int end in MatchEnds(compactHaystack, needle, maxErrors))
        {
            var errors = WordErrors(compactHaystack, needle, end, maxErrors, words);
            // Per word a little more than its share (at least 1): "szałnlazz.hu" (m → łn) and "Ellerdrző" are real OCR
            // errors of "szamlazz.hu" and "Ellenőrző"; "salat" for "METRO" (4 of 5 letters) is another word.
            if (errors != null && checkedWords.All(w => errors[words.IndexOf(w)] <= Math.Max(1, (int)Math.Ceiling(w.Length * tolerance)))) return true;
        }
        return false;
    }

    /// <summary>The haystack positions (last character) where the needle ends with at most <paramref name="maxErrors"/> errors.</summary>
    private static IEnumerable<int> MatchEnds(string haystack, string needle, int maxErrors)
    {
        int m = needle.Length;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (int i = 0; i <= m; i++) prev[i] = i;
        for (int j = 0; j < haystack.Length; j++)
        {
            cur[0] = 0;
            for (int i = 1; i <= m; i++)
            {
                int cost = needle[i - 1] == haystack[j] ? 0 : 1;
                cur[i] = Math.Min(Math.Min(prev[i - 1] + cost, prev[i] + 1), cur[i - 1] + 1);
            }
            if (cur[m] <= maxErrors) yield return j;
            (prev, cur) = (cur, prev);
        }
    }

    /// <summary>
    /// Aligns the needle with the haystack text that ends at <paramref name="end"/> and counts the errors falling on each
    /// word (a missing or wrong letter of the word, or an extra letter inside it). Null when there is no such alignment.
    /// </summary>
    private static int[]? WordErrors(string haystack, string needle, int end, int maxErrors, List<(int Start, int Length)> words)
    {
        int m = needle.Length;
        int start = Math.Max(0, end - m - maxErrors + 1);
        var window = haystack.AsSpan(start, end - start + 1);
        int n = window.Length;
        var d = new int[m + 1, n + 1];
        for (int i = 0; i <= m; i++) d[i, 0] = i;
        for (int j = 0; j <= n; j++) d[0, j] = 0;
        for (int i = 1; i <= m; i++)
            for (int j = 1; j <= n; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j - 1] + (needle[i - 1] == window[j - 1] ? 0 : 1), d[i - 1, j] + 1), d[i, j - 1] + 1);
        if (d[m, n] > maxErrors) return null;

        int WordOf(int needleIndex) => words.FindIndex(w => needleIndex >= w.Start && needleIndex < w.Start + w.Length);
        var errors = new int[words.Count];
        int a = m, b = n;
        while (a > 0 && b > 0)
        {
            if (d[a, b] == d[a - 1, b - 1] + (needle[a - 1] == window[b - 1] ? 0 : 1))
            {
                if (needle[a - 1] != window[b - 1]) errors[WordOf(a - 1)]++;
                a--; b--;
            }
            else if (d[a, b] == d[a - 1, b] + 1)
            {
                errors[WordOf(a - 1)]++;   // a letter of the word is missing
                a--;
            }
            else
            {
                // An extra letter: it counts for the word only when it stands inside it, not between two words.
                int left = WordOf(a - 1);
                if (a < m && left == WordOf(a)) errors[left]++;
                b--;
            }
        }
        while (a > 0) errors[WordOf(--a)]++;
        return errors;
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
