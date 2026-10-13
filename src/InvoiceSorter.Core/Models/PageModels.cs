using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using InvoiceSorter.Core.Processing;

namespace InvoiceSorter.Core.Models;

/// <summary>An OCR word with a normalized bounding box (relative to the upright page image).</summary>
public sealed class OcrWordBox
{
    public string Text { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;
}

public sealed class OcrTextLine
{
    public List<OcrWordBox> Words { get; set; } = new();
    public string Text => string.Join(" ", Words.Select(w => w.Text));
}

/// <summary>A single page of the source PDF, rendered and OCR'd.</summary>
public sealed class PageInfo
{
    private string? _compact;

    /// <summary>0-based page index in the source PDF.</summary>
    public int Index { get; init; }
    public int Number => Index + 1;

    /// <summary>Upright page image (JPEG) in the job work folder.</summary>
    public string ImagePath { get; set; } = "";
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }

    /// <summary>Clockwise rotation (0/90/180/270) that was applied to make the page upright.</summary>
    public int Rotation { get; set; }

    public bool IsBlank { get; set; }
    public double InkRatio { get; set; }
    public string? Error { get; set; }

    private List<OcrTextLine> _lines = new();

    public List<OcrTextLine> Lines
    {
        get => _lines;
        set { _lines = value; _compact = null; }
    }

    /// <summary>Which OCR engine produced <see cref="Lines"/> ("Windows" or "Surya").</summary>
    public string Engine { get; set; } = "Windows";

    /// <summary>
    /// Additional readings of the same page (higher magnification, other OCR language) made only for pages of
    /// invoices that had problems. Used as fallbacks when a value cannot be read from the normal OCR text.
    /// </summary>
    public List<PageInfo> ExtraReadings { get; set; } = new();

    /// <summary>How many of the re-read passes (see PageRefiner.Passes) have been done for this page.</summary>
    public int RefinePassesDone { get; set; }

    /// <summary>The normal reading followed by the extra readings.</summary>
    public IEnumerable<PageInfo> Readings => ExtraReadings.Prepend(this);

    public string Text => string.Join("\n", Lines.Select(l => l.Text));

    /// <summary>Diacritic-free, lowercase, letters/digits only version of the page text (for fuzzy matching).</summary>
    public string CompactText => _compact ??= TextUtil.Compact(Text);

    public int WordCount => Lines.Sum(l => l.Words.Count);

    public override string ToString() => $"{Number}. oldal";
}

/// <summary>Why a page started a new invoice.</summary>
public enum StartReason { FirstPage, Style, Receiver, Title, Manual }

public enum VatStatus
{
    NotChecked,
    Valid,
    Mismatch,
    NotFound,
    NotConfigured,
}

public sealed record VatCheckResult(VatStatus Status, string? Found, string Message)
{
    public static readonly VatCheckResult None = new(VatStatus.NotChecked, null, "Nincs ellenőrizve");
}

/// <summary>One invoice produced by splitting the source PDF.</summary>
public sealed class InvoiceDocument : ObservableObject
{
    private int _number;
    private InvoiceStyle? _style;
    private string _issuer = "";
    private string _invoiceNumber = "";
    private VatCheckResult _vat = VatCheckResult.None;
    private bool _vatApproved;
    private int? _duplicateOf;

    public int Number { get => _number; set => SetProperty(ref _number, value); }

    public ObservableCollection<PageInfo> Pages { get; } = new();

    public InvoiceStyle? Style
    {
        get => _style;
        set { if (SetProperty(ref _style, value)) Refresh(); }
    }

    public string Issuer
    {
        get => _issuer;
        set { if (SetProperty(ref _issuer, value ?? "")) Refresh(); }
    }

    public string InvoiceNumber
    {
        get => _invoiceNumber;
        set { if (SetProperty(ref _invoiceNumber, value ?? "")) Refresh(); }
    }

    public VatCheckResult Vat
    {
        get => _vat;
        set { if (SetProperty(ref _vat, value)) Refresh(); }
    }

    /// <summary>The user checked the VAT number by eye and accepted the invoice.</summary>
    public bool VatApproved
    {
        get => _vatApproved;
        set { if (SetProperty(ref _vatApproved, value)) Refresh(); }
    }

    public int? DuplicateOf
    {
        get => _duplicateOf;
        set { if (SetProperty(ref _duplicateOf, value)) Refresh(); }
    }

    private bool _excluded;
    private bool _duplicateAccepted;

    /// <summary>The user opted this invoice out of the export (e.g. a second scan of the same paper).</summary>
    public bool Excluded
    {
        get => _excluded;
        set { if (SetProperty(ref _excluded, value)) Refresh(); }
    }

