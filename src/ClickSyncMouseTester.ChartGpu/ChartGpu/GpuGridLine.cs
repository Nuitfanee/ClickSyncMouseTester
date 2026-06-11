using System.Windows.Media;

namespace ClickSyncMouseTester.ChartGpu;

public sealed class GpuGridLine
{
    public bool IsVertical { get; set; }

    public float PositionPixels { get; set; }

    public float ThicknessPixels { get; set; } = 1f;

    public Color Color { get; set; } = Colors.Transparent;
}


