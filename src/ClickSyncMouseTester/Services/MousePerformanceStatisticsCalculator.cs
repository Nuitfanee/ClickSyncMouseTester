using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ClickSyncMouseTester.Services;

internal static class MousePerformanceStatisticsCalculator
{
    private const int CancellationCheckMask = 1023;

    public static MousePerformanceVelocityStatisticsSummary ComputeVelocityStatistics(MousePerformanceSnapshot snapshot, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, CancellationToken cancellationToken = default)
    {
        if (snapshot == null || snapshot.Status == MousePerformanceSessionStatus.Collecting || !snapshot.CanComputeVelocity || !snapshot.EffectiveCpi.HasValue || snapshot.EffectiveCpi.Value <= 0.0 || !MousePerformancePlotTraits.IsVelocityPlot(plotType))
        {
            return null;
        }
        MousePerformanceSessionAnalysisIndex index = new MousePerformanceSessionAnalysisIndex(snapshot.Events, cancellationToken);
        return CreateVelocityStatisticsSummary(ResolveVelocitySamples(index, plotType, startIndex, endIndex, snapshot.EffectiveCpi.Value, timeBasis, cancellationToken), cancellationToken);
    }

    public static MousePerformanceVelocityStatisticsSummary ComputeVelocityStatistics(MousePerformanceChartRenderFrame frame)
    {
        return CreateVelocityStatisticsSummary(ResolveVelocitySamples(frame));
    }

    public static MousePerformanceTimingStatisticsSummary ComputeTimingStatistics(MousePerformanceSnapshot snapshot, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, CancellationToken cancellationToken = default)
    {
        if (snapshot == null || snapshot.Status == MousePerformanceSessionStatus.Collecting || !MousePerformancePlotTraits.IsTimingPlot(plotType))
        {
            return null;
        }
        return CreateTimingStatisticsSummary(ResolveTimingStatisticsSamples(snapshot, plotType, startIndex, endIndex, timeBasis, null, cancellationToken), plotType != MousePerformancePlotType.FrequencyVsTime, cancellationToken);
    }

    public static MousePerformanceTimingStatisticsSummary ComputeTimingStatistics(MousePerformanceChartRenderFrame frame)
    {
        return CreateTimingStatisticsSummary(ResolveTimingSamples(frame), frame != null && frame.PlotType != MousePerformancePlotType.FrequencyVsTime);
    }

    public static MousePerformanceResidualDispersionSummary ComputeResidualDispersion(MousePerformanceSnapshot snapshot, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, CancellationToken cancellationToken = default)
    {
        if (snapshot == null || snapshot.Status == MousePerformanceSessionStatus.Collecting || !MousePerformancePlotTraits.IsCountPlot(plotType))
        {
            return null;
        }
        return CreateResidualDispersionSummary(ResolveCountResidualSamples(snapshot, plotType, startIndex, endIndex, timeBasis, null, cancellationToken), cancellationToken);
    }

    public static MousePerformanceResidualDispersionSummary ComputeResidualDispersion(MousePerformanceChartRenderFrame frame)
    {
        if (frame == null || !frame.IsAvailable || !MousePerformancePlotTraits.IsCountPlot(frame.PlotType))
        {
            return null;
        }
        return CreateResidualDispersionSummary(ResolveCountResidualSamples(frame));
    }

