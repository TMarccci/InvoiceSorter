using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace InvoiceSorter.App;

/// <summary>
/// Keeps the windows usable on small screens (a 1366×768 laptop at 125 % scaling has a 1093×614 work area): a window
/// that is larger than the screen opens maximized instead of hanging off it, and F12 (or F11) switches full screen –
/// no title bar, no taskbar – on and off.
/// </summary>
public static class WindowFit
{
    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => FitToScreen(window);
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.F12 or Key.F11 && Keyboard.Modifiers == ModifierKeys.None)
            {
                ToggleFullScreen(window);
                e.Handled = true;
            }
        };
    }

    private sealed record Saved(WindowStyle Style, ResizeMode Resize, WindowState State);

    private static readonly ConditionalWeakTable<Window, Saved> FullScreenWindows = new();

    public static bool IsFullScreen(Window window) => FullScreenWindows.TryGetValue(window, out _);

    public static void ToggleFullScreen(Window window)
    {
        if (FullScreenWindows.TryGetValue(window, out var saved))
        {
            FullScreenWindows.Remove(window);
            window.WindowState = WindowState.Normal;
            window.WindowStyle = saved.Style;
            window.ResizeMode = saved.Resize;
            window.WindowState = saved.State;
        }
        else
        {
            FullScreenWindows.Add(window, new Saved(window.WindowStyle, window.ResizeMode, window.WindowState));
            // Through "Normal": a window that is maximized already would keep the taskbar visible.
            window.WindowState = WindowState.Normal;
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Shrinks the size limits to the work area of the window's monitor; a window that does not fit is maximized.</summary>
    private static void FitToScreen(Window window)
    {
        if (WorkArea(window) is not { } work) return;
        window.MinWidth = Math.Min(window.MinWidth, work.Width);
        window.MinHeight = Math.Min(window.MinHeight, work.Height);
        if (window.Width <= work.Width && window.Height <= work.Height) return;

        window.Width = Math.Min(window.Width, work.Width);
        window.Height = Math.Min(window.Height, work.Height);
        window.Left = work.Left;
        window.Top = work.Top;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.WindowState = WindowState.Maximized;
    }

    /// <summary>The work area (screen without the taskbar) of the monitor the window opens on, in WPF units.</summary>
    private static Rect? WorkArea(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var owner = window.Owner != null ? new WindowInteropHelper(window.Owner).Handle : IntPtr.Zero;
            var monitor = owner != IntPtr.Zero
                ? MonitorFromWindow(owner, MonitorDefaultToNearest)
                : GetCursorPos(out var cursor) ? MonitorFromPoint(cursor, MonitorDefaultToNearest) : MonitorFromWindow(handle, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
            var dpi = VisualTreeHelper.GetDpi(window);
            var r = info.Work;
            return new Rect(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY, (r.Right - r.Left) / dpi.DpiScaleX, (r.Bottom - r.Top) / dpi.DpiScaleY);
        }
        catch
        {
            return SystemParameters.WorkArea;
        }
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
