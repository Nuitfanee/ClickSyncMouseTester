using System;
using System.Windows.Media;

namespace ClickSyncMouseTester.ChartGpu;

public sealed class GpuSeriesSubmission
{
    public object? SourceKey { get; set; }

    public GpuSeriesKind Kind { get; set; }

    public int DatasetSlot { get; set; }

    public double XOffset { get; set; }

    public object? GeometryKey { get; set; }

    public Color Color { get; set; } = Colors.Transparent;

    public float RadiusPixels { get; set; }

    public float ThicknessPixels { get; set; }

    public bool UseDataCoordinates { get; set; }

    public GpuPointChunk[] PointChunks { get; set; } = Array.Empty<GpuPointChunk>();

    public GpuSegmentChunk[] SegmentChunks { get; set; } = Array.Empty<GpuSegmentChunk>();

    public GpuHistogramBinChunk[] HistogramBinChunks { get; set; } = Array.Empty<GpuHistogramBinChunk>();
}


