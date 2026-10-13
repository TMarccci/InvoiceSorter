using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using InvoiceSorter.Core.Central;
using InvoiceSorter.Core.Logging;
using InvoiceSorter.Core.Storage;

namespace InvoiceSorter.ServerManager;

/// <summary>
/// State of the local Invoice Sorter server for the manager window: the Windows service, the API (health, queue,
/// central configuration) and server.json. Refreshed every two seconds while the window is open.
/// </summary>
public sealed class ManagerViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private bool _refreshing;

    public ManagerViewModel(string dataDir)
    {
        DataDir = dataDir;
        Config = ServerConfig.LoadOrCreate(dataDir);
        Edit = JsonStore.DeepClone(Config);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public string DataDir { get; }

    /// <summary>server.json as the service uses it (saved).</summary>
    public ServerConfig Config { get; private set; }

    /// <summary>The copy edited on the "Szerver" page.</summary>
    public ServerConfig Edit { get; private set; }

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public CentralClient Client() => new($"http://127.0.0.1:{Config.Port}", Config.AdminKey, TimeSpan.FromSeconds(30));

    // ------------------------------------------------------------------------------------------ service

    private string _serviceText = "";
    private bool _serviceInstalled, _serviceRunning, _serviceBusy;

    public string ServiceText { get => _serviceText; private set => SetProperty(ref _serviceText, value); }
    public bool ServiceInstalled { get => _serviceInstalled; private set => SetProperty(ref _serviceInstalled, value); }
    public bool ServiceRunning { get => _serviceRunning; private set { if (SetProperty(ref _serviceRunning, value)) OnPropertyChanged(nameof(ServiceStopped)); } }
    public bool ServiceStopped => ServiceInstalled && !ServiceRunning;
    public bool ServiceBusy { get => _serviceBusy; private set => SetProperty(ref _serviceBusy, value); }

    private void ReadService()
    {
        try
        {
            using var sc = new ServiceController(ServerConfig.ServiceName);
            var status = sc.Status;
            ServiceInstalled = true;
            ServiceRunning = status == ServiceControllerStatus.Running;
            ServiceText = status switch
            {
                ServiceControllerStatus.Running => "Fut",
                ServiceControllerStatus.Stopped => "Leállítva",
                ServiceControllerStatus.StartPending => "Indul…",
                ServiceControllerStatus.StopPending => "Leáll…",
                _ => status.ToString(),
            };
        }
        catch (InvalidOperationException)
        {
            ServiceInstalled = false;
            ServiceRunning = false;
            ServiceText = "Nincs telepítve";
        }
        OnPropertyChanged(nameof(ServiceStopped));
    }

    public Task StartServiceAsync() => ControlServiceAsync(sc =>
    {
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }, "elindítva");

    public Task StopServiceAsync() => ControlServiceAsync(sc =>
    {
        if (sc.Status != ServiceControllerStatus.Stopped) sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
    }, "leállítva");

    public Task RestartServiceAsync() => ControlServiceAsync(sc =>
    {
        if (sc.Status != ServiceControllerStatus.Stopped)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
        }
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }, "újraindítva");

    private async Task ControlServiceAsync(Action<ServiceController> action, string done)
    {
        ServiceBusy = true;
        try
        {
            await Task.Run(() =>
            {
                using var sc = new ServiceController(ServerConfig.ServiceName);
                action(sc);
            });
            AppLog.Info($"Szerverkezelő: a szolgáltatás {done}");
            Health = null;
        }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow!, ex.InnerException?.Message ?? ex.Message, "Szolgáltatás", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ServiceBusy = false;
            await RefreshAsync();
        }
    }

    // ------------------------------------------------------------------------------------------ API

    private ServerHealth? _health;
    private string _apiError = "";
    private CentralConfig? _central;

    public ServerHealth? Health
    {
        get => _health;
        private set
        {
            SetProperty(ref _health, value);
            OnPropertyChanged(nameof(SuryaText));
            OnPropertyChanged(nameof(QueueText));
            OnPropertyChanged(nameof(Online));
        }
    }

    public bool Online => Health != null;
    public string ApiError { get => _apiError; private set => SetProperty(ref _apiError, value); }

    public CentralConfig? Central
    {
        get => _central;
        private set { SetProperty(ref _central, value); OnPropertyChanged(nameof(CentralText)); }
    }

    public string SuryaText => Health?.Surya.State switch
    {
        null => "–",
        "ready" => $"Kész · {Health.Surya.Device} · surya {Health.Surya.Version}",
        "starting" => "Indul (a modellek betöltése)…",
        "error" => "Hiba: " + Health.Surya.Message,
        _ => "Kikapcsolva",
    };

    public string QueueText => Health == null ? "–" :
        $"{Health.Queue.Waiting} vár · {Health.Queue.Running} fut · {Health.Queue.Processed} kész · {Health.Queue.Failed} sikertelen" +
        (Health.Queue.AverageSeconds is { } avg ? $" · átlag {avg:0.0} mp/oldal" : "");

    public string CentralText => Central == null ? "–" :
        $"{Central.Revision}. változat · {Central.Styles.Count} sablon · {Central.Settings.ReceiverCompanies.Count} vevő cég · " +
        $"módosította: {(string.IsNullOrEmpty(Central.UpdatedBy) ? "–" : Central.UpdatedBy)}, {Central.UpdatedAt:yyyy.MM.dd HH:mm}";

    public ObservableCollection<OcrJobInfo> Jobs { get; } = new();

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            ReadService();
            if (ServiceInstalled && !ServiceRunning)
            {
                Health = null;
                ApiError = "A szolgáltatás nem fut.";
                Jobs.Clear();
                return;
            }
            // Not installed as a service: the server may still run in a console window (InvoiceSorterServer.exe).
            using var client = Client();
            var health = await client.GetHealthAsync();
            Health = health;
            ApiError = "";
            if (!ServiceInstalled) ServiceText = "Konzolos módban fut";
            if (Central == null || Central.Revision != health.Revision) Central = await client.GetConfigAsync();
            var jobs = await client.GetQueueAsync();
            Jobs.Clear();
            foreach (var j in jobs.OrderBy(j => j.IsFinished).ThenByDescending(j => j.QueuedAt)) Jobs.Add(j);
        }
        catch (Exception ex)
        {
            Health = null;
            ApiError = ServiceInstalled ? ex.Message : "A szerver szolgáltatás nincs telepítve, és konzolos módban sem fut.";
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Saves the central configuration (styles and/or settings) on the server – the clients get it automatically.</summary>
    public async Task<bool> SaveCentralAsync(List<Core.Models.InvoiceStyle>? styles, Core.Models.AppSettings? settings)
    {
        if (Central == null) return false;
        try
        {
            using var client = Client();
            Central = await client.PutConfigAsync(new ConfigUpdate
            {
                Styles = styles ?? Central.Styles,
                Settings = settings ?? Central.Settings,
                BasedOnRevision = Central.Revision,
                UpdatedBy = $"{Environment.UserName} (szerverkezelő)",
            });
            AppLog.Info($"Szerverkezelő: központi konfiguráció mentve – {Central.Revision}. változat");
            return true;
        }
        catch (CentralServerException ex) when (ex.Status == 409)
        {
            MessageBox.Show(Application.Current.MainWindow!, ex.Message + "\n\nA legfrissebb változat betöltődött – nyisd meg újra a szerkesztőt.",
                "Közben módosult", MessageBoxButton.OK, MessageBoxImage.Warning);
            Central = null;
            await RefreshAsync();
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow!, ex.Message, "Mentés", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ------------------------------------------------------------------------------------------ connection info

    /// <summary>Addresses the clients can use ("http://SZERVER:8770", one per IPv4 address).</summary>
    public IReadOnlyList<string> Addresses
    {
        get
        {
            var scheme = string.IsNullOrWhiteSpace(Config.CertificatePath) ? "http" : "https";
            var list = new List<string> { $"{scheme}://{Environment.MachineName}:{Config.Port}" };
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                                                                          n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                    foreach (var a in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                        list.Add($"{scheme}://{a.Address}:{Config.Port}");
            }
            catch { /* only the machine name */ }
            if (Config.BindAddress == "127.0.0.1") list = new List<string> { $"{scheme}://127.0.0.1:{Config.Port} (csak erről a gépről)" };
            return list.Distinct().ToList();
        }
    }

    // ------------------------------------------------------------------------------------------ server.json

    /// <summary>Writes the edited server settings; they take effect when the service restarts.</summary>
    public void SaveServerConfig()
    {
        Edit.Port = Math.Clamp(Edit.Port, 1, 65535);
        Edit.MaxQueue = Math.Clamp(Edit.MaxQueue, 1, 100000);
        Edit.JobTimeoutMinutes = Math.Clamp(Edit.JobTimeoutMinutes, 1, 240);
        Edit.MaxImageMb = Math.Clamp(Edit.MaxImageMb, 1, 500);
        Edit.Save(DataDir);
        if (Edit.Port != Config.Port) UpdateFirewallPort(Edit.Port);
        Config = JsonStore.DeepClone(Edit);
        OnPropertyChanged(nameof(Config));
        OnPropertyChanged(nameof(Addresses));
        AppLog.Info("Szerverkezelő: server.json mentve");
    }

    /// <summary>Moves the firewall rule made by the installer to the new port (no rule: nothing happens).</summary>
    private static void UpdateFirewallPort(int port)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh.exe",
                $"advfirewall firewall set rule name=\"Invoice Sorter Server\" new localport={port}")
            { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(10000);
            AppLog.Info($"Szerverkezelő: tűzfalszabály a(z) {port}-es portra");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"A tűzfalszabály nem módosítható: {ex.Message}");
        }
    }

    public void NewClientKey()
    {
        Edit.ClientKey = ServerConfig.NewKey();
        Edit = JsonStore.DeepClone(Edit);
        OnPropertyChanged(nameof(Edit));
    }

    public void ReloadServerConfig()
    {
        Config = ServerConfig.LoadOrCreate(DataDir);
        Edit = JsonStore.DeepClone(Config);
        OnPropertyChanged(nameof(Config));
        OnPropertyChanged(nameof(Edit));
        OnPropertyChanged(nameof(Addresses));
    }

    public static void OpenFolder(string folder)
    {
        if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
