namespace ClickSyncMouseTester.ChartGpu;

public sealed class GpuRenderStats
{
    public bool IsDeviceAvailable { get; internal set; }

    public double LastRenderMilliseconds { get; internal set; }

    public int SubmittedSeriesCount { get; internal set; }

    public int SubmittedChunkCount { get; internal set; }
}


