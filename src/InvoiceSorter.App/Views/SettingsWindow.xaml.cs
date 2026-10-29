using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using InvoiceSorter.Core;
using InvoiceSorter.Core.Central;
using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;
using InvoiceSorter.Core.Storage;

namespace InvoiceSorter.App.Views;

/// <summary>Who uses the settings window.</summary>
public enum SettingsWindowMode
{
    /// <summary>The client: every page, editable (in central mode only the Configuration page).</summary>
    Client,
    /// <summary>The server manager edits the central settings: no Configuration page, no client-only Surya options.</summary>
    Central,
}

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private readonly SettingsWindowMode _mode;
    private readonly bool _centralLocked;

    public AppSettings Result => _vm.BuildResult();

    public SettingsWindow(AppSettings settings, SettingsWindowMode mode = SettingsWindowMode.Client, string? saveCaption = null)
    {
        InitializeComponent();
        WindowFit.Attach(this);
        _mode = mode;
        _vm = new SettingsViewModel(JsonStore.DeepClone(settings), mode);
        _vm.ConfigModeChanged += UpdateLock;
        DataContext = _vm;
        SuryaApiKeyBox.Password = _vm.Settings.SuryaApiKey;
        CentralKeyBox.Password = _vm.Settings.CentralClientKey;
        if (saveCaption != null) SaveButton.Content = saveCaption;

        // Central mode of the client: the central settings can only be looked at (they come from the server).
        _centralLocked = mode == SettingsWindowMode.Client && settings.ConfigMode == ConfigMode.Central;
        if (mode == SettingsWindowMode.Central)
        {
            Title = "Központi beállítások";
            ConfigNavItem.Visibility = Visibility.Collapsed;
            SuryaTestSection.Visibility = Visibility.Collapsed;
            FooterText.Text = "Mentés után minden központi módban futó kliens automatikusan megkapja a módosításokat.";
            Nav.SelectedIndex = Math.Max(1, _lastPage);
        }
        else
        {
            Nav.SelectedIndex = _lastPage;
        }
        UpdateLock();
    }

    /// <summary>The page shown last time (within this run of the app).</summary>
    private static int _lastPage;

    /// <summary>Number of pages in the menu on the left.</summary>
    public int PageCount => Nav.Items.Count;

    /// <summary>Shows one of the pages (0 = configuration, 1 = receiving companies … 7 = dates … 9 = output).</summary>
    public void ShowPage(int index) => Nav.SelectedIndex = Math.Clamp(index, 0, Nav.Items.Count - 1);

    private void Nav_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Nav.SelectedIndex < 0 || (_mode == SettingsWindowMode.Central && Nav.SelectedIndex == 0))
        {
            Nav.SelectedIndex = _mode == SettingsWindowMode.Central ? 1 : 0;
            return;
        }
        _lastPage = Nav.SelectedIndex;
        PageScroll?.ScrollToTop();
        UpdateLock();
    }

    /// <summary>Central mode: the pages after "Konfiguráció" are read-only, with a note on top.</summary>
    private void UpdateLock()
    {
        if (PagesGrid == null || _vm == null) return;
        bool central = _centralLocked && _vm.IsCentralMode && Nav.SelectedIndex > 0;
        bool receivers = Nav.SelectedIndex == 1;   // new receiving companies can be added: they go to the server
        CentralBanner.Visibility = central ? Visibility.Visible : Visibility.Collapsed;
        CentralBannerText.Text = receivers
            ? "Központi konfiguráció: az itt felvett új vevő cégek mentéskor a szerverre kerülnek, és minden gép megkapja őket. Meglévő cég módosítása vagy törlése a szerverkezelőben lehetséges."
            : "Központi konfiguráció: ezeket a beállításokat a szerver rendszergazdája kezeli a szerverkezelőben, itt csak megtekinthetők. Helyi szerkesztéshez válts 'Helyi konfiguráció'-ra a Konfiguráció oldalon.";
        PagesGrid.IsEnabled = !central || receivers;
    }

    private void CentralKey_Changed(object sender, RoutedEventArgs e) => _vm.Settings.CentralClientKey = CentralKeyBox.Password;

    private async void TestCentral_Click(object sender, RoutedEventArgs e)
    {
        TestCentralButton.IsEnabled = false;
        _vm.CentralStatus = "Kapcsolódás…";
        try
        {
            using var client = new CentralClient(_vm.Settings.CentralServerUrl, _vm.Settings.CentralClientKey, TimeSpan.FromSeconds(15));
            var health = await client.GetHealthAsync();
            var surya = health.Surya.State switch
            {
                "ready" => $"Surya kész ({health.Surya.Device})",
                "starting" => "a Surya indul",
                "error" => "a Surya hibás: " + health.Surya.Message,
                _ => "Surya kikapcsolva",
            };
            _vm.CentralStatus = $"✔ Kapcsolódva: {health.MachineName} (szerver {health.Version}) · konfiguráció: {health.Revision}. változat · {surya} · sorban: {health.Queue.Waiting}";
        }
        catch (Exception ex)
        {
            _vm.CentralStatus = "✖ " + ex.Message;
        }
        finally
        {
            TestCentralButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Keyboard.ClearFocus();
        if (_mode == SettingsWindowMode.Client && _vm.IsCentralMode && string.IsNullOrWhiteSpace(_vm.Settings.CentralServerUrl))
        {
            MessageBox.Show(this, "A központi konfigurációhoz add meg a szerver címét (Konfiguráció oldal).", "Beállítások", MessageBoxButton.OK, MessageBoxImage.Warning);
            Nav.SelectedIndex = 0;
            return;
        }
        var receivers = _vm.Receivers.Where(r => !string.IsNullOrWhiteSpace(r.VatNumber)).ToList();
        if (receivers.Count == 0 && !_vm.IsCentralMode &&
            MessageBox.Show(this, "Nincs megadva vevő cég adószáma, így minden számla hibásnak számít.\n\nMégis mented?",
                "Beállítások", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var invalid = receivers.Where(r => VatValidator.Parse(r.VatNumber) is not { } t || !VatValidator.IsChecksumValid(t.Core)).ToList();
        if (invalid.Count > 0 &&
            MessageBox.Show(this, "Ezek az adószámok nem tűnnek érvényesnek (formátum: 12345678-1-23, ellenőrzőszám):\n" +
                                  string.Join("\n", invalid.Select(r => $"{r.Name}: {r.VatNumber}")) + "\n\nMégis mented?",
                "Beállítások", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        DialogResult = true;
    }

    private void InstallSurya_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Megnyílik egy ablak, amely a program saját Python-környezetébe telepíti a Surya OCR-t:\n\n" +
            "• Python 3.10+ kell hozzá (python.org)\n• kb. 3–5 GB-ot tölt le (PyTorch, Surya, modellek)\n• 10–30 percig tart\n\n" +
            "A Surya kódja GPL-3.0, a modellsúlyoknak saját licencük van – üzleti használat előtt nézd meg.\n\nFolytatod?",
            "Surya OCR telepítése", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            SuryaOcr.StartInstaller();
            _vm.SuryaStatus = "A telepítés a PowerShell-ablakban fut. Ha végzett, nyomd meg a „Surya tesztelése” gombot.";
        }
        catch (Exception ex)
        {
            _vm.SuryaStatus = "A telepítő nem indítható: " + ex.Message;
        }
    }

    private void ResetDateLabels_Click(object sender, RoutedEventArgs e) => _vm.ResetDateLabels();

    private void SuryaApiKey_Changed(object sender, RoutedEventArgs e) => _vm.Settings.SuryaApiKey = SuryaApiKeyBox.Password;

    private async void TestSurya_Click(object sender, RoutedEventArgs e)
    {
        TestSuryaButton.IsEnabled = false;
        _vm.SuryaStatus = _vm.SuryaOnServer || _vm.IsCentralMode
            ? "Kapcsolódás a szerverhez és egy mintaoldal olvasása…"
            : "A Surya indul (a modellek betöltése akár egy percig tart; első alkalommal le is tölti őket)…";
        try
        {
            var settings = _vm.BuildResult();
            var sample = Directory.Exists(AppPaths.TempRoot)
                ? Directory.EnumerateFiles(AppPaths.TempRoot, "page_*.jpg", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            _vm.SuryaStatus = "✔ " + await Task.Run(() => SuryaOcr.TestAsync(settings, sample));
        }
        catch (Exception ex)
        {
            _vm.SuryaStatus = "✖ " + ex.Message;
        }
        finally
        {
            TestSuryaButton.IsEnabled = true;
        }
    }
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsWindowMode _mode;

    public SettingsViewModel(AppSettings settings, SettingsWindowMode mode = SettingsWindowMode.Client)
    {
        _mode = mode;
        Settings = settings;
        Receivers = new ObservableCollection<ReceiverCompany>(settings.ReceiverCompanies);
        TitleKeywords = string.Join(Environment.NewLine, settings.InvoiceTitleKeywords);
        ContinuationKeywords = string.Join(Environment.NewLine, settings.ContinuationKeywords);
        ContinuationTitles = string.Join(Environment.NewLine, settings.ContinuationTitles);
        NonInvoiceKeywords = string.Join(Environment.NewLine, settings.NonInvoiceKeywords);
        KnownIssuers = string.Join(Environment.NewLine, settings.KnownIssuers);
        IssueDateLabels = string.Join(Environment.NewLine, settings.IssueDateLabels);
        FulfillmentDateLabels = string.Join(Environment.NewLine, settings.FulfillmentDateLabels);
        DueDateLabels = string.Join(Environment.NewLine, settings.DueDateLabels);
        NotDateLabels = string.Join(Environment.NewLine, settings.NotDateLabels);
        SingleDateKeywords = string.Join(Environment.NewLine, settings.SingleDateKeywords);
        OcrLanguages = new[] { "" }.Concat(PageAnalyzer.AvailableOcrLanguages()).ToList();
    }

    public AppSettings Settings { get; }
    public ObservableCollection<ReceiverCompany> Receivers { get; }

    public sealed record SuryaModeOption(SuryaMode Mode, string Text);

    public IReadOnlyList<SuryaModeOption> SuryaModes { get; } = new[]
    {
        new SuryaModeOption(SuryaMode.Off, "Kikapcsolva – csak Windows OCR (leggyorsabb)"),
        new SuryaModeOption(SuryaMode.UnclearOnly, "Csak a bizonytalan számlákhoz – a problémás oldalakat a Surya újraolvassa (ajánlott)"),
        new SuryaModeOption(SuryaMode.AllPages, "Minden oldal – a Surya minden oldalt elolvas (legpontosabb, leglassabb)"),
    };

    // ------------------------------------------------------------------ configuration mode

    public bool IsLocalMode
    {
        get => Settings.ConfigMode == ConfigMode.Local;
        set { if (value) SetConfigMode(ConfigMode.Local); }
    }

    public bool IsCentralMode
    {
        get => Settings.ConfigMode == ConfigMode.Central;
        set { if (value) SetConfigMode(ConfigMode.Central); }
    }

    public event Action? ConfigModeChanged;

    private void SetConfigMode(ConfigMode mode)
    {
        if (Settings.ConfigMode == mode) return;
        Settings.ConfigMode = mode;
        OnPropertyChanged(nameof(IsLocalMode));
        OnPropertyChanged(nameof(IsCentralMode));
        OnSuryaVisibilityChanged();
        ConfigModeChanged?.Invoke();
    }

    private string _centralStatus = "";
    public string CentralStatus { get => _centralStatus; set => SetProperty(ref _centralStatus, value); }

    /// <summary>Surya on this computer / on another server: only in the client's local mode.</summary>
    public bool ShowSuryaLocation => _mode == SettingsWindowMode.Client && !IsCentralMode;
    public bool ShowSuryaLocalSection => ShowSuryaLocation && SuryaLocal;
    public bool ShowSuryaServerSection => ShowSuryaLocation && SuryaOnServer;
    public bool ShowSuryaCentralNote => !ShowSuryaLocation;

    public string SuryaCentralNote => _mode == SettingsWindowMode.Central
        ? "A Surya a szerveren fut (szerverkezelő › Szerver). Itt azt állítod be, mikor kérjék a kliensek: a szerver egyszerre egy oldalt olvas, a többi sorban áll."
        : "Központi módban a Surya az Invoice Sorter szerveren fut, sorban állással.";

    private void OnSuryaVisibilityChanged()
    {
        OnPropertyChanged(nameof(ShowSuryaLocation));
        OnPropertyChanged(nameof(ShowSuryaLocalSection));
        OnPropertyChanged(nameof(ShowSuryaServerSection));
        OnPropertyChanged(nameof(ShowSuryaCentralNote));
    }

    // ------------------------------------------------------------------ Surya

    private string _suryaStatus = "";

    /// <summary>Surya runs on this computer (radio button).</summary>
    public bool SuryaLocal
    {
        get => Settings.SuryaLocation == SuryaLocation.Local;
        set { if (value) SetLocation(SuryaLocation.Local); }
    }

    /// <summary>Surya runs on a server (radio button).</summary>
    public bool SuryaOnServer
    {
        get => Settings.SuryaLocation == SuryaLocation.Server;
        set { if (value) SetLocation(SuryaLocation.Server); }
    }

    private void SetLocation(SuryaLocation location)
    {
        if (Settings.SuryaLocation == location) return;
        Settings.SuryaLocation = location;
        OnPropertyChanged(nameof(SuryaLocal));
        OnPropertyChanged(nameof(SuryaOnServer));
        OnSuryaVisibilityChanged();
        SuryaStatus = "";
    }

    public string SuryaStatus { get => _suryaStatus; set => SetProperty(ref _suryaStatus, value); }
    public IReadOnlyList<string> OcrLanguages { get; }
    public string TitleKeywords { get; set; }
    public string ContinuationKeywords { get; set; }
    public string ContinuationTitles { get; set; }
    public string NonInvoiceKeywords { get; set; }
    public string KnownIssuers { get; set; }

    private string _issueDateLabels = "", _fulfillmentDateLabels = "", _dueDateLabels = "", _notDateLabels = "", _singleDateKeywords = "";
    public string IssueDateLabels { get => _issueDateLabels; set => SetProperty(ref _issueDateLabels, value); }
    public string FulfillmentDateLabels { get => _fulfillmentDateLabels; set => SetProperty(ref _fulfillmentDateLabels, value); }
    public string DueDateLabels { get => _dueDateLabels; set => SetProperty(ref _dueDateLabels, value); }
    public string NotDateLabels { get => _notDateLabels; set => SetProperty(ref _notDateLabels, value); }
    public string SingleDateKeywords { get => _singleDateKeywords; set => SetProperty(ref _singleDateKeywords, value); }

    public void ResetDateLabels()
    {
        IssueDateLabels = string.Join(Environment.NewLine, AppSettings.DefaultIssueDateLabels());
        FulfillmentDateLabels = string.Join(Environment.NewLine, AppSettings.DefaultFulfillmentDateLabels());
        DueDateLabels = string.Join(Environment.NewLine, AppSettings.DefaultDueDateLabels());
        NotDateLabels = string.Join(Environment.NewLine, AppSettings.DefaultNotDateLabels());
        SingleDateKeywords = string.Join(Environment.NewLine, AppSettings.DefaultSingleDateKeywords());
    }

    public string BlankThresholdPercent
    {
        get => (Settings.BlankInkThreshold * 100).ToString("0.###", CultureInfo.CurrentCulture);
        set
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var v)) Settings.BlankInkThreshold = Math.Clamp(v / 100, 0, 0.5);
        }
    }

    private static List<string> Lines(string s) =>
        s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    public AppSettings BuildResult()
    {
        Settings.ReceiverCompanies = Receivers.Where(r => !string.IsNullOrWhiteSpace(r.VatNumber) || !string.IsNullOrWhiteSpace(r.Name)).ToList();
        Settings.InvoiceTitleKeywords = Lines(TitleKeywords);
        Settings.ContinuationKeywords = Lines(ContinuationKeywords);
        Settings.ContinuationTitles = Lines(ContinuationTitles);
        Settings.NonInvoiceKeywords = Lines(NonInvoiceKeywords);
        Settings.KnownIssuers = Lines(KnownIssuers).Distinct().OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase).ToList();
        Settings.IssueDateLabels = Lines(IssueDateLabels).Distinct().ToList();
        Settings.FulfillmentDateLabels = Lines(FulfillmentDateLabels).Distinct().ToList();
        Settings.DueDateLabels = Lines(DueDateLabels).Distinct().ToList();
        Settings.NotDateLabels = Lines(NotDateLabels).Distinct().ToList();
        Settings.SingleDateKeywords = Lines(SingleDateKeywords).Distinct().ToList();
        Settings.FuzzyTolerance = Math.Clamp(Settings.FuzzyTolerance, 0, 0.4);
        Settings.RenderDpi = Math.Clamp(Settings.RenderDpi, 100, 400);
        return Settings;
    }
}
