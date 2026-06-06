using System;

namespace ClickSyncMouseTester.ChartGpu;

public sealed class GpuPlotSceneFrame
{
    public bool IsAvailable { get; set; }

    public bool ScreenYAxisPositiveDown { get; set; }

    public bool EnableAutomaticWheelZoom { get; set; }

    public string UnavailableMessage { get; set; } = string.Empty;

    public GpuViewportState DefaultViewport { get; set; } = new GpuViewportState();

    public GpuViewportState Viewport { get; set; } = new GpuViewportState();

    public GpuViewportState DataBounds { get; set; } = new GpuViewportState();

    public GpuPlotStyleSnapshot Style { get; set; } = new GpuPlotStyleSnapshot();

    public GpuGridLine[] GridLines { get; set; } = Array.Empty<GpuGridLine>();

    public GpuGapBand[] GapBands { get; set; } = Array.Empty<GpuGapBand>();

    public GpuSeriesSubmission[] Series { get; set; } = Array.Empty<GpuSeriesSubmission>();
}


