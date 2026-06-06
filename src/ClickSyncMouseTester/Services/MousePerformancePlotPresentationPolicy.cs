using ClickSyncMouseTester.Models;

namespace ClickSyncMouseTester.Services;

internal enum MousePerformanceLinePresentationMode
{
    RawConnectedToggle,
    RawConnectedLocked,
    ScatterWithTrendToggle,
    ScatterOnlyLocked,
    ContinuousEstimateLocked,
    TrajectoryLocked,
    DistributionLocked
}

internal enum MousePerformanceStemPresentationMode
{
    RawImpulseToggle,
    Unavailable
}

internal readonly struct MousePerformancePlotPresentationState
{
    public MousePerformancePlotPresentationState(bool showStem, bool showLines)
    {
        ShowStem = showStem;
        ShowLines = showLines;
    }

    public bool ShowStem { get; }

    public bool ShowLines { get; }
}

internal readonly struct MousePerformancePlotPresentationPolicy
{
    public MousePerformancePlotPresentationPolicy(MousePerformanceLinePresentationMode lineMode, MousePerformanceStemPresentationMode stemMode)
    {
        LineMode = lineMode;
        StemMode = stemMode;
    }

    public MousePerformanceLinePresentationMode LineMode { get; }

    public MousePerformanceStemPresentationMode StemMode { get; }

    public bool CanToggleStem => StemMode == MousePerformanceStemPresentationMode.RawImpulseToggle;

    public bool CanToggleLines => LineMode == MousePerformanceLinePresentationMode.RawConnectedToggle
        || LineMode == MousePerformanceLinePresentationMode.ScatterWithTrendToggle;

    public bool UsesContinuousEstimateLine => LineMode == MousePerformanceLinePresentationMode.ContinuousEstimateLocked;

    public string ToolbarSemanticsResourceKey
    {
        get
        {
            return LineMode switch
            {
                MousePerformanceLinePresentationMode.ContinuousEstimateLocked => "MousePerformance.Chart.Description.Semantics.ContinuousEstimate",
                MousePerformanceLinePresentationMode.RawConnectedLocked => "MousePerformance.Chart.Description.Semantics.RawConnected",
                MousePerformanceLinePresentationMode.ScatterOnlyLocked => "MousePerformance.Chart.Description.Semantics.ScatterOnly",
                MousePerformanceLinePresentationMode.TrajectoryLocked => "MousePerformance.Chart.Description.Semantics.Trajectory",
                MousePerformanceLinePresentationMode.DistributionLocked => "MousePerformance.Chart.Description.Semantics.Distribution",
                _ => "MousePerformance.Chart.Description.Semantics.TrendLine",
            };
        }
    }

    public MousePerformancePlotPresentationState ResolveState(bool showStem, bool showLines)
    {
        return new MousePerformancePlotPresentationState(ShouldShowStem(showStem), ResolveEffectiveShowLines(showLines));
    }

    public bool ShouldUseSeriesAsAutomaticViewportSource(MousePerformanceChartSeriesKind seriesKind)
    {
        return seriesKind == MousePerformanceChartSeriesKind.Scatter
            || seriesKind == MousePerformanceChartSeriesKind.Histogram
            || (UsesContinuousEstimateLine && seriesKind == MousePerformanceChartSeriesKind.Line);
    }

    public bool ShouldUseTrendLine(bool showLines)
    {
        return CanToggleLines && !showLines;
    }

    public bool ShouldShowConnectedRawLine(bool showLines)
    {
        return LineMode == MousePerformanceLinePresentationMode.RawConnectedLocked
            || (LineMode == MousePerformanceLinePresentationMode.RawConnectedToggle && showLines);
    }

    public bool ShouldShowStem(bool showStem)
    {
        return CanToggleStem && showStem;
    }

    public bool ResolveEffectiveShowLines(bool showLines)
    {
        return CanToggleLines && showLines;
    }

    public static MousePerformancePlotPresentationPolicy Resolve(MousePerformancePlotType plotType)
    {
        if (MousePerformancePlotTraits.IsHistogramPlot(plotType))
        {
            return new MousePerformancePlotPresentationPolicy(MousePerformanceLinePresentationMode.DistributionLocked, MousePerformanceStemPresentationMode.Unavailable);
        }

        if (MousePerformancePlotTraits.IsVelocityPlot(plotType) || plotType == MousePerformancePlotType.FrequencyVsTime)
        {
            return new MousePerformancePlotPresentationPolicy(MousePerformanceLinePresentationMode.ContinuousEstimateLocked, MousePerformanceStemPresentationMode.Unavailable);
        }

        if (plotType == MousePerformancePlotType.IntervalVsTime)
        {
            return new MousePerformancePlotPresentationPolicy(MousePerformanceLinePresentationMode.ScatterOnlyLocked, MousePerformanceStemPresentationMode.Unavailable);
        }

        if (plotType == MousePerformancePlotType.XVsY)
        {
            return new MousePerformancePlotPresentationPolicy(MousePerformanceLinePresentationMode.TrajectoryLocked, MousePerformanceStemPresentationMode.Unavailable);
        }

        if (MousePerformancePlotTraits.IsCountPlot(plotType))
        {
            return new MousePerformancePlotPresentationPolicy(MousePerformanceLinePresentationMode.RawConnectedToggle, MousePerformanceStemPresentationMode.RawImpulseToggle);
        }

        if (MousePerformancePlotTraits.IsSumPlot(plotType))
        {
            return new MousePerformancePlotPresentationPolicy(MousePerformanceLinePresentationMode.RawConnectedLocked, MousePerformanceStemPresentationMode.Unavailable);
        }

        return new MousePerformancePlotPresentationPolicy(MousePerformanceLinePresentationMode.TrajectoryLocked, MousePerformanceStemPresentationMode.Unavailable);
    }
}
