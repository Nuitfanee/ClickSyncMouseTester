using System.Windows.Media;

namespace ClickSyncMouseTester.ChartGpu;

public sealed class GpuPlotStyleSnapshot
{
    public Color PlotBackgroundColor { get; set; } = Colors.Transparent;

    public Color PlotBorderColor { get; set; } = Colors.Transparent;

    public Color UnavailableForegroundColor { get; set; } = Colors.White;
}


