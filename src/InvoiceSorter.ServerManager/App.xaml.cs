using System.Windows;
using System.Windows.Threading;
using InvoiceSorter.Core;
using InvoiceSorter.Core.Central;
using InvoiceSorter.Core.Logging;

namespace InvoiceSorter.ServerManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // The manager works on the data folder of the server (server.json, central.json, Logs, surya).
        var dataDir = ServerConfig.ResolveDataDir(e.Args);
        AppPaths.LocalDir = dataDir;
        AppPaths.DataDir = dataDir;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
        var window = new MainWindow(dataDir);
        MainWindow = window;
        window.Show();
        if (e.Args.Contains("--screenshots")) _ = window.TakeScreenshotsAsync(e.Args);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Szerverkezelő: váratlan hiba", e.Exception);
        MessageBox.Show(e.Exception.Message, "Váratlan hiba", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
