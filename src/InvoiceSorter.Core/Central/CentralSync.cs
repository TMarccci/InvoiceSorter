using System.Text.Json;
using InvoiceSorter.Core.Logging;
using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Storage;

namespace InvoiceSorter.Core.Central;

/// <summary>
/// Central mode of the client: the styles and settings come from the Invoice Sorter server. The last configuration
/// received is kept in central-cache.json, so the program also works while the server cannot be reached.
/// The user's own (local) styles.json and settings.json stay untouched.
/// </summary>
public static class CentralSync
{
    public static string CacheFile => Path.Combine(AppPaths.DataDir, "central-cache.json");

    public static CentralConfig? LoadCache(string? path = null)
    {
        path ??= CacheFile;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<CentralConfig>(File.ReadAllText(path), JsonStore.Options);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"A központi konfiguráció helyi másolata nem olvasható: {ex.Message}");
        }
        return null;
    }

    public static void SaveCache(CentralConfig config, string? path = null)
    {
        path ??= CacheFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonStore.Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static CentralClient ClientFor(AppSettings settings) =>
        new(settings.CentralServerUrl, settings.CentralClientKey, TimeSpan.FromSeconds(30));

    /// <summary>
    /// Downloads the central configuration when the server has a newer revision than <paramref name="knownRevision"/>
    /// (or always, when <paramref name="force"/>). Returns null when the copy at hand is up to date.
    /// </summary>
    public static async Task<CentralConfig?> FetchIfChangedAsync(AppSettings settings, int knownRevision, bool force, CancellationToken ct = default)
    {
        using var client = ClientFor(settings);
        if (!force)
        {
            var revision = await client.GetRevisionAsync(ct).ConfigureAwait(false);
            if (revision == knownRevision) return null;
        }
        var config = await client.GetConfigAsync(ct).ConfigureAwait(false);
        SaveCache(config);
        return config;
    }
}
