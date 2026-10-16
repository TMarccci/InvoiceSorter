using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InvoiceSorter.App.Controls;
using InvoiceSorter.Core;
using InvoiceSorter.Core.Logging;
using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;
using InvoiceSorter.Core.Storage;
using Microsoft.Win32;

namespace InvoiceSorter.App.ViewModels;

public enum DrawTarget { None, Identifier, Issuer, InvoiceNumber, IssueDate, FulfillmentDate, DueDate }

/// <summary>Edits a working copy of the styles; nothing is saved until the user presses Save.</summary>
public sealed class StyleEditorViewModel : ObservableObject, IDisposable
{
    private static readonly Brush IdentifierBrush = Brushes.MediumPurple;
    private static readonly Brush IssuerBrush = Brushes.SeaGreen;
    private static readonly Brush NumberBrush = Brushes.RoyalBlue;

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private string _lastSnapshot = "";

    // Page source: either the pages of the current job, or a separately opened sample PDF (analyzed on demand).
    private List<PageInfo?> _pages = new();
    private PageAnalyzer? _sampleAnalyzer;
    private string? _sampleWorkDir;

    private InvoiceStyle? _selectedStyle;
    private MatchRule? _selectedRule;
    private DrawTarget _drawTarget = DrawTarget.Identifier;
    private int _pageNumber;
    private PageInfo? _currentPage;
    private bool _isLoadingPage;
    private string _pageSourceText = "";
    private IReadOnlyList<PageOverlay> _overlays = Array.Empty<PageOverlay>();
    private string _matchResult = "";
    private bool _matches;
    private string _issuerResult = "";
    private string _numberResult = "";
    private string _vatResult = "";
    private string _testAllResult = "";
    private string _hint = "";

