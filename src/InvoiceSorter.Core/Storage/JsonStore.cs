using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using InvoiceSorter.Core.Logging;
using InvoiceSorter.Core.Models;

namespace InvoiceSorter.Core.Storage;

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static List<InvoiceStyle> LoadStyles(string? path = null)
    {
        path ??= AppPaths.StylesFile;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<InvoiceStyle>>(File.ReadAllText(path), Options) ?? new();

            var defaults = LoadDefaultStyles();
            AppLog.Info($"Nincs sablonfájl, {defaults.Count} beépített sablonnal indul.");
            SaveStyles(defaults, path);
            return defaults;
        }
        catch (Exception ex)
        {
            AppLog.Error($"A sablonok nem tölthetők be innen: {path}", ex);
            return new();
        }
    }

    /// <summary>Version of the built-in styles shipped in DefaultStyles.json. Increase when they change.</summary>
    public const int BuiltInStylesVersion = 10;

    // Company-name filter of the built-in styles, in its successive versions (the last one is current).
    private const string CompanyV1 = @"^(?<value>.{2,}?\b(?:K\s?[fti1l]\s?[ti1l]|Z\s?[rt]\s?t|N\s?y\s?r\s?t|B\s?t|K\s?k\s?t)\b\.?)";
    private const string CompanyV2 = @"^(?:[^\n:]{0,25}?(?:n[ée]v|neve|c[ée]gn[ée]v)\s*:\s*)?(?:\S+\.(?:hu|com|eu|net)\s+)?[^\p{L}\p{Nd}\n\""„]{0,3}(?<value>(?-i:(?!(?:Az?|Ez|Egy) )[A-ZÁÉÍÓÖŐÚÜŰ0-9\""„])[^\n:,]{1,60}?\b(?:K\s?[fti1l]\s?[ti1l]|[ZI2]\s?[rt]\s?t|N\s?y\s?r\s?t|B\s?t|K\s?k\s?t)\b\.?)";
    private const string CompanyV3 = @"^(?:[^\n:]{0,25}?(?:n[ée]v|neve|c[ée]gn[ée]v)\s*:\s*)?(?:digitally signed by\s+)?(?:\S+\.(?:hu|com|eu|net)\s+)?[^\p{L}\p{Nd}\n\""„]{0,3}(?<value>(?-i:(?!(?:Az?|Ez|Egy) )[A-ZÁÉÍÓÖŐÚÜŰ0-9\""„])[^\n:,]{1,60}?\b(?:K\s?[fti1l]\s?[ti1l]|[ZI2]\s?[rt]\s?t|N\s?y\s?r\s?t|B\s?t|K\s?k\s?t)\b\.?)";

    /// <summary>
    /// Improvements of built-in regular expressions. Applied to every field that still contains the old built-in text,
    /// so styles the user edited (other regexes) are never touched.
    /// </summary>
    private static readonly (string Old, string New, bool WholeValue)[] RegexMigrations =
    {
        (CompanyV1, CompanyV3, true),
        (CompanyV2, CompanyV3, true),
        (@"(?!(?:19|20)\d{2}(?![0-9A-Za-z/._-]))", @"(?!(?:19|20)\d{2}\.?(?![0-9A-Za-z/_-]))", false),   // "2026." is a bare year
        // Version 7: only real dates are rejected ("2026/41/1100/0538" is an invoice number, not 2026-41-11) …
        (@"(?!\d{4}[./-]\d{2}[./-]\d{2})", @"(?!\d{4}[./-](?:0[1-9]|1[0-2])[./-](?:0[1-9]|[12]\d|3[01])(?![0-9/]))", false),
        (@"(?!\d{2}[./-]\d{2}[./-]\d{4})", @"(?!(?:0[1-9]|[12]\d|3[01])[./-](?:0[1-9]|1[0-2])[./-]\d{4}(?![0-9/]))", false),
        // … and e-mail addresses ("siometrik2019@gmail.com") are not invoice numbers.
        (@"\s*\()(?<value>(?=(?:[^\s\d]*\d){3})", @"\s*\()(?![A-Z0-9/._-]*@)(?<value>(?=(?:[^\s\d]*\d){3})", false),
    };

    /// <summary>Changes of built-in label texts: (style id, old label text, new label text) of the invoice number field.</summary>
    private static readonly (string StyleId, string Old, string New)[] LabelMigrations =
    {
        ("generic",
         "Számlaszám | Számla sorszáma | Számla száma | Számlasorszám | Számla szám | Sorszám | Bizonylatszám | Invoice No | Invoice number",
         "Számlaszám | Számla sorszáma | Számla száma | Számlasorszám | Számla szám | Sorszám | Bizonylatszám | Invoice No | Invoice number | Számla / Invoice"),
    };

    // Version 5: receipts with OCR-damaged closing/serial numbers ("0340/OOOOl", "U298"), the "NAV El. kód" line,
    // and METRO numbers including year, type and store ("2026-0/0(11) 0068/003434" instead of "0068/003434").
    private const string NavNumberV4 = @"\b(?<p>[A-Z])\s?(?<digits>[0-9OoIl|](?:\s?[0-9OoIl|]){7})\s*/\s*(?<z>\d{4})\s*/\s*(?<s>\d{5})\b";
    private const string NavNumberV5 = @"\b(?<p>[A-Z])\s?(?<digits>[0-9OoIl|](?:\s?[0-9OoIlA|]){7})\s*/\s*(?<digitsZ>[0-9OoIlU|]{4})\s*/\s*(?<digitsS>[0-9OoIlU|]{5})\b";
    private const string MetroNumberV4 = @"(\d{4}\s*/\s*\d{6})";
    private const string MetroNumberV5 = @"(?<digitsY>(?:19|20)\d{2})\s*-\s*(?<digitsA>[0-9OoU])\s*/\s*(?<digitsB>[0-9OoU])\s*\(\s*(?<digitsT>[0-9OoIl]{1,3})\s*\)\s*(?<digitsR>[0-9OoIlU]{4})\s*/\s*(?<digitsN>[0-9OoIlU]{6})";

    /// <summary>Changes of a built-in field (regex + format together): (style id, old regex, old format, new regex, new format).</summary>
    private static readonly (string StyleId, string OldRegex, string OldFormat, string NewRegex, string NewFormat)[] FieldMigrations =
    {
        ("nav-penztargep", NavNumberV4, "${p}${digits}/${z}/${s}", NavNumberV5, "${p}${digits}/${digitsZ}/${digitsS}"),
        ("metro", MetroNumberV4, "", MetroNumberV5, "${digitsY}-${digitsA}/${digitsB}(${digitsT}) ${digitsR}/${digitsN}"),
    };

    /// <summary>Changes of built-in identifier rules: (style id, old text, new text).</summary>
    private static readonly (string StyleId, string Old, string New)[] RuleMigrations =
    {
        ("nav-penztargep", @"NAV Ellenőrző kód | re:\b[A-Z]\s?[0-9OIl](?:\s?[0-9OIl]){7}\s*/\s*\d{4}\s*/\s*\d{5}\b",
                           @"NAV Ellenőrző kód | re:\bNAV\s?El\S{0,9}\s?k[oó]d | re:\b[A-Z]\s?[0-9OIl](?:\s?[0-9OIlA]){7}\s*/\s*[0-9OIlU]{4}\s*/\s*[0-9OIlU]{5}\b"),
        // Version 8: also the cash register (AP) code at the bottom of the receipt ("RA30500107").
        ("nav-penztargep", @"NAV Ellenőrző kód | re:\bNAV\s?El\S{0,9}\s?k[oó]d | re:\b[A-Z]\s?[0-9OIl](?:\s?[0-9OIlA]){7}\s*/\s*[0-9OIlU]{4}\s*/\s*[0-9OIlU]{5}\b",
                           @"NAV Ellenőrző kód | re:\bNAV\s?El\S{0,9}\s?k[oó]d | re:\b[A-Z]\s?[0-9OIl](?:\s?[0-9OIlA]){7}\s*/\s*[0-9OIlU]{4}\s*/\s*[0-9OIlU]{5}\b | narrow:re:\b(?:AP|[A-Z])?\s?A\d{8}\b"),
    };

    private static string? _genericNumberRegex;
    private static string GenericNumberRegex() =>
        _genericNumberRegex ??= LoadDefaultStyles().FirstOrDefault(b => b.IsFallback)?.InvoiceNumber.Regex ?? "";

    private sealed record BuiltInText(string OldName, string OldNotes, string Name, string Notes);

    /// <summary>The English name / notes (hash) of the built-in styles up to version 8 and their Hungarian version.</summary>
    private static readonly Lazy<Dictionary<string, BuiltInText>> BuiltInTexts = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("InvoiceSorter.Core.BuiltInStyleTexts.json");
        return stream == null ? new() : JsonSerializer.Deserialize<Dictionary<string, BuiltInText>>(stream, Options) ?? new();
    });

    /// <summary>Applies <see cref="RegexMigrations"/> to all fields of all styles; returns the number of changed fields.</summary>
    public static int MigrateRegexes(IEnumerable<InvoiceStyle> styles)
    {
        int changed = 0;
        void Fix(FieldDefinition? field)
        {
            for (; field != null; field = field.Fallback)
            {
                foreach (var (oldText, newText, whole) in RegexMigrations)
                {
                    if (whole ? field.Regex == oldText : field.Regex.Contains(oldText))
                    {
                        field.Regex = whole ? newText : field.Regex.Replace(oldText, newText);
                        changed++;
                    }
                }
            }
        }
        foreach (var s in styles) { Fix(s.Issuer); Fix(s.InvoiceNumber); }
        foreach (var s in styles)
        {
            foreach (var m in FieldMigrations.Where(m => m.StyleId == s.Id))
            {
                foreach (var field in new[] { s.Issuer, s.InvoiceNumber })
                {
                    if (field.Regex != m.OldRegex || (field.Format ?? "") != m.OldFormat) continue;
                    field.Regex = m.NewRegex;
                    field.Format = m.NewFormat;
                    // The new built-in version may come with a fallback rule (e.g. receipts with another number format).
                    if (field.Fallback == null && field == s.InvoiceNumber &&
                        LoadDefaultStyles().FirstOrDefault(b => b.Id == s.Id)?.InvoiceNumber.Fallback is { } fallback)
                        field.Fallback = fallback;
                    changed++;
                }
            }
            // Version 8: Diamond Stúdió's fallback no longer accepts truncated numbers ("26/00"): the built-in fallback
            // replaces the generic one it had in version 7 (only if the user did not change it).
            if (s.Id == "diamond-studio" && s.InvoiceNumber.Fallback is { } fb && fb.Regex == GenericNumberRegex() &&
                LoadDefaultStyles().FirstOrDefault(b => b.Id == s.Id)?.InvoiceNumber.Fallback is { } newFallback && newFallback.Regex != fb.Regex)
            {
                s.InvoiceNumber.Fallback = newFallback;
                changed++;
            }
            foreach (var m in LabelMigrations.Where(m => m.StyleId == s.Id && s.InvoiceNumber.LabelText == m.Old))
            {
                s.InvoiceNumber.LabelText = m.New;
                changed++;
            }
            foreach (var m in RuleMigrations.Where(m => m.StyleId == s.Id))
            {
                foreach (var rule in s.IdentifierRules.Where(r => r.Text == m.Old))
                {
                    rule.Text = m.New;
                    changed++;
                }
            }
        }
        // Version 9: the built-in styles are named and described in Hungarian (only where the English text is unchanged).
        foreach (var s in styles)
        {
            if (!BuiltInTexts.Value.TryGetValue(s.Id, out var t)) continue;
            if (s.Name == t.OldName && s.Name != t.Name) { s.Name = t.Name; changed++; }
            if (SameText(s.Notes, t.OldNotes) && s.Notes != t.Notes) { s.Notes = t.Notes; changed++; }
        }
        return changed;
    }

    /// <summary>The old English notes are stored as "sha256:&lt;hex&gt;" (they named example companies).</summary>
    private static bool SameText(string? value, string old)
    {
        if (value == null) return false;
        if (!old.StartsWith("sha256:", StringComparison.Ordinal)) return value == old;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
        return string.Equals(hash, old[7..], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Built-in style ids that were replaced by newer ones (removed on upgrade).</summary>
    private static readonly string[] ObsoleteBuiltInIds = Array.Empty<string>();

    /// <summary>
    /// Brings the built-in styles of an existing installation up to date. New built-in styles are added; existing
    /// ones are only replaced when upgrading from the very first version (before users could edit them) - later the
    /// user's version is kept and differences are logged ("Restore built-in styles…" resets them on request).
    /// A backup of the styles file is written first. Returns true if something changed.
    /// </summary>
    public static bool UpgradeBuiltInStyles(IList<InvoiceStyle> styles, AppSettings settings, string? path = null)
    {
        if (settings.BuiltInStylesVersion >= BuiltInStylesVersion) return false;
        path ??= AppPaths.StylesFile;
        try
        {
            if (File.Exists(path)) File.Copy(path, Path.ChangeExtension(path, $".backup-{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"A sablonfájlról nem készíthető biztonsági mentés: {ex.Message}");
        }

        bool firstGeneration = settings.BuiltInStylesVersion < 2;
        int replaced = MergeBuiltInStyles(styles, replaceExisting: firstGeneration, out int added);
        int migrated = MigrateRegexes(styles);
        if (migrated > 0) AppLog.Info($"{migrated} beépített szabály frissítve a javított változatra");
        var changed = firstGeneration ? new List<string>() : LoadDefaultStyles()
            .Where(b => styles.FirstOrDefault(s => s.Id == b.Id) is { } mine &&
                        JsonSerializer.Serialize(mine, Options) != JsonSerializer.Serialize(b, Options))
            .Select(b => b.Name).ToList();
        if (firstGeneration)
            for (int i = styles.Count - 1; i >= 0; i--)
                if (ObsoleteBuiltInIds.Contains(styles[i].Id)) styles.RemoveAt(i);
        // Known issuers learned from the sample scans (names and tax numbers) are merged into the user's list.
        int issuers = AppSettings.DefaultKnownIssuers().Select(Processing.InvoiceProcessor.ParseKnownIssuer)
            .Count(k => Processing.InvoiceProcessor.LearnIssuer(k.Name, settings, k.Tax));
        if (issuers > 0) AppLog.Info($"{issuers} ismert kibocsátó hozzáadva vagy adószámmal kiegészítve");
        // Version 6: file names start with the invoice number ("{InvoiceNumber}_{Issuer}") unless the user chose a pattern.
        if (settings.FileNameTemplate == AppSettings.OldDefaultFileNameTemplate)
        {
            settings.FileNameTemplate = AppSettings.DefaultFileNameTemplate;
            AppLog.Info($"A fájlnévminta az új alapértékre változott: {AppSettings.DefaultFileNameTemplate}");
        }
        foreach (var keyword in AppSettings.DefaultContinuationKeywords().Where(k => !settings.ContinuationKeywords.Contains(k, StringComparer.OrdinalIgnoreCase)))
        {
            settings.ContinuationKeywords.Add(keyword);
            AppLog.Info($"Folytatólap-kulcsszó hozzáadva: {keyword}");
        }
        if (changed.Count > 0)
            AppLog.Info($"Ezeknek a beépített sablonoknak a te változatod eltér az aktuálistól (a tiéd megmaradt; visszaállítás: 'Beépített sablonok visszaállítása…'): {string.Join(", ", changed)}");

        settings.BuiltInStylesVersion = BuiltInStylesVersion;
        AppLog.Info($"A beépített sablonok frissítve ({BuiltInStylesVersion}. verzió): {added} új, {replaced} frissítve (a régi fájl biztonsági mentése mellette).");
        return true;
    }

    /// <summary>Adds the built-in styles that are missing (and optionally replaces the ones with the same Id).</summary>
    public static int MergeBuiltInStyles(IList<InvoiceStyle> styles, bool replaceExisting, out int added)
    {
        added = 0;
        int replaced = 0;
        foreach (var builtIn in LoadDefaultStyles())
        {
            var idx = styles.Select((s, i) => (s, i)).FirstOrDefault(x => x.s.Id == builtIn.Id).i;
            bool exists = styles.Any(s => s.Id == builtIn.Id);
            if (!exists) { styles.Add(builtIn); added++; }
            else if (replaceExisting) { styles[idx] = builtIn; replaced++; }
        }
        return replaced;
    }

    public static List<InvoiceStyle> LoadDefaultStyles()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("InvoiceSorter.Core.DefaultStyles.json");
        if (stream == null) return new();
        return JsonSerializer.Deserialize<List<InvoiceStyle>>(stream, Options) ?? new();
    }

    public static void SaveStyles(IEnumerable<InvoiceStyle> styles, string? path = null)
    {
        path ??= AppPaths.StylesFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAtomic(path, JsonSerializer.Serialize(styles.ToList(), Options));
    }

    public static AppSettings LoadSettings(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? AppSettings.CreateDefault();
        }
        catch (Exception ex)
        {
            AppLog.Error($"A beállítások nem tölthetők be innen: {path}", ex);
        }
        return AppSettings.CreateDefault();
    }

    public static void SaveSettings(AppSettings settings, string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAtomic(path, JsonSerializer.Serialize(settings, Options));
    }

    public static T DeepClone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
