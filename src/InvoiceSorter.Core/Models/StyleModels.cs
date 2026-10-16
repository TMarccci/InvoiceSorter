using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace InvoiceSorter.Core.Models;

/// <summary>Rectangle in normalized page coordinates (0..1, origin top-left, page upright).</summary>
public sealed class NormRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }

    public NormRect() { }
    public NormRect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }

    public bool Contains(double px, double py, double pad = 0) =>
        px >= X - pad && px <= X + W + pad && py >= Y - pad && py <= Y + H + pad;

    public NormRect Clone() => new(X, Y, W, H);

    public override string ToString() => $"x={X:0.000} y={Y:0.000} w={W:0.000} h={H:0.000}";
}

public enum FieldMode
{
    /// <summary>The value is always the same text (e.g. the issuer name of a supplier-specific style).</summary>
    Fixed,
    /// <summary>The value is read from a rectangle on the page (optionally refined by a regex).</summary>
    Region,
    /// <summary>The value is next to a label text (e.g. "Számlaszám:"). Robust against shifted scans.</summary>
    Label,
    /// <summary>The value is found by a regex anywhere on the page.</summary>
    PageRegex,
}

public enum SuryaMode
{
    /// <summary>Only the built-in Windows OCR is used.</summary>
    Off,
    /// <summary>Surya re-reads the pages of invoices that still have a problem (first re-read pass).</summary>
    UnclearOnly,
    /// <summary>Surya reads every page (main OCR); Windows OCR is kept as a fallback reading.</summary>
    AllPages,
}

/// <summary>Where Surya runs.</summary>
public enum SuryaLocation
{
    /// <summary>On this computer (Python worker process, the app's own environment or a configured python.exe).</summary>
    Local,
    /// <summary>On an Invoice Sorter server elsewhere on the network (src/InvoiceSorter.Server), e.g. a machine with a GPU.</summary>
    Server,
}

public enum LabelDirection
{
    /// <summary>Value is on the same line, right of the label.</summary>
    Right,
    /// <summary>Value is on the line(s) under the label.</summary>
    Below,
    /// <summary>Right of the label if a value is there, otherwise below it.</summary>
    Auto,
}

/// <summary>A text that must be found on a page for it to be recognized as a given style.</summary>
public sealed class MatchRule : ObservableObject
{
    private string _text = "";
    private NormRect? _region;

    public string Text { get => _text; set => SetProperty(ref _text, value); }

    /// <summary>Optional: only look for the text inside this region. Null = anywhere on the page.</summary>
    public NormRect? Region { get => _region; set => SetProperty(ref _region, value); }
}

/// <summary>Describes how a single data field (issuer, invoice number) is read.</summary>
public sealed class FieldDefinition : ObservableObject
{
    private FieldMode _mode = FieldMode.Region;
    private string _fixedValue = "";
    private NormRect? _region;
    private string _regex = "";
    private bool _removeSpaces;
    private string _labelText = "";
    private LabelDirection _labelDirection = LabelDirection.Right;

    public FieldMode Mode { get => _mode; set => SetProperty(ref _mode, value); }
    public string FixedValue { get => _fixedValue; set => SetProperty(ref _fixedValue, value); }

    /// <summary>Region mode: the area to read. Label mode: optional area the label must be in.</summary>
    public NormRect? Region { get => _region; set => SetProperty(ref _region, value); }

    /// <summary>Label mode: the label printed before the value, e.g. "Számlaszám".</summary>
    public string LabelText { get => _labelText; set => SetProperty(ref _labelText, value); }

    public LabelDirection LabelDirection { get => _labelDirection; set => SetProperty(ref _labelDirection, value); }

    /// <summary>
    /// Optional regex applied to the region (or page) text. If it has a group named "value" that is used,
    /// otherwise the first group, otherwise the whole match.
    /// </summary>
    public string Regex { get => _regex; set => SetProperty(ref _regex, value); }

    /// <summary>
    /// Optional output format using the regex groups, e.g. "$1/$2" (only used together with <see cref="Regex"/>).
    /// </summary>
    public string Format { get => _format; set => SetProperty(ref _format, value); }

    /// <summary>Remove all whitespace from the read value (useful for invoice numbers).</summary>
    public bool RemoveSpaces { get => _removeSpaces; set => SetProperty(ref _removeSpaces, value); }

    /// <summary>Replace letters OCR typically confuses with digits (O→0, I/l→1, S→5, B→8, Z→2) when they stand between digits.</summary>
    public bool FixOcrDigits { get => _fixOcrDigits; set => SetProperty(ref _fixOcrDigits, value); }