    /// <summary>The user confirmed that the same issuer + invoice number really occurs twice; both are exported.</summary>
    public bool DuplicateAccepted
    {
        get => _duplicateAccepted;
        set { if (SetProperty(ref _duplicateAccepted, value)) Refresh(); }
    }

    /// <summary>The user typed the issuer in the review screen (the name is learned as a known issuer on save).</summary>
    public bool IssuerEditedByUser { get; set; }

    private readonly SortedSet<string> _edits = new();

    /// <summary>What the user changed by hand in the review screen (shown highlighted and in the report).</summary>
    public string EditsText => string.Join(", ", _edits.Select(e => EditNames.TryGetValue(e, out var hu) ? hu : e));

    /// <summary>Display names of the manual changes (the keys stay English: they are used in code and in old data).</summary>
    private static readonly Dictionary<string, string> EditNames = new()
    {
        ["issuer"] = "kibocsátó", ["invoice number"] = "számlaszám", ["VAT accepted"] = "adószám elfogadva",
        ["excluded"] = "kizárva", ["duplicate kept"] = "duplikátum megtartva", ["confirmed as invoice"] = "számlának jelölve",
        ["split"] = "szétvágva", ["merged"] = "összevonva", ["style"] = "sablon", ["page order"] = "oldalsorrend",
        ["page rotated"] = "oldal elforgatva",
    };

    public bool IsManuallyEdited => _edits.Count > 0;

    /// <summary>True when the user changed the given item by hand ("issuer", "invoice number", "split" …).</summary>
    public bool HasEdit(string what) => _edits.Contains(what);

    // ------------------------------------------------------------------ trust score

    private int _trust;
    private IReadOnlyList<string> _trustReasons = Array.Empty<string>();

    /// <summary>How the first page of this invoice was recognized (used by the trust score).</summary>
    public StartReason StartReason { get; set; }

    /// <summary>0-100: how sure the program is that issuer, invoice number, VAT check and page split are right.</summary>
    public int Trust { get => _trust; set { if (SetProperty(ref _trust, value)) { OnPropertyChanged(nameof(TrustLevel)); OnPropertyChanged(nameof(TrustText)); } } }

    /// <summary>Why points were deducted (shown in the review screen and the report).</summary>
    public IReadOnlyList<string> TrustReasons { get => _trustReasons; set { SetProperty(ref _trustReasons, value); OnPropertyChanged(nameof(TrustReasonsText)); OnPropertyChanged(nameof(TrustReasonsBullets)); } }

    public string TrustLevel => Trust >= Processing.TrustScorer.HighThreshold ? "Magas" : Trust >= Processing.TrustScorer.MediumThreshold ? "Közepes" : "Alacsony";
    public string TrustText => $"{Trust} ({TrustLevel})";
    /// <summary>The deductions as a bulleted list, one per line (tooltips).</summary>
    public string TrustReasonsBullets => TrustReasons.Count == 0 ? "Nincs levonás" : string.Join(Environment.NewLine, TrustReasons.Select(r => "• " + r));

    public string TrustReasonsText => TrustReasons.Count == 0 ? "Nincs levonás" : string.Join("; ", TrustReasons);

    /// <summary>Records a manual change, e.g. "invoice number" or "split".</summary>
    public void MarkEdited(string what)
    {
        if (!_edits.Add(what)) return;
        OnPropertyChanged(nameof(EditsText));
        OnPropertyChanged(nameof(IsManuallyEdited));
    }

    /// <summary>Free-form notes from the reading step (shown in the review screen).</summary>
    public List<string> Notes { get; } = new();

    private string? _nonInvoiceTitle;
    private bool _isInvoiceConfirmed;

    /// <summary>Title of a non-invoice document found on the first page ("Szállítólevél", "Számla melléklet" …), or null.</summary>
    public string? NonInvoiceTitle
    {
        get => _nonInvoiceTitle;
        set { if (SetProperty(ref _nonInvoiceTitle, value)) { OnPropertyChanged(nameof(IsNonInvoice)); Refresh(); } }
    }

    public bool IsNonInvoice => NonInvoiceTitle != null;

    /// <summary>The user confirmed that the document is an invoice although it looks like something else.</summary>
    public bool IsInvoiceConfirmed
    {
        get => _isInvoiceConfirmed;
        set { if (SetProperty(ref _isInvoiceConfirmed, value)) Refresh(); }
    }

    /// <summary>When set, a trust score below this value is an error too (setting "Low trust is an error").</summary>
    public int? LowTrustLimit { get; set; }

    public IReadOnlyList<string> Errors
    {
        get
        {
            var errors = new List<string>(BaseErrors);
            if (errors.Count == 0 && !Excluded && LowTrustLimit is int limit && Trust < limit)
                errors.Add($"Alacsony megbízhatóság ({Trust}) – ellenőrizd a kibocsátót és a számlaszámot");
            return errors;
        }
    }