    public static IReadOnlyList<double> ResolveTimingStatisticsSamples(MousePerformanceSnapshot snapshot, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, MousePerformanceAnalysisOptions analysisOptions = null, CancellationToken cancellationToken = default)
    {
        List<double> statisticsSamples = new List<double>();
        if (snapshot == null || snapshot.Status == MousePerformanceSessionStatus.Collecting || !MousePerformancePlotTraits.IsTimingPlot(plotType))
        {
            return statisticsSamples;
        }
        IReadOnlyList<MousePerformanceEvent> events = snapshot.Events ?? Array.Empty<MousePerformanceEvent>();
        if (events.Count == 0)
        {
            return statisticsSamples;
        }
        MousePerformanceSessionAnalysisIndex index = new MousePerformanceSessionAnalysisIndex(events, cancellationToken);
        int clampedStartIndex = index.ClampStartIndex(startIndex);
        int clampedEndIndex = index.ClampEndIndex(clampedStartIndex, endIndex);
        IReadOnlyList<MousePerformanceChartPoint> sourcePoints = plotType == MousePerformancePlotType.IntervalVsTime
            ? MousePerformanceSeriesBuilder.BuildRawTimingSeriesPoints(index, clampedStartIndex, clampedEndIndex, timeBasis, MousePerformanceTimingSeriesMode.Interval, cancellationToken)
            : MousePerformanceSeriesBuilder.BuildTimingSeriesSamples(index, clampedStartIndex, clampedEndIndex, timeBasis, analysisOptions, MousePerformanceTimingSeriesMode.Frequency, cancellationToken).DisplayPoints;
        foreach (MousePerformanceChartPoint point in sourcePoints)
        {
            if (IsFinite(point.Y))
            {
                statisticsSamples.Add(Math.Max(0.0, point.Y));
            }
        }
        return statisticsSamples;
    }

    public static IReadOnlyList<double> ResolveCountResidualSamples(MousePerformanceSnapshot snapshot, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, MousePerformanceAnalysisOptions analysisOptions = null, CancellationToken cancellationToken = default)
    {
        List<double> residuals = new List<double>();
        IReadOnlyList<MousePerformanceEvent> events = snapshot?.Events ?? Array.Empty<MousePerformanceEvent>();
        if (snapshot == null || snapshot.Status == MousePerformanceSessionStatus.Collecting || events.Count == 0 || !MousePerformancePlotTraits.IsCountPlot(plotType))
        {
            return residuals;
        }

        MousePerformanceSessionAnalysisIndex index = new MousePerformanceSessionAnalysisIndex(events, cancellationToken);
        MousePerformanceAnalysisOptions resolvedOptions = analysisOptions ?? MousePerformanceAnalysisOptions.Default;
        int clampedStartIndex = index.ClampStartIndex(startIndex);
        int clampedEndIndex = index.ClampEndIndex(clampedStartIndex, endIndex);
        switch (plotType)
        {
            case MousePerformancePlotType.XCountVsTime:
                AppendSingleAxisCountResidualSamples(index, clampedStartIndex, clampedEndIndex, isXAxis: true, timeBasis, resolvedOptions, residuals, cancellationToken);
                break;
            case MousePerformancePlotType.YCountVsTime:
                AppendSingleAxisCountResidualSamples(index, clampedStartIndex, clampedEndIndex, isXAxis: false, timeBasis, resolvedOptions, residuals, cancellationToken);
                break;
            case MousePerformancePlotType.XYCountVsTime:
                AppendDualAxisCountResidualSamples(index, clampedStartIndex, clampedEndIndex, timeBasis, resolvedOptions, residuals, cancellationToken);
                break;
        }
        return residuals;
    }

    public static double? ResolvePercentileFromSortedValues(IReadOnlyList<double> sortedValues, double percentile)
    {
        return MousePerformanceDistributionCalculator.ResolvePercentileFromSortedValues(sortedValues, percentile);
    }

    private static MousePerformanceVelocityStatisticsSummary CreateVelocityStatisticsSummary(IReadOnlyList<double> samples, CancellationToken cancellationToken = default)
    {
        MousePerformanceDistributionStatisticsSummary statistics = MousePerformanceDistributionCalculator.Compute(samples, includeDispersionMetrics: true, cancellationToken);
        if (statistics == null)
        {
            return null;
        }
        return new MousePerformanceVelocityStatisticsSummary(statistics.SampleCount, statistics.AverageValue, statistics.StandardDeviationValue, statistics.P50Value, statistics.P95Value, statistics.P99Value, statistics.P999Value, statistics.MadValue, statistics.IqrValue);
    }

