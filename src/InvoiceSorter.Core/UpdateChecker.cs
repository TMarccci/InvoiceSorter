using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using InvoiceSorter.Core.Logging;

namespace InvoiceSorter.Core;

/// <summary>
/// Asks GitHub for the newest release of Invoice Sorter and installs it: the installer of the release
/// (InvoiceSorter-Setup-x.y.z.exe / InvoiceSorter-Server-Setup-x.y.z.exe) is downloaded and started.
/// </summary>
public static class UpdateChecker
{
    public const string Repository = "TMarccci/InvoiceSorter";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases/latest";

    /// <summary>Installer name prefixes in the release assets.</summary>
    public const string ClientInstaller = "InvoiceSorter-Setup-";
    public const string ServerInstaller = "InvoiceSorter-Server-Setup-";

    public sealed record Asset(string Name, string Url, long Size);

    public sealed record Release(string Version, string Url, string Name, IReadOnlyList<Asset> Assets)
    {
        /// <summary>The installer whose file name starts with <paramref name="prefix"/>.</summary>
        public Asset? Installer(string prefix) =>
            Assets.FirstOrDefault(a => a.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    private static HttpClient NewClient(string currentVersion, TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("InvoiceSorter", Clean(currentVersion)));
        return http;
    }

    /// <summary>The newest release when it is newer than <paramref name="currentVersion"/>, otherwise null.</summary>
    public static async Task<Release?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        using var http = NewClient(currentVersion, TimeSpan.FromSeconds(15));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var release = Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return release != null && IsNewer(release.Version, currentVersion) ? release : null;
    }

    /// <summary>Reads a GitHub "release" JSON answer.</summary>
    public static Release? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (tag.Length == 0) return null;
        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
        var assets = new List<Asset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var a in list.EnumerateArray())
                if (a.TryGetProperty("name", out var an) && a.TryGetProperty("browser_download_url", out var au))
                    assets.Add(new Asset(an.GetString() ?? "", au.GetString() ?? "", a.TryGetProperty("size", out var size) ? size.GetInt64() : 0));
        return new Release(Clean(tag), url, name, assets);
    }

    /// <summary>Downloads an installer to %TEMP%\InvoiceSorter-update; progress 0…1.</summary>
    public static async Task<string> DownloadAsync(Asset asset, IProgress<double>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "InvoiceSorter-update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(asset.Name));
        using var http = NewClient("1.0.0", TimeSpan.FromMinutes(30));
        using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? asset.Size;
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = File.Create(path + ".part"))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report(Math.Min(1, done / (double)total));
            }
        }
        File.Move(path + ".part", path, overwrite: true);
        AppLog.Info($"Frissítés letöltve: {path}");
        return path;
    }

    /// <summary>Starts the downloaded installer (it asks for administrator rights and closes the running program).</summary>
    public static void StartInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path, "/SP- /NOCANCEL /CLOSEAPPLICATIONS") { UseShellExecute = true });

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
