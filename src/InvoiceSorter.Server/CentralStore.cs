using System.Text.Json;
using InvoiceSorter.Core.Central;
using InvoiceSorter.Core.Logging;
using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;
using InvoiceSorter.Core.Storage;

namespace InvoiceSorter.Server;

/// <summary>
/// The central configuration (central.json in the data folder): the styles and settings every client receives.
/// Each change increases the revision and keeps a copy of the previous version in backups\.
/// </summary>
public sealed class CentralStore
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly string _backupDir;
    private CentralConfig _config;

    public CentralStore(ServerPaths paths)
    {
        _path = Path.Combine(paths.DataDir, "central.json");
        _backupDir = Path.Combine(paths.DataDir, "backups");
        _config = Load();
    }

    public int Revision { get { lock (_sync) return _config.Revision; } }

    public CentralConfig Get()
    {
        lock (_sync) return JsonStore.DeepClone(_config);
    }

    private CentralConfig Load()
    {
        CentralConfig config;
        if (File.Exists(_path))
        {
            config = JsonSerializer.Deserialize<CentralConfig>(File.ReadAllText(_path), JsonStore.Options) ?? new();
        }
        else
        {
            config = new CentralConfig
            {
                Revision = 1,
                UpdatedBy = "telepítés",
                Styles = JsonStore.LoadDefaultStyles(),
                Settings = AppSettings.CreateDefault(),
            };
            config.Settings.BuiltInStylesVersion = JsonStore.BuiltInStylesVersion;
            AppLog.Info("Új központi konfiguráció a beépített sablonokkal");
            Write(config);
        }

        // A newer server version brings new / improved built-in styles: the same upgrade as on the clients.
        if (JsonStore.UpgradeBuiltInStyles(config.Styles, config.Settings, _path))
        {
            config.Revision++;
            config.UpdatedAt = DateTime.Now;
            config.UpdatedBy = "szerverfrissítés";
            Write(config);
        }
        return config;
    }

    /// <summary>Saves new styles and settings. Throws <see cref="ConflictException"/> when someone saved in between.</summary>
    public CentralConfig Update(ConfigUpdate update)
    {
        lock (_sync)
        {
            if (update.BasedOnRevision != 0 && update.BasedOnRevision != _config.Revision)
                throw new ConflictException($"Időközben valaki más is mentett (most {_config.Revision}. változat, a tiéd a(z) {update.BasedOnRevision}. alapján készült). Töltsd be újra a konfigurációt.");
            Backup();
            var settings = JsonStore.DeepClone(update.Settings);
            // Nothing machine-specific in the shared settings (keys, paths, folders).
            settings.ConfigMode = ConfigMode.Local;
            settings.CentralServerUrl = settings.CentralClientKey = settings.SuryaServerUrl = settings.SuryaApiKey = settings.SuryaPython = "";
            settings.LastSourceFolder = settings.LastOutputFolder = "";
            settings.CentralRevision = 0;
            _config = new CentralConfig
            {
                Revision = _config.Revision + 1,
                UpdatedAt = DateTime.Now,
                UpdatedBy = string.IsNullOrWhiteSpace(update.UpdatedBy) ? "?" : update.UpdatedBy,
                Styles = StyleOrder.Sort(update.Styles),
                Settings = settings,
            };
            Write(_config);
            AppLog.Info($"Központi konfiguráció mentve: {_config.Revision}. változat ({_config.UpdatedBy}), {_config.Styles.Count} sablon");
            return JsonStore.DeepClone(_config);
        }
    }

    /// <summary>An issuer name corrected on a client: added to the known issuers (with its tax number). True when new.</summary>
    public bool LearnIssuer(string name, string? taxNumber, string who)
    {
        lock (_sync)
        {
            var tax = VatValidator.Parse(taxNumber);
            if (!InvoiceProcessor.LearnIssuer(name, _config.Settings, tax)) return false;
            Backup();
            _config.Revision++;
            _config.UpdatedAt = DateTime.Now;
            _config.UpdatedBy = who;
            Write(_config);
            AppLog.Info($"Ismert kibocsátó hozzáadva: {name}{(tax != null ? " | " + tax.Full : "")} ({who}) – {_config.Revision}. változat");
            return true;
        }
    }

    private void Backup()
    {
        try
        {
            if (!File.Exists(_path)) return;
            Directory.CreateDirectory(_backupDir);
            File.Copy(_path, Path.Combine(_backupDir, $"central-{_config.Revision:00000}.json"), overwrite: true);
            // Keep the last 50 versions.
            foreach (var old in Directory.GetFiles(_backupDir, "central-*.json").OrderByDescending(f => f).Skip(50))
                File.Delete(old);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"A központi konfigurációról nem készült biztonsági másolat: {ex.Message}");
        }
    }

    private void Write(CentralConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(config, JsonStore.Options));
        File.Move(_path + ".tmp", _path, overwrite: true);
    }
}

public sealed class ConflictException(string message) : Exception(message);

/// <summary>The data folder of the server.</summary>
public sealed record ServerPaths(string DataDir)
{
    public string TempDir => Path.Combine(DataDir, "tmp");
}