    public StyleEditorViewModel(IEnumerable<InvoiceStyle> styles, AppSettings settings, InvoiceJob? job, PageInfo? startPage, bool createNew)
    {
        _settings = settings;
        // Most common invoicing programs first (generic rules, cash registers, NAV Online Számlázó, Billingo, Számlázz.hu …).
        Styles = new ObservableCollection<InvoiceStyle>(StyleOrder.Sort(styles.Select(JsonStore.DeepClone)));

        if (job != null)
        {
            _pages = job.Pages.Cast<PageInfo?>().ToList();
            PageSourceText = Path.GetFileName(job.SourcePath);
        }

        AddStyleCommand = new RelayCommand(AddStyle);
        DuplicateStyleCommand = new RelayCommand(DuplicateStyle, () => SelectedStyle != null);
        DeleteStyleCommand = new RelayCommand(DeleteStyle, () => SelectedStyle != null);
        AddRuleCommand = new RelayCommand(() => AddRule(new MatchRule { Text = "" }), () => SelectedStyle != null);
        RemoveRuleCommand = new RelayCommand(RemoveRule, () => SelectedRule != null);
        ClearRuleRegionCommand = new RelayCommand(() => { if (SelectedRule != null) SelectedRule.Region = null; }, () => SelectedRule != null);
        ClearIssuerRegionCommand = new RelayCommand(() => { if (SelectedStyle != null) SelectedStyle.Issuer.Region = null; });
        ClearNumberRegionCommand = new RelayCommand(() => { if (SelectedStyle != null) SelectedStyle.InvoiceNumber.Region = null; });
        PrevPageCommand = new AsyncRelayCommand(() => GoToPageAsync(PageNumber - 1), () => PageNumber > 1);
        NextPageCommand = new AsyncRelayCommand(() => GoToPageAsync(PageNumber + 1), () => PageNumber < PageCount);
        NextMatchCommand = new AsyncRelayCommand(NextMatchAsync, () => SelectedStyle != null && PageCount > 0);
        OpenPdfCommand = new AsyncRelayCommand(OpenSamplePdfAsync);
        TestAllCommand = new RelayCommand(TestAll, () => SelectedStyle != null && _pages.Any(p => p != null));
        RestoreBuiltInCommand = new RelayCommand(RestoreBuiltIn);

        if (createNew)
        {
            AddStyle();
            DrawTarget = DrawTarget.Identifier;
        }
        else
        {
            SelectedStyle = Styles.FirstOrDefault(s => startPage != null && FieldReader.MatchScore(s, startPage, settings) > 0) ?? Styles.FirstOrDefault();
        }

        if (_pages.Count > 0)
        {
            _pageNumber = startPage?.Number ?? (_pages.FirstOrDefault(p => p is { IsBlank: false })?.Number ?? 1);
            CurrentPage = _pages[_pageNumber - 1];
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => RefreshIfChanged();
        _timer.Start();
        UpdateHint();
        Recompute();
    }

    public ObservableCollection<InvoiceStyle> Styles { get; }

    public InvoiceStyle? SelectedStyle
    {
        get => _selectedStyle;
        set
        {
            if (!SetProperty(ref _selectedStyle, value)) return;
            SelectedRule = value?.IdentifierRules.FirstOrDefault();
            OnPropertyChanged(nameof(HasIssueDateRule));
            OnPropertyChanged(nameof(HasFulfillmentDateRule));
            OnPropertyChanged(nameof(HasDueDateRule));
            TestAllResult = "";
            NotifyCommands();
            Recompute();
        }
    }

    public MatchRule? SelectedRule
    {
        get => _selectedRule;
        set { if (SetProperty(ref _selectedRule, value)) NotifyCommands(); }
    }

    public IReadOnlyList<FieldMode> FieldModes { get; } = Enum.GetValues<FieldMode>();
    public IReadOnlyList<LabelDirection> LabelDirections { get; } = Enum.GetValues<LabelDirection>();

    public DrawTarget DrawTarget
    {
        get => _drawTarget;
        set
        {
            if (!SetProperty(ref _drawTarget, value)) return;
            OnPropertyChanged(nameof(DrawIdentifier));
            OnPropertyChanged(nameof(DrawIssuer));
            OnPropertyChanged(nameof(DrawNumber));
            OnPropertyChanged(nameof(DrawIssueDate));
            OnPropertyChanged(nameof(DrawFulfillmentDate));
            OnPropertyChanged(nameof(DrawDueDate));
            OnPropertyChanged(nameof(CanDraw));
            UpdateHint();
        }
    }

    // Radio button helpers.
    public bool DrawIdentifier { get => DrawTarget == DrawTarget.Identifier; set { if (value) DrawTarget = DrawTarget.Identifier; } }
    public bool DrawIssuer { get => DrawTarget == DrawTarget.Issuer; set { if (value) DrawTarget = DrawTarget.Issuer; } }
    public bool DrawNumber { get => DrawTarget == DrawTarget.InvoiceNumber; set { if (value) DrawTarget = DrawTarget.InvoiceNumber; } }
    public bool DrawIssueDate { get => DrawTarget == DrawTarget.IssueDate; set { if (value) DrawTarget = DrawTarget.IssueDate; } }
    public bool DrawFulfillmentDate { get => DrawTarget == DrawTarget.FulfillmentDate; set { if (value) DrawTarget = DrawTarget.FulfillmentDate; } }
    public bool DrawDueDate { get => DrawTarget == DrawTarget.DueDate; set { if (value) DrawTarget = DrawTarget.DueDate; } }

    // ---- optional own date rules of the style (null: the generic date labels of the settings)

    /// <summary>A new date rule: the value next to (or under) a label, the first date in it is used.</summary>
    private static FieldDefinition NewDateRule() => new() { Mode = FieldMode.Label, LabelDirection = LabelDirection.Auto };

    private FieldDefinition? GetDateRule(DateKind kind) => SelectedStyle == null ? null : DateReader.StyleField(SelectedStyle, kind);

    private void SetDateRule(DateKind kind, bool on)
    {
        if (SelectedStyle == null || (GetDateRule(kind) != null) == on) return;
        var rule = on ? NewDateRule() : null;
        switch (kind)
        {
            case DateKind.Issue: SelectedStyle.IssueDate = rule; break;
            case DateKind.Fulfillment: SelectedStyle.FulfillmentDate = rule; break;
            default: SelectedStyle.DueDate = rule; break;
        }
        OnPropertyChanged(nameof(HasIssueDateRule));
        OnPropertyChanged(nameof(HasFulfillmentDateRule));
        OnPropertyChanged(nameof(HasDueDateRule));
        Recompute();
    }

    public bool HasIssueDateRule { get => GetDateRule(DateKind.Issue) != null; set => SetDateRule(DateKind.Issue, value); }
    public bool HasFulfillmentDateRule { get => GetDateRule(DateKind.Fulfillment) != null; set => SetDateRule(DateKind.Fulfillment, value); }
    public bool HasDueDateRule { get => GetDateRule(DateKind.Due) != null; set => SetDateRule(DateKind.Due, value); }

    private string _dateResult = "";

    /// <summary>The three dates the program reads from the current page with this style (and the generic labels).</summary>
    public string DateResult { get => _dateResult; private set => SetProperty(ref _dateResult, value); }
    public bool CanDraw => DrawTarget != DrawTarget.None && SelectedStyle != null && CurrentPage != null;

    public string Hint { get => _hint; private set => SetProperty(ref _hint, value); }

    // ------------------------------------------------------------- pages

    public int PageCount => _pages.Count;

    public int PageNumber
    {
        get => _pageNumber;
        set => _ = GoToPageAsync(value);
    }

    public PageInfo? CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (!SetProperty(ref _currentPage, value)) return;
            OnPropertyChanged(nameof(CurrentImage));
            OnPropertyChanged(nameof(CanDraw));
            Recompute();
        }
    }

    public string? CurrentImage => CurrentPage?.ImagePath;
    public bool IsLoadingPage { get => _isLoadingPage; private set => SetProperty(ref _isLoadingPage, value); }
    public string PageSourceText { get => _pageSourceText; private set => SetProperty(ref _pageSourceText, value); }
    public bool HasPages => PageCount > 0;

    public IReadOnlyList<PageOverlay> Overlays { get => _overlays; private set => SetProperty(ref _overlays, value); }

    // ------------------------------------------------------------- test results

    public bool Matches { get => _matches; private set => SetProperty(ref _matches, value); }
    public string MatchResult { get => _matchResult; private set => SetProperty(ref _matchResult, value); }
    public string IssuerResult { get => _issuerResult; private set => SetProperty(ref _issuerResult, value); }
    public string NumberResult { get => _numberResult; private set => SetProperty(ref _numberResult, value); }
    public string VatResult { get => _vatResult; private set => SetProperty(ref _vatResult, value); }
    public string TestAllResult { get => _testAllResult; private set => SetProperty(ref _testAllResult, value); }

    // ------------------------------------------------------------- commands

    public IRelayCommand AddStyleCommand { get; }
    public IRelayCommand DuplicateStyleCommand { get; }
    public IRelayCommand DeleteStyleCommand { get; }
    public IRelayCommand AddRuleCommand { get; }
    public IRelayCommand RemoveRuleCommand { get; }
    public IRelayCommand ClearRuleRegionCommand { get; }
    public IRelayCommand ClearIssuerRegionCommand { get; }
    public IRelayCommand ClearNumberRegionCommand { get; }
    public IAsyncRelayCommand PrevPageCommand { get; }
    public IAsyncRelayCommand NextPageCommand { get; }
    public IAsyncRelayCommand NextMatchCommand { get; }
    public IAsyncRelayCommand OpenPdfCommand { get; }
    public IRelayCommand TestAllCommand { get; }
    public IRelayCommand RestoreBuiltInCommand { get; }

    private void RestoreBuiltIn()
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)!;
        var answer = MessageBox.Show(owner,
            "Hozzáadja a hiányzó beépített sablonokat?\n\nIgen = csak a hiányzókat adja hozzá\nNem = a módosított beépített sablonokat is visszaállítja az eredetire",
            "Beépített sablonok visszaállítása", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;
        int replaced = JsonStore.MergeBuiltInStyles(Styles, replaceExisting: answer == MessageBoxResult.No, out int added);
        Hint = $"{added} beépített sablon hozzáadva, {replaced} visszaállítva. A megtartáshoz nyomd meg a 'Sablonok mentése' gombot.";
        SelectedStyle ??= Styles.FirstOrDefault();
    }

    private void NotifyCommands()
    {
        foreach (var c in new IRelayCommand?[] { DuplicateStyleCommand, DeleteStyleCommand, AddRuleCommand, RemoveRuleCommand, ClearRuleRegionCommand, PrevPageCommand, NextPageCommand, NextMatchCommand, TestAllCommand })
            c?.NotifyCanExecuteChanged();
    }

    private void AddStyle()
    {
        var style = new InvoiceStyle
        {
            Name = "Új sablon",
            Issuer = new FieldDefinition { Mode = FieldMode.Fixed },
            InvoiceNumber = new FieldDefinition { Mode = FieldMode.Label, LabelText = "Számlaszám", LabelDirection = LabelDirection.Right, RemoveSpaces = true },
        };
        Styles.Add(style);
        SelectedStyle = style;
        DrawTarget = DrawTarget.Identifier;
    }

    private void DuplicateStyle()
    {
        if (SelectedStyle == null) return;
        var copy = JsonStore.DeepClone(SelectedStyle);
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = SelectedStyle.Name + " (másolat)";
        Styles.Insert(Styles.IndexOf(SelectedStyle) + 1, copy);
        SelectedStyle = copy;
    }

    private void DeleteStyle()
    {
        if (SelectedStyle == null) return;
        var answer = MessageBox.Show(Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)!,
            $"Törlöd a(z) „{SelectedStyle.Name}” sablont?", "Sablon törlése", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        int idx = Styles.IndexOf(SelectedStyle);
        Styles.Remove(SelectedStyle);
        SelectedStyle = Styles.Count == 0 ? null : Styles[Math.Min(idx, Styles.Count - 1)];
    }

    private void AddRule(MatchRule rule)
    {
        if (SelectedStyle == null) return;
        SelectedStyle.IdentifierRules.Add(rule);
        SelectedRule = rule;
    }

    private void RemoveRule()
    {
        if (SelectedStyle == null || SelectedRule == null) return;
        SelectedStyle.IdentifierRules.Remove(SelectedRule);
        SelectedRule = SelectedStyle.IdentifierRules.FirstOrDefault();
    }

    // ------------------------------------------------------------- drawing

    /// <summary>The user drew a rectangle on the page: apply it to the current draw target.</summary>
    public void OnRegionDrawn(NormRect rect)
    {
        if (SelectedStyle == null || CurrentPage == null) return;
        var text = FieldReader.GetRegionText(CurrentPage, rect, _settings.RegionPadding);
        var oneLine = TextUtil.CollapseWhitespace(text.Replace('\n', ' '));
        var firstLine = text.Split('\n')[0].Trim();

        switch (DrawTarget)
        {
            case DrawTarget.Identifier:
                if (string.IsNullOrWhiteSpace(oneLine))
                {
                    Hint = "Ezen a területen nem ismert fel szöveget. Jól olvasható nyomtatott szöveg köré rajzolj.";
                    return;
                }
                // Use a slightly larger area so small scan shifts don't break the match.
                AddRule(new MatchRule { Text = firstLine, Region = Grow(rect, 0.03) });
                if (SelectedStyle.Name == "Új sablon") SelectedStyle.Name = firstLine.Length > 40 ? firstLine[..40] : firstLine;
                Hint = $"Azonosító hozzáadva: „{firstLine}”. Ha kell, javítsd az OCR-hibákat a szövegben, vagy töröld a területet, hogy az egész oldalon keressen.";
                break;

            case DrawTarget.Issuer:
                ApplyToField(SelectedStyle.Issuer, rect, oneLine, firstLine, "Kibocsátó");
                break;

            case DrawTarget.InvoiceNumber:
                ApplyToField(SelectedStyle.InvoiceNumber, rect, oneLine, firstLine, "Számlaszám");
                break;

            case DrawTarget.IssueDate or DrawTarget.FulfillmentDate or DrawTarget.DueDate:
            {
                var kind = DrawTarget switch { DrawTarget.IssueDate => DateKind.Issue, DrawTarget.FulfillmentDate => DateKind.Fulfillment, _ => DateKind.Due };
                SetDateRule(kind, true);
                ApplyToField(GetDateRule(kind)!, rect, oneLine, firstLine, char.ToUpper(DateReader.Name(kind)[0]) + DateReader.Name(kind)[1..]);
                break;
            }
        }
        Recompute();
    }

    private void ApplyToField(FieldDefinition field, NormRect rect, string oneLine, string firstLine, string name)
    {
        switch (field.Mode)
        {
            case FieldMode.Fixed:
                field.FixedValue = oneLine;
                Hint = $"{name}: állandó szöveg „{oneLine}”. Ha az OCR rosszul olvasta, javítsd a helyesírást.";
                break;
            case FieldMode.Region:
                field.Region = rect;
                Hint = $"{name}: a rajzolt területről olvassa.";
                break;
            case FieldMode.Label:
                field.LabelText = firstLine;
                field.Region = null;
                Hint = $"{name} címkéje: „{firstLine}”. Az értéket a címke {(field.LabelDirection == LabelDirection.Right ? "jobb oldalán" : "alatt")} olvassa. Csak a címke köré rajzolj (az érték köré ne).";
                break;
            default:
                Hint = $"'Regex az egész oldalon' módban a(z) {name.ToLowerInvariant()} a reguláris kifejezéssel kerül elő; rajzolásra nincs szükség.";
                break;
        }
    }

    private static NormRect Grow(NormRect r, double m) =>
        new(Math.Max(0, r.X - m), Math.Max(0, r.Y - m), Math.Min(1, r.W + 2 * m), Math.Min(1, r.H + 2 * m));

    private void UpdateHint()
    {
        Hint = DrawTarget switch
        {
            DrawTarget.Identifier => "Rajzolj keretet egy olyan szöveg köré, amely csak az ilyen számlák ELSŐ oldalán szerepel – általában a kibocsátó neve a fejlécben.",
            DrawTarget.Issuer => "Állandó mód: a kibocsátó neve köré rajzolva átmásolja. Terület mód: rajzold meg az olvasandó területet. Címke mód: a címke köré rajzolj.",
            DrawTarget.InvoiceNumber => "Címke mód (ajánlott): rajzolj keretet a címke köré, pl. 'Számlaszám:'. Terület mód: magát a számot keretezd be.",
            DrawTarget.IssueDate or DrawTarget.FulfillmentDate or DrawTarget.DueDate =>
                "Csak akkor kell, ha az általános dátumcímkék ennél az elrendezésnél tévednek. Címke mód: a címke köré rajzolj (pl. 'Dátum/Teljesítés ideje'); Terület mód: a dátum köré. A rajzolás bekapcsolja a sablon saját szabályát.",
            _ => "",
        };
    }

    // ------------------------------------------------------------- live test

    private void RefreshIfChanged()
    {
        var snapshot = SelectedStyle == null ? "" : JsonSerializer.Serialize(SelectedStyle, JsonStore.Options);
        if (snapshot == _lastSnapshot) return;
        _lastSnapshot = snapshot;
        Recompute();
    }

    private void Recompute()
    {
        var page = CurrentPage;
        var style = SelectedStyle;
        var overlays = new List<PageOverlay>();

        if (page == null || style == null)
        {
            MatchResult = page == null ? "Nincs betöltött oldal" : "Nincs kiválasztott sablon";
            Matches = false;
            IssuerResult = NumberResult = VatResult = "";
            Overlays = overlays;
            return;
        }

        foreach (var rule in style.IdentifierRules.Where(r => r.Region != null))
            overlays.Add(new PageOverlay(rule.Region!, IdentifierBrush, "Azonosító"));

        var score = FieldReader.MatchScore(style, page, _settings);
        Matches = score > 0;
        var failed = style.IdentifierRules.Where(r => !string.IsNullOrWhiteSpace(r.Text) && !FieldReader.RuleMatches(r, page, _settings)).Select(r => $"'{r.Text}'").ToList();
        MatchResult = style.IdentifierRules.All(r => string.IsNullOrWhiteSpace(r.Text))
            ? "Adj meg legalább egy azonosító szöveget"
            : Matches ? "Ezt az oldalt erre a sablonra ismeri fel" : "Nem ismeri fel – nem található: " + string.Join(", ", failed);

        IssuerResult = Describe(style.Issuer, page, "Kibocsátó", IssuerBrush, overlays);
        var rawIssuer = style.Issuer.Mode == FieldMode.Fixed ? null : InvoiceProcessor.ReadFieldWithFallback(style.Issuer, page, _settings).Value;
        if (rawIssuer != null)
        {
            var clean = InvoiceProcessor.NormalizeIssuer(rawIssuer, _settings);
            IssuerResult = clean == rawIssuer ? clean : $"{clean}   (olvasott: {rawIssuer})";
        }
        NumberResult = Describe(style.InvoiceNumber, page, "Számlaszám", NumberBrush, overlays);
        VatResult = InvoiceProcessor.CheckVat(new[] { page }, _settings).Message;
        DateResult = DescribeDates(style, page, overlays);
        Overlays = overlays;
    }

    /// <summary>"kelte 2026.09.02. („Számla kelte” alatt) · teljesítés … · határidő –" for the current page.</summary>
    private string DescribeDates(InvoiceStyle style, PageInfo page, List<PageOverlay> overlays)
    {
        var doc = new InvoiceDocument { Style = style };
        doc.Pages.Add(page);
        DateReadResult dates;
        try { dates = DateReader.Read(doc, _settings); }
        catch (Exception ex) { return "✖ " + ex.Message; }
        var parts = new List<string>();
        foreach (var kind in new[] { DateKind.Issue, DateKind.Fulfillment, DateKind.Due })
        {
            var f = dates[kind];
            var name = kind switch { DateKind.Issue => "kelte", DateKind.Fulfillment => "teljesítés", _ => "határidő" };
            parts.Add(f.Value is { } v ? $"{name} {DateReader.Format(v)} ({f.Source})" : f.Ambiguous ? $"{name} ?? (több eltérő)" : $"{name} –");
            if (f.Token != null && f.Value != null) overlays.Add(new PageOverlay(f.Token.Box, DateBrush, name));
            if (f.LabelBox != null && f.Value != null) overlays.Add(new PageOverlay(f.LabelBox, Brushes.Gray, name + " (címke)"));
        }
        return string.Join("  ·  ", parts);
    }

    private static readonly Brush DateBrush = Frozen(Color.FromRgb(0xC0, 0x26, 0xD3));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private string Describe(FieldDefinition field, PageInfo page, string caption, Brush brush, List<PageOverlay> overlays)
    {
        if (!string.IsNullOrWhiteSpace(field.Regex))
        {
            try { _ = new Regex(field.Regex); }
            catch (ArgumentException ex) { return "Hibás reguláris kifejezés: " + ex.Message; }
        }

        var r = InvoiceProcessor.ReadFieldWithFallback(field, page, _settings);
        if (field.Mode == FieldMode.Region && field.Region != null) overlays.Add(new PageOverlay(field.Region, brush, caption + " (terület)"));
        if (r.LabelBox != null) overlays.Add(new PageOverlay(r.LabelBox, Brushes.Gray, caption + " (címke)"));
        if (r.ValueBox != null && field.Mode != FieldMode.Region) overlays.Add(new PageOverlay(r.ValueBox, brush, caption));

        if (r.Value != null) return r.Value;
        return field.Mode switch
        {
            FieldMode.Fixed => "(add meg az állandó szöveget)",
            FieldMode.Label when string.IsNullOrWhiteSpace(field.LabelText) => "(add meg vagy rajzold meg a címkét)",
            FieldMode.Label when r.LabelBox == null => "✖ a címke nem található ezen az oldalon",
            FieldMode.Region when field.Region == null => "(rajzold meg a területet)",
            _ when !string.IsNullOrWhiteSpace(r.SourceText) => $"✖ a regex nem illeszkedik erre: „{TextUtil.CollapseWhitespace(r.SourceText)}”",
            _ => "✖ nem található",
        };
    }

    private void TestAll()
    {
        if (SelectedStyle == null) return;
        var pages = _pages.Where(p => p is { IsBlank: false }).Cast<PageInfo>().ToList();
        var matched = pages.Where(p => FieldReader.MatchScore(SelectedStyle, p, _settings) > 0).ToList();
        var noNumber = matched.Where(p => InvoiceProcessor.ReadFieldWithFallback(SelectedStyle.InvoiceNumber, p, _settings).Value == null).Select(p => p.Number).ToList();
        var conflicts = matched.Where(p => FieldReader.FindStyle(p, Styles, _settings) != SelectedStyle).Select(p => p.Number).ToList();

        var text = $"{pages.Count} betöltött oldalból {matched.Count} oldalon ismeri fel";
        if (matched.Count > 0) text += ": " + string.Join(", ", matched.Take(40).Select(p => p.Number)) + (matched.Count > 40 ? " …" : "");
        if (noNumber.Count > 0) text += $"\nNincs meg a számlaszám ezeken az oldalakon: {string.Join(", ", noNumber.Take(30))}";
        if (conflicts.Count > 0) text += $"\nMás sablon nyer ezeken az oldalakon: {string.Join(", ", conflicts.Take(30))}";
        TestAllResult = text;
    }

    private async Task NextMatchAsync()
    {
        if (SelectedStyle == null) return;
        for (int i = 1; i <= _pages.Count; i++)
        {
            int n = (PageNumber - 1 + i) % _pages.Count + 1;
            var p = _pages[n - 1];
            if (p != null && !p.IsBlank && FieldReader.MatchScore(SelectedStyle, p, _settings) > 0)
            {
                await GoToPageAsync(n);
                return;
            }
        }
        Hint = "Más betöltött oldalt nem ismer fel erre a sablonra.";
    }

    // ------------------------------------------------------------- page loading

    public async Task GoToPageAsync(int number)
    {
        if (_pages.Count == 0) return;
        number = Math.Clamp(number, 1, _pages.Count);
        _pageNumber = number;
        OnPropertyChanged(nameof(PageNumber));
        NotifyCommands();

        var page = _pages[number - 1];
        if (page == null && _sampleAnalyzer != null && _sampleWorkDir != null)
        {
            IsLoadingPage = true;
            try
            {
                page = await _sampleAnalyzer.AnalyzePageAsync(number - 1, _sampleWorkDir, _settings);
                _pages[number - 1] = page;
            }
            catch (Exception ex)
            {
                AppLog.Error($"A minta PDF {number}. oldala nem olvasható", ex);
            }
            finally
            {
                IsLoadingPage = false;
            }
        }
        if (_pageNumber == number) CurrentPage = page;
    }

    private async Task OpenSamplePdfAsync()
    {
        var dlg = new OpenFileDialog { Title = "Minta PDF megnyitása a sablonok tervezéséhez", Filter = "PDF-fájlok (*.pdf)|*.pdf" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _sampleAnalyzer?.Dispose();
            _sampleAnalyzer = await PageAnalyzer.OpenAsync(dlg.FileName, _settings.OcrLanguage);
            _sampleWorkDir = AppPaths.CreateJobDir();
            _pages = Enumerable.Repeat<PageInfo?>(null, _sampleAnalyzer.PageCount).ToList();
            PageSourceText = Path.GetFileName(dlg.FileName) + " (az oldalakat megnyitáskor olvassa be)";
            OnPropertyChanged(nameof(PageCount));
            OnPropertyChanged(nameof(HasPages));
            AppLog.Info($"Sablonszerkesztő: minta PDF megnyitva: {dlg.FileName} ({_sampleAnalyzer.PageCount} oldal)");
            await GoToPageAsync(1);
        }
        catch (Exception ex)
        {
            AppLog.Error("A minta PDF nem nyitható meg", ex);
            MessageBox.Show(ex.Message, "A PDF nem nyitható meg", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------- save

    /// <summary>Returns a list of problems that prevent saving (empty = OK).</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        foreach (var s in Styles)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) problems.Add("Az egyik sablonnak nincs neve.");
            foreach (var f in new[] { ("kibocsátó", s.Issuer), ("számlaszám", s.InvoiceNumber) })
            {
                if (string.IsNullOrWhiteSpace(f.Item2.Regex)) continue;
                try { _ = new Regex(f.Item2.Regex); }
                catch (ArgumentException ex) { problems.Add($"„{s.Name}”: hibás reguláris kifejezés ({f.Item1}) – {ex.Message}"); }
            }
        }
        return problems;
    }

    public List<string> Warnings() =>
        Styles.Where(s => s.IdentifierRules.All(r => string.IsNullOrWhiteSpace(r.Text)))
              .Select(s => $"„{s.Name}” sablonnak nincs azonosító szövege, így soha nem ismeri fel.").ToList();

    public void Dispose()
    {
        _timer.Stop();
        _sampleAnalyzer?.Dispose();
    }
}
