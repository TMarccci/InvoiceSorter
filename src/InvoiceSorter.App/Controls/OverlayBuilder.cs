using System.Windows.Media;
using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;

namespace InvoiceSorter.App.Controls;

/// <summary>Builds the boxes drawn over a page: recognized text lines, identifier, issuer, invoice number, buyer VAT.</summary>
public static class OverlayBuilder
{
    private static readonly Brush LineBrush = Frozen(Color.FromRgb(0x60, 0xA5, 0xFA));
    public static readonly Brush IdentifierBrush = Brushes.MediumPurple;
    public static readonly Brush IssuerBrush = Brushes.SeaGreen;
    public static readonly Brush NumberBrush = Brushes.RoyalBlue;
    public static readonly Brush VatBrush = Brushes.DarkOrange;

    /// <param name="showLines">Also outline every recognized text line (thin, used for the live OCR preview).</param>
    public static List<PageOverlay> Build(PageInfo page, InvoiceStyle? style, AppSettings settings, bool showLines)
    {
        var list = new List<PageOverlay>();
        if (showLines)
        {
            foreach (var line in page.Lines.Where(l => l.Words.Count > 0))
            {
                double x0 = line.Words.Min(w => w.X), y0 = line.Words.Min(w => w.Y);
                double x1 = line.Words.Max(w => w.X + w.W), y1 = line.Words.Max(w => w.Y + w.H);
                list.Add(new PageOverlay(new NormRect(x0, y0, x1 - x0, y1 - y0), LineBrush, "", 1, false));
            }
        }

        if (style != null)
        {
            foreach (var rule in style.IdentifierRules.Where(r => r.Region != null))
                list.Add(new PageOverlay(rule.Region!, IdentifierBrush, "Azonosító", 1.5, false));
            AddField(list, style.Issuer, page, settings, "Kibocsátó", IssuerBrush);
            AddField(list, style.InvoiceNumber, page, settings, "Számlaszám", NumberBrush);
        }

        var vat = FindVatBox(page, settings);
        if (vat != null) list.Add(new PageOverlay(vat, VatBrush, "Vevő adószáma"));
        return list;
    }

    private static void AddField(List<PageOverlay> list, FieldDefinition field, PageInfo page, AppSettings settings, string caption, Brush brush)
    {
        var r = InvoiceProcessor.ReadFieldWithFallback(field, page, settings);
        if (r.LabelBox != null) list.Add(new PageOverlay(r.LabelBox, Brushes.Gray, caption + " (címke)"));
        if (r.ValueBox != null) list.Add(new PageOverlay(r.ValueBox, r.Value != null ? brush : Brushes.Red, r.Value != null ? caption : caption + " (nem olvasható)"));
    }

    /// <summary>Locates the receiver VAT number on the page (word level), in any reading of the page.</summary>
    public static NormRect? FindVatBox(PageInfo page, AppSettings settings)
    {
        var cores = settings.ReceiverCompanies.Select(r => VatValidator.Parse(r.VatNumber)?.Core).OfType<string>().ToList();
        if (cores.Count == 0) return null;
        foreach (var reading in page.Readings)
        foreach (var line in reading.Lines)
        {
            if (!VatValidator.FindCandidates(line.Text).Any(c => cores.Contains(c.Core))) continue;
            var hit = line.Words.FirstOrDefault(w => cores.Any(c => new string(w.Text.Where(char.IsDigit).ToArray()).Contains(c[..4])));
            if (hit != null) return new NormRect(hit.X - 0.003, hit.Y - 0.003, hit.W + 0.006, hit.H + 0.006);
        }
        return null;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
