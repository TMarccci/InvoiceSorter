using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InvoiceSorter.App.Controls;
using InvoiceSorter.Core;
using InvoiceSorter.Core.Central;
using InvoiceSorter.Core.Logging;
using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;
using InvoiceSorter.Core.Storage;
using Microsoft.Win32;

namespace InvoiceSorter.App.ViewModels;

public enum WizardStep { Source = 1, Split = 2, Read = 3, Review = 4, Save = 5 }

public sealed class MainViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private CancellationTokenSource? _cts;

    private WizardStep _step = WizardStep.Source;
    private string _sourcePath = "";
    private string _sourceInfo = "";
    private InvoiceJob? _job;
    private bool _isBusy;
    private double _progressValue;
    private double _progressMax = 1;
    private string _progressText = "";
    private bool _showErrorsOnly;
    private InvoiceDocument? _selectedDocument;
    private PageInfo? _selectedPage;
    private IReadOnlyList<PageOverlay> _pageOverlays = Array.Empty<PageOverlay>();
    private string _outputFolder = "";
    private string _saveSummary = "";
    private bool _showDebugLog;
    private bool _isLogExpanded;

    public MainViewModel()
    {
        _localSettings = JsonStore.LoadSettings();
        Settings = _localSettings;
        Styles = new ObservableCollection<InvoiceStyle>();
        LoadConfiguration();
        _outputFolder = Settings.LastOutputFolder;

        AppLog.EntryAdded += OnLogEntry;
        foreach (var e in AppLog.EntriesSince(DateTime.Today)) AllLogEntries.Add(e);
        LogView = CollectionViewSource.GetDefaultView(AllLogEntries);
        LogView.Filter = o => ShowDebugLog || ((LogEntry)o).Level != LogLevel.Debug;

        BrowseSourceCommand = new RelayCommand(BrowseSource, () => !IsBusy);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy && File.Exists(SourcePath));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        RerunDetectionCommand = new RelayCommand(() =>
        {
            var answer = MessageBox.Show(Application.Current.MainWindow!,
                "Újra szétbontja és beolvassa az összes oldalt a jelenlegi sablonokkal?\n\nA kézi módosítások (javított értékek, szétvágások, összevonások) elvesznek.",
                "Felismerés újrafuttatása", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.OK) RerunDetection();
        }, () => Job != null && !IsBusy);
        RereadDocumentCommand = new RelayCommand(RereadDocument, () => SelectedDocument != null);
        SplitHereCommand = new RelayCommand(SplitHere, () => SelectedDocument != null && SelectedPage != null && SelectedDocument.Pages.IndexOf(SelectedPage) > 0);
        MergeWithPreviousCommand = new RelayCommand(MergeWithPrevious, () => SelectedDocument != null && Job != null && Job.Documents.IndexOf(SelectedDocument) > 0);
        MovePageUpCommand = new RelayCommand(() => MoveSelectedPage(-1), () => CanMoveSelectedPage(-1));
        MovePageDownCommand = new RelayCommand(() => MoveSelectedPage(+1), () => CanMoveSelectedPage(+1));
        RotateLeftCommand = new AsyncRelayCommand(() => RotateSelectedPageAsync(270), () => SelectedPage != null && !_rotating && !IsBusy);
        RotateRightCommand = new AsyncRelayCommand(() => RotateSelectedPageAsync(90), () => SelectedPage != null && !_rotating && !IsBusy);
        NextErrorCommand = new RelayCommand(NextError, () => Job != null && Job.Documents.Any(d => d.HasErrors));
        GoToSaveCommand = new RelayCommand(() => Step = WizardStep.Save, () => Job != null && !IsBusy);
        BackToReviewCommand = new RelayCommand(() => Step = WizardStep.Review, () => Job != null && !IsBusy);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !IsBusy);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => Job != null && !IsBusy && !string.IsNullOrWhiteSpace(OutputFolder));
        OpenOutputFolderCommand = new RelayCommand(() => OpenFolder(OutputFolder), () => Directory.Exists(OutputFolder));
        NewJobCommand = new RelayCommand(NewJob, () => !IsBusy);
        OpenLogFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.LogDir));
        ClearLogCommand = new RelayCommand(() => AllLogEntries.Clear());
        AddSuggestedReceiverCommand = new RelayCommand(AddSuggestedReceiver);

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();

        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _syncTimer.Tick += async (_, _) => { if (!IsBusy) await SyncCentralAsync(); };
        _syncTimer.Start();
        SuryaOcr.QueueStatusChanged += text => _dispatcher.BeginInvoke(() => QueueText = text);

        AppLog.Info($"Invoice Sorter {Version} elindult. {Styles.Count} sablon betöltve " +
                    (IsCentral ? $"(központi konfiguráció, {Settings.CentralServerUrl})" : $"({AppPaths.StylesFile})"));
        AppLog.Info($"Windows OCR nyelvek: {string.Join(", ", PageAnalyzer.AvailableOcrLanguages())}; " +
                    $"{Environment.OSVersion.VersionString}, {Environment.ProcessorCount} processzormag");
    }

    /// <summary>Called by the window once it is shown: central configuration and update check in the background.</summary>
    public async Task StartBackgroundChecksAsync()
    {
        await SyncCentralAsync();
        await CheckForUpdatesAsync();
    }

    // ---------------------------------------------------------------- configuration: local or central

    /// <summary>The settings.json of this computer. In central mode only its machine-specific fields are used.</summary>
    private AppSettings _localSettings;
    private CentralConfig? _central;
    private readonly DispatcherTimer _syncTimer;
    private string _centralStatus = "";
    private bool _centralError;

    public bool IsCentral => Settings.ConfigMode == ConfigMode.Central;

    /// <summary>Header chip: "Központi konfiguráció · 12. változat" / "Helyi konfiguráció".</summary>
    public string CentralStatus { get => _centralStatus; private set => SetProperty(ref _centralStatus, value); }

    /// <summary>The server could not be reached at the last check (the cached copy is in use).</summary>
    public bool CentralError { get => _centralError; private set => SetProperty(ref _centralError, value); }

    /// <summary>Loads the styles and settings of the current mode (central: from the local copy of the last download).</summary>
    private void LoadConfiguration()
    {
        if (_localSettings.ConfigMode == ConfigMode.Central)
        {
            _central = CentralSync.LoadCache();
            if (_central != null && _central.Revision == _localSettings.CentralRevision)
            {
                Settings = AppSettings.MergeCentral(_localSettings, _central.Settings);
                SetStyles(_central.Styles);
            }
            else
            {
                // Nothing downloaded yet: the built-in styles until the server answers.
                _central = null;
                Settings = _localSettings;
                SetStyles(JsonStore.LoadDefaultStyles());
            }
        }
        else
        {
            _central = null;
            Settings = _localSettings;
            var styles = JsonStore.LoadStyles();
            if (JsonStore.UpgradeBuiltInStyles(styles, Settings))
            {
                JsonStore.SaveStyles(styles);
                SaveSettingsQuietly();
            }
            SetStyles(styles);
        }
        UpdateCentralStatus();
    }

    private void SetStyles(IEnumerable<InvoiceStyle> styles)
    {
        Styles.Clear();
        foreach (var s in StyleOrder.Sort(styles)) Styles.Add(s);
        OnPropertyChanged(nameof(StyleChoices));
    }

    private void UpdateCentralStatus()
    {
        OnPropertyChanged(nameof(IsCentral));
        CentralStatus = !IsCentral ? "Helyi konfiguráció"
            : _central == null ? "Központi konfiguráció · még nincs letöltve"
            : $"Központi konfiguráció · {_central.Revision}. változat";
    }

    /// <summary>
    /// Central mode: downloads the configuration when the server has a newer one. Runs at start, every 5 minutes and
    /// before processing. When the server cannot be reached, the last downloaded copy stays in use.
    /// </summary>
    public async Task SyncCentralAsync(bool force = false)
    {
        if (!IsCentral || string.IsNullOrWhiteSpace(Settings.CentralServerUrl)) return;
        try
        {
            var config = await CentralSync.FetchIfChangedAsync(Settings, _central?.Revision ?? -1, force || _central == null);
            CentralError = false;
            if (config != null)
            {
                bool first = _central == null;
                _central = config;
                _localSettings.CentralRevision = config.Revision;
                SaveLocalSettings();
                Settings = AppSettings.MergeCentral(_localSettings, config.Settings);
                SetStyles(config.Styles);
                OnPropertyChanged(nameof(Settings));
                OnReceiverChanged();
                AppLog.Info(first
                    ? $"Központi konfiguráció letöltve ({config.Revision}. változat, {config.Styles.Count} sablon)"
                    : $"A központi konfiguráció frissült: {config.Revision}. változat ({config.UpdatedBy}, {config.UpdatedAt:yyyy.MM.dd HH:mm})");
            }
            UpdateCentralStatus();
        }
        catch (Exception ex)
        {
            if (!CentralError) AppLog.Warn($"A központi szerver nem érhető el, a legutóbb letöltött konfiguráció marad: {ex.Message}");
            CentralError = true;
            CentralStatus = _central == null ? "Központi szerver nem érhető el" : $"Központi konfiguráció · {_central.Revision}. változat (offline)";
        }
    }

    // ---------------------------------------------------------------- new version

    private UpdateChecker.Release? _update;

    private string _updateText = "";

    public UpdateChecker.Release? AvailableUpdate
    {
        get => _update;
        private set { SetProperty(ref _update, value); UpdateText = value == null ? "" : $"Új verzió érhető el: {value.Version} – telepítés"; }
    }

    /// <summary>Text of the update button (also the download progress).</summary>
    public string UpdateText { get => _updateText; set => SetProperty(ref _updateText, value); }

    private async Task CheckForUpdatesAsync()
    {
        if (!Settings.CheckForUpdates) return;
        try
        {
            AvailableUpdate = await UpdateChecker.CheckAsync(Version);
            if (AvailableUpdate != null) AppLog.Info($"Új verzió érhető el: {AvailableUpdate.Version} – {AvailableUpdate.Url}");
        }
        catch (Exception ex)
        {
            AppLog.Debug($"A frissítések nem ellenőrizhetők: {ex.Message}");
        }
    }

    public Task InstallUpdateAsync() => AvailableUpdate == null ? Task.CompletedTask :
        UpdateInstaller.RunAsync(Application.Current.MainWindow!, AvailableUpdate, UpdateChecker.ClientInstaller, "Invoice Sorter", t => UpdateText = t);

    // ---------------------------------------------------------------- Surya queue on the server

    private string _queueText = "";

    /// <summary>"Sorban áll a szerveren: 3. hely" while a page waits for the server's Surya OCR.</summary>
    public string QueueText { get => _queueText; private set => SetProperty(ref _queueText, value); }

    public AppSettings Settings { get; private set; }

    /// <summary>Version of the program ("v1.4.0"), set from the git tag by the release build.</summary>
    public static string Version { get; } = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "1.0.0");

    public string AppVersion => Version;
    public ObservableCollection<InvoiceStyle> Styles { get; }

    /// <summary>Style choices for the review combo box (null = no style).</summary>
    public IEnumerable<InvoiceStyle?> StyleChoices => new InvoiceStyle?[] { null }.Concat(Styles);

    // ---------------------------------------------------------------- wizard

    public WizardStep Step
    {
        get => _step;
        set
        {
            if (!SetProperty(ref _step, value)) return;
            OnPropertyChanged(nameof(StepNumber));
            if (value == WizardStep.Save) OnPropertyChanged(nameof(SavePreview));
            RefreshCommands();
        }
    }

    public int StepNumber => (int)Step;

    public string SourcePath
    {
        get => _sourcePath;
        set { if (SetProperty(ref _sourcePath, value)) RefreshCommands(); }
    }

    public string SourceInfo { get => _sourceInfo; set => SetProperty(ref _sourceInfo, value); }

    // ---------------------------------------------------------------- receiving company of the scan

    private ReceiverCompany? _selectedReceiver;

    /// <summary>Choices for the receiving company: null = detect automatically.</summary>
    public IEnumerable<ReceiverCompany?> ReceiverChoices => new ReceiverCompany?[] { null }.Concat(Settings.ReceiverCompanies);

    /// <summary>The receiving company chosen before processing (null = detect automatically).</summary>
    public ReceiverCompany? SelectedReceiver { get => _selectedReceiver; set => SetProperty(ref _selectedReceiver, value); }

    /// <summary>The settings for the current job: buyer VAT is checked against the job's receiving company only.</summary>
    public AppSettings JobSettings => Job == null ? Settings : InvoiceProcessor.SettingsFor(Job, Settings);

    public string ReceiverText => Job?.Receiver is { } r
        ? $"Vevő cég: {r.Name} ({r.VatNumber})" + (Job.Detection?.Match == r ? $" – {Job.Detection.Pages} oldalból {Job.Detection.MatchPages} oldalon felismerve" : "")
        : "Vevő cég: bármelyik beállított cég";

    /// <summary>A company that most pages are addressed to but which is not configured.</summary>
    public ReceiverCompany? SuggestedReceiver => Job?.Detection?.Suggestion;

    public string SuggestionText => SuggestedReceiver is { } s
        ? $"Az oldalak többsége ({Job!.Detection!.Pages} oldalból {Job.Detection.SuggestionPages}) a(z) {(string.IsNullOrEmpty(s.Name) ? "egy cég" : s.Name)} ({s.VatNumber}) nevére szól, amely nem szerepel a vevő cégeid között. " +
          "A vevő adószámai csak a felvétele után ellenőrizhetők."
        : "";

    /// <summary>Adds the suggested company as receiving company and checks all invoices against it.</summary>
    private void AddSuggestedReceiver()
    {
        if (Job?.Detection?.Suggestion is not { } s) return;
        var name = string.IsNullOrWhiteSpace(s.Name) ? $"Cég {s.VatNumber}" : s.Name;
        var answer = MessageBox.Show(Application.Current.MainWindow!,
            $"Felveszed ezt a vevő céget?\n\n{name}\nAdószám: {s.VatNumber}\n\n(A nevét később a Beállításokban módosíthatod.)",
            "Vevő cég felvétele", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        var company = new ReceiverCompany { Name = name, VatNumber = s.VatNumber };
        Settings.ReceiverCompanies.Add(company);
        if (IsCentral)
        {
            // The list of receiving companies belongs to the central configuration: the server shares it with every computer.
            _ = SendReceiversAsync(new[] { company });
        }
        else
        {
            SaveSettingsQuietly();
            AppLog.Info($"Vevő cég felvéve: {company.Name} {company.VatNumber}");
        }
        Job.Receiver = company;
        Job.Detection = Job.Detection with { Match = company, MatchPages = Job.Detection.SuggestionPages, Suggestion = null, SuggestionPages = 0 };
        OnReceiverChanged();
        RerunDetection();
    }

    private void OnReceiverChanged()
    {
        OnPropertyChanged(nameof(ReceiverChoices));
        OnPropertyChanged(nameof(ReceiverText));
        OnPropertyChanged(nameof(SuggestedReceiver));
        OnPropertyChanged(nameof(SuggestionText));
        OnPropertyChanged(nameof(JobReceiver));
    }

    /// <summary>The receiving company of the current job, changeable in the review screen.</summary>
    public ReceiverCompany? JobReceiver
    {
        get => Job?.Receiver;
        set
        {
            if (Job == null || Job.Receiver == value) return;
            Job.Receiver = value;
            AppLog.Info($"A szkennelés vevő cége: {value?.Name ?? "bármelyik beállított cég"}");
            OnReceiverChanged();
            RerunDetection();
        }
    }

    public InvoiceJob? Job
    {
        get => _job;
        private set
        {
            if (!SetProperty(ref _job, value)) return;
            DocumentsView = value == null ? null : CollectionViewSource.GetDefaultView(value.Documents);
            if (DocumentsView != null)
                DocumentsView.Filter = o => o is InvoiceDocument d && (!ShowErrorsOnly || d.HasErrors) && (!ShowLowTrustOnly || d.Trust < Settings.LowTrustThreshold);
            OnPropertyChanged(nameof(DocumentsView));
            UpdateCounts();
        }
    }

    public ICollectionView? DocumentsView { get; private set; }

    public bool IsBusy
    {
        get => _isBusy;
        set { if (SetProperty(ref _isBusy, value)) RefreshCommands(); }
    }

    public double ProgressValue { get => _progressValue; set => SetProperty(ref _progressValue, value); }
    public double ProgressMax { get => _progressMax; set => SetProperty(ref _progressMax, value); }
    public string ProgressText { get => _progressText; set => SetProperty(ref _progressText, value); }

    // ---------------------------------------------------------------- review

    public bool ShowErrorsOnly
    {
        get => _showErrorsOnly;
        set { if (SetProperty(ref _showErrorsOnly, value)) DocumentsView?.Refresh(); }
    }

    public int TotalCount => Job?.Documents.Count ?? 0;
    public int ErrorCount => Job?.Documents.Count(d => d.HasErrors) ?? 0;
    public int ExcludedCount => Job?.Documents.Count(d => d.Excluded) ?? 0;
    public int EditedCount => Job?.Documents.Count(d => d.IsManuallyEdited) ?? 0;
    public int LowTrustCount => Job?.Documents.Count(d => !d.Excluded && d.Trust < Settings.LowTrustThreshold) ?? 0;

    private bool _showLowTrustOnly;

    /// <summary>List filter: only invoices whose trust score is below the low-trust threshold.</summary>
    public bool ShowLowTrustOnly
    {
        get => _showLowTrustOnly;
        set { if (SetProperty(ref _showLowTrustOnly, value)) DocumentsView?.Refresh(); }
    }
    public int OkCount => TotalCount - ErrorCount - ExcludedCount;

    /// <summary>"1:13" – how long splitting, reading and validating took (all steps).</summary>
    public string ProcessingTimeText => Job == null || Job.Timings.Count == 0 ? "" : FormatTime(Job.TotalTime);

    /// <summary>The time of each step, one per line (tooltip).</summary>
    public string ProcessingTimeDetails => Job == null ? "" :
        string.Join(Environment.NewLine, Job.Timings.Select(t => $"{t.Step}: {FormatTime(t.Duration)}")
            .Append($"{Job.Pages.Count} oldal · oldalanként {(Job.Pages.Count == 0 ? 0 : Job.TotalTime.TotalSeconds / Job.Pages.Count):0.0} mp"));

    private IReadOnlyList<InvoiceProcessor.ValueSuggestion> _numberSuggestions = Array.Empty<InvoiceProcessor.ValueSuggestion>();

    /// <summary>
    /// Other invoice numbers the OCR readings show for the selected invoice (style rule, generic rules, text next to the
    /// invoice number labels, re-reads); a click takes one over.
    /// </summary>
    public IReadOnlyList<InvoiceProcessor.ValueSuggestion> NumberSuggestions { get => _numberSuggestions; private set => SetProperty(ref _numberSuggestions, value); }

    private IReadOnlyList<InvoiceProcessor.ValueSuggestion> _issuerSuggestions = Array.Empty<InvoiceProcessor.ValueSuggestion>();

    /// <summary>Other issuer names: by the supplier tax number (known / company register) and from the readings.</summary>
    public IReadOnlyList<InvoiceProcessor.ValueSuggestion> IssuerSuggestions { get => _issuerSuggestions; private set => SetProperty(ref _issuerSuggestions, value); }

    private IReadOnlyList<InvoiceProcessor.ValueSuggestion> _issueDateSuggestions = Array.Empty<InvoiceProcessor.ValueSuggestion>();
    private IReadOnlyList<InvoiceProcessor.ValueSuggestion> _fulfillmentDateSuggestions = Array.Empty<InvoiceProcessor.ValueSuggestion>();
    private IReadOnlyList<InvoiceProcessor.ValueSuggestion> _dueDateSuggestions = Array.Empty<InvoiceProcessor.ValueSuggestion>();

    /// <summary>The other dates printed on the invoice (labelled as this kind first), one click takes one over.</summary>
    public IReadOnlyList<InvoiceProcessor.ValueSuggestion> IssueDateSuggestions { get => _issueDateSuggestions; private set => SetProperty(ref _issueDateSuggestions, value); }
    public IReadOnlyList<InvoiceProcessor.ValueSuggestion> FulfillmentDateSuggestions { get => _fulfillmentDateSuggestions; private set => SetProperty(ref _fulfillmentDateSuggestions, value); }
    public IReadOnlyList<InvoiceProcessor.ValueSuggestion> DueDateSuggestions { get => _dueDateSuggestions; private set => SetProperty(ref _dueDateSuggestions, value); }

    private void UpdateSuggestions()
    {
        var doc = SelectedDocument;
        if (doc == null || Job == null)
        {
            NumberSuggestions = IssuerSuggestions = Array.Empty<InvoiceProcessor.ValueSuggestion>();
            IssueDateSuggestions = FulfillmentDateSuggestions = DueDateSuggestions = Array.Empty<InvoiceProcessor.ValueSuggestion>();
            return;
        }
        try
        {
            NumberSuggestions = InvoiceProcessor.InvoiceNumberSuggestions(doc, Styles.ToList(), JobSettings);
            IssuerSuggestions = InvoiceProcessor.IssuerSuggestions(doc, Styles.ToList(), JobSettings);
            UpdateDateSuggestions(doc);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"A javaslatok nem számíthatók: {ex.Message}");
        }
    }

    /// <summary>Takes over a suggested issuer name (counts as a manual edit and is learned on save).</summary>
    public void ApplyIssuerSuggestion(string value)
    {
        if (SelectedDocument == null) return;
        SelectedDocument.Issuer = value;
        UpdateSuggestions();
    }

    private void UpdateDateSuggestions(InvoiceDocument doc)
    {
        IssueDateSuggestions = DateReader.Suggestions(doc, DateKind.Issue);
        FulfillmentDateSuggestions = DateReader.Suggestions(doc, DateKind.Fulfillment);
        DueDateSuggestions = DateReader.Suggestions(doc, DateKind.Due);
    }

    /// <summary>Takes over a suggested date (counts as a manual edit).</summary>
    public void ApplyDateSuggestion(DateKind kind, string value)
    {
        if (SelectedDocument == null) return;
        SelectedDocument.SetDate(kind, value);
        UpdateDateSuggestions(SelectedDocument);
    }

    /// <summary>Takes over a suggested invoice number (counts as a manual edit).</summary>
    public void ApplyNumberSuggestion(string value)
    {
        if (SelectedDocument == null) return;
        SelectedDocument.InvoiceNumber = value;   // raises the normal "edited by hand" handling
        UpdateSuggestions();
    }
    public int PageCount => Job?.Pages.Count ?? 0;
    public int BlankCount => Job?.Pages.Count(p => p.IsBlank) ?? 0;

    public InvoiceDocument? SelectedDocument
    {
        get => _selectedDocument;
        set
        {
            if (_selectedDocument != null) _selectedDocument.PropertyChanged -= OnSelectedDocumentChanged;
            if (!SetProperty(ref _selectedDocument, value)) return;
            if (value != null) value.PropertyChanged += OnSelectedDocumentChanged;
            SelectedPage = value?.Pages.FirstOrDefault();
            UpdateSuggestions();
            OnPropertyChanged(nameof(ProposedFileName));
            RefreshCommands();
        }
    }

    public PageInfo? SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (!SetProperty(ref _selectedPage, value)) return;
            OnPropertyChanged(nameof(SelectedPageImage));
            UpdateOverlays();
            RefreshCommands();
        }
    }

    public string? SelectedPageImage => SelectedPage?.ImagePath;

    public IReadOnlyList<PageOverlay> PageOverlays { get => _pageOverlays; private set => SetProperty(ref _pageOverlays, value); }

    public string ProposedFileName => SelectedDocument == null ? "" :
        SelectedDocument.Excluded ? "(kizárva – nem kerül mentésre)" :
        SelectedDocument.HasErrors
            ? (Settings.SaveErrorDocuments ? $"{Settings.ErrorFolderName}\\{PdfExporter.BuildErrorFileName(SelectedDocument)}" : "(nem kerül mentésre – hibás)")
            : PdfExporter.BuildFileName(SelectedDocument, Settings);

    // ---------------------------------------------------------------- save

    public string OutputFolder
    {
        get => _outputFolder;
        set { if (SetProperty(ref _outputFolder, value)) RefreshCommands(); }
    }

    public string SaveSummary { get => _saveSummary; set => SetProperty(ref _saveSummary, value); }

    public sealed record SavePreviewRow(int Number, string Pages, string FileName, string StatusText);

    /// <summary>The file each invoice will be saved as.</summary>
    public IReadOnlyList<SavePreviewRow> SavePreview => Job == null ? Array.Empty<SavePreviewRow>() : Job.Documents.Select(d => new SavePreviewRow(
        d.Number, d.PageRange,
        d.Excluded ? "(kizárva – nem kerül mentésre)"
            : !d.HasErrors ? PdfExporter.BuildFileName(d, Settings)
            : Settings.SaveErrorDocuments ? $"{Settings.ErrorFolderName}\\{PdfExporter.BuildErrorFileName(d)}   ({d.ErrorsText})"
            : $"(nem kerül mentésre: {d.ErrorsText})",
        d.StatusText)).ToList();

    // ---------------------------------------------------------------- log

    public ObservableCollection<LogEntry> AllLogEntries { get; } = new();
    public ICollectionView LogView { get; }

    public bool ShowDebugLog
    {
        get => _showDebugLog;
        set { if (SetProperty(ref _showDebugLog, value)) LogView.Refresh(); }
    }

    public bool IsLogExpanded { get => _isLogExpanded; set => SetProperty(ref _isLogExpanded, value); }

    public string LastLogLine => AllLogEntries.LastOrDefault(e => e.Level != LogLevel.Debug)?.ToString() ?? "";

    // ---------------------------------------------------------------- commands

    public IRelayCommand BrowseSourceCommand { get; }
    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand RerunDetectionCommand { get; }
    public IRelayCommand RereadDocumentCommand { get; }
    public IRelayCommand SplitHereCommand { get; }
    public IRelayCommand MergeWithPreviousCommand { get; }
    public IRelayCommand MovePageUpCommand { get; }
    public IRelayCommand MovePageDownCommand { get; }
    public IAsyncRelayCommand RotateLeftCommand { get; }
    public IAsyncRelayCommand RotateRightCommand { get; }
    public IRelayCommand NextErrorCommand { get; }
    public IRelayCommand GoToSaveCommand { get; }
    public IRelayCommand BackToReviewCommand { get; }
    public IRelayCommand BrowseOutputCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand OpenOutputFolderCommand { get; }
    public IRelayCommand NewJobCommand { get; }
    public IRelayCommand OpenLogFolderCommand { get; }
    public IRelayCommand ClearLogCommand { get; }
    public IRelayCommand AddSuggestedReceiverCommand { get; }

    private void RefreshCommands()
    {
        foreach (var c in new IRelayCommand[]
                 {
                     BrowseSourceCommand, StartCommand, CancelCommand, RerunDetectionCommand, RereadDocumentCommand, SplitHereCommand,
                     MergeWithPreviousCommand, MovePageUpCommand, MovePageDownCommand, RotateLeftCommand, RotateRightCommand, NextErrorCommand, GoToSaveCommand, BackToReviewCommand, BrowseOutputCommand, SaveCommand,
                     OpenOutputFolderCommand, NewJobCommand,
                 })
            c?.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------- step 1: source

    private void BrowseSource()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Válaszd ki a szkennelt PDF-et",
            Filter = "PDF-fájlok (*.pdf)|*.pdf",
            InitialDirectory = Directory.Exists(Settings.LastSourceFolder) ? Settings.LastSourceFolder : "",
        };
        if (dlg.ShowDialog() != true) return;
        SetSource(dlg.FileName);
    }

    public void SetSource(string path)
    {
        SourcePath = path;
        Settings.LastSourceFolder = Path.GetDirectoryName(path) ?? "";
        SaveSettingsQuietly();
        var size = new FileInfo(path).Length / 1024.0 / 1024.0;
        SourceInfo = $"{Path.GetFileName(path)}  ·  {size:0.0} MB";
        AppLog.Info($"Forrásfájl: {path}");
    }

    // ---------------------------------------------------------------- steps 2+3: split + read

    // ---------------------------------------------------------------- live preview while processing

    public sealed record FeedItem(string Page, string Text, Brush Color);

    private string? _previewImage;
    private IReadOnlyList<PageOverlay> _previewOverlays = Array.Empty<PageOverlay>();
    private string _previewTitle = "";
    private string _previewDetail = "";
    private DateTime _lastPreview;
    private int _feedRecognized, _feedBlank, _feedUnknown;

    /// <summary>The page that was just read, shown on the processing screen.</summary>
    public string? PreviewImage { get => _previewImage; private set => SetProperty(ref _previewImage, value); }
    public IReadOnlyList<PageOverlay> PreviewOverlays { get => _previewOverlays; private set => SetProperty(ref _previewOverlays, value); }
    public string PreviewTitle { get => _previewTitle; private set => SetProperty(ref _previewTitle, value); }
    public string PreviewDetail { get => _previewDetail; private set => SetProperty(ref _previewDetail, value); }

    /// <summary>What each page turned out to be, newest first.</summary>
    public ObservableCollection<FeedItem> ProcessingFeed { get; } = new();

    public string FeedSummary => $"{_feedRecognized} felismert · {_feedUnknown} egyéb · {_feedBlank} üres";

    private static readonly Brush FeedOk = Frozen(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush FeedOther = Frozen(Color.FromRgb(0xB4, 0x53, 0x09));
    private static readonly Brush FeedBlank = Frozen(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly Brush FeedRetry = Frozen(Color.FromRgb(0x63, 0x66, 0xF1));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void ResetPreview()
    {
        ProcessingFeed.Clear();
        _feedRecognized = _feedBlank = _feedUnknown = 0;
        PreviewImage = null;
        PreviewOverlays = Array.Empty<PageOverlay>();
        PreviewTitle = PreviewDetail = "";
        OnPropertyChanged(nameof(FeedSummary));
    }

    // ---------------------------------------------------------------- elapsed time and estimate

    private readonly DispatcherTimer _clock;
    private readonly Stopwatch _runWatch = new();
    private readonly Stopwatch _stageWatch = new();
    private string _stage = "";
    private double _stageStartValue, _stageCurrent, _stageTotal;
    private string _elapsedText = "";
    private string _remainingText = "";

    /// <summary>Time since processing started ("02:14").</summary>
    public string ElapsedText { get => _elapsedText; private set => SetProperty(ref _elapsedText, value); }

    /// <summary>Estimated time left of the current step ("~01:30 left"), empty while it cannot be estimated.</summary>
    public string RemainingText { get => _remainingText; private set => SetProperty(ref _remainingText, value); }

    private void StartClock()
    {
        _runWatch.Restart();
        _stageWatch.Restart();
        _stage = "";
        _stageStartValue = _stageCurrent = _stageTotal = 0;
        ElapsedText = "00:00";
        RemainingText = "";
        _clock.Start();
    }

    private void StopClock()
    {
        _clock.Stop();
        _runWatch.Stop();
        UpdateClock();
        RemainingText = "";
    }

    private static string FormatTime(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    /// <summary>Remaining time of the current step, from its speed so far (pages per second).</summary>
    private void UpdateClock()
    {
        ElapsedText = FormatTime(_runWatch.Elapsed);
        double done = _stageCurrent - _stageStartValue;
        double left = _stageTotal - _stageCurrent;
        if (_stageTotal <= 1 || done < 1 || left <= 0 || _stageWatch.Elapsed.TotalSeconds < 3)
        {
            RemainingText = _stageTotal > 1 && left > 0 ? "becslés…" : "";
            return;
        }
        var remaining = TimeSpan.FromSeconds(Math.Ceiling(_stageWatch.Elapsed.TotalSeconds / done * left));
        RemainingText = $"~{FormatTime(remaining)}";
    }

    /// <summary>A new stage (OCR, re-reading …) restarts the estimate.</summary>
    private void TrackStage(ProgressInfo p)
    {
        if (p.Stage != _stage || p.Current < _stageCurrent)
        {
            _stage = p.Stage;
            _stageWatch.Restart();
            _stageStartValue = p.Current;
        }
        _stageCurrent = p.Current;
        _stageTotal = p.Total;
    }

    /// <summary>Progress callback (UI thread): progress bar, feed and the live page preview.</summary>
    private void OnProgress(ProgressInfo p)
    {
        ProgressMax = Math.Max(1, p.Total);
        ProgressValue = p.Current;
        ProgressText = p.Total > 1 ? $"{p.Stage}: {p.Current} / {p.Total}" : p.Stage;
        TrackStage(p);
        if (p.Stage is ProgressInfo.Splitting or ProgressInfo.Refine) Step = WizardStep.Read;
        if (p.Page == null) return;

        var page = p.Page;
        bool retry = p.Stage == ProgressInfo.Refine;
        var style = page.IsBlank ? null : FieldReader.FindStyle(page, Styles, Settings);
        string text;
        Brush color;
        if (page.IsBlank)
        {
            text = "üres – kihagyva";
            color = FeedBlank;
            if (!retry) _feedBlank++;
        }
        else if (style != null)
        {
            var number = InvoiceProcessor.ReadFieldWithFallback(style.InvoiceNumber, page, Settings).Value;
            text = $"{style.Name} · {number ?? "a számlaszám nem olvasható"}";
            color = retry ? FeedRetry : FeedOk;
            if (!retry) _feedRecognized++;
        }
        else
        {
            text = page.Rotation != 0 ? $"nincs sablon (elforgatva {page.Rotation}°)" : "nincs sablon – folytatólap vagy ismeretlen elrendezés";
            color = retry ? FeedRetry : FeedOther;
            if (!retry) _feedUnknown++;
        }
        if (retry) text = $"{p.Detail} → {text}";
        ProcessingFeed.Insert(0, new FeedItem($"{page.Number}. o.", text, color));
        if (ProcessingFeed.Count > 400) ProcessingFeed.RemoveAt(ProcessingFeed.Count - 1);
        OnPropertyChanged(nameof(FeedSummary));

        // The preview is redrawn at most ~4 times per second (OCR runs on several pages in parallel).
        if (page.IsBlank || (!retry && (DateTime.Now - _lastPreview).TotalMilliseconds < 250)) return;
        _lastPreview = DateTime.Now;
        PreviewImage = page.ImagePath;
        PreviewOverlays = OverlayBuilder.Build(page, style, Settings, showLines: true);
        PreviewTitle = $"{page.Number}. oldal" + (style != null ? $" · {style.Name}" : "");
        PreviewDetail = retry ? $"Újraolvasás: {p.Detail}" : $"{page.WordCount} szó felismerve" + (page.Rotation != 0 ? $", elforgatva {page.Rotation}°" : "");
    }

    private async Task StartAsync()
    {
        IsBusy = true;
        Step = WizardStep.Split;
        SaveSummary = "";
        ResetPreview();
        _cts = new CancellationTokenSource();
        var progress = new Progress<ProgressInfo>(OnProgress);
        StartClock();

        try
        {
            await SyncCentralAsync();
            var sw = Stopwatch.StartNew();
            var styles = Styles.ToList();
            var settings = Settings;
            var receiver = SelectedReceiver;
            var job = await Task.Run(() => InvoiceProcessor.RunAsync(SourcePath, styles, settings, progress, _cts.Token, receiver));
            Job = job;
            OnReceiverChanged();
            AppLog.Info($"A feldolgozás kész: {sw.Elapsed.TotalSeconds:0.0} mp");
            Step = WizardStep.Review;
            ShowErrorsOnly = false;
            SelectedDocument = job.Documents.FirstOrDefault(d => d.HasErrors) ?? job.Documents.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            AppLog.Warn("A feldolgozást a felhasználó megszakította");
            Step = WizardStep.Source;
        }
        catch (Exception ex)
        {
            AppLog.Error("A feldolgozás sikertelen", ex);
            MessageBox.Show(Application.Current.MainWindow!, ex.Message, "A feldolgozás sikertelen", MessageBoxButton.OK, MessageBoxImage.Error);
            Step = WizardStep.Source;
        }
        finally
        {
            StopClock();
            QueueText = "";
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ---------------------------------------------------------------- step 4: review

    /// <summary>Re-runs splitting and reading with the current styles (OCR results are reused, manual edits are lost).</summary>
    public async void RerunDetection()
    {
        if (Job == null || IsBusy) return;
        var job = Job;
        var keep = SelectedDocument?.Pages.FirstOrDefault();
        var styles = Styles.ToList();
        InvoiceProcessor.BuildDocuments(job, styles, JobSettings);
        // Names of supplier tax numbers not looked up yet (e.g. after changing the receiving company); continues on the UI thread.
        await InvoiceProcessor.LookupIssuerNamesAsync(job, styles, JobSettings, null, CancellationToken.None);

        // Invoices that now have problems are re-read (magnified, other language, exposure variants); shown live.
        var documents = PageRefiner.InvoicesNeedingRefinement(job.Documents, styles, JobSettings);
        if (documents.Count > 0)
        {
            IsBusy = true;
            ResetPreview();
            Step = WizardStep.Read;
            StartClock();
            try
            {
                var settings = JobSettings;
                var progress = new Progress<ProgressInfo>(OnProgress);
                var watch = Stopwatch.StartNew();
                await Task.Run(async () =>
                {
                    using var surya = await InvoiceProcessor.StartSuryaAsync(settings, progress, CancellationToken.None);
                    await PageRefiner.RefineAsync(documents, styles, settings, progress, CancellationToken.None, surya);
                });
                job.AddTiming("újraolvasás", watch.Elapsed);
                InvoiceProcessor.BuildDocuments(job, styles, settings); // on the UI thread: the list is bound to the view
                await InvoiceProcessor.LookupIssuerNamesAsync(job, styles, settings, null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppLog.Error("Az újraolvasás sikertelen", ex);
            }
            finally
            {
                StopClock();
                IsBusy = false;
                Step = WizardStep.Review;
            }
        }

        UpdateCounts();
        DocumentsView?.Refresh();
        SelectedDocument = job.Documents.FirstOrDefault(d => keep != null && d.Pages.Contains(keep)) ?? job.Documents.FirstOrDefault();
    }

    private void RereadDocument()
    {
        if (SelectedDocument == null) return;
        RunProgrammatic(() => InvoiceProcessor.ReadDocument(SelectedDocument, JobSettings));
        SelectedDocument.IssuerEditedByUser = false;
        AfterDocumentEdit();
        AppLog.Info($"A(z) #{SelectedDocument.Number} számla újraolvasva a(z) „{SelectedDocument.StyleName}” sablonnal: „{SelectedDocument.Issuer}” / „{SelectedDocument.InvoiceNumber}”");
    }

    private void SplitHere()
    {
        if (Job == null || SelectedDocument == null || SelectedPage == null) return;
        InvoiceDocument newDoc = null!;
        var original = SelectedDocument;
        RunProgrammatic(() => newDoc = InvoiceProcessor.SplitAt(Job, SelectedDocument, SelectedPage, Styles.ToList(), JobSettings));
        original.MarkEdited("split");
        newDoc.MarkEdited("split");
        AfterDocumentEdit();
        SelectedDocument = newDoc;
    }

    private void MergeWithPrevious()
    {
        if (Job == null || SelectedDocument == null) return;
        InvoiceDocument prev = null!;
        RunProgrammatic(() => prev = InvoiceProcessor.MergeWithPrevious(Job, SelectedDocument, JobSettings));
        prev.MarkEdited("merged");
        AfterDocumentEdit();
        SelectedDocument = prev;
    }

    /// <summary>Invoices the selected one can be merged into (all others), for the "Merge into…" menu.</summary>
    public IReadOnlyList<InvoiceDocument> MergeTargets =>
        Job == null || SelectedDocument == null ? Array.Empty<InvoiceDocument>() : Job.Documents.Where(d => d != SelectedDocument).ToList();

    /// <summary>Appends the pages of the selected invoice to another invoice chosen by the user.</summary>
    public void MergeSelectedInto(InvoiceDocument target)
    {
        if (Job == null || SelectedDocument == null || target == SelectedDocument) return;
        var source = SelectedDocument;
        RunProgrammatic(() => InvoiceProcessor.MergeInto(Job, source, target, JobSettings));
        target.MarkEdited("merged");
        AfterDocumentEdit();
        SelectedDocument = target;
    }

    private bool CanMoveSelectedPage(int delta)
    {
        if (SelectedDocument == null || SelectedPage == null || SelectedDocument.Pages.Count < 2) return false;
        int i = SelectedDocument.Pages.IndexOf(SelectedPage);
        return i >= 0 && i + delta >= 0 && i + delta < SelectedDocument.Pages.Count;
    }

    private void MoveSelectedPage(int delta)
    {
        if (SelectedDocument == null || SelectedPage == null) return;
        MovePage(SelectedPage, SelectedDocument.Pages.IndexOf(SelectedPage) + delta);
    }

    /// <summary>Moves a page of the selected invoice to a new position (buttons and drag &amp; drop of the thumbnails).</summary>
    public void MovePage(PageInfo page, int newIndex)
    {
        if (Job == null || SelectedDocument == null || !SelectedDocument.Pages.Contains(page)) return;
        var doc = SelectedDocument;
        if (doc.Pages.IndexOf(page) == Math.Clamp(newIndex, 0, doc.Pages.Count - 1)) return;
        RunProgrammatic(() => InvoiceProcessor.MovePage(Job, doc, page, newIndex, JobSettings));
        doc.MarkEdited("page order");
        AfterDocumentEdit();
        SelectedPage = page;
    }

    private bool _rotating;

    /// <summary>
    /// Turns the selected page (sideways / upside-down scan the automatic rotation missed): the image is rotated and
    /// read again, the saved PDF page is rotated too. A new first page is read again unless the values were edited.
    /// </summary>
    private async Task RotateSelectedPageAsync(int degrees)
    {
        if (Job == null || SelectedDocument == null || SelectedPage == null) return;
        var doc = SelectedDocument;
        var page = SelectedPage;
        _rotating = true;
        RefreshCommands();
        try
        {
            var language = Settings.OcrLanguage;
            await Task.Run(() => PageAnalyzer.RotatePageAsync(page, degrees, language));

            var settings = JobSettings;
            RunProgrammatic(() =>
            {
                if (doc.Pages.FirstOrDefault() == page && !doc.HasEdit("issuer") && !doc.HasEdit("invoice number"))
                {
                    // The page may now be recognized by a layout style.
                    if (FieldReader.FindStyle(page, Styles, settings) is { } style) doc.Style = style;
                    InvoiceProcessor.ReadDocument(doc, settings);
                }
                else
                {
                    doc.Vat = InvoiceProcessor.CheckVat(doc.Pages, settings);
                    doc.Refresh();
                }
                InvoiceProcessor.UpdateDuplicates(Job);
                InvoiceProcessor.Score(doc, settings);
            });
            int i = doc.Pages.IndexOf(page);
            if (i >= 0) doc.Pages[i] = page;   // re-creates the thumbnail with the new image
            doc.MarkEdited("page rotated");
            AfterDocumentEdit();
            SelectedPage = null;
            SelectedPage = page;
        }
        catch (Exception ex)
        {
            AppLog.Error($"A(z) {page.Number}. oldal nem forgatható", ex);
            MessageBox.Show(Application.Current.MainWindow!, ex.Message, "Oldal forgatása", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _rotating = false;
            RefreshCommands();
        }
    }

    private void NextError()
    {
        if (Job == null) return;
        var docs = Job.Documents;
        int start = SelectedDocument == null ? -1 : docs.IndexOf(SelectedDocument);
        for (int i = 1; i <= docs.Count; i++)
        {
            var d = docs[(start + i) % docs.Count];
            if (d.HasErrors) { SelectedDocument = d; return; }
        }
    }

    /// <summary>True while the program (not the user) sets document values.</summary>
    private bool _programmaticChange;

    private void RunProgrammatic(Action action)
    {
        _programmaticChange = true;
        try { action(); }
        finally { _programmaticChange = false; }
    }

    private void OnSelectedDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InvoiceDocument.Issuer) && !_programmaticChange && sender is InvoiceDocument edited)
            edited.IssuerEditedByUser = true;

        // Remember every change the user makes by hand, so edited invoices stand out in the list and the report.
        if (!_programmaticChange && sender is InvoiceDocument doc)
        {
            var what = e.PropertyName switch
            {
                nameof(InvoiceDocument.Issuer) => "issuer",
                nameof(InvoiceDocument.InvoiceNumber) => "invoice number",
                nameof(InvoiceDocument.VatApproved) => "VAT accepted",
                nameof(InvoiceDocument.Excluded) => "excluded",
                nameof(InvoiceDocument.DuplicateAccepted) => "duplicate kept",
                nameof(InvoiceDocument.IsInvoiceConfirmed) => "confirmed as invoice",
                nameof(InvoiceDocument.IssueDate) => "issue date",
                nameof(InvoiceDocument.FulfillmentDate) => "fulfillment date",
                nameof(InvoiceDocument.DueDate) => "due date",
                nameof(InvoiceDocument.DatesApproved) => "dates accepted",
                _ => null,
            };
            if (what != null) doc.MarkEdited(what);
        }
        if (e.PropertyName is nameof(InvoiceDocument.IssueDate) or nameof(InvoiceDocument.FulfillmentDate) or nameof(InvoiceDocument.DueDate)
            && sender is InvoiceDocument dated)
        {
            UpdateDateSuggestions(dated);
            UpdateOverlays();
        }

        if (e.PropertyName is nameof(InvoiceDocument.Issuer) or nameof(InvoiceDocument.InvoiceNumber) or nameof(InvoiceDocument.VatApproved)
            or nameof(InvoiceDocument.Excluded) or nameof(InvoiceDocument.DuplicateAccepted) or nameof(InvoiceDocument.IsInvoiceConfirmed)
            or nameof(InvoiceDocument.IssueDate) or nameof(InvoiceDocument.FulfillmentDate) or nameof(InvoiceDocument.DueDate)
            or nameof(InvoiceDocument.DatesApproved))
        {
            OnPropertyChanged(nameof(ProposedFileName));
            // Duplicate check and trust score only after a short pause in typing (they re-read the page text).
            if (sender is InvoiceDocument changed) ScheduleRecheck(changed);
        }
        if (e.PropertyName == nameof(InvoiceDocument.Style)) UpdateOverlays();
        if (e.PropertyName is nameof(InvoiceDocument.HasErrors) or nameof(InvoiceDocument.Excluded)) OnPropertyChanged(nameof(ProposedFileName));
    }

    private DispatcherTimer? _recheckTimer;
    private readonly HashSet<InvoiceDocument> _recheckDocs = new();

    private void ScheduleRecheck(InvoiceDocument doc)
    {
        _recheckDocs.Add(doc);
        if (_recheckTimer == null)
        {
            _recheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _recheckTimer.Tick += (_, _) => { _recheckTimer!.Stop(); RecheckEdited(); };
        }
        _recheckTimer.Stop();
        _recheckTimer.Start();
    }

    /// <summary>
    /// Duplicate check of the whole job (cheap) and the trust score of the edited invoices and of those whose duplicate
    /// state changed – not of every invoice, which made typing slow on big scans.
    /// </summary>
    private void RecheckEdited()
    {
        if (Job == null) { _recheckDocs.Clear(); return; }
        var before = Job.Documents.ToDictionary(d => d, d => d.DuplicateOf);
        InvoiceProcessor.UpdateDuplicates(Job);
        foreach (var d in Job.Documents.Where(d => before[d] != d.DuplicateOf || _recheckDocs.Contains(d)).ToList())
            InvoiceProcessor.Score(d, JobSettings);   // manual values count as confirmed
        _recheckDocs.Clear();
        UpdateCounts();
        OnPropertyChanged(nameof(ProposedFileName));
    }

    /// <summary>Called by the view after the user picked another style in the combo box.</summary>
    public void ApplyStyleToSelected(InvoiceStyle? style)
    {
        if (SelectedDocument == null || SelectedDocument.Style == style) return;
        RunProgrammatic(() => SelectedDocument.Style = style);
        SelectedDocument.MarkEdited("style");
        RereadDocument();
    }

    private void AfterDocumentEdit()
    {
        UpdateSuggestions();
        UpdateCounts();
        DocumentsView?.Refresh();
        UpdateOverlays();
        OnPropertyChanged(nameof(ProposedFileName));
        RefreshCommands();
    }

    private void UpdateCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(ExcludedCount));
        OnPropertyChanged(nameof(EditedCount));
        OnPropertyChanged(nameof(LowTrustCount));
        OnPropertyChanged(nameof(OkCount));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(BlankCount));
        OnPropertyChanged(nameof(ProcessingTimeText));
        OnPropertyChanged(nameof(ProcessingTimeDetails));
        NextErrorCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>Shows where the style reads the issuer and invoice number on the selected page.</summary>
    private void UpdateOverlays()
    {
        PageOverlays = SelectedPage == null
            ? Array.Empty<PageOverlay>()
            : OverlayBuilder.Build(SelectedPage, SelectedDocument?.Style, JobSettings, showLines: false)
                .Concat(OverlayBuilder.Dates(SelectedDocument, SelectedPage)).ToList();
    }

    // ---------------------------------------------------------------- step 5: save

    private void BrowseOutput()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Válaszd ki, hová mentse a számlákat",
            InitialDirectory = Directory.Exists(OutputFolder) ? OutputFolder : "",
        };
        if (dlg.ShowDialog() == true) OutputFolder = dlg.FolderName;
    }

    private async Task SaveAsync()
    {
        if (Job == null) return;
        if (Job.Documents.Any(d => d.HasErrors) && !Settings.SaveErrorDocuments)
        {
            var answer = MessageBox.Show(Application.Current.MainWindow!,
                $"{ErrorCount} számla még hibás, ezek NEM kerülnek mentésre (a hibás számlák mentése ki van kapcsolva a Beállításokban).\n\nFolytatod?",
                "Hibás számlák", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        IsBusy = true;
        try
        {
            Settings.LastOutputFolder = OutputFolder;
            SaveSettingsQuietly();
            var job = Job;
            var folder = OutputFolder;
            var settings = Settings;
            var progress = new Progress<ProgressInfo>(p =>
            {
                ProgressMax = Math.Max(1, p.Total);
                ProgressValue = p.Current;
                ProgressText = $"{p.Stage}: {p.Current} / {p.Total}";
            });
            var result = await Task.Run(() => PdfExporter.Export(job, folder, settings, progress));
            OnPropertyChanged(nameof(SavePreview));

            // Issuer names corrected by hand are remembered, so the same OCR mistake is fixed automatically next time.
            // With the supplier's tax number (when the first page shows exactly one), so the corrected name is also used
            // for later invoices of that tax number instead of the name read from the page or the register.
            var learned = job.Documents.Where(d => d.IssuerEditedByUser && !d.HasErrors)
                             .Select(d => (d.Issuer, Taxes: InvoiceProcessor.SupplierTaxNumbers(d.Pages.Take(1), JobSettings)))
                             .DistinctBy(x => x.Issuer)
                             .Where(x => InvoiceProcessor.LearnIssuer(x.Issuer, settings, x.Taxes.Count == 1 ? x.Taxes[0] : null))
                             .Select(x => x.Issuer).ToList();
            if (learned.Count > 0)
            {
                if (IsCentral) _ = SendLearnedIssuersAsync(job, learned);
                else SaveSettingsQuietly();
                AppLog.Info($"Megjegyzett kibocsátónevek: {string.Join(", ", learned)}");
            }
            SaveSummary = $"{result.Saved} számla mentve" +
                          (result.SavedWithErrors > 0 ? $", {result.SavedWithErrors} hibás a(z) „{settings.ErrorFolderName}” mappába" : "") +
                          (result.Excluded > 0 ? $", {result.Excluded} kizárva" : "") +
                          (result.Skipped > 0 ? $", {result.Skipped} kihagyva" : "") +
                          $".\nJelentés: {Path.GetFileName(result.ReportPath)}";
        }
        catch (Exception ex)
        {
            AppLog.Error("A mentés sikertelen", ex);
            MessageBox.Show(Application.Current.MainWindow!, ex.Message, "A mentés sikertelen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NewJob()
    {
        if (Job != null && string.IsNullOrEmpty(SaveSummary))
        {
            var answer = MessageBox.Show(Application.Current.MainWindow!, "A jelenlegi számlák még nincsenek mentve. Mégis újrakezded?",
                "Újrakezdés", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }
        SelectedDocument = null;
        Job = null;
        SaveSummary = "";
        SourcePath = "";
        SourceInfo = "";
        Step = WizardStep.Source;
    }

    // ---------------------------------------------------------------- styles & settings

    public void ReplaceStyles(IEnumerable<InvoiceStyle> styles)
    {
        if (IsCentral) return;   // central styles are edited in the server manager
        SetStyles(styles);
        JsonStore.SaveStyles(Styles);
        AppLog.Info($"Sablonok mentve ({Styles.Count} db)");
    }

    /// <summary>
    /// Settings from the settings window. In central mode only the machine-specific fields and new receiving companies
    /// (sent to the server) could be edited.
    /// </summary>
    public async void ReplaceSettings(AppSettings settings)
    {
        bool wasCentral = IsCentral;
        var newReceivers = wasCentral && settings.ConfigMode == ConfigMode.Central
            ? settings.ReceiverCompanies.Where(r => !string.IsNullOrWhiteSpace(r.VatNumber) && !Settings.ReceiverCompanies.Any(c => SameTaxNumber(c.VatNumber, r.VatNumber))).ToList()
            : new List<ReceiverCompany>();
        var oldServer = (_localSettings.CentralServerUrl, _localSettings.CentralClientKey);
        _localSettings = wasCentral ? AppSettings.MergeCentral(settings, _localSettings) : settings;
        bool modeChanged = wasCentral != (_localSettings.ConfigMode == ConfigMode.Central)
                           || oldServer != (_localSettings.CentralServerUrl, _localSettings.CentralClientKey);
        if (modeChanged) _localSettings.CentralRevision = 0;
        SaveLocalSettings();
        if (modeChanged)
        {
            LoadConfiguration();
            AppLog.Info(IsCentral ? $"Központi konfiguráció bekapcsolva: {Settings.CentralServerUrl}" : "Helyi konfiguráció bekapcsolva");
        }
        else
        {
            Settings = IsCentral && _central != null ? AppSettings.MergeCentral(_localSettings, _central.Settings) : _localSettings;
        }
        OnPropertyChanged(nameof(Settings));
        OnReceiverChanged();
        OnPropertyChanged(nameof(ProposedFileName));
        AppLog.Info("Beállítások mentve");
        if (modeChanged && IsCentral) await SyncCentralAsync(force: true);
        if (newReceivers.Count > 0) await SendReceiversAsync(newReceivers);
        if (Settings.CheckForUpdates && AvailableUpdate == null) await CheckForUpdatesAsync();
    }

    /// <summary>Saves the settings (central mode: only the machine-specific fields, into the local settings.json).</summary>
    private void SaveSettingsQuietly()
    {
        if (IsCentral) _localSettings = AppSettings.MergeCentral(Settings, _localSettings);
        else _localSettings = Settings;
        SaveLocalSettings();
    }

    private void SaveLocalSettings()
    {
        try { JsonStore.SaveSettings(_localSettings); }
        catch (Exception ex) { AppLog.Error("A beállítások nem menthetők", ex); }
    }

    private static bool SameTaxNumber(string a, string b) =>
        VatValidator.Parse(a)?.Core is { } x && x == VatValidator.Parse(b)?.Core;

    /// <summary>Central mode: receiving companies added on this computer go to the server, which shares them with every computer.</summary>
    private async Task SendReceiversAsync(IReadOnlyList<ReceiverCompany> companies)
    {
        try
        {
            using var client = CentralSync.ClientFor(Settings);
            foreach (var company in companies)
            {
                await client.AddReceiverAsync(company);
                AppLog.Info($"Vevő cég felvéve a központi konfigurációba: {company.Name} {company.VatNumber}");
            }
            await SyncCentralAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"A vevő cég nem küldhető el a szervernek (erre a futásra érvényes marad): {ex.Message}");
        }
    }

    /// <summary>Central mode: the issuer names corrected by hand go to the server, so every computer learns them.</summary>
    private async Task SendLearnedIssuersAsync(InvoiceJob job, IReadOnlyList<string> names)
    {
        try
        {
            using var client = CentralSync.ClientFor(Settings);
            foreach (var name in names)
            {
                var doc = job.Documents.FirstOrDefault(d => d.Issuer == name);
                var taxes = doc == null ? new List<VatValidator.TaxNumber>() : InvoiceProcessor.SupplierTaxNumbers(doc.Pages.Take(1), JobSettings);
                await client.LearnIssuerAsync(name, taxes.Count == 1 ? taxes[0].Full : null);
            }
            await SyncCentralAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"A javított kibocsátónevek nem küldhetők el a szervernek: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- misc

    private void OnLogEntry(LogEntry entry)
    {
        _dispatcher.BeginInvoke(() =>
        {
            AllLogEntries.Add(entry);
            if (AllLogEntries.Count > 20000) AllLogEntries.RemoveAt(0);
            if (entry.Level != LogLevel.Debug) OnPropertyChanged(nameof(LastLogLine));
        });
    }

    public static void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
