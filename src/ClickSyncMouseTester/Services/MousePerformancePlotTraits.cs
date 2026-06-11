using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Services;

internal static class MousePerformancePlotTraits
{
    private static readonly MousePerformancePlotType[] PlotDisplayOrder =
    {
        MousePerformancePlotType.XCountVsTime,
        MousePerformancePlotType.YCountVsTime,
        MousePerformancePlotType.XYCountVsTime,
        MousePerformancePlotType.IntervalVsTime,
        MousePerformancePlotType.FrequencyVsTime,
        MousePerformancePlotType.XVelocityVsTime,
        MousePerformancePlotType.YVelocityVsTime,
        MousePerformancePlotType.XYVelocityVsTime,
        MousePerformancePlotType.PathSpeedVsTime,
        MousePerformancePlotType.XSumVsTime,
        MousePerformancePlotType.YSumVsTime,
        MousePerformancePlotType.XYSumVsTime,
        MousePerformancePlotType.XVsY,
        MousePerformancePlotType.IntervalHistogram,
        MousePerformancePlotType.DeltaXHistogram,
        MousePerformancePlotType.DeltaYHistogram,
        MousePerformancePlotType.DeltaMagnitudeHistogram
    };

    public static IReadOnlyList<MousePerformancePlotType> ResolvePlotDisplayOrder()
    {
        return PlotDisplayOrder;
    }

    public static bool IsCountPlot(MousePerformancePlotType plotType)
    {
        return plotType == MousePerformancePlotType.XCountVsTime
            || plotType == MousePerformancePlotType.YCountVsTime
            || plotType == MousePerformancePlotType.XYCountVsTime;
    }

    public static bool IsVelocityPlot(MousePerformancePlotType plotType)
    {
        return plotType == MousePerformancePlotType.XVelocityVsTime
            || plotType == MousePerformancePlotType.YVelocityVsTime
            || plotType == MousePerformancePlotType.XYVelocityVsTime
            || plotType == MousePerformancePlotType.PathSpeedVsTime;
    }

    public static bool IsSumPlot(MousePerformancePlotType plotType)
    {
        return plotType == MousePerformancePlotType.XSumVsTime
            || plotType == MousePerformancePlotType.YSumVsTime
            || plotType == MousePerformancePlotType.XYSumVsTime;
    }

    public static bool IsTimingPlot(MousePerformancePlotType plotType)
    {
        return plotType == MousePerformancePlotType.IntervalVsTime
            || plotType == MousePerformancePlotType.FrequencyVsTime;
    }

    public static bool IsHistogramPlot(MousePerformancePlotType plotType)
    {
        return plotType == MousePerformancePlotType.IntervalHistogram
            || plotType == MousePerformancePlotType.DeltaXHistogram
            || plotType == MousePerformancePlotType.DeltaYHistogram
            || plotType == MousePerformancePlotType.DeltaMagnitudeHistogram;
    }

    public static bool IsDeltaHistogramPlot(MousePerformancePlotType plotType)
    {
        return plotType == MousePerformancePlotType.DeltaXHistogram
            || plotType == MousePerformancePlotType.DeltaYHistogram
            || plotType == MousePerformancePlotType.DeltaMagnitudeHistogram;
    }

    public static MousePerformanceScalarSampleKind ResolveHistogramSampleKind(MousePerformancePlotType plotType)
    {
        return plotType switch
        {
            MousePerformancePlotType.IntervalHistogram => MousePerformanceScalarSampleKind.ReportInterval,
            MousePerformancePlotType.DeltaXHistogram => MousePerformanceScalarSampleKind.DeltaX,
            MousePerformancePlotType.DeltaYHistogram => MousePerformanceScalarSampleKind.DeltaY,
            MousePerformancePlotType.DeltaMagnitudeHistogram => MousePerformanceScalarSampleKind.DeltaMagnitude,
            _ => throw new InvalidOperationException($"Plot type {plotType} is not a histogram plot.")
        };
    }

    public static MousePerformanceSampleBasis ResolveHistogramSampleBasis(MousePerformancePlotType plotType)
    {
        return plotType switch
        {
            MousePerformancePlotType.IntervalHistogram => MousePerformanceSampleBasis.ReportTiming,
            MousePerformancePlotType.DeltaXHistogram or MousePerformancePlotType.DeltaYHistogram or MousePerformancePlotType.DeltaMagnitudeHistogram => MousePerformanceSampleBasis.RawReport,
            _ => throw new InvalidOperationException($"Plot type {plotType} is not a histogram plot.")
        };
    }
}