    /// <summary>The errors found while reading (without the low-trust rule).</summary>
    public IReadOnlyList<string> BaseErrors
    {
        get
        {
            var errors = new List<string>();
            if (Excluded) return errors;
            var noStyle = Style == null ? " (nincs illeszkedő sablon)" : Style.IsFallback ? " (nincs sablon – általános szabályok)" : "";
            if (string.IsNullOrWhiteSpace(Issuer)) errors.Add("Kibocsátó nem található" + noStyle);
            if (string.IsNullOrWhiteSpace(InvoiceNumber)) errors.Add("Számlaszám nem található" + noStyle);
            if (!VatApproved)
            {
                switch (Vat.Status)
                {
                    case VatStatus.NotFound: errors.Add("A vevő adószáma nem található"); break;
                    case VatStatus.Mismatch: errors.Add("A vevő adószáma hibás: " + Vat.Message); break;
                    case VatStatus.NotConfigured: errors.Add("Nincs megadva vevő cég adószáma (Beállítások)"); break;
                    case VatStatus.NotChecked: errors.Add("A vevő adószáma nincs ellenőrizve"); break;
                }
            }
            if (DuplicateOf is int d && !DuplicateAccepted) errors.Add($"Duplikátum: ugyanaz a kibocsátó és számlaszám, mint a(z) {d}. számlán");
            if (NonInvoiceTitle is { } title && !IsInvoiceConfirmed)
                errors.Add($"Nem számla? Az oldal címe: „{title}” – valószínűleg egy számlához tartozik (Összevonás az előzővel / Összevonás…), vagy kizárható");
            return errors;
        }
    }

    public bool HasErrors => Errors.Count > 0;
    public bool IsDuplicate => DuplicateOf != null;

    /// <summary>Read by the generic fallback style (no layout style matched) - worth a glance in the review.</summary>
    public bool IsGeneric => Style?.IsFallback == true;
    public string ErrorsText => string.Join("; ", Errors);
    public string StatusText => Excluded ? "Kizárva" : HasErrors ? "Hibás" : "Rendben";
    public string StyleName => Style?.Name ?? "(ismeretlen)";
    public string VatText => VatApproved ? "Kézzel elfogadva" : Vat.Status switch
    {
        VatStatus.Valid => "Rendben " + Vat.Found,
        VatStatus.Mismatch => "Hibás " + Vat.Found,
        VatStatus.NotFound => "Hiányzik",
        VatStatus.NotConfigured => "Nincs beállítva",
        _ => "-",
    };

    public string PageRange
    {
        get
        {
            if (Pages.Count == 0) return "";
            var nums = Pages.Select(p => p.Number).ToList();
            var parts = new List<string>();
            int start = nums[0], prev = nums[0];
            foreach (var n in nums.Skip(1).Append(int.MinValue))
            {
                if (n == prev + 1) { prev = n; continue; }
                parts.Add(start == prev ? $"{start}" : $"{start}-{prev}");
                start = prev = n;
            }
            return string.Join(", ", parts);
        }
    }

    /// <summary>Raises change notifications for all computed properties.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Errors));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(ErrorsText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsDuplicate));
        OnPropertyChanged(nameof(IsGeneric));
        OnPropertyChanged(nameof(TrustText));
        OnPropertyChanged(nameof(StyleName));
        OnPropertyChanged(nameof(VatText));
        OnPropertyChanged(nameof(PageRange));
    }
}

/// <summary>A processing run on one source PDF.</summary>
public sealed class InvoiceJob
{
    public required string SourcePath { get; init; }
    public required string WorkDir { get; init; }
    public DateTime StartedAt { get; } = DateTime.Now;

    /// <summary>
    /// The company this scan belongs to (chosen by the user or detected). The buyer VAT number of every invoice
    /// is checked against this company only. Null = any configured receiving company is accepted.
    /// </summary>
    public ReceiverCompany? Receiver { get; set; }

    /// <summary>Result of the automatic receiver detection (null before OCR).</summary>
    public Processing.ReceiverDetection? Detection { get; set; }
    public List<PageInfo> Pages { get; } = new();
    public ObservableCollection<InvoiceDocument> Documents { get; } = new();

    /// <summary>How long each step of the processing took ("Reading pages (OCR)", "Splitting and reading", "Re-reading" …).</summary>
    public List<(string Step, TimeSpan Duration)> Timings { get; } = new();

    public TimeSpan TotalTime => Timings.Aggregate(TimeSpan.Zero, (sum, t) => sum + t.Duration);

    /// <summary>Adds the time of a step (several runs of the same step are added up).</summary>
    public void AddTiming(string step, TimeSpan duration)
    {
        int i = Timings.FindIndex(t => t.Step == step);
        if (i >= 0) Timings[i] = (step, Timings[i].Duration + duration);
        else Timings.Add((step, duration));
    }
}