    private static MousePerformanceTimingStatisticsSummary CreateTimingStatisticsSummary(IReadOnlyList<double> samples, bool includeDispersionMetrics = true, CancellationToken cancellationToken = default)
    {
        MousePerformanceDistributionStatisticsSummary statistics = MousePerformanceDistributionCalculator.Compute(samples, includeDispersionMetrics, cancellationToken);
        if (statistics == null)
        {
            return null;
        }
        return new MousePerformanceTimingStatisticsSummary(statistics.SampleCount, statistics.AverageValue, statistics.StandardDeviationValue, statistics.P50Value, statistics.P95Value, statistics.P99Value, statistics.P999Value, statistics.MadValue, statistics.IqrValue);
    }

    private static MousePerformanceResidualDispersionSummary CreateResidualDispersionSummary(IReadOnlyList<double> residualSamples, CancellationToken cancellationToken = default)
    {
        MousePerformanceDistributionStatisticsSummary statistics = MousePerformanceDistributionCalculator.Compute(residualSamples, includeDispersionMetrics: true, cancellationToken);
        if (statistics == null)
        {
            return null;
        }
        return new MousePerformanceResidualDispersionSummary(statistics.MadValue, statistics.IqrValue);
    }

    private static List<double> ResolveVelocitySamples(MousePerformanceSessionAnalysisIndex index, MousePerformancePlotType plotType, int startIndex, int endIndex, double cpi, MousePerformanceTimeBasis timeBasis, CancellationToken cancellationToken = default)
    {
        List<double> velocitySamples = new List<double>();
        if (index == null || index.Count <= 1 || cpi <= 0.0 || !MousePerformancePlotTraits.IsVelocityPlot(plotType))
        {
            return velocitySamples;
        }
        int clampedStartIndex = index.ClampStartIndex(startIndex);
        int clampedEndIndex = index.ClampEndIndex(clampedStartIndex, endIndex);
        if (plotType == MousePerformancePlotType.PathSpeedVsTime)
        {
            velocitySamples.AddRange(MousePerformanceScalarSampleExtractor.Extract(index, MousePerformanceScalarSampleKind.PathSpeed, clampedStartIndex, clampedEndIndex, cpi, timeBasis, MousePerformanceAnalysisOptions.Default, cancellationToken));
            return velocitySamples;
        }
        if (plotType == MousePerformancePlotType.XYVelocityVsTime)
        {
            List<MousePerformanceChartPoint> primaryTrend = new List<MousePerformanceChartPoint>();
            List<MousePerformanceChartPoint> secondaryTrend = new List<MousePerformanceChartPoint>();
            MousePerformanceSeriesBuilder.BuildDualAxisVelocityTrend(index, clampedStartIndex, clampedEndIndex, cpi, MousePerformanceAnalysisOptions.Default, timeBasis, primaryTrend, secondaryTrend, cancellationToken);
            int sampleCount = Math.Min(primaryTrend.Count, secondaryTrend.Count);
            velocitySamples.Capacity = Math.Max(velocitySamples.Capacity, sampleCount);
            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                MousePerformanceChartPoint primaryPoint = primaryTrend[sampleIndex];
                MousePerformanceChartPoint secondaryPoint = secondaryTrend[sampleIndex];
                if (IsFinite(primaryPoint.Y) && IsFinite(secondaryPoint.Y))
                {
                    double combinedVelocity = Math.Sqrt(primaryPoint.Y * primaryPoint.Y + secondaryPoint.Y * secondaryPoint.Y);
                    if (IsFinite(combinedVelocity))
                    {
                        velocitySamples.Add(combinedVelocity);
                    }
                }
            }
            return velocitySamples;
        }

