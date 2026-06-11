using System;

namespace ClickSyncMouseTester.ChartGpu;

public sealed class GpuSegmentChunk
{
    public int ChunkIndex { get; set; }

    public double OriginX { get; set; }

    public double OriginY { get; set; }

    public double MinimumX { get; set; }

    public double MaximumX { get; set; }

    public double MinimumY { get; set; }

    public double MaximumY { get; set; }

    public GpuSegmentVertex[] Segments { get; set; } = Array.Empty<GpuSegmentVertex>();
}


