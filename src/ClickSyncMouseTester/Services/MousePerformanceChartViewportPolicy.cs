using ClickSyncMouseTester.Models;
using System;

namespace ClickSyncMouseTester.Services;

internal sealed class MousePerformanceChartViewportPolicy
{
    internal struct AutomaticViewportRobustRangeSettings
    {
        public double OutlierFenceMultiplier { get; }

        public double TrimRatioPerSide { get; }

        public int TrimCountFloorPerSide { get; }

        public int MinimumRobustSampleCount { get; }

        public AutomaticViewportRobustRangeSettings(double outlierFenceMultiplier, double trimRatioPerSide, int trimCountFloorPerSide, int minimumRobustSampleCount)
        {
            OutlierFenceMultiplier = Math.Max(0.0, outlierFenceMultiplier);
            TrimRatioPerSide = Math.Max(0.0, trimRatioPerSide);
            TrimCountFloorPerSide = Math.Max(0, trimCountFloorPerSide);
            MinimumRobustSampleCount = Math.Max(2, minimumRobustSampleCount);
        }
    }

    internal struct AutomaticViewportDenseRangeSettings
    {
        public double CoverageRatio { get; }

        public int MinimumSampleCount { get; }

        public AutomaticViewportDenseRangeSettings(double coverageRatio, int minimumSampleCount)
        {
            CoverageRatio = Math.Max(0.0, Math.Min(1.0, coverageRatio));
            MinimumSampleCount = Math.Max(2, minimumSampleCount);
        }
    }

    private const double DefaultAutomaticViewportOutlierFenceMultiplier = 4.75;

    private const double DefaultAutomaticViewportTrimRatioPerSide = 0.0005;

    private const int DefaultAutomaticViewportTrimCountFloorPerSide = 1;

    private const int DefaultAutomaticViewportMinimumRobustSampleCount = 12;

    private const double TimingAutomaticViewportOutlierFenceMultiplier = 2.25;

    private const double TimingAutomaticViewportTrimRatioPerSide = 0.0075;

    private const int TimingAutomaticViewportTrimCountFloorPerSide = 24;

    private const int TimingAutomaticViewportMinimumRobustSampleCount = 12;

    private const double IntervalTimingAutomaticViewportDenseCoverageRatio = 0.95;

    private const int IntervalTimingAutomaticViewportDenseMinimumSampleCount = 24;

    internal static bool ShouldPreferNonZeroSingleAxisViewportRange(MousePerformancePlotType plotType)
    {
        if (plotType != MousePerformancePlotType.XCountVsTime && plotType != MousePerformancePlotType.YCountVsTime && plotType != MousePerformancePlotType.XVelocityVsTime)
        {
            return plotType == MousePerformancePlotType.YVelocityVsTime;
        }
        return true;
    }

    internal static AutomaticViewportRobustRangeSettings ResolveAutomaticViewportRobustRangeSettings(MousePerformancePlotType plotType, bool isHorizontalAxis)
    {
        return (isHorizontalAxis || plotType != MousePerformancePlotType.FrequencyVsTime)
            ? new AutomaticViewportRobustRangeSettings(DefaultAutomaticViewportOutlierFenceMultiplier, DefaultAutomaticViewportTrimRatioPerSide, DefaultAutomaticViewportTrimCountFloorPerSide, DefaultAutomaticViewportMinimumRobustSampleCount)
            : new AutomaticViewportRobustRangeSettings(TimingAutomaticViewportOutlierFenceMultiplier, TimingAutomaticViewportTrimRatioPerSide, TimingAutomaticViewportTrimCountFloorPerSide, TimingAutomaticViewportMinimumRobustSampleCount);
    }

    internal static AutomaticViewportDenseRangeSettings? ResolveAutomaticViewportDenseRangeSettings(MousePerformancePlotType plotType, bool isHorizontalAxis)
    {
        return (isHorizontalAxis || plotType != MousePerformancePlotType.IntervalVsTime)
            ? null
            : new AutomaticViewportDenseRangeSettings(IntervalTimingAutomaticViewportDenseCoverageRatio, IntervalTimingAutomaticViewportDenseMinimumSampleCount);
    }

