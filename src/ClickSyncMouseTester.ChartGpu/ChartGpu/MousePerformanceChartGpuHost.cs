using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClickSyncMouseTester.ChartGpu;

[SupportedOSPlatform("windows")]
public sealed class MousePerformanceChartGpuHost : HwndHost
{
    private readonly record struct SizePixels(int Width, int Height);

    private struct NativePoint
    {
        public int X;

        public int Y;
    }

    private struct Rect
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(nint hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(nint hwnd, out Rect rect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetCapture(nint hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetFocus(nint hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ScreenToClient(nint hwnd, ref NativePoint point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ValidateRect(nint hwnd, nint rect);
    }

    private const int WM_ERASEBKGND = 20;

    private const int WM_PAINT = 15;

    private const int WM_LBUTTONDOWN = 513;

    private const int WM_LBUTTONUP = 514;

    private const int WM_MOUSEMOVE = 512;

    private const int WM_MOUSEWHEEL = 522;

    private const int WM_LBUTTONDBLCLK = 515;

    private const int WM_CANCELMODE = 31;

    private const int WM_CAPTURECHANGED = 533;

    private const int WM_SIZE = 5;

    private const uint WS_CHILD = 1073741824u;

    private const uint WS_VISIBLE = 268435456u;

    private const uint WS_CLIPCHILDREN = 33554432u;

    private const uint WS_CLIPSIBLINGS = 67108864u;

    private const uint SS_NOTIFY = 256u;

    private nint _hwnd;

    private readonly ChartGpuRenderer _renderer = new ChartGpuRenderer();

    private GpuPlotSceneFrame? _scene;

    private bool _isPanning;

    private bool _isRenderQueued;

    private bool _isRendering;

    private bool _renderRequestedWhileRendering;

    private int _lastRenderedPixelWidth;

    private int _lastRenderedPixelHeight;

    private Point _panStartPoint;

    private GpuViewportState? _panStartViewport;

    public GpuRenderStats RenderStats => _renderer.RenderStats;

    public bool IsRendererAvailable => _renderer.IsAvailable;

    public string RendererUnavailableMessage => _renderer.FailureReason;

    public event EventHandler<GpuViewportChangedEventArgs>? ViewportChanged;

    public bool FocusHost()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return false;
        }
        NativeMethods.SetFocus(_hwnd);
        Keyboard.Focus(this);
        return true;
    }

    public void SetScene(GpuPlotSceneFrame? scene)
    {
        _scene = scene;
        if (_scene != null && _scene.Viewport == null)
        {
            _scene.Viewport = _scene.DefaultViewport?.Clone() ?? _scene.DataBounds?.Clone() ?? new GpuViewportState();
        }
        RenderNow();
    }

    public bool CanRenderBitmapSize(int pixelWidth, int pixelHeight)
    {
        if (_hwnd == IntPtr.Zero || pixelWidth <= 0 || pixelHeight <= 0)
        {
            return false;
        }
        SizePixels clientSizePixels = GetClientSizePixels();
        if (clientSizePixels.Width >= pixelWidth)
        {
            return clientSizePixels.Height >= pixelHeight;
        }
        return false;
    }

    public BitmapSource? RenderSceneBitmap(GpuPlotSceneFrame? scene, double logicalWidth, double logicalHeight, int pixelWidth, int pixelHeight, out string failureReason)
    {
        failureReason = string.Empty;
        if (_hwnd == IntPtr.Zero || scene == null)
        {
            return null;
        }
        BitmapSource? result = _renderer.RenderToBitmap(_hwnd, scene, logicalWidth, logicalHeight, pixelWidth, pixelHeight);
        failureReason = _renderer.FailureReason;
        return result;
    }

    public bool TryHandleMouseWheelFromScreen(int screenX, int screenY, int wheelDelta, bool shiftPressed, bool controlPressed)
    {
        if (_hwnd == IntPtr.Zero || _scene == null || !_scene.IsAvailable || wheelDelta == 0)
        {
            return false;
        }
        NativePoint screenPoint = new NativePoint
        {
            X = screenX,
            Y = screenY
        };
        HandleMouseWheel(screenPoint, wheelDelta, shiftPressed, controlPressed);
        FocusHost();
        return true;
    }

    public void RenderNow()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }
        if (_isRendering)
        {
            _renderRequestedWhileRendering = true;
            return;
        }
        _isRendering = true;
        _renderRequestedWhileRendering = false;
        _isRenderQueued = false;
        try
        {
            RenderNowCore();
        }
        finally
        {
            _isRendering = false;
            if (_renderRequestedWhileRendering)
            {
                _renderRequestedWhileRendering = false;
                RequestRender();
            }
        }
    }

