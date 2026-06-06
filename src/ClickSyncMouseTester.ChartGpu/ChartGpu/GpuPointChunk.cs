using System;

namespace ClickSyncMouseTester.ChartGpu;

public sealed class GpuPointChunk
{
    public int ChunkIndex { get; set; }

    public double OriginX { get; set; }

    public double OriginY { get; set; }

    public double MinimumX { get; set; }

    public double MaximumX { get; set; }

    public double MinimumY { get; set; }

    public double MaximumY { get; set; }

    public bool IsMonotonicX { get; set; }

    public GpuPointVertex[] Points { get; set; } = Array.Empty<GpuPointVertex>();
}


