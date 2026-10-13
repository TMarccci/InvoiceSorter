using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using InvoiceSorter.Core.Models;

namespace InvoiceSorter.App.Controls;

/// <summary>A rectangle drawn over the page image.</summary>
public sealed record PageOverlay(NormRect Rect, Brush Stroke, string Caption, double Thickness = 2, bool Fill = true);

/// <summary>
/// Shows a page image fitted to the available width with overlay rectangles, and lets the user
/// draw a rectangle with the mouse (when <see cref="CanDraw"/> is true).
/// </summary>
public sealed class PageViewer : Border
{
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Grid _surface = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly TextBlock _placeholder = new()
    {
        Text = "Nincs kijelölt oldal", Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private Point? _dragStart;
    private Rectangle? _dragRect;
    private double _pixelW = 1, _pixelH = 1;

    public static readonly DependencyProperty ImagePathProperty = DependencyProperty.Register(
        nameof(ImagePath), typeof(string), typeof(PageViewer), new PropertyMetadata(null, (d, _) => ((PageViewer)d).LoadImage()));

    public static readonly DependencyProperty OverlaysProperty = DependencyProperty.Register(
        nameof(Overlays), typeof(IEnumerable), typeof(PageViewer), new PropertyMetadata(null, (d, _) => ((PageViewer)d).DrawOverlays()));

    public static readonly DependencyProperty CanDrawProperty = DependencyProperty.Register(
        nameof(CanDraw), typeof(bool), typeof(PageViewer), new PropertyMetadata(false, (d, _) => ((PageViewer)d).UpdateCursor()));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(PageViewer), new PropertyMetadata(1.0, (d, _) => ((PageViewer)d).UpdateSize()));

    public static readonly DependencyProperty FitWholePageProperty = DependencyProperty.Register(
        nameof(FitWholePage), typeof(bool), typeof(PageViewer), new PropertyMetadata(false, (d, _) => ((PageViewer)d).UpdateSize()));

    /// <summary>
    /// Zoom 1.0 shows the whole page (fits width and height, centered) instead of fitting the width - for the live
    /// preview while processing, where nobody scrolls.
    /// </summary>
    public bool FitWholePage { get => (bool)GetValue(FitWholePageProperty); set => SetValue(FitWholePageProperty, value); }

    public string? ImagePath { get => (string?)GetValue(ImagePathProperty); set => SetValue(ImagePathProperty, value); }
    public IEnumerable? Overlays { get => (IEnumerable?)GetValue(OverlaysProperty); set => SetValue(OverlaysProperty, value); }
    public bool CanDraw { get => (bool)GetValue(CanDrawProperty); set => SetValue(CanDrawProperty, value); }

    /// <summary>1.0 = fit to width.</summary>
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    /// <summary>Raised when the user finished drawing a rectangle (normalized coordinates).</summary>
    public event EventHandler<NormRect>? RegionDrawn;

