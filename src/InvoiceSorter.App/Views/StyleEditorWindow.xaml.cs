using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using InvoiceSorter.App.ViewModels;
using InvoiceSorter.Core.Models;

namespace InvoiceSorter.App.Views;

public partial class StyleEditorWindow : Window
{
    private readonly StyleEditorViewModel _vm;

    /// <summary>The edited styles (valid after the dialog returned true).</summary>
    public List<InvoiceStyle> ResultStyles { get; private set; } = new();

    public StyleEditorWindow(MainViewModel main, PageInfo? startPage, bool createNew)
        : this(main.Styles, main.Settings, main.Job, startPage, createNew, readOnly: main.IsCentral)
    {
    }

    /// <param name="readOnly">Central mode of the client: the styles can be looked at and tried, but are edited in the server manager.</param>
    public StyleEditorWindow(IEnumerable<InvoiceStyle> styles, AppSettings settings, InvoiceJob? job, PageInfo? startPage, bool createNew,
                             bool readOnly = false, string? saveCaption = null)
    {
        InitializeComponent();
        _vm = new StyleEditorViewModel(styles, settings, job, startPage, createNew && !readOnly);
        DataContext = _vm;
        Viewer.RegionDrawn += (_, rect) => _vm.OnRegionDrawn(rect);
        Closed += (_, _) => _vm.Dispose();
        if (saveCaption != null) SaveButton.Content = saveCaption;
        if (readOnly)
        {
            Title = "Sablonok (központi – csak olvasható)";
            SaveButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Bezárás";
            FooterText.Text = "Központi konfiguráció: a sablonokat a szerver rendszergazdája szerkeszti a szerverkezelőben. Itt kipróbálhatod őket, de a módosítások nem menthetők.";
            FooterText.Foreground = (System.Windows.Media.Brush)FindResource("AccentDarkBrush");
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Commit a cell that is still being edited.
        Keyboard.ClearFocus();

        var problems = _vm.Validate();
        if (problems.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", problems), "Javítsd ezeket a hibákat", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var warnings = _vm.Warnings();
        if (warnings.Count > 0 &&
            MessageBox.Show(this, string.Join("\n", warnings) + "\n\nMégis mented?", "Sablonok ellenőrzése", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        ResultStyles = _vm.Styles.ToList();
        DialogResult = true;
    }

    private void ClearFieldRegion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FieldDefinition field }) field.Region = null;
    }

    private void PageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        if (int.TryParse(box.Text, out var n)) _ = _vm.GoToPageAsync(n);
    }
}
