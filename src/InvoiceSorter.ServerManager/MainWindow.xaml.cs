using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InvoiceSorter.App.Views;
using InvoiceSorter.Core;
using InvoiceSorter.Core.Central;
using InvoiceSorter.Core.Logging;
using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;
using InvoiceSorter.Core.Storage;
using Microsoft.Win32;

namespace InvoiceSorter.ServerManager;

public partial class MainWindow : Window
{
    private readonly ManagerViewModel _vm;

    public MainWindow(string dataDir)
    {
        InitializeComponent();
        _vm = new ManagerViewModel(dataDir);
        DataContext = _vm;
        VersionText.Text = "v" + ManagerViewModel.Version;
        CertPasswordBox.Password = _vm.Edit.CertificatePassword;
        Loaded += (_, _) => _vm.Start();
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PageScroll?.ScrollToTop();
        if (Nav.SelectedIndex == 4) LoadLog();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.AvailableUpdate is { } release)
            await global::InvoiceSorter.App.UpdateInstaller.RunAsync(this, release, UpdateChecker.ServerInstaller, "Invoice Sorter Server", t => _vm.UpdateText = t);
    }

    // ------------------------------------------------------------------------------------------ service

    private async void StartService_Click(object sender, RoutedEventArgs e) => await _vm.StartServiceAsync();
    private async void StopService_Click(object sender, RoutedEventArgs e) => await _vm.StopServiceAsync();
    private async void RestartService_Click(object sender, RoutedEventArgs e) => await _vm.RestartServiceAsync();

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string text }) Clipboard.SetText(text.Split(' ')[0]);
    }

    private void CopyClientKey_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(_vm.Config.ClientKey);

    // ------------------------------------------------------------------------------------------ central configuration

    private bool CheckOnline()
    {
        if (_vm.Central != null) return true;
        MessageBox.Show(this, "A szerver nem érhető el – indítsd el a szolgáltatást (Áttekintés).\n\n" + _vm.ApiError, "Központi konfiguráció",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private async void EditStyles_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckOnline()) return;
        var central = _vm.Central!;
        var editor = new StyleEditorWindow(central.Styles, central.Settings, null, null, createNew: false, saveCaption: "Mentés a szerverre") { Owner = this };
        if (editor.ShowDialog() == true && await _vm.SaveCentralAsync(editor.ResultStyles, null))
            MessageBox.Show(this, $"A sablonok mentve ({_vm.Central!.Revision}. változat). A kliensek automatikusan megkapják.", "Központi konfiguráció",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void EditSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckOnline()) return;
        var window = new SettingsWindow(_vm.Central!.Settings, SettingsWindowMode.Central, "Mentés a szerverre") { Owner = this };
        if (window.ShowDialog() == true && await _vm.SaveCentralAsync(null, window.Result))
            MessageBox.Show(this, $"A beállítások mentve ({_vm.Central!.Revision}. változat). A kliensek automatikusan megkapják.", "Központi konfiguráció",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Takes over the local configuration of a client (styles.json and/or settings.json).</summary>
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckOnline()) return;
        var dlg = new OpenFileDialog
        {
            Title = "Válaszd ki a styles.json és/vagy settings.json fájlt (Ctrl-lal mindkettőt)",
            Filter = "Invoice Sorter konfiguráció (styles.json; settings.json)|styles.json;settings.json|JSON-fájlok (*.json)|*.json",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        List<InvoiceStyle>? styles = null;
        AppSettings? settings = null;
        try
        {
            foreach (var file in dlg.FileNames)
            {
                var json = File.ReadAllText(file);
                if (json.TrimStart().StartsWith('[')) styles = JsonStore.LoadStyles(file);
                else settings = JsonStore.LoadSettings(file);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Importálás", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (styles == null && settings == null) return;
        var what = string.Join(" és ", new[] { styles != null ? $"{styles.Count} sablon" : null, settings != null ? "a beállítások" : null }.OfType<string>());
        if (MessageBox.Show(this, $"Lecseréli a központi konfigurációt: {what}.\n\nAz előző változat biztonsági mentésként megmarad. Folytatod?",
                "Importálás", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (styles != null) JsonStore.MigrateRegexes(styles);
        if (await _vm.SaveCentralAsync(styles, settings))
            MessageBox.Show(this, $"Importálva – {_vm.Central!.Revision}. változat.", "Importálás", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckOnline()) return;
        var dlg = new SaveFileDialog { Title = "Központi konfiguráció exportálása", FileName = $"invoice-sorter-central-{_vm.Central!.Revision}.json", Filter = "JSON-fájl (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(_vm.Central, JsonStore.Options), new UTF8Encoding(false));
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e) => ManagerViewModel.OpenFolder(Path.Combine(_vm.DataDir, "backups"));

    // ------------------------------------------------------------------------------------------ queue / Surya

    private async void RestartSurya_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckOnline()) return;
        try
        {
            using var client = _vm.Client();
            await client.RestartSuryaAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Surya újraindítása", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await _vm.RefreshAsync();
    }

    private void InstallSurya_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Megnyílik egy ablak, amely a szerver adatmappájába telepíti a Surya OCR-t:\n\n" +
            "• Python 3.10+ kell hozzá (python.org, 'minden felhasználónak' telepítve)\n• kb. 3–5 GB-ot tölt le (PyTorch, Surya, modellek)\n• 10–30 percig tart\n\n" +
            "Ha végzett: Surya-sor › Surya újraindítása (vagy a szolgáltatás újraindítása).\n\n" +
            "A Surya kódja GPL-3.0, a modellsúlyoknak saját licencük van – üzleti használat előtt nézd meg.\n\nFolytatod?",
            "Surya OCR telepítése", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK) return;
        try { SuryaOcr.StartInstaller(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Surya OCR telepítése", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OpenSuryaFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SuryaOcr.HomeDir);
        ManagerViewModel.OpenFolder(SuryaOcr.HomeDir);
    }

    // ------------------------------------------------------------------------------------------ server.json

    private void CertPassword_Changed(object sender, RoutedEventArgs e) => _vm.Edit.CertificatePassword = CertPasswordBox.Password;

    private void BrowseCertificate_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "HTTPS tanúsítvány", Filter = "PFX-tanúsítvány (*.pfx;*.p12)|*.pfx;*.p12" };
        if (dlg.ShowDialog(this) != true) return;
        _vm.Edit.CertificatePath = dlg.FileName;
        DataContext = null;
        DataContext = _vm;
    }

    private void NewClientKey_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Új kliens kulcsot készít. Mentés után minden kliensen át kell írni a kulcsot.\n\nFolytatod?", "Új kliens kulcs",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            _vm.NewClientKey();
    }

    private async void SaveServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _vm.SaveServerConfig();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Mentés", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (_vm.ServiceInstalled) await _vm.RestartServiceAsync();
    }

    private void RevertServer_Click(object sender, RoutedEventArgs e)
    {
        _vm.ReloadServerConfig();
        CertPasswordBox.Password = _vm.Edit.CertificatePassword;
    }

    // ------------------------------------------------------------------------------------------ log

    private void RefreshLog_Click(object sender, RoutedEventArgs e) => LoadLog();

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) => ManagerViewModel.OpenFolder(AppPaths.LogDir);

    /// <summary>The newest log file of the server (the last 2000 lines).</summary>
    private void LoadLog()
    {
        try
        {
            var file = Directory.Exists(AppPaths.LogDir)
                ? new DirectoryInfo(AppPaths.LogDir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTime).FirstOrDefault()
                : null;
            if (file == null)
            {
                LogFileText.Text = "Még nincs naplófájl.";
                LogBox.Text = "";
                return;
            }
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n');
            LogBox.Text = string.Join("\n", lines.TakeLast(2000));
            LogBox.ScrollToEnd();
            LogFileText.Text = file.FullName;
        }
        catch (Exception ex)
        {
            LogBox.Text = ex.Message;
        }
    }

    // ------------------------------------------------------------------------------------------ documentation screenshots

    /// <summary>Developer aid: "--screenshots &lt;folder&gt;" saves every page as PNG (README / documentation).</summary>
    public async Task TakeScreenshotsAsync(string[] args)
    {
        var i = Array.IndexOf(args, "--screenshots");
        var dir = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : Path.Combine(Path.GetTempPath(), "manager-shots");
        Directory.CreateDirectory(dir);
        await Task.Delay(3000);
        for (int page = 0; page < Nav.Items.Count; page++)
        {
            Nav.SelectedIndex = page;
            await Task.Delay(800);
            Save(this, Path.Combine(dir, $"manager-{page}.png"));
        }
        Close();
    }

    private static void Save(FrameworkElement element, string file)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var bmp = new RenderTargetBitmap((int)(element.ActualWidth * dpi.DpiScaleX), (int)(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(element);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        png.Save(fs);
    }
}