        IReadOnlyList<MousePerformanceChartPoint> trendPoints = MousePerformanceSeriesBuilder.BuildVelocityTrend(index, clampedStartIndex, clampedEndIndex, cpi, plotType, MousePerformanceAnalysisOptions.Default, timeBasis, cancellationToken);
        velocitySamples.Capacity = Math.Max(velocitySamples.Capacity, trendPoints.Count);
        foreach (MousePerformanceChartPoint point in trendPoints)
        {
            if (IsFinite(point.Y))
            {
                velocitySamples.Add(Math.Abs(point.Y));
            }
        }
        return velocitySamples;
    }

    private static List<double> ResolveVelocitySamples(MousePerformanceChartRenderFrame frame)
    {
        List<double> velocitySamples = new List<double>();
        if (frame == null || !frame.IsAvailable || frame.Series == null || !MousePerformancePlotTraits.IsVelocityPlot(frame.PlotType))
        {
            return velocitySamples;
        }
        MousePerformanceChartSeries primaryVelocitySeries = ResolveSeries(frame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Primary)
            ?? ResolveSeries(frame, MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Primary);
        if (primaryVelocitySeries == null)
        {
            return velocitySamples;
        }
        switch (frame.PlotType)
        {
            case MousePerformancePlotType.XVelocityVsTime:
            case MousePerformancePlotType.YVelocityVsTime:
            case MousePerformancePlotType.PathSpeedVsTime:
                foreach (MousePerformanceChartPoint point in primaryVelocitySeries.Points)
                {
                    if (IsFinite(point.Y))
                    {
                        velocitySamples.Add(Math.Abs(point.Y));
                    }
                }
                break;
            case MousePerformancePlotType.XYVelocityVsTime:
                {
                    MousePerformanceChartSeries secondaryVelocitySeries = ResolveSeries(frame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Secondary)
                        ?? ResolveSeries(frame, MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Secondary);
                    if (secondaryVelocitySeries == null)
                    {
                        return velocitySamples;
                    }
                    int sampleCount = Math.Min(primaryVelocitySeries.Points.Count, secondaryVelocitySeries.Points.Count);
                    for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
                    {
                        MousePerformanceChartPoint primaryPoint = primaryVelocitySeries.Points[sampleIndex];
                        MousePerformanceChartPoint secondaryPoint = secondaryVelocitySeries.Points[sampleIndex];
                        if (IsFinite(primaryPoint.Y) && IsFinite(secondaryPoint.Y))
                        {
                            double combinedVelocity = Math.Sqrt(primaryPoint.Y * primaryPoint.Y + secondaryPoint.Y * secondaryPoint.Y);
                            if (IsFinite(combinedVelocity))
                            {
                                velocitySamples.Add(combinedVelocity);
                            }
                        }
                    }
                    break;
                }
        }
        return velocitySamples;
    }

    private static List<double> ResolveTimingSamples(MousePerformanceChartRenderFrame frame)
    {
        List<double> timingSamples = new List<double>();
        if (frame == null || !frame.IsAvailable || frame.Series == null || !MousePerformancePlotTraits.IsTimingPlot(frame.PlotType))
        {
            return timingSamples;
        }
        MousePerformanceChartSeries primaryTimingSeries = frame.PlotType == MousePerformancePlotType.FrequencyVsTime
            ? ResolveSeries(frame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Primary)
            : ResolveSeries(frame, MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Primary);
        if (primaryTimingSeries == null)
        {
            return timingSamples;
        }
        foreach (MousePerformanceChartPoint point in primaryTimingSeries.Points)
        {
            if (IsFinite(point.Y))
            {
                timingSamples.Add(Math.Max(0.0, point.Y));
            }
        }
        return timingSamples;
    }

    private static List<double> ResolveCountResidualSamples(MousePerformanceChartRenderFrame frame)
    {
        List<double> residualSamples = new List<double>();
        if (frame == null || frame.Series == null || !MousePerformancePlotTraits.IsCountPlot(frame.PlotType))
        {
            return residualSamples;
        }

        MousePerformanceChartSeries rawPrimary = ResolveSeries(frame, MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Primary);
        MousePerformanceChartSeries trendPrimary = ResolveSeries(frame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Primary);
        if (rawPrimary == null || trendPrimary == null)
        {
            return residualSamples;
        }

        if (frame.PlotType == MousePerformancePlotType.XYCountVsTime)
        {
            MousePerformanceChartSeries rawSecondary = ResolveSeries(frame, MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Secondary);
            MousePerformanceChartSeries trendSecondary = ResolveSeries(frame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Secondary);
            if (rawSecondary == null || trendSecondary == null)
            {
                return residualSamples;
            }

            int sampleCount = Math.Min(Math.Min(rawPrimary.Points.Count, rawSecondary.Points.Count), Math.Min(trendPrimary.Points.Count, trendSecondary.Points.Count));
            residualSamples.Capacity = Math.Max(residualSamples.Capacity, sampleCount);
            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                MousePerformanceChartPoint rawPrimaryPoint = rawPrimary.Points[sampleIndex];
                MousePerformanceChartPoint rawSecondaryPoint = rawSecondary.Points[sampleIndex];
                MousePerformanceChartPoint trendPrimaryPoint = trendPrimary.Points[sampleIndex];
                MousePerformanceChartPoint trendSecondaryPoint = trendSecondary.Points[sampleIndex];
                if (IsFinite(rawPrimaryPoint.Y) && IsFinite(rawSecondaryPoint.Y) && IsFinite(trendPrimaryPoint.Y) && IsFinite(trendSecondaryPoint.Y))
                {
                    double xResidual = rawPrimaryPoint.Y - trendPrimaryPoint.Y;
                    double yResidual = rawSecondaryPoint.Y - trendSecondaryPoint.Y;
                    residualSamples.Add(Math.Sqrt(xResidual * xResidual + yResidual * yResidual));
                }
            }
            return residualSamples;
        }

        int singleAxisSampleCount = Math.Min(rawPrimary.Points.Count, trendPrimary.Points.Count);
        residualSamples.Capacity = Math.Max(residualSamples.Capacity, singleAxisSampleCount);
        for (int sampleIndex = 0; sampleIndex < singleAxisSampleCount; sampleIndex++)
        {
            MousePerformanceChartPoint rawPoint = rawPrimary.Points[sampleIndex];
            MousePerformanceChartPoint trendPoint = trendPrimary.Points[sampleIndex];
            if (IsFinite(rawPoint.Y) && IsFinite(trendPoint.Y))
            {
                residualSamples.Add(Math.Abs(rawPoint.Y - trendPoint.Y));
            }
        }
        return residualSamples;
    }

    private static void AppendSingleAxisCountResidualSamples(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, bool isXAxis, MousePerformanceTimeBasis timeBasis, MousePerformanceAnalysisOptions analysisOptions, ICollection<double> residuals, CancellationToken cancellationToken = default)
    {
        if (index == null || residuals == null || startIndex > endIndex)
        {
            return;
        }
        List<MousePerformanceChartPoint> rawPoints = new List<MousePerformanceChartPoint>(Math.Max(0, endIndex - startIndex + 1));
        for (int eventIndex = startIndex; eventIndex <= endIndex; eventIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, eventIndex - startIndex);
            rawPoints.Add(new MousePerformanceChartPoint(index.GetTimeMs(eventIndex, timeBasis), isXAxis ? index.GetDeltaX(eventIndex) : index.GetDeltaY(eventIndex)));
        }
        AppendSingleAxisResidualSamples(rawPoints, MousePerformanceSeriesBuilder.BuildMovingAverageTrend(rawPoints, analysisOptions, cancellationToken), residuals);
    }

    private static void AppendDualAxisCountResidualSamples(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, MousePerformanceAnalysisOptions analysisOptions, ICollection<double> residuals, CancellationToken cancellationToken = default)
    {
        if (index == null || residuals == null || startIndex > endIndex)
        {
            return;
        }
        int capacity = Math.Max(0, endIndex - startIndex + 1);
        List<MousePerformanceChartPoint> xAxisPoints = new List<MousePerformanceChartPoint>(capacity);
        List<MousePerformanceChartPoint> yAxisPoints = new List<MousePerformanceChartPoint>(capacity);
        for (int eventIndex = startIndex; eventIndex <= endIndex; eventIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, eventIndex - startIndex);
            double eventTimeMs = index.GetTimeMs(eventIndex, timeBasis);
            xAxisPoints.Add(new MousePerformanceChartPoint(eventTimeMs, index.GetDeltaX(eventIndex)));
            yAxisPoints.Add(new MousePerformanceChartPoint(eventTimeMs, index.GetDeltaY(eventIndex)));
        }
        AppendDualAxisResidualSamples(xAxisPoints, yAxisPoints, MousePerformanceSeriesBuilder.BuildMovingAverageTrend(xAxisPoints, analysisOptions, cancellationToken), MousePerformanceSeriesBuilder.BuildMovingAverageTrend(yAxisPoints, analysisOptions, cancellationToken), residuals);
    }

    private static void AppendSingleAxisResidualSamples(IReadOnlyList<MousePerformanceChartPoint> rawPoints, IReadOnlyList<MousePerformanceChartPoint> trendPoints, ICollection<double> residuals)
    {
        if (rawPoints == null || trendPoints == null || residuals == null)
        {
            return;
        }
        int sampleCount = Math.Min(rawPoints.Count, trendPoints.Count);
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            MousePerformanceChartPoint rawPoint = rawPoints[sampleIndex];
            MousePerformanceChartPoint trendPoint = trendPoints[sampleIndex];
            if (IsFinite(rawPoint.Y) && IsFinite(trendPoint.Y))
            {
                residuals.Add(Math.Abs(rawPoint.Y - trendPoint.Y));
            }
        }
    }

    private static void AppendDualAxisResidualSamples(IReadOnlyList<MousePerformanceChartPoint> rawPrimary, IReadOnlyList<MousePerformanceChartPoint> rawSecondary, IReadOnlyList<MousePerformanceChartPoint> trendPrimary, IReadOnlyList<MousePerformanceChartPoint> trendSecondary, ICollection<double> residuals)
    {
        if (rawPrimary == null || rawSecondary == null || trendPrimary == null || trendSecondary == null || residuals == null)
        {
            return;
        }
        int sampleCount = Math.Min(Math.Min(rawPrimary.Count, rawSecondary.Count), Math.Min(trendPrimary.Count, trendSecondary.Count));
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            MousePerformanceChartPoint rawPrimaryPoint = rawPrimary[sampleIndex];
            MousePerformanceChartPoint rawSecondaryPoint = rawSecondary[sampleIndex];
            MousePerformanceChartPoint trendPrimaryPoint = trendPrimary[sampleIndex];
            MousePerformanceChartPoint trendSecondaryPoint = trendSecondary[sampleIndex];
            if (IsFinite(rawPrimaryPoint.Y) && IsFinite(rawSecondaryPoint.Y) && IsFinite(trendPrimaryPoint.Y) && IsFinite(trendSecondaryPoint.Y))
            {
                double xResidual = rawPrimaryPoint.Y - trendPrimaryPoint.Y;
                double yResidual = rawSecondaryPoint.Y - trendSecondaryPoint.Y;
                residuals.Add(Math.Sqrt(xResidual * xResidual + yResidual * yResidual));
            }
        }
    }

    private static MousePerformanceChartSeries ResolveSeries(MousePerformanceChartRenderFrame frame, MousePerformanceChartSeriesKind kind, MousePerformanceChartSeriesPalette palette)
    {
        if (frame?.Series == null)
        {
            return null;
        }
        return frame.Series.FirstOrDefault(series => series != null && series.Kind == kind && series.Palette == palette && series.Points != null && series.Points.Count > 0);
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static void ThrowIfCancellationRequested(CancellationToken cancellationToken, int iteration)
    {
        if (cancellationToken.CanBeCanceled && (Math.Max(0, iteration) & CancellationCheckMask) == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
