using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using InvoiceSorter.App.ViewModels;
using InvoiceSorter.App.Views;
using InvoiceSorter.Core.Logging;

namespace InvoiceSorter.App;

/// <summary>
/// Developer aid: "InvoiceSorter.exe sample.pdf --screenshots outDir" processes the file and saves
/// screenshots of every screen, then exits. Used to check the UI without clicking through it.
/// </summary>
internal static class DevScreenshots
{
    public static async Task RunAsync(MainWindow window, string[] args)
    {
        var idx = Array.IndexOf(args, "--screenshots");
        var outDir = idx + 1 < args.Length ? args[idx + 1] : Path.Combine(Path.GetTempPath(), "InvoiceSorterShots");
        Directory.CreateDirectory(outDir);
        var vm = window.ViewModel;
        try
        {
            await Settle();
            Capture(window, Path.Combine(outDir, "1_source.png"));

            var start = vm.StartCommand.ExecuteAsync(null);
            await Task.Delay(6000);
            Capture(window, Path.Combine(outDir, "2_processing.png"));
            bool refineCaptured = false;
            while (!start.IsCompleted)
            {
                await Task.Delay(500);
                if (!refineCaptured && vm.ProgressText.StartsWith(InvoiceSorter.Core.Processing.ProgressInfo.Refine) && vm.ProgressValue >= 3)
                {
                    await Settle(300);
                    Capture(window, Path.Combine(outDir, "3_rereading.png"));
                    refineCaptured = true;
                }
            }
            await start;

            await Settle();
            Capture(window, Path.Combine(outDir, "4_review.png"));
            vm.IsLogExpanded = true;
            await Settle();
            Capture(window, Path.Combine(outDir, "4_review_log.png"));
            vm.IsLogExpanded = false;

            var ok = vm.Job!.Documents.FirstOrDefault(d => !d.HasErrors && d.Style?.Name == "METRO");
            if (ok != null) vm.SelectedDocument = ok;
            await Settle();
            Capture(window, Path.Combine(outDir, "4_review_ok.png"));

            // Simulate review work: exclude a duplicate scan and correct an issuer by hand.
            var duplicate = vm.Job.Documents.LastOrDefault(d => d.IsDuplicate);
            if (duplicate != null)
            {
                vm.SelectedDocument = duplicate;
                duplicate.Excluded = true;
            }
            var unreadable = vm.Job.Documents.FirstOrDefault(d => d.HasErrors && string.IsNullOrEmpty(d.Issuer));
            if (unreadable != null)
            {
                vm.SelectedDocument = unreadable;
                unreadable.Issuer = "Minta Virág Kft.";
            }
            await Settle();
            Capture(window, Path.Combine(outDir, "4_review_edited.png"));

            // Keyboard shortcuts: F10 hides the boxes over the page, Shift+F6 outlines every recognized text line.
            Hotkey.Press(window.HotkeyButtons, "F10");
            await Settle();
            Capture(window, Path.Combine(outDir, "4_review_boxes_off.png"));
            Hotkey.Press(window.HotkeyButtons, "F10");
            Hotkey.Press(window.HotkeyButtons, "Shift+F6");
            await Settle();
            Capture(window, Path.Combine(outDir, "4_review_all_text.png"));
            Hotkey.Press(window.HotkeyButtons, "Shift+F6");

            vm.Step = WizardStep.Save;
            await Settle();
            Capture(window, Path.Combine(outDir, "5_save.png"));
            vm.Step = WizardStep.Review;

            var editor = new StyleEditorWindow(vm, vm.Job.Pages.FirstOrDefault(p => p.Number == 131) ?? vm.Job.Pages.First(p => !p.IsBlank), createNew: false) { Owner = window };
            editor.Show();
            await Settle(1500);
            Capture(editor, Path.Combine(outDir, "6_styles.png"));
            editor.Close();

            var ocrPage = vm.Job.Documents.FirstOrDefault(d => d.HasErrors)?.Pages.FirstOrDefault() ?? vm.Job.Pages.First(p => !p.IsBlank);
            var ocr = new OcrTextWindow(ocrPage, vm.SelectedDocument) { Owner = window };
            ocr.Show();
            await Settle(800);
            Capture(ocr, Path.Combine(outDir, "8_ocr_text.png"));
            ocr.Close();

            var settings = new SettingsWindow(vm.Settings) { Owner = window };
            settings.Show();
            await Settle();
            // Every page of the settings window.
            for (int i = 0; i < settings.PageCount; i++)
            {
                settings.ShowPage(i);
                await Settle(400);
                Capture(settings, Path.Combine(outDir, $"7_settings_{i}.png"));
            }
            settings.ShowPage(0);
            settings.Close();

            // Deleting a page of a multi-page invoice, then the "Deleted pages" menu, then full screen (F12).
            var multi = vm.Job.Documents.FirstOrDefault(d => d.Pages.Count >= 3) ?? vm.Job.Documents.FirstOrDefault(d => d.Pages.Count >= 2);
            if (multi != null)
            {
                vm.SelectedDocument = multi;
                vm.SelectedPage = multi.Pages[1];
                vm.DeleteSelectedPage(ask: false);
                await Settle();
                Capture(window, Path.Combine(outDir, "9_page_deleted.png"));
                var deleted = vm.DeletedPages.FirstOrDefault();
                if (deleted != null)
                {
                    vm.RestoreDeletedPage(deleted);
                    await Settle();
                    Capture(window, Path.Combine(outDir, "9_page_restored.png"));
                }
            }
            WindowFit.ToggleFullScreen(window);
            await Settle(1200);
            Capture(window, Path.Combine(outDir, "9_fullscreen.png"));
            WindowFit.ToggleFullScreen(window);
            await Settle();

            // A small laptop screen (1366×768 at 125 %): the details scroll, the page stays visible.
            var (state, width, height) = (window.WindowState, window.Width, window.Height);
            window.WindowState = WindowState.Normal;
            window.MinWidth = 600;
            window.MinHeight = 400;
            window.Width = 1093;
            window.Height = 614;
            await Settle(1200);
            Capture(window, Path.Combine(outDir, "9_small_screen.png"));
            (window.Width, window.Height, window.WindowState) = (width, height, state);
            await Settle();
        }
        catch (Exception ex)
        {
            AppLog.Error("Screenshot run failed", ex);
            File.WriteAllText(Path.Combine(outDir, "error.txt"), ex.ToString());
        }
        Application.Current.Shutdown();
    }

    private static async Task Settle(int ms = 800)
    {
        await Task.Delay(ms);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static T? FindChild<T>(DependencyObject parent, Func<T, bool>? match = null) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t && (match == null || match(t))) return t;
            if (FindChild(child, match) is { } found) return found;
        }
        return null;
    }

    private static void Capture(Window w, string path)
    {
        w.UpdateLayout();
        var source = PresentationSource.FromVisual(w);
        double dpiX = 96, dpiY = 96;
        if (source?.CompositionTarget != null)
        {
            dpiX = 96 * source.CompositionTarget.TransformToDevice.M11;
            dpiY = 96 * source.CompositionTarget.TransformToDevice.M22;
        }
        var root = (FrameworkElement)w.Content;
        var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpiX / 96), (int)(root.ActualHeight * dpiY / 96), dpiX, dpiY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawRectangle(w.Background ?? Brushes.White, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            ctx.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
