using System.Diagnostics;
using System.Windows;
using InvoiceSorter.Core;
using InvoiceSorter.Core.Logging;

namespace InvoiceSorter.App;

/// <summary>
/// "New version" button of the client and the server manager: downloads the installer of the release, starts it and
/// closes the program (the installer replaces the files, then the program can be started again).
/// </summary>
public static class UpdateInstaller
{
    /// <param name="installerPrefix"><see cref="UpdateChecker.ClientInstaller"/> or <see cref="UpdateChecker.ServerInstaller"/>.</param>
    /// <param name="status">Shows the download progress ("Letöltés… 45%").</param>
    public static async Task RunAsync(Window owner, UpdateChecker.Release release, string installerPrefix, string what, Action<string> status)
    {
        var asset = release.Installer(installerPrefix);
        if (asset == null)
        {
            // No installer attached (yet): the release page.
            Process.Start(new ProcessStartInfo(release.Url) { UseShellExecute = true });
            return;
        }
        var answer = MessageBox.Show(owner,
            $"Elérhető: {what} {release.Version}.\n\nLetölti és telepíti most? ({asset.Size / 1024.0 / 1024.0:0} MB)\n\n" +
            "A program bezárul, a telepítő rendszergazdai jóváhagyást kér. A beállítások és a sablonok megmaradnak.\n\n" +
            "Nem = a kiadás oldalának megnyitása (újdonságok).",
            "Frissítés", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.No) Process.Start(new ProcessStartInfo(release.Url) { UseShellExecute = true });
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            var progress = new Progress<double>(p => status($"Letöltés… {p:P0}"));
            var path = await UpdateChecker.DownloadAsync(asset, progress);
            status("A telepítő indul…");
            UpdateChecker.StartInstaller(path);
            AppLog.Info($"Frissítés telepítése: {what} {release.Version}");
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            AppLog.Error("A frissítés nem tölthető le", ex);
            status($"Új verzió érhető el: {release.Version}");
            MessageBox.Show(owner, "A frissítés nem tölthető le: " + ex.Message + "\n\nA kiadás oldaláról kézzel is letöltheted.",
                "Frissítés", MessageBoxButton.OK, MessageBoxImage.Warning);
            Process.Start(new ProcessStartInfo(release.Url) { UseShellExecute = true });
        }
    }
}
