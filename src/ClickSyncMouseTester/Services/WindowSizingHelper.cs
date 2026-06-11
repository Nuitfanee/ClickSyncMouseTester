using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;

namespace ClickSyncMouseTester.Services;

[SupportedOSPlatform("windows")]
internal sealed class WindowSizingHelper
{
    private WindowSizingHelper()
    {
    }

    public static void ApplyWindowBounds(Window window, nint hwnd, nint lParam)
    {
        if (window == null || hwnd == IntPtr.Zero || lParam == IntPtr.Zero)
        {
            return;
        }

        nint monitorHandle = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitorHandle == IntPtr.Zero)
        {
            return;
        }
        NativeMethods.MONITORINFOEX monitorInfo = new NativeMethods.MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
        };
        if (!NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return;
        }

        NativeMethods.MINMAXINFO minMaxInfo = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
        NativeMethods.RECT monitorBounds = monitorInfo.rcMonitor;
        NativeMethods.RECT workAreaBounds = monitorInfo.rcWork;
        int workAreaWidth = Math.Max(0, workAreaBounds.Right - workAreaBounds.Left);
        int workAreaHeight = Math.Max(0, workAreaBounds.Bottom - workAreaBounds.Top);
        minMaxInfo.ptMaxPosition.X = workAreaBounds.Left - monitorBounds.Left;
        minMaxInfo.ptMaxPosition.Y = workAreaBounds.Top - monitorBounds.Top;
        minMaxInfo.ptMaxSize.X = workAreaWidth;
        minMaxInfo.ptMaxSize.Y = workAreaHeight;
        minMaxInfo.ptMaxTrackSize.X = workAreaWidth;
        minMaxInfo.ptMaxTrackSize.Y = workAreaHeight;

        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        int minimumWidthPixels = DipToPixels(window.MinWidth, dpi.DpiScaleX);
        int minimumHeightPixels = DipToPixels(window.MinHeight, dpi.DpiScaleY);
        if (minimumWidthPixels > 0)
        {
            minMaxInfo.ptMinTrackSize.X = Math.Max(minMaxInfo.ptMinTrackSize.X, minimumWidthPixels);
        }
        if (minimumHeightPixels > 0)
        {
            minMaxInfo.ptMinTrackSize.Y = Math.Max(minMaxInfo.ptMinTrackSize.Y, minimumHeightPixels);
        }
        Marshal.StructureToPtr(minMaxInfo, lParam, fDeleteOld: false);
    }

    private static int DipToPixels(double value, double dpiScale)
    {
        if (double.IsNaN(value) || value <= 0.0 || dpiScale <= 0.0)
        {
            return 0;
        }
        return (int)Math.Ceiling(value * dpiScale);
    }
}
