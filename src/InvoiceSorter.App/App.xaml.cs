using System.Windows;
using System.Windows.Threading;
using InvoiceSorter.Core;
using InvoiceSorter.Core.Logging;

namespace InvoiceSorter.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Kezeletlen hiba", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Kezeletlen háttérfeladat-hiba", args.Exception);
            args.SetObserved();
        };

        // Developer/test aid: "--data <folder>" uses another folder for settings and styles.
        var dataIndex = Array.IndexOf(e.Args, "--data");
        if (dataIndex >= 0 && dataIndex + 1 < e.Args.Length) AppPaths.DataDir = e.Args[dataIndex + 1];

        // Page images of earlier runs are only needed while the app is open.
        AppPaths.CleanupOldJobs(TimeSpan.FromDays(2));

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // A PDF path on the command line (e.g. "Open with") pre-selects the source file.
        var pdf = e.Args.FirstOrDefault(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        if (pdf != null) window.ViewModel.SetSource(pdf);
        if (e.Args.Contains("--screenshots")) _ = DevScreenshots.RunAsync(window, e.Args);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Váratlan hiba", e.Exception);
        MessageBox.Show(e.Exception.Message, "Váratlan hiba", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