    public PageViewer()
    {
        Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED));
        _surface.Children.Add(_image);
        _surface.Children.Add(_canvas);
        _surface.HorizontalAlignment = HorizontalAlignment.Center;
        _surface.VerticalAlignment = VerticalAlignment.Top;
        _surface.Margin = new Thickness(8);
        _surface.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.25 };
        _scroll.Content = _surface;

        var root = new Grid();
        root.Children.Add(_scroll);
        root.Children.Add(_placeholder);
        Child = root;

        _scroll.SizeChanged += (_, _) => UpdateSize();
        _canvas.MouseLeftButtonDown += OnMouseDown;
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseLeftButtonUp += OnMouseUp;
        PreviewMouseWheel += OnMouseWheel;
    }

    private void LoadImage()
    {
        _image.Source = null;
        var path = ImagePath;
        _placeholder.Visibility = string.IsNullOrEmpty(path) ? Visibility.Visible : Visibility.Collapsed;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path!);
            bmp.EndInit();
            bmp.Freeze();
            _image.Source = bmp;
            _pixelW = bmp.PixelWidth;
            _pixelH = bmp.PixelHeight;
        }
        catch (Exception ex)
        {
            _placeholder.Text = "Az oldal képe nem tölthető be: " + ex.Message;
            _placeholder.Visibility = Visibility.Visible;
        }
        UpdateSize();
        DrawOverlays();
    }

    private void UpdateSize()
    {
        var available = Math.Max(100, _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth - 16 : ActualWidth - 32);
        if (FitWholePage && _scroll.ActualHeight > 0)
        {
            // The scroll bars are not needed then; use the full area so they do not toggle the size.
            available = Math.Max(100, Math.Min(_scroll.ActualWidth - 16, (_scroll.ActualHeight - 16) * _pixelW / _pixelH));
        }
        _surface.VerticalAlignment = FitWholePage ? VerticalAlignment.Center : VerticalAlignment.Top;
        var width = available * Math.Max(0.2, Zoom);
        _surface.Width = width;
        _surface.Height = width * _pixelH / _pixelW;
        _canvas.Width = _surface.Width;
        _canvas.Height = _surface.Height;
        DrawOverlays();
    }

    private void DrawOverlays()
    {
        _canvas.Children.Clear();
        if (Overlays == null || _image.Source == null) return;
        double w = _canvas.Width, h = _canvas.Height;
        if (double.IsNaN(w) || double.IsNaN(h)) return;

        foreach (var o in Overlays.OfType<PageOverlay>())
        {
            Brush? fill = null;
            if (o.Fill)
            {
                fill = o.Stroke.Clone();
                fill.Opacity = 0.12;
            }
            var rect = new Rectangle { Width = Math.Max(2, o.Rect.W * w), Height = Math.Max(2, o.Rect.H * h), Stroke = o.Stroke, StrokeThickness = o.Thickness, Fill = fill, IsHitTestVisible = false };
            Canvas.SetLeft(rect, o.Rect.X * w);
            Canvas.SetTop(rect, o.Rect.Y * h);
            _canvas.Children.Add(rect);

            if (!string.IsNullOrEmpty(o.Caption))
            {
                var label = new Border
                {
                    Background = o.Stroke, CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 1, 4, 1), IsHitTestVisible = false,
                    Child = new TextBlock { Text = o.Caption, Foreground = Brushes.White, FontSize = 11 },
                };
                Canvas.SetLeft(label, o.Rect.X * w);
                Canvas.SetTop(label, Math.Max(0, o.Rect.Y * h - 18));
                _canvas.Children.Add(label);
            }
        }
    }

    private void UpdateCursor() => _canvas.Cursor = CanDraw ? Cursors.Cross : Cursors.Arrow;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanDraw || _image.Source == null) return;
        _dragStart = e.GetPosition(_canvas);
        _dragRect = new Rectangle { Stroke = Brushes.DodgerBlue, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 2 }, Fill = new SolidColorBrush(Color.FromArgb(40, 30, 144, 255)) };
        Canvas.SetLeft(_dragRect, _dragStart.Value.X);
        Canvas.SetTop(_dragRect, _dragStart.Value.Y);
        _canvas.Children.Add(_dragRect);
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start || _dragRect == null) return;
        var p = e.GetPosition(_canvas);
        Canvas.SetLeft(_dragRect, Math.Min(start.X, p.X));
        Canvas.SetTop(_dragRect, Math.Min(start.Y, p.Y));
        _dragRect.Width = Math.Abs(p.X - start.X);
        _dragRect.Height = Math.Abs(p.Y - start.Y);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not Point start) return;
        _canvas.ReleaseMouseCapture();
        var p = e.GetPosition(_canvas);
        _dragStart = null;
        if (_dragRect != null) _canvas.Children.Remove(_dragRect);
        _dragRect = null;

        double w = _canvas.Width, h = _canvas.Height;
        double x0 = Math.Clamp(Math.Min(start.X, p.X) / w, 0, 1), y0 = Math.Clamp(Math.Min(start.Y, p.Y) / h, 0, 1);
        double x1 = Math.Clamp(Math.Max(start.X, p.X) / w, 0, 1), y1 = Math.Clamp(Math.Max(start.Y, p.Y) / h, 0, 1);
        if (x1 - x0 < 0.005 || y1 - y0 < 0.003) return; // a click, not a drag
        RegionDrawn?.Invoke(this, new NormRect(x0, y0, x1 - x0, y1 - y0));
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        Zoom = Math.Clamp(Zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.3, 5);
        e.Handled = true;
    }
}
