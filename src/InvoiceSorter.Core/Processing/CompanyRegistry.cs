using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using InvoiceSorter.Core.Logging;
using InvoiceSorter.Core.Storage;

namespace InvoiceSorter.Core.Processing;

/// <summary>
/// Official company names by Hungarian tax number, from the free EU VIES service (no key needed). Results are kept in
/// a local cache (company-registry.json next to the settings), so every tax number is asked only once; reading the
/// invoices only uses the cache.
/// </summary>
public static class CompanyRegistry
{
    public sealed class Entry
    {
        public bool Valid { get; set; }
        /// <summary>Official name as registered, e.g. "METRO KERESKEDELMI KORLÁTOLT FELELŐSSÉGŰ TÁRSASÁG".</summary>
        public string RegisteredName { get; set; } = "";
        public string Address { get; set; } = "";
        public DateTime Checked { get; set; }
    }

    private const string ViesUrl = "https://ec.europa.eu/taxation_customs/vies/rest-api/ms/HU/vat/";
    private static readonly object Gate = new();
    private static Dictionary<string, Entry>? _cache;
    private static string? _cachePath;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Cache file; tests and the command line may point it elsewhere.</summary>
    public static string CachePath
    {
        get => _cachePath ?? Path.Combine(AppPaths.DataDir, "company-registry.json");
        set { lock (Gate) { _cachePath = value; _cache = null; } }
    }

