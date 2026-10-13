using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InvoiceSorter.Core.Logging;

namespace InvoiceSorter.App;

/// <summary>Step indicator color: current step = accent, done = green, upcoming = gray. Parameter = step number.</summary>
public sealed class StepBrushConverter : IValueConverter
{
    public static readonly Brush Active = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1));
    public static readonly Brush Done = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    public static readonly Brush Upcoming = new SolidColorBrush(Color.FromRgb(0xB0, 0xB7, 0xC3));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int step = (int)value, p = int.Parse((string)parameter);
        // Steps 2 and 3 run together; while on step 3, step 2 is done.
        return step == p ? Active : step > p ? Done : Upcoming;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the step number is one of the comma separated values in the parameter.</summary>
public sealed class StepVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ((string)parameter).Split('|', ',').Select(int.Parse).Contains((int)value) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ErrorBrushConverter : IValueConverter
{
    public static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    public static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? ErrorBrush : OkBrush;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Trust score badge color: high = green, medium = amber, low = red.</summary>
public sealed class TrustBrushConverter : IValueConverter
{
    private static readonly Brush High = Frozen(0x16, 0xA3, 0x4A);
    private static readonly Brush Medium = Frozen(0xD9, 0x77, 0x06);
    private static readonly Brush Low = Frozen(0xDC, 0x26, 0x26);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int t
        ? t >= InvoiceSorter.Core.Processing.TrustScorer.HighThreshold ? High
        : t >= InvoiceSorter.Core.Processing.TrustScorer.MediumThreshold ? Medium : Low
        : Low;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Dot color for an invoice status text: OK = green, Error = red, Excluded = gray.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "Rendben" => ErrorBrushConverter.OkBrush,
        "Kizárva" => Brushes.Silver,
        _ => ErrorBrushConverter.ErrorBrush,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ErrorBackgroundConverter : IValueConverter
{
    private static readonly Brush ErrorBg = new SolidColorBrush(Color.FromRgb(0xFE, 0xF2, 0xF2));
    private static readonly Brush OkBg = new SolidColorBrush(Color.FromRgb(0xF0, 0xFD, 0xF4));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? ErrorBg : OkBg;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class LogLevelBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        LogLevel.Error => ErrorBrushConverter.ErrorBrush,
        LogLevel.Warning => new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)),
        LogLevel.Debug => Brushes.Gray,
        _ => SystemColors.ControlTextBrush,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>Set to true to show the element when the value IS null.</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool hasValue = value != null && !(value is string s && s.Length == 0);
        return hasValue ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the enum value's name is one of the comma separated names in the parameter.</summary>
public sealed class EnumVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value != null && ((string)parameter).Split('|', ',').Contains(value.ToString()) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class RegionTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is InvoiceSorter.Core.Models.NormRect r ? $"terület: {r.X:P0}, {r.Y:P0} ({r.W:P0} × {r.H:P0})" : (string)(parameter ?? "bárhol az oldalon");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Loads a small, cached thumbnail of a page image.</summary>
public sealed class ThumbnailConverter : IValueConverter
{
    private static readonly Dictionary<string, BitmapImage> Cache = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 110;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            if (Cache.Count > 1000) Cache.Clear();
            Cache[path] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Validity of a receiving company's tax number, for the column in Settings ("✔ valid", "✖ check digit wrong").</summary>
public sealed class VatCheckConverter : IValueConverter
{
    public static (bool? Ok, string Text) Check(string? vat)
    {
        if (string.IsNullOrWhiteSpace(vat)) return (null, "");
        var t = Core.Processing.VatValidator.Parse(vat);
        if (t == null) return (false, "✖ 8 vagy 11 számjegy kell");
        if (!Core.Processing.VatValidator.IsChecksumValid(t.Core)) return (false, "✖ hibás ellenőrzőszám – elírás?");
        if (t.Vat != null && !Core.Processing.VatValidator.IsPlausibleFull(t)) return (false, "✖ hibás ÁFA-kód / megyekód");
        return (true, t.Vat == null ? "✔ érvényes (8 jegyű törzsszám)" : "✔ érvényes");
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Check(value as string).Text;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class VatCheckBrushConverter : IValueConverter
{
    private static readonly Brush Ok = Frozen(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush Bad = Frozen(Color.FromRgb(0xDC, 0x26, 0x26));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        VatCheckConverter.Check(value as string).Ok == false ? Bad : Ok;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the number is 0 (e.g. "No deductions" for an empty list).</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    /// <summary>Visible when the number is NOT 0.</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is int n && n == 0) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Hungarian names of the enum values shown in combo boxes (field modes, label directions …).</summary>
public sealed class EnumTextConverter : IValueConverter
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["Fixed"] = "Állandó szöveg",
        ["Region"] = "Terület",
        ["Label"] = "Címke mellett",
        ["PageRegex"] = "Regex az egész oldalon",
        ["Right"] = "jobbra",
        ["Below"] = "alatta",
        ["Auto"] = "automatikus",
    };

    public static string Text(object? value) =>
        value == null ? "" : Names.TryGetValue(value.ToString()!, out var name) ? name : value.ToString()!;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Text(value);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
