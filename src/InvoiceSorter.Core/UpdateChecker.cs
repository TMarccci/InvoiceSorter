using System.Net.Http.Headers;
using System.Text.Json;

namespace InvoiceSorter.Core;

/// <summary>Asks GitHub for the newest release of Invoice Sorter.</summary>
public static class UpdateChecker
{
    public const string Repository = "TMarccci/InvoiceSorter";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases/latest";

    public sealed record Release(string Version, string Url, string Name);

    /// <summary>The newest release when it is newer than <paramref name="currentVersion"/>, otherwise null.</summary>
    public static async Task<Release?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("InvoiceSorter", Clean(currentVersion)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
        return IsNewer(tag, currentVersion) ? new Release(Clean(tag), url, name) : null;
    }

    /// <summary>"v1.2.3" / "1.2.3-beta" → "1.2.3".</summary>
    public static string Clean(string version)
    {
        var v = version.Trim().TrimStart('v', 'V');
        int cut = v.IndexOfAny(new[] { '-', '+', ' ' });
        return cut > 0 ? v[..cut] : v;
    }

    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(Pad(Clean(candidate)), out var a) && Version.TryParse(Pad(Clean(current)), out var b) && a > b;

    private static string Pad(string v) => v.Count(c => c == '.') == 0 ? v + ".0" : v;
}