    internal static double ResolveHorizontalAxisPaddingRatio(MousePerformancePlotType plotType)
    {
        if (plotType == MousePerformancePlotType.XVsY)
        {
            return 0.05;
        }
        if (ShouldPreferNonZeroSingleAxisViewportRange(plotType) || plotType == MousePerformancePlotType.IntervalVsTime || plotType == MousePerformancePlotType.FrequencyVsTime)
        {
            return 0.0025;
        }
        return 0.01;
    }

    internal static double ResolveVerticalAxisPaddingRatio(MousePerformancePlotType plotType)
    {
        if (plotType == MousePerformancePlotType.XVsY)
        {
            return 0.05;
        }
        if (ShouldPreferNonZeroSingleAxisViewportRange(plotType))
        {
            return 0.01;
        }
        if (plotType == MousePerformancePlotType.IntervalVsTime || plotType == MousePerformancePlotType.FrequencyVsTime)
        {
            return 0.02;
        }
        return 0.03;
    }

    internal static double ResolveSinglePointAxisPaddingRatio(MousePerformancePlotType plotType, bool isHorizontalAxis)
    {
        if (isHorizontalAxis)
        {
            return Math.Max(0.0025, ResolveHorizontalAxisPaddingRatio(plotType));
        }
        if (ShouldPreferNonZeroSingleAxisViewportRange(plotType))
        {
            return 0.015;
        }
        return Math.Max(0.02, ResolveVerticalAxisPaddingRatio(plotType));
    }

    internal static void ExpandAxisRange(ref double minimum, ref double maximum, double paddingRatio = 0.05, double singlePointPaddingRatio = 0.05)
    {
        if (minimum == double.MaxValue || maximum == double.MinValue)
        {
            minimum = -1.0;
            maximum = 1.0;
            return;
        }
        double rangePaddingRatio = Math.Max(0.0, paddingRatio);
        double singlePointPadding = Math.Max(rangePaddingRatio, Math.Max(0.0005, singlePointPaddingRatio));
        if (Math.Abs(maximum - minimum) < 1E-06)
        {
            double padding = Math.Max(0.0005, Math.Max(1.0, Math.Abs(maximum)) * singlePointPadding);
            minimum -= padding;
            maximum += padding;
        }
        else
        {
            double padding = (maximum - minimum) * rangePaddingRatio;
            minimum -= padding;
            maximum += padding;
        }
    }

    internal static void ExpandYAxisRange(MousePerformancePlotType plotType, ref double minimum, ref double maximum)
    {
        double paddingRatio = ResolveVerticalAxisPaddingRatio(plotType);
        double singlePointPaddingRatio = ResolveSinglePointAxisPaddingRatio(plotType, isHorizontalAxis: false);
        if (plotType == MousePerformancePlotType.XVsY || !ShouldClampSingleSidedYAxisToZero(plotType))
        {
            ExpandAxisRange(ref minimum, ref maximum, paddingRatio, singlePointPaddingRatio);
        }
        else if (minimum == double.MaxValue || maximum == double.MinValue)
        {
            minimum = -1.0;
            maximum = 1.0;
        }
        else if (minimum >= 0.0)
        {
            minimum = 0.0;
            double padding = Math.Max(0.0005, Math.Max(1.0, maximum - minimum) * Math.Max(0.0005, paddingRatio));
            maximum += padding;
        }
        else if (maximum <= 0.0)
        {
            maximum = 0.0;
            double padding = Math.Max(0.0005, Math.Max(1.0, maximum - minimum) * Math.Max(0.0005, paddingRatio));
            minimum -= padding;
        }
        else
        {
            ExpandAxisRange(ref minimum, ref maximum, paddingRatio, singlePointPaddingRatio);
        }
    }

    private static bool ShouldClampSingleSidedYAxisToZero(MousePerformancePlotType plotType)
    {
        if (!IsCountPlot(plotType))
        {
            return !IsVelocityPlot(plotType);
        }
        return false;
    }

    private static bool IsVelocityPlot(MousePerformancePlotType plotType)
    {
        return MousePerformancePlotTraits.IsVelocityPlot(plotType);
    }

    private static bool IsCountPlot(MousePerformancePlotType plotType)
    {
        return MousePerformancePlotTraits.IsCountPlot(plotType);
    }

    private static bool IsTimingPlot(MousePerformancePlotType plotType)
    {
        return MousePerformancePlotTraits.IsTimingPlot(plotType);
    }
}




