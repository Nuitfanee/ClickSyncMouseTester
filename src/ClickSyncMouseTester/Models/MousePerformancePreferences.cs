namespace ClickSyncMouseTester.Models;

public class MousePerformancePreferences
{
    private readonly double? _lastCpi;

    private readonly MousePerformancePlotType _chartPlotType;

    private readonly bool _chartShowStem;

    private readonly bool _chartShowLines;

    private readonly MousePerformanceChartWindowPlacement _chartWindowPlacement;

    public double? LastCpi => _lastCpi;

    public MousePerformancePlotType ChartPlotType => _chartPlotType;

    public bool ChartShowStem => _chartShowStem;

    public bool ChartShowLines => _chartShowLines;

    public MousePerformanceChartWindowPlacement ChartWindowPlacement => _chartWindowPlacement;

    public MousePerformancePreferences(double? lastCpi, MousePerformancePlotType chartPlotType, bool chartShowStem, bool chartShowLines, MousePerformanceChartWindowPlacement chartWindowPlacement)
    {
        _lastCpi = lastCpi;
        _chartPlotType = chartPlotType;
        _chartShowStem = chartShowStem;
        _chartShowLines = chartShowLines;
        _chartWindowPlacement = chartWindowPlacement ?? new MousePerformanceChartWindowPlacement(hasSavedBounds: false, 0.0, 0.0, 0.0, 0.0, isMaximized: false);
    }
}





