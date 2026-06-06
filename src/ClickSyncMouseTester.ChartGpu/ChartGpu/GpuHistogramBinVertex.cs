namespace ClickSyncMouseTester.ChartGpu;

public readonly struct GpuHistogramBinVertex(float minimumX, float maximumX, float value)
{
    public readonly float MinimumX = minimumX;

    public readonly float MaximumX = maximumX;

    public readonly float Value = value;
}
