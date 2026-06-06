namespace ClickSyncMouseTester.ChartGpu;

public sealed class GpuViewportState
{
    public double XMinimum { get; set; }

    public double XMaximum { get; set; }

    public double YMinimum { get; set; }

    public double YMaximum { get; set; }

    public GpuViewportState Clone()
    {
        return new GpuViewportState
        {
            XMinimum = XMinimum,
            XMaximum = XMaximum,
            YMinimum = YMinimum,
            YMaximum = YMaximum
        };
    }
}


