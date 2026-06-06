using System.Windows.Media;

namespace ClickSyncMouseTester.ChartGpu;

public sealed class GpuGapBand
{
    public float LeftPixels { get; set; }

    public float WidthPixels { get; set; }

    public float CenterXPixels { get; set; }

    public Color FillColor { get; set; } = Colors.Transparent;

    public Color LineColor { get; set; } = Colors.Transparent;

    public float LineThicknessPixels { get; set; } = 1f;
}


