using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using InvoiceSorter.App.ViewModels;
using InvoiceSorter.App.Views;
using InvoiceSorter.Core.Models;

namespace InvoiceSorter.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    private List<System.Windows.Controls.Primitives.ButtonBase> _hotkeyButtons = new();

    /// <summary>The buttons with a keyboard shortcut (<see cref="Hotkey"/>).</summary>
    internal IReadOnlyList<System.Windows.Controls.Primitives.ButtonBase> HotkeyButtons => _hotkeyButtons;

    public MainWindow()
    {
        InitializeComponent();
        WindowFit.Attach(this, f11: false);
        ViewModel = new MainViewModel();
        DataContext = ViewModel;

        // F1-F11 and Shift+F1-F11: the action buttons of the review screen (F12: full screen, see WindowFit).
        Loaded += (_, _) => _hotkeyButtons = Hotkey.Collect(this);
        PreviewKeyDown += (_, e) =>
        {
            if (Hotkey.Gesture(e) is { } gesture && Hotkey.Press(_hotkeyButtons, gesture)) e.Handled = true;
        };

        // Keep the log scrolled to the newest entry.
        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedDocument) && ViewModel.SelectedDocument != null)
                DocumentsGrid.ScrollIntoView(ViewModel.SelectedDocument);
        };

        // Central configuration and the new-version check run once the window is visible.
        ContentRendered += async (_, _) => await ViewModel.StartBackgroundChecksAsync();
    }

    private async void Update_Click(object sender, RoutedEventArgs e) => await ViewModel.InstallUpdateAsync();

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel.IsBusy || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var file = ((string[])e.Data.GetData(DataFormats.FileDrop)).FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        if (file == null) return;
        if (ViewModel.Job != null && ViewModel.Step != WizardStep.Source) return; // don't replace a job in progress by accident
        ViewModel.SetSource(file);
    }

    private void OpenStyles_Click(object sender, RoutedEventArgs e) => OpenStyleEditor(null, createNew: false);

    private void CreateStyleFromPage_Click(object sender, RoutedEventArgs e) => OpenStyleEditor(ViewModel.SelectedPage, createNew: true);

    public bool? OpenStyleEditor(PageInfo? page, bool createNew)
    {
        var editor = new StyleEditorWindow(ViewModel, page, createNew) { Owner = this };
        var result = editor.ShowDialog();
        if (result != true || ViewModel.IsCentral) return result;

        ViewModel.ReplaceStyles(editor.ResultStyles);
        if (ViewModel.Job != null)
        {
            var answer = MessageBox.Show(this,
                "A sablonok mentve. Újra szétbontja és beolvassa a jelenlegi fájlt a módosított sablonokkal?\n\n(Az ellenőrző lista kézi módosításai elvesznek. A Nem gombbal csak a kijelölt számlát olvassa újra.)",
                "Sablonok alkalmazása", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) ViewModel.RerunDetection();
            else if (ViewModel.SelectedDocument != null) ViewModel.RereadDocumentCommand.Execute(null);
        }
        return result;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(ViewModel.Settings) { Owner = this };
        if (window.ShowDialog() == true) ViewModel.ReplaceSettings(window.Result);
    }

    /// <summary>"Merge into…": a menu of all other invoices of the scan; the chosen one receives the pages.</summary>
    private void MergeInto_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button) return;
        var targets = ViewModel.MergeTargets;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxHeight = 480 };
        if (targets.Count == 0) menu.Items.Add(new MenuItem { Header = "Nincs másik számla", IsEnabled = false });
        foreach (var target in targets)
        {
            var header = $"#{target.Number}  ·  {target.PageRange}. o.  ·  {Shorten(target.Issuer, 34)}  ·  {Shorten(target.InvoiceNumber, 26)}";
            var item = new MenuItem { Header = header, ToolTip = target.HasErrors ? target.ErrorsText : null };
            item.Click += (_, _) => ViewModel.MergeSelectedInto(target);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>"Deleted pages…": a menu of the deleted pages; the chosen one goes back where it was deleted from.</summary>
    private void RestorePages_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxHeight = 480 };
        foreach (var page in ViewModel.DeletedPages)
        {
            var item = new MenuItem { Header = ViewModel.RestoreTargetText(page) };
            item.Click += (_, _) => ViewModel.RestoreDeletedPage(page);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "Nincs törölt oldal", IsEnabled = false });
        menu.IsOpen = true;
    }

    /// <summary>Delete on a page thumbnail: delete that page (the command asks first).</summary>
    private void PagesList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete || !ViewModel.DeletePageCommand.CanExecute(null)) return;
        ViewModel.DeletePageCommand.Execute(null);
        e.Handled = true;
    }

    private static string Shorten(string s, int max) =>
        string.IsNullOrWhiteSpace(s) ? "–" : s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Shows the full OCR text of the selected page.</summary>
    private void OcrText_Click(object sender, RoutedEventArgs e)
    {
        var page = ViewModel.SelectedPage ?? ViewModel.SelectedDocument?.Pages.FirstOrDefault();
        if (page == null) return;
        new OcrTextWindow(page, ViewModel.SelectedDocument) { Owner = this }.Show();
    }

    /// <summary>A suggested value (chip under issuer / invoice number) was clicked: take it over.</summary>
    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: InvoiceSorter.Core.Processing.InvoiceProcessor.ValueSuggestion s } button) return;
        if (Equals(button.Tag, "issuer")) ViewModel.ApplyIssuerSuggestion(s.Value);
        else ViewModel.ApplyNumberSuggestion(s.Value);
    }

    /// <summary>A suggested date chip was clicked: it becomes the value of its date field.</summary>
    private void DateSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: InvoiceSorter.Core.Processing.InvoiceProcessor.ValueSuggestion s } button) return;
        var kind = (button.Tag as string) switch
        {
            "issue" => InvoiceSorter.Core.Processing.DateKind.Issue,
            "fulfillment" => InvoiceSorter.Core.Processing.DateKind.Fulfillment,
            _ => InvoiceSorter.Core.Processing.DateKind.Due,
        };
        ViewModel.ApplyDateSuggestion(kind, s.Value);
    }

    /// <summary>Enter in a date field commits the typed date (it is normalized to 2026.09.02.).</summary>
    private void DateBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        box.SelectAll();
        e.Handled = true;
    }

    // ---- page thumbnails: drag & drop to reorder

    private Point _dragStart;
    private PageInfo? _dragPage;

    private void PagesList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragPage = (e.OriginalSource as DependencyObject) is { } source && ItemsControl.ContainerFromElement(PagesList, source) is ListBoxItem item
            ? item.DataContext as PageInfo
            : null;
    }

    private void PagesList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragPage == null || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var page = _dragPage;
        _dragPage = null;
        DragDrop.DoDragDrop(PagesList, new DataObject(typeof(PageInfo), page), DragDropEffects.Move);
    }

    private void PagesList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(PageInfo)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void PagesList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PageInfo)) is not PageInfo page || ViewModel.SelectedDocument is not { } doc) return;
        // Dropped on a thumbnail: take its place (upper half = before it, lower half = after it); on empty space: last.
        int index = doc.Pages.Count - 1;
        if ((e.OriginalSource as DependencyObject) is { } source && ItemsControl.ContainerFromElement(PagesList, source) is ListBoxItem item &&
            item.DataContext is PageInfo over)
        {
            index = doc.Pages.IndexOf(over);
            bool after = e.GetPosition(item).Y > item.ActualHeight / 2;
            int from = doc.Pages.IndexOf(page);
            if (after && from > index) index++;
            else if (!after && from < index) index--;
        }
        ViewModel.MovePage(page, index);
        e.Handled = true;
    }

    private void StyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only react to the user picking a style, not to the combo following the selected invoice.
        if (sender is not ComboBox combo || !combo.IsDropDownOpen && !combo.IsKeyboardFocusWithin) return;
        ViewModel.ApplyStyleToSelected(combo.SelectedItem as InvoiceStyle);
    }
}
