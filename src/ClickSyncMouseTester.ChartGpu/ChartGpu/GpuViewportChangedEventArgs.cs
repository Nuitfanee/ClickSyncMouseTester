using System;

namespace ClickSyncMouseTester.ChartGpu;

public sealed class GpuViewportChangedEventArgs : EventArgs
{
    public GpuViewportState Viewport { get; }

    public GpuViewportChangedEventArgs(GpuViewportState viewport)
    {
        Viewport = viewport?.Clone() ?? new GpuViewportState();
    }
}


