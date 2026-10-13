using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;

namespace InvoiceSorter.App.Views;

/// <summary>
/// The full OCR text of a page, one tab per reading (normal reading, re-reads with zoom / other language /
/// exposure variants, Surya) – to check what the program could actually read.
/// </summary>
public sealed class OcrTextWindow : Window
{
    public OcrTextWindow(PageInfo page, InvoiceDocument? doc)
    {
        Title = $"OCR-szöveg – {page.Number}. oldal" + (doc != null ? $" (#{doc.Number} számla)" : "");
        Width = 980;
        Height = 820;
        MinWidth = 400;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["PageBackground"];

        var tabs = new TabControl { Margin = new Thickness(0, 0, 0, 8), TabStripPlacement = Dock.Left };
        var readings = page.Readings.ToList();
        for (int i = 0; i < readings.Count; i++)
        {
            var r = readings[i];
            var engine = r.Engine == "Surya" ? "Surya" : "Windows OCR";
            var header = i == 0 ? $"Olvasás ({engine})" : $"{i}. újraolvasás ({engine})";
            var text = FieldReader.WordsToText(r.Lines.SelectMany(l => l.Words));
            var box = new TextBox
            {
                Text = text.Length == 0 ? "(nem ismert fel szöveget)" : text,
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(6),
            };
            tabs.Items.Add(new TabItem
            {
                Header = $"{header} · {r.WordCount} szó",
                Content = box,
                ToolTip = i == 0 ? "Az oldal első olvasata" : "Kiegészítő olvasat, mert adatok hiányoztak (nagyítás / másik nyelv / expozíció / Surya)",
            });
        }

        var info = new TextBlock
        {
            Text = $"Forgatás {page.Rotation}° · {page.PixelWidth}×{page.PixelHeight} px · {readings.Count} olvasat. " +
                   "A sorok az oldalon elfoglalt helyük szerint rendezve (fentről lefelé, balról jobbra).",
            Foreground = (Brush)Application.Current.Resources["MutedText"],
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var copy = new Button { Content = "Szöveg másolása", Margin = new Thickness(8, 0, 8, 0), MinWidth = 100 };
        copy.Click += (_, _) =>
        {
            if (tabs.SelectedItem is TabItem { Content: TextBox t }) Clipboard.SetText(t.Text);
        };
        var close = new Button { Content = "Bezárás", IsCancel = true, MinWidth = 100 };
        close.Click += (_, _) => Close();

        var bottom = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(copy, Dock.Right);
        bottom.Children.Add(close);
        bottom.Children.Add(copy);
        bottom.Children.Add(info);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(tabs);
        Content = root;
        if (tabs.Items.Count > 0) tabs.SelectedIndex = 0;
    }
}
