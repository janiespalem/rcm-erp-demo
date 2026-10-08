using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Rcm.Desktop;

public static class WindowViewport
{
    public static readonly DependencyProperty FitToWorkAreaProperty = DependencyProperty.RegisterAttached(
        "FitToWorkArea", typeof(bool), typeof(WindowViewport), new PropertyMetadata(false, Changed));

    public static bool GetFitToWorkArea(DependencyObject value) => (bool)value.GetValue(FitToWorkAreaProperty);
    public static void SetFitToWorkArea(DependencyObject value, bool enabled) => value.SetValue(FitToWorkAreaProperty, enabled);

    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not Window window) return;
        if ((bool)args.NewValue) { window.Loaded += Loaded; window.DpiChanged += DpiChanged; }
        else { window.Loaded -= Loaded; window.DpiChanged -= DpiChanged; }
    }

    private static void Loaded(object sender, RoutedEventArgs args) => Fit((Window)sender);
    private static void DpiChanged(object sender, DpiChangedEventArgs args) => Fit((Window)sender);

    private static void Fit(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (info.Work.Right - info.Work.Left) / dpi.DpiScaleX;
        var height = (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY;
        // Window dimensions include chrome; keep the save bar above the taskbar at every DPI.
        window.MinWidth = Math.Min(window.MinWidth, width);
        window.MinHeight = Math.Min(window.MinHeight, height);
        if (window.WindowState != WindowState.Normal) return;
        window.Width = Math.Min(window.ActualWidth, width);
        window.Height = Math.Min(window.ActualHeight, height);
        window.Left = Math.Clamp(window.Left, info.Work.Left / dpi.DpiScaleX, info.Work.Right / dpi.DpiScaleX - window.Width);
        window.Top = Math.Clamp(window.Top, info.Work.Top / dpi.DpiScaleY, info.Work.Bottom / dpi.DpiScaleY - window.Height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
