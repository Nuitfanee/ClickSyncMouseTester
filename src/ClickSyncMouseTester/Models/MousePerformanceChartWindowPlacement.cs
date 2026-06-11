namespace ClickSyncMouseTester.Models;

public class MousePerformanceChartWindowPlacement
{
    private readonly bool _hasSavedBounds;

    private readonly double _left;

    private readonly double _top;

    private readonly double _width;

    private readonly double _height;

    private readonly bool _isMaximized;

    public bool HasSavedBounds => _hasSavedBounds;

    public double Left => _left;

    public double Top => _top;

    public double Width => _width;

    public double Height => _height;

    public bool IsMaximized => _isMaximized;

    public MousePerformanceChartWindowPlacement(bool hasSavedBounds, double left, double top, double width, double height, bool isMaximized)
    {
        _hasSavedBounds = hasSavedBounds;
        _left = left;
        _top = top;
        _width = width;
        _height = height;
        _isMaximized = isMaximized;
    }
}





