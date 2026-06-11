using System;
using System.Runtime.Versioning;
using System.Windows.Media.Imaging;

namespace ClickSyncMouseTester.ChartGpu;

[SupportedOSPlatform("windows")]
public sealed class MousePerformanceChartGpuOffscreenRenderer : IDisposable
{
    private readonly ChartGpuRenderer _renderer = new ChartGpuRenderer();

    private bool _isDisposed;

    public BitmapSource? Render(GpuPlotSceneFrame? scene, double logicalWidth, double logicalHeight, int pixelWidth, int pixelHeight, out string failureReason)
    {
        failureReason = string.Empty;
        if (_isDisposed)
        {
            return null;
        }

        BitmapSource? result = _renderer.RenderOffscreen(scene, logicalWidth, logicalHeight, pixelWidth, pixelHeight);
        failureReason = _renderer.FailureReason;
        return result;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _renderer.Dispose();
    }
}
