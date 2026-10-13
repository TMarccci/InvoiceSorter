using System.Security.Cryptography;
using System.Text.Json;
using InvoiceSorter.Core.Storage;

namespace InvoiceSorter.Core.Central;

/// <summary>
/// Settings of the server itself (server.json in the data folder): port, keys and Surya. Edited with the server
/// manager; a change takes effect when the service restarts.
/// </summary>
public sealed class ServerConfig
{
    public const string ServiceName = "InvoiceSorterServer";
    public const string FileName = "server.json";

    /// <summary>TCP port of the HTTP API.</summary>
    public int Port { get; set; } = 8770;

    /// <summary>Address to listen on: "0.0.0.0" = every network card, "127.0.0.1" = only this computer.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Key of the Invoice Sorter clients (central configuration, Surya queue).</summary>
    public string ClientKey { get; set; } = "";

    /// <summary>Key for changing the central configuration (server manager).</summary>
    public string AdminKey { get; set; } = "";

    /// <summary>Run Surya OCR on this server for the clients.</summary>
    public bool SuryaEnabled { get; set; } = true;

    /// <summary>python.exe of a Surya environment; empty = the server's own (surya\venv in the data folder).</summary>
    public string SuryaPython { get; set; } = "";

    /// <summary>Most pages waiting in the queue at the same time.</summary>
    public int MaxQueue { get; set; } = 500;

    /// <summary>Longest time one page may take.</summary>
    public int JobTimeoutMinutes { get; set; } = 10;

    /// <summary>Largest accepted page image.</summary>
    public int MaxImageMb { get; set; } = 40;

    /// <summary>Optional HTTPS certificate (.pfx) and its password.</summary>
    public string CertificatePath { get; set; } = "";
    public string CertificatePassword { get; set; } = "";

    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>The data folder: --data &lt;dir&gt;, INVOICESORTER_SERVER_DATA, or C:\ProgramData\TMarccci\InvoiceSorter\Server.</summary>
    public static string ResolveDataDir(string[] args)
    {
        var i = Array.IndexOf(args, "--data");
        if (i >= 0 && i + 1 < args.Length) return Path.GetFullPath(args[i + 1]);
        var env = Environment.GetEnvironmentVariable("INVOICESORTER_SERVER_DATA");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        return DefaultDataDir;
    }

    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TMarccci", "InvoiceSorter", "Server");

    /// <summary>Loads server.json; the first start creates it with new random keys.</summary>
    public static ServerConfig LoadOrCreate(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, FileName);
        ServerConfig config;
        if (File.Exists(path))
            config = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), JsonStore.Options) ?? new();
        else
            config = new();
        bool changed = !File.Exists(path);
        if (string.IsNullOrWhiteSpace(config.ClientKey)) { config.ClientKey = NewKey(); changed = true; }
        if (string.IsNullOrWhiteSpace(config.AdminKey)) { config.AdminKey = NewKey(); changed = true; }
        if (changed) config.Save(dataDir);
        return config;
    }

    public void Save(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, FileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, JsonStore.Options));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