    private static Dictionary<string, Entry> Cache
    {
        get
        {
            lock (Gate)
            {
                if (_cache != null) return _cache;
                try
                {
                    _cache = File.Exists(CachePath)
                        ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(CachePath), JsonStore.Options) ?? new()
                        : new();
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"A cégnév-gyorsítótár nem olvasható: {ex.Message}");
                    _cache = new();
                }
                return _cache;
            }
        }
    }

    /// <summary>Adds a known entry to the cache (tests, or names confirmed elsewhere).</summary>
    public static void Remember(string core, string registeredName, bool valid = true)
    {
        lock (Gate) Cache[core] = new Entry { Valid = valid, RegisteredName = registeredName, Checked = DateTime.Now };
    }

    /// <summary>The cached short company name ("Metro Kereskedelmi Kft.") of a tax number core, or null.</summary>
    public static string? CachedName(string core)
    {
        lock (Gate)
            return Cache.TryGetValue(core, out var e) && e.Valid && e.RegisteredName.Length > 2 && !IsGroupName(e.RegisteredName)
                ? ShortName(e.RegisteredName) : null;
    }

    private static bool IsGroupName(string name) =>
        name.Contains("csoport", StringComparison.OrdinalIgnoreCase) && TextUtil.Fold(name).Contains("afa") ||
        name.Contains("VAT group", StringComparison.OrdinalIgnoreCase);

    public static bool IsCached(string core)
    {
        lock (Gate) return Cache.ContainsKey(core);
    }

    /// <summary>
    /// Looks up the tax numbers that are not cached yet. Network problems are logged and skipped (asked again next
    /// time). Returns the number of new names.
    /// </summary>
    public static async Task<int> LookupAsync(IEnumerable<string> cores, CancellationToken ct)
    {
        var missing = cores.Distinct().Where(c => Regex.IsMatch(c, @"^\d{8}$") && !IsCached(c)).ToList();
        if (missing.Count == 0) return 0;
        int found = 0, failed = 0;
        foreach (var core in missing)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await Http.GetAsync(ViesUrl + core, ct);
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var userError = Str("userError");
                if (!root.TryGetProperty("isValid", out var validProp) || (userError != "VALID" && userError != "INVALID"))
                {
                    failed++;                                       // service busy etc. – try again next time
                    AppLog.Debug($"Company lookup {core}: {userError}");
                    continue;
                }
                bool valid = validProp.ValueKind == JsonValueKind.True;
                var name = Str("name");
                if (name == "---") name = "";
                // VAT group numbers return the group ("Áfa csoport / Vat Group …"), not the issuing member company.
                if (IsGroupName(name)) name = "";
                lock (Gate)
                    Cache[core] = new Entry { Valid = valid && name.Length > 0, RegisteredName = name, Address = Str("address"), Checked = DateTime.Now };
                if (valid && name.Length > 0)
                {
                    found++;
                    AppLog.Info($"Cégnév a(z) {core} adószámhoz: {ShortName(name)}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failed++;
                AppLog.Debug($"Company lookup {core} failed: {ex.Message}");
                if (failed >= 3 && found == 0)
                {
                    AppLog.Warn($"A cégnév-lekérdezés (EU VIES) nem érhető el – a kibocsátóneveket csak a lapról olvassa. {ex.Message}");
                    break;
                }
            }
        }
        Save();
        if (failed > 0) AppLog.Warn($"A cégnév-lekérdezés {failed} adószámnál nem sikerült; legközelebb újra próbálja");
        return found;
    }

    private static void Save()
    {
        try
        {
            string json;
            lock (Gate) json = JsonSerializer.Serialize(Cache, JsonStore.Options);
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, json);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"A cégnév-gyorsítótár nem menthető: {ex.Message}");
        }
    }

    // Long legal forms of the register → the short forms used on invoices (compared without accents, upper case).
    private static readonly (string[] Words, string Short)[] LegalForms =
    {
        (new[] { "KORLATOLT", "FELELOSSEGU", "TARSASAG" }, "Kft."),
        (new[] { "ZARTKORUEN", "MUKODO", "RESZVENYTARSASAG" }, "Zrt."),
        (new[] { "ZARTKORUEN", "MUKODO", "RT" }, "Zrt."),
        (new[] { "NYILVANOSAN", "MUKODO", "RESZVENYTARSASAG" }, "Nyrt."),
        (new[] { "RESZVENYTARSASAG" }, "Rt."),
        (new[] { "BETETI", "TARSASAG" }, "Bt."),
        (new[] { "KOZKERESETI", "TARSASAG" }, "Kkt."),
        (new[] { "EGYENI", "CEG" }, "e.c."),
        (new[] { "KFT" }, "Kft."), (new[] { "ZRT" }, "Zrt."), (new[] { "NYRT" }, "Nyrt."), (new[] { "BT" }, "Bt."), (new[] { "KKT" }, "Kkt."),
    };

    private static readonly HashSet<string> LowerWords = new(StringComparer.OrdinalIgnoreCase) { "és", "es", "and", "a", "az" };

    /// <summary>
    /// "METRO KERESKEDELMI KORLÁTOLT FELELŐSSÉGŰ TÁRSASÁG" → "Metro Kereskedelmi Kft.";
    /// "DELTA KERESKEDELMI, KORLÁTOLT FELELŐSSÉGŰ TÁRSASÁG" → "Delta Kereskedelmi Kft.".
    /// </summary>
    public static string ShortName(string registered)
    {
        var words = Regex.Split(registered.Replace(',', ' ').Trim(), @"\s+").Where(w => w.Length > 0).ToList();
        string? form = null;
        foreach (var (formWords, shortForm) in LegalForms)
        {
            if (words.Count <= formWords.Length) continue;
            var tail = words.Skip(words.Count - formWords.Length).Select(w => TextUtil.Fold(w).ToUpperInvariant().Trim('.')).ToArray();
            if (!tail.SequenceEqual(formWords)) continue;
            words.RemoveRange(words.Count - formWords.Length, formWords.Length);
            form = shortForm;
            break;
        }
        var cased = words.Select((w, i) => Case(w, i == 0));
        var result = string.Join(" ", cased);
        return form == null ? result : $"{result} {form}";
    }

    private static string Case(string word, bool first)
    {
        if (!first && LowerWords.Contains(word)) return word.ToLowerInvariant();
        bool allUpper = word.Any(char.IsLetter) && !word.Any(char.IsLower);
        if (!allUpper) return word;
        // Short all-capital words without accents are abbreviations (MVM, OMV, UPS…): keep them ("MÁS" is a word).
        if (word.Length <= 3 && word.All(c => c < 128)) return word;
        // Capitalize every part of "TESCO-GLOBAL" / "BAU-TRANS".
        return string.Join("-", word.Split('-').Select(p =>
            p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }
}