    private void RenderNowCore()
    {
        if (_hwnd != IntPtr.Zero)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            SizePixels clientSizePixels = GetClientSizePixels();
            double logicalWidth = ResolveLogicalLength(clientSizePixels.Width, dpi.DpiScaleX, base.ActualWidth);
            double logicalHeight = ResolveLogicalLength(clientSizePixels.Height, dpi.DpiScaleY, base.ActualHeight);
            _renderer.Render(_hwnd, _scene, logicalWidth, logicalHeight, clientSizePixels.Width, clientSizePixels.Height);
            _lastRenderedPixelWidth = clientSizePixels.Width;
            _lastRenderedPixelHeight = clientSizePixels.Height;
        }
    }

    private void RequestRender()
    {
        if (_hwnd != IntPtr.Zero)
        {
            if (_isRendering)
            {
                _renderRequestedWhileRendering = true;
            }
            else if (!_isRenderQueued)
            {
                _isRenderQueued = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RenderNow));
            }
        }
    }

    private void RenderNowForResize()
    {
        if (_hwnd != IntPtr.Zero)
        {
            SizePixels clientSizePixels = GetClientSizePixels();
            if (clientSizePixels.Width > 0 && clientSizePixels.Height > 0 && (_isRendering || _isRenderQueued || _lastRenderedPixelWidth != clientSizePixels.Width || _lastRenderedPixelHeight != clientSizePixels.Height))
            {
                RenderNow();
            }
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = NativeMethods.CreateWindowExW(0u, "static", string.Empty, 1442840832u, 0, 0, GetInitialWindowWidthPixels(), GetInitialWindowHeightPixels(), hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create GPU plot host window.");
        }
        RequestRender();
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        nint handle = hwnd.Handle;
        CancelPan();
        _scene = null;
        _isRenderQueued = false;
        _isRendering = false;
        _renderRequestedWhileRendering = false;
        _lastRenderedPixelWidth = 0;
        _lastRenderedPixelHeight = 0;
        _hwnd = IntPtr.Zero;
        _renderer.Dispose();
        if (handle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(handle);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RenderNowForResize();
    }

    protected override bool TabIntoCore(TraversalRequest request)
    {
        if (_hwnd != IntPtr.Zero)
        {
            FocusHost();
            return true;
        }
        return base.TabIntoCore(request);
    }

    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case 20:
                handled = true;
                return new IntPtr(1);
            case 15:
                HandlePaint(hwnd);
                handled = true;
                return IntPtr.Zero;
            case 31:
            case 533:
                CancelPan();
                break;
            case 5:
                RenderNowForResize();
                break;
            case 515:
                HandleDoubleClick();
                handled = true;
                return IntPtr.Zero;
            case 513:
                HandleMouseLeftButtonDown(lParam);
                handled = true;
                return IntPtr.Zero;
            case 512:
                handled = HandleMouseMove(lParam);
                break;
            case 514:
                HandleMouseLeftButtonUp(lParam);
                handled = true;
                return IntPtr.Zero;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private void HandlePaint(nint hwnd)
    {
        NativeMethods.ValidateRect(hwnd, IntPtr.Zero);
        RequestRender();
    }

    private void HandleDoubleClick()
    {
        if (_scene != null)
        {
            _scene.Viewport = ChartViewportController.Reset(_scene);
            RaiseViewportChanged(_scene.Viewport);
            RequestRender();
        }
    }

    private void HandleMouseLeftButtonDown(nint lParam)
    {
        BeginPan(ToLogicalPoint(UnpackClientPoint(lParam)));
    }

    private bool HandleMouseMove(nint lParam)
    {
        if (!_isPanning)
        {
            return false;
        }
        return UpdatePan(ToLogicalPoint(UnpackClientPoint(lParam)));
    }

    private void HandleMouseLeftButtonUp(nint lParam)
    {
        CompletePan(ToLogicalPoint(UnpackClientPoint(lParam)));
    }

    private void HandleMouseWheel(NativePoint screenPoint, int wheelDelta, bool shiftPressed, bool controlPressed)
    {
        if (_scene != null && _scene.IsAvailable)
        {
            NativePoint point = screenPoint;
            NativeMethods.ScreenToClient(_hwnd, ref point);
            Point val = ToLogicalPoint(new Point((double)point.X, (double)point.Y));
            Size logicalRenderSize = GetLogicalRenderSize();
            _scene.Viewport = ChartViewportController.Zoom(_scene, val.X, val.Y, logicalRenderSize.Width, logicalRenderSize.Height, wheelDelta, shiftPressed, controlPressed);
            RaiseViewportChanged(_scene.Viewport);
            RequestRender();
        }
    }

    private void RaiseViewportChanged(GpuViewportState viewport)
    {
        this.ViewportChanged?.Invoke(this, new GpuViewportChangedEventArgs(viewport));
    }

    private bool BeginPan(Point logicalPoint)
    {
        if (_isPanning || _scene == null || !_scene.IsAvailable)
        {
            return false;
        }
        _isPanning = true;
        _panStartPoint = logicalPoint;
        _panStartViewport = _scene.Viewport?.Clone() ?? _scene.DataBounds?.Clone();
        NativeMethods.SetCapture(_hwnd);
        FocusHost();
        return true;
    }

    private bool UpdatePan(Point logicalPoint)
    {
        if (!_isPanning || _scene == null || _panStartViewport == null)
        {
            return false;
        }
        Size logicalRenderSize = GetLogicalRenderSize();
        _scene.Viewport = ChartViewportController.BuildPannedViewport(_scene, _panStartViewport, _panStartPoint.X, _panStartPoint.Y, logicalPoint.X, logicalPoint.Y, logicalRenderSize.Width, logicalRenderSize.Height);
        RaiseViewportChanged(_scene.Viewport);
        RequestRender();
        return true;
    }

    private bool CompletePan(Point logicalPoint)
    {
        if (!_isPanning || _scene == null || _panStartViewport == null)
        {
            CancelPan();
            return false;
        }
        UpdatePan(logicalPoint);
        CancelPan();
        return true;
    }

    private void CancelPan()
    {
        _isPanning = false;
        _panStartPoint = default(Point);
        _panStartViewport = null;
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.ReleaseCapture();
        }
    }

    private static Point UnpackClientPoint(nint lParam)
    {
        long num = lParam;
        short num2 = (short)(num & 0xFFFF);
        short num3 = (short)((num >> 16) & 0xFFFF);
        return new Point((double)num2, (double)num3);
    }

    private Size GetLogicalRenderSize()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        SizePixels clientSizePixels = GetClientSizePixels();
        return new Size(ResolveLogicalLength(clientSizePixels.Width, dpi.DpiScaleX, base.ActualWidth), ResolveLogicalLength(clientSizePixels.Height, dpi.DpiScaleY, base.ActualHeight));
    }

    private Point ToLogicalPoint(Point physicalPoint)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double num = ((dpi.DpiScaleX > 0.0) ? dpi.DpiScaleX : 1.0);
        double num2 = ((dpi.DpiScaleY > 0.0) ? dpi.DpiScaleY : 1.0);
        return new Point(physicalPoint.X / num, physicalPoint.Y / num2);
    }

    private SizePixels GetClientSizePixels()
    {
        if (_hwnd != IntPtr.Zero && NativeMethods.GetClientRect(_hwnd, out var rect))
        {
            return new SizePixels(Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
        }
        return new SizePixels(GetInitialWindowWidthPixels(), GetInitialWindowHeightPixels());
    }

    private int GetInitialWindowWidthPixels()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        return Math.Max(1, (int)Math.Ceiling(Math.Max(1.0, base.ActualWidth) * Math.Max(1.0, dpi.DpiScaleX)));
    }

    private int GetInitialWindowHeightPixels()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        return Math.Max(1, (int)Math.Ceiling(Math.Max(1.0, base.ActualHeight) * Math.Max(1.0, dpi.DpiScaleY)));
    }

    private static double ResolveLogicalLength(int pixelLength, double dpiScale, double fallbackLogicalLength)
    {
        if (pixelLength > 0 && dpiScale > 0.0)
        {
            return (double)pixelLength / dpiScale;
        }
        return Math.Max(1.0, fallbackLogicalLength);
    }
}