    private string _format = "";
    private bool _fixOcrDigits;
    private bool _excludeReceiver;

    /// <summary>Never accept a receiving company's name as the value (for issuer fields read from the page).</summary>
    public bool ExcludeReceiver { get => _excludeReceiver; set => SetProperty(ref _excludeReceiver, value); }

    /// <summary>Optional second way to read the value, used when this definition finds nothing.</summary>
    public FieldDefinition? Fallback { get; set; }
}

/// <summary>An invoice layout ("style"): how to recognize it and where its data is located.</summary>
public sealed class InvoiceStyle : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "New style";
    private bool _enabled = true;
    private string _notes = "";

    public string Id { get => _id; set => SetProperty(ref _id, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public string Notes { get => _notes; set => SetProperty(ref _notes, value); }

    private bool _isFallback;

    /// <summary>
    /// A generic style used for invoices that no other style recognizes. It never starts a new invoice and is not
    /// used to recognize pages; it only reads issuer and invoice number with general rules.
    /// </summary>
    public bool IsFallback { get => _isFallback; set => SetProperty(ref _isFallback, value); }

    /// <summary>All rules must match for a page to be recognized as this style (first page of an invoice).</summary>
    public ObservableCollection<MatchRule> IdentifierRules { get; set; } = new();

    public FieldDefinition Issuer { get; set; } = new() { Mode = FieldMode.Fixed };
    public FieldDefinition InvoiceNumber { get; set; } = new() { Mode = FieldMode.Region, RemoveSpaces = true };

    // Optional own rules for the dates (null = the generic date labels of the settings). The first date in the
    // value read by the rule is used.
    private FieldDefinition? _issueDate, _fulfillmentDate, _dueDate;
    public FieldDefinition? IssueDate { get => _issueDate; set => SetProperty(ref _issueDate, value); }
    public FieldDefinition? FulfillmentDate { get => _fulfillmentDate; set => SetProperty(ref _fulfillmentDate, value); }
    public FieldDefinition? DueDate { get => _dueDate; set => SetProperty(ref _dueDate, value); }

    public override string ToString() => Name;
}

public sealed class ReceiverCompany : ObservableObject
{
    private string _name = "";
    private string _vatNumber = "";

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    /// <summary>Hungarian tax number, e.g. 12345678-2-41.</summary>
    public string VatNumber { get => _vatNumber; set => SetProperty(ref _vatNumber, value); }
}

/// <summary>Where the styles and settings come from.</summary>
public enum ConfigMode
{
    /// <summary>Stored on this computer and edited here.</summary>
    Local,
    /// <summary>Downloaded from an Invoice Sorter server and edited centrally (server manager); Surya runs on the server.</summary>
    Central,
}

public sealed class AppSettings
{
    // ------------------------------------------------------------------ configuration source (local only)

    /// <summary>Local or centrally managed configuration.</summary>
    public ConfigMode ConfigMode { get; set; } = ConfigMode.Local;

    /// <summary>Address of the Invoice Sorter server, e.g. "http://192.168.1.20:8770".</summary>
    public string CentralServerUrl { get; set; } = "";

    /// <summary>The server's client key (shown by the server manager).</summary>
    public string CentralClientKey { get; set; } = "";

    /// <summary>The central configuration revision applied last (0 = none yet).</summary>
    public int CentralRevision { get; set; }

    /// <summary>Look for a newer version on GitHub at start-up.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// The central settings with this computer's own values kept: configuration source, folders, local Surya
    /// installation and the update check.
    /// </summary>
    public static AppSettings MergeCentral(AppSettings local, AppSettings central)
    {
        var merged = Storage.JsonStore.DeepClone(central);
        merged.ConfigMode = local.ConfigMode;
        merged.CentralServerUrl = local.CentralServerUrl;
        merged.CentralClientKey = local.CentralClientKey;
        merged.CentralRevision = local.CentralRevision;
        merged.CheckForUpdates = local.CheckForUpdates;
        merged.LastSourceFolder = local.LastSourceFolder;
        merged.LastOutputFolder = local.LastOutputFolder;
        merged.SuryaPython = local.SuryaPython;
        merged.SuryaLocation = local.SuryaLocation;
        merged.SuryaServerUrl = local.SuryaServerUrl;
        merged.SuryaApiKey = local.SuryaApiKey;
        merged.BuiltInStylesVersion = local.BuiltInStylesVersion;
        return merged;
    }

    // ------------------------------------------------------------------ processing

    /// <summary>The companies that receive the invoices. The buyer VAT number must match one of them.</summary>
    public List<ReceiverCompany> ReceiverCompanies { get; set; } = new();

    public int RenderDpi { get; set; } = 200;
    public string OcrLanguage { get; set; } = "";
    public bool AutoRotate { get; set; } = true;

    public bool RemoveBlankPages { get; set; } = true;
    /// <summary>A page whose share of dark pixels is below this value is blank.</summary>
    public double BlankInkThreshold { get; set; } = 0.0005;

    /// <summary>A page containing any of these words never starts a new invoice.</summary>
    public List<string> ContinuationKeywords { get; set; } = DefaultContinuationKeywords();

    /// <summary>"Folytatólap"; the printed back of thermal paper till receipts ("A hőpapír pénztárgép … hőnyomtatóban").</summary>
    public static List<string> DefaultContinuationKeywords() => new() { "Folytatólap", "hőpapír pénztárgép" };

    /// <summary>
    /// Titles of attachment pages (general terms printed on the back): a page whose top starts with one of these lines
    /// is a continuation of the invoice before it, even if it carries the issuer's logo.
    /// </summary>
    public List<string> ContinuationTitles { get; set; } = new() { "Általános szerződési feltételek", "Általános Szállítási Feltételek", "ÁSZF" };

    /// <summary>A page with no known style starts a new invoice when it mentions a receiver company.</summary>
    public bool NewDocumentOnReceiverMatch { get; set; } = true;

    /// <summary>A page with a text line equal to one of these titles starts a new invoice.</summary>
    public List<string> InvoiceTitleKeywords { get; set; } = new()
    {
        "Számla", "Elektronikus számla", "Egyszerűsített számla", "Készpénzfizetési számla",
        "Jóváíró számla", "Helyesbítő számla", "Sztornó számla", "Invoice",
    };

    /// <summary>
    /// Titles of documents that are not invoices (delivery note, bank statement, transfer, invoice attachment …). A
    /// document read with the generic rules whose first page has such a title but no invoice title gets a warning:
    /// it usually belongs to the invoice before it.
    /// </summary>
    public List<string> NonInvoiceKeywords { get; set; } = DefaultNonInvoiceKeywords();

    public static List<string> DefaultNonInvoiceKeywords() => new()
    {
        "Szállítólevél", "Szállítólevél / Delivery note", "Delivery note", "Számlakivonat", "Bankszámlakivonat",
        "Folyószámla kivonat", "Számla melléklet", "Számlamelléklet", "Melléklet", "Átutalási megbízás",
        "Utalási értesítő", "Utalás", "Díjbekérő", "Előlegbekérő", "Proforma számla", "Pro forma", "Árajánlat",
        "Ajánlat", "Megrendelés", "Rendelés visszaigazolás", "Megrendelés visszaigazolás", "Fizetési felszólítás",
        "Fizetési emlékeztető", "Egyenlegközlő", "Egyenlegértesítő", "Teljesítésigazolás", "Átvételi elismervény",
        "Kísérőlevél", "Értesítés", "Szerződés", "Jótállási jegy", "Munkalap",
        // Not "Számlaösszesítő" alone: Magyar Telekom invoices start with a page titled like that.
        "Számlaösszesítő / Invoice summary", "Nem adóügyi bizonylat / Not valid for VAT purposes",
        "Nem adóügyi bizonylat",
    };

    /// <summary>Consecutive documents with the same style and invoice number are merged into one.</summary>
    public bool MergeSameInvoiceNumber { get; set; } = true;

    /// <summary>Extra margin (normalized) around regions when collecting words.</summary>
    public double RegionPadding { get; set; } = 0.005;

    /// <summary>Allowed OCR errors per character when matching identifier texts (0.15 = 15%).</summary>
    public double FuzzyTolerance { get; set; } = 0.2;

    /// <summary>
    /// Accept the buyer's 8-digit tax number core (törzsszám) when the full number is not printed - simplified
    /// invoices and till receipts only have to show the first 8 digits.
    /// </summary>
    public bool AcceptTaxNumberCore { get; set; } = true;

    /// <summary>Optional Surya OCR (local AI model, more accurate, needs Python + surya-ocr).</summary>
    public SuryaMode SuryaMode { get; set; } = SuryaMode.Off;

    /// <summary>python.exe of the environment where surya-ocr is installed (empty = the app's own environment).</summary>
    public string SuryaPython { get; set; } = "";

    /// <summary>Run Surya on this computer or use a Surya server.</summary>
    public SuryaLocation SuryaLocation { get; set; } = SuryaLocation.Local;

    /// <summary>Address of the Invoice Sorter server, e.g. "http://192.168.1.20:8770" (see docs/SZERVER.md).</summary>
    public string SuryaServerUrl { get; set; } = "";

    /// <summary>API key of the Surya server (SURYA_SERVER_API_KEY in its server.env); empty = none.</summary>
    public string SuryaApiKey { get; set; } = "";

    /// <summary>Longest wait for one page from the Surya server (the server reads pages one after the other).</summary>
    public int SuryaServerTimeoutSeconds { get; set; } = 300;

    /// <summary>Invoices with a trust score below this are highlighted as "low trust".</summary>
    public int LowTrustThreshold { get; set; } = 60;

    /// <summary>Treat low-trust invoices as errors (they go to the error list / error folder).</summary>
    public bool LowTrustIsError { get; set; }

    /// <summary>
    /// Look up the official name of supplier tax numbers in the free EU VIES company register (only the 8-digit tax
    /// number is sent; results are cached locally) and use it as the issuer name.
    /// </summary>
    public bool LookupIssuerNames { get; set; } = true;

    /// <summary>Repair OCR damage in issuer names read from the page (accents, "Ktt" → "Kft." …).</summary>
    public bool RepairIssuerNames { get; set; } = true;

    // ------------------------------------------------------------------ dates

    /// <summary>Labels of the issue date (printed right before the date or as the column heading above it).</summary>
    public List<string> IssueDateLabels { get; set; } = DefaultIssueDateLabels();
    public List<string> FulfillmentDateLabels { get; set; } = DefaultFulfillmentDateLabels();
    public List<string> DueDateLabels { get; set; } = DefaultDueDateLabels();

    /// <summary>Labels of dates that are none of the three (periods, balances, contracts …); they win over shorter date labels.</summary>
    public List<string> NotDateLabels { get; set; } = DefaultNotDateLabels();

    /// <summary>
    /// Payment methods of invoices that may print a single date, which is then the issue, fulfilment and due date
    /// (as the payment method cell / start of a line, or after "Fizetés módja"). Till receipts and the handwritten
    /// invoice pad style count anyway.
    /// </summary>
    public List<string> SingleDateKeywords { get; set; } = DefaultSingleDateKeywords();

    /// <summary>A missing date is an error (the invoice goes to the error list); otherwise only the trust score drops.</summary>
    public bool DatesRequired { get; set; } = true;

    public static List<string> DefaultIssueDateLabels() => new()
    {
        "Számla kelte", "Kelte", "Kelt", "Kiállítás dátuma", "Kiállítás kelte", "Kiállítás napja", "Kiállítás", "Kiállítva",
        "Kibocsátás dátuma", "Kibocsátás kelte", "Számla kiállításának dátuma", "Számla kiállítás dátuma", "Számlakiállítás napja",
        "Számla kiállítás napja", "Számla kiállításának napja", "Számla dátuma", "Keltezés", "Kibocsátás napja", "Kiállítás időpontja", "Okirat kelte",
        "Számlázás dátuma", "Bizonylat kelte", "Bizonylat dátuma", "Bizonylatdátum", "Invoice date", "Date of invoice",
        "Date of issue", "Issue date", "Rechnungsdatum",
    };

    public static List<string> DefaultFulfillmentDateLabels() => new()
    {
        "Teljesítés dátuma", "Teljesítés kelte", "Teljesítés időpontja", "Teljesítés napja", "Teljesítési dátum",
        "Teljesítési idő", "Teljesítési időpont", "Teljesítés", "Telj. kelte", "Telj. dátuma", "Telj. időpontja", "Telj. dátum",
        "Szolgáltatás teljesítése", "Teljesítés ideje", "Áfa kibocsátásának dátuma", "Áfa teljesítés dátuma", "Tax point date", "Tax point", "Delivery date", "Date of delivery", "Date of service", "Date of supply",
        "Date of performance", "Performance date", "Fulfilment date", "Fulfillment date", "Leistungsdatum",
    };

    public static List<string> DefaultDueDateLabels() => new()
    {
        "Fizetési határidő", "Fizetési határidő dátuma", "Fiz. határidő", "Fiz.határidő", "Fizetés határideje", "Fizetési határnap",
        "Fizetési hat.", "Fiz. hat.", "Esedékesség", "Esedékesség dátuma", "Esedékes", "Határidő", "Beérkezési határidő", "Befizetési határidő", "Lejárat",
        "Lejárat napja", "Due date", "Payment due", "Payment deadline", "Date due", "Fälligkeit", "Fällig am",
    };

    public static List<string> DefaultNotDateLabels() => new()
    {
        "Teljesítési időszak", "Számlázási időszak", "Számlázott időszak", "Elszámolási időszak", "Időszak", "Időszak kezdete",
        "Időszak vége", "Tárgyidőszak", "Nyitóegyenleg", "Záróegyenleg", "Egyenleg", "Szerződés kelte", "Szerződés dátuma",
        "Szerződés", "Szerződésszám", "Hűség", "Hűségidő", "lejár", "Lejárati dátum", "Érvényes", "Érvényesség",
        "Rendelés dátuma", "Megrendelés dátuma", "Megrendelés", "Rendelés", "Szállítólevél dátuma", "Szállítólevél",
        "Szállítás dátuma", "Felrak", "Lerak", "Nyomtatva", "Nyomtatás", "Leolvasás", "Leolvasás dátuma", "Mérőállás",
        "Születési dátum", "Gyártási dátum", "Befizetve", "Fizetve", "Fizetés dátuma", "Előző számla",
        "Használhatóságának határideje", "Hőpapír", "Elküldés tervezett dátuma", "Lejárati idő", "Lejárati ideje", "Felhasználható", "Terhelés napja", "Terhelés várható napja", "Szavatosság", "Minőségét megőrzi", "Akció", "Érvényes:",
    };

    public static List<string> DefaultSingleDateKeywords() => new()
    {
        "Készpénz", "Készpénzes", "Készpénzzel", "Készpénzes számla", "Készpénzfizetési számla", "Készpénzes fizetés", "Kp.", "Bankkártya", "Kártya", "Kártyás", "Nyugta",
        "Utánvét", "Utánvétel", "Azonnal", "Azonnali",
    };

    /// <summary>
    /// Correctly spelled issuer names. An issuer read from the page that is close to one of these
    /// (OCR errors, missing accents) is replaced by it. Names typed in the review screen are added automatically.
    /// </summary>
    public List<string> KnownIssuers { get; set; } = DefaultKnownIssuers();

    /// <summary>
    /// Issuers seen in the sample scans. "Name | tax number" entries also identify the issuer by its tax number when
    /// the name cannot be read.
    /// </summary>
    public static List<string> DefaultKnownIssuers() => new()
    {
        "Alza.hu Kft.", "DM Kft.", "ELMŰ Hálózati Kft.", "Fővárosi Csatornázási Művek Zrt.", "Fővárosi Vízművek Zrt.",
        "iPon Computer Kft.", "LIDL Magyarország Bt. | 21588017-2-44", "Magyar Posta Zrt.", "Magyar Telekom Nyrt.",
        "MÁV Személyszállítási Zrt. | 13834492-2-44", "METRO Kereskedelmi Kft. | 10828663-2-44", "MOL Nyrt.",
        "MVM Next Energiakereskedelmi Zrt.", "OBI Hungary Retail Kft. | 13136062-2-44", "OMV Hungária Kft. | 10542925-2-44",
        "One Magyarország Zrt.", "Pepco Hungary Kft.", "Praktiker Áruházak Kft.",
        "SHELL Hungary Zrt. | 13891810-2-44", "SimplePay Zrt. | 32835155-2-44", "SPAR Magyarország Kereskedelmi Kft. | 10485824-2-07",
        "TESCO-GLOBAL Zrt. | 10307078-2-44", "ZNET Telekom Zrt. | 25134270-2-20",
    };

    public string FileNameTemplate { get; set; } = DefaultFileNameTemplate;

    /// <summary>Default file name: invoice number first, then the issuer ("A20401313-0340-00001_SPAR Magyarország Kereskedelmi Kft").</summary>
    public const string DefaultFileNameTemplate = "{InvoiceNumber}_{Issuer}";

    /// <summary>The default of earlier versions (replaced on upgrade when the user had not changed it).</summary>
    public const string OldDefaultFileNameTemplate = "{Issuer}_{InvoiceNumber}";
    public bool SaveErrorDocuments { get; set; } = true;
    public string ErrorFolderName { get; set; } = "_Errors";

    /// <summary>Version of the built-in styles last merged into the user's styles (see JsonStore.UpgradeBuiltInStyles).</summary>
    public int BuiltInStylesVersion { get; set; }

    public string LastSourceFolder { get; set; } = "";
    public string LastOutputFolder { get; set; } = "";

    /// <summary>A copy of these settings that checks the buyer VAT number against the given companies only.</summary>
    public AppSettings WithReceivers(IEnumerable<ReceiverCompany> receivers)
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.ReceiverCompanies = receivers.ToList();
        return copy;
    }

    public static AppSettings CreateDefault() => new()
    {
    };
}
