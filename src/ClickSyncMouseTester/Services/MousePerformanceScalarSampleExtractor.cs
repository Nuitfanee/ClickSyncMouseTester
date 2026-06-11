using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ClickSyncMouseTester.Services;

internal static class MousePerformanceScalarSampleExtractor
{
    private const int CancellationCheckMask = 1023;

    public static IReadOnlyList<double> Extract(MousePerformanceSnapshot snapshot, MousePerformanceScalarSampleKind sampleKind, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, MousePerformanceAnalysisOptions analysisOptions = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MousePerformanceEvent> events = snapshot?.Events ?? Array.Empty<MousePerformanceEvent>();
        if (events.Count == 0 || snapshot.Status == MousePerformanceSessionStatus.Collecting)
        {
            return Array.Empty<double>();
        }

        MousePerformanceSessionAnalysisIndex index = new MousePerformanceSessionAnalysisIndex(events, cancellationToken);
        int clampedStartIndex = index.ClampStartIndex(startIndex);
        int clampedEndIndex = index.ClampEndIndex(clampedStartIndex, endIndex);
        return Extract(index, sampleKind, clampedStartIndex, clampedEndIndex, snapshot.EffectiveCpi, timeBasis, analysisOptions, cancellationToken);
    }

    public static IReadOnlyList<double> Extract(MousePerformanceSessionAnalysisIndex index, MousePerformanceScalarSampleKind sampleKind, int startIndex, int endIndex, double? cpi, MousePerformanceTimeBasis timeBasis, MousePerformanceAnalysisOptions analysisOptions = null, CancellationToken cancellationToken = default)
    {
        if (index == null || index.Count == 0 || startIndex > endIndex)
        {
            return Array.Empty<double>();
        }

        int clampedStartIndex = index.ClampStartIndex(startIndex);
        int clampedEndIndex = index.ClampEndIndex(clampedStartIndex, endIndex);
        switch (sampleKind)
        {
            case MousePerformanceScalarSampleKind.ReportInterval:
                return ExtractReportIntervals(index, clampedStartIndex, clampedEndIndex, timeBasis, cancellationToken);
            case MousePerformanceScalarSampleKind.FrequencyEstimate:
                return ExtractFrequencyEstimates(index, clampedStartIndex, clampedEndIndex, timeBasis, analysisOptions, cancellationToken);
            case MousePerformanceScalarSampleKind.XVelocity:
                return ExtractVelocityTrendValues(index, clampedStartIndex, clampedEndIndex, cpi, MousePerformancePlotType.XVelocityVsTime, timeBasis, analysisOptions, cancellationToken);
            case MousePerformanceScalarSampleKind.YVelocity:
                return ExtractVelocityTrendValues(index, clampedStartIndex, clampedEndIndex, cpi, MousePerformancePlotType.YVelocityVsTime, timeBasis, analysisOptions, cancellationToken);
            case MousePerformanceScalarSampleKind.XYVelocityMagnitude:
                return ExtractXYVelocityMagnitudeValues(index, clampedStartIndex, clampedEndIndex, cpi, timeBasis, analysisOptions, cancellationToken);
            case MousePerformanceScalarSampleKind.PathSpeed:
                return ExtractPathSpeedValues(index, clampedStartIndex, clampedEndIndex, cpi, timeBasis, analysisOptions, cancellationToken);
            case MousePerformanceScalarSampleKind.DeltaX:
                return ExtractDeltaValues(index, clampedStartIndex, clampedEndIndex, isXAxis: true, cancellationToken);
            case MousePerformanceScalarSampleKind.DeltaY:
                return ExtractDeltaValues(index, clampedStartIndex, clampedEndIndex, isXAxis: false, cancellationToken);
            case MousePerformanceScalarSampleKind.DeltaMagnitude:
                return ExtractDeltaMagnitudeValues(index, clampedStartIndex, clampedEndIndex, cancellationToken);
            default:
                return Array.Empty<double>();
        }
    }

    private static IReadOnlyList<double> ExtractReportIntervals(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, CancellationToken cancellationToken)
    {
        List<double> samples = new List<double>(Math.Max(0, endIndex - Math.Max(1, startIndex) + 1));
        int firstIntervalEventIndex = Math.Max(1, startIndex);
        for (int eventIndex = firstIntervalEventIndex; eventIndex <= endIndex; eventIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, eventIndex - firstIntervalEventIndex);
            double intervalMs = 0.0;
            if (index.TryGetIntervalMs(eventIndex - 1, eventIndex, timeBasis, ref intervalMs))
            {
                samples.Add(intervalMs);
            }
        }
        return samples.Count == 0 ? Array.Empty<double>() : samples.ToArray();
    }

    private static IReadOnlyList<double> ExtractFrequencyEstimates(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, MousePerformanceAnalysisOptions analysisOptions, CancellationToken cancellationToken)
    {
        MousePerformanceTimingSeriesSamples timingSamples = MousePerformanceSeriesBuilder.BuildTimingSeriesSamples(index, startIndex, endIndex, timeBasis, analysisOptions, MousePerformanceTimingSeriesMode.Frequency, cancellationToken);
        return ExtractNonNegativePointValues(timingSamples.DisplayPoints, cancellationToken);
    }

    private static IReadOnlyList<double> ExtractVelocityTrendValues(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, double? cpi, MousePerformancePlotType plotType, MousePerformanceTimeBasis timeBasis, MousePerformanceAnalysisOptions analysisOptions, CancellationToken cancellationToken)
    {
        IReadOnlyList<MousePerformanceChartPoint> trendPoints = MousePerformanceSeriesBuilder.BuildVelocityTrend(index, startIndex, endIndex, cpi, plotType, analysisOptions, timeBasis, cancellationToken);
        return ExtractAbsolutePointValues(trendPoints, cancellationToken);
    }

    private static IReadOnlyList<double> ExtractXYVelocityMagnitudeValues(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, double? cpi, MousePerformanceTimeBasis timeBasis, MousePerformanceAnalysisOptions analysisOptions, CancellationToken cancellationToken)
    {
        List<MousePerformanceChartPoint> primaryTrend = new List<MousePerformanceChartPoint>();
        List<MousePerformanceChartPoint> secondaryTrend = new List<MousePerformanceChartPoint>();
        MousePerformanceSeriesBuilder.BuildDualAxisVelocityTrend(index, startIndex, endIndex, cpi, analysisOptions, timeBasis, primaryTrend, secondaryTrend, cancellationToken);
        int sampleCount = Math.Min(primaryTrend.Count, secondaryTrend.Count);
        List<double> samples = new List<double>(sampleCount);
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, sampleIndex);
            double xVelocity = primaryTrend[sampleIndex].Y;
            double yVelocity = secondaryTrend[sampleIndex].Y;
            if (IsFinite(xVelocity) && IsFinite(yVelocity))
            {
                samples.Add(Math.Sqrt(xVelocity * xVelocity + yVelocity * yVelocity));
            }
        }
        return samples.Count == 0 ? Array.Empty<double>() : samples.ToArray();
    }

    private static IReadOnlyList<double> ExtractPathSpeedValues(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, double? cpi, MousePerformanceTimeBasis timeBasis, MousePerformanceAnalysisOptions analysisOptions, CancellationToken cancellationToken)
    {
        IReadOnlyList<MousePerformanceChartPoint> trendPoints = MousePerformanceSeriesBuilder.BuildPathSpeedTrend(index, startIndex, endIndex, cpi, analysisOptions, timeBasis, cancellationToken);
        return ExtractNonNegativePointValues(trendPoints, cancellationToken);
    }

    private static IReadOnlyList<double> ExtractDeltaValues(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, bool isXAxis, CancellationToken cancellationToken)
    {
        List<double> samples = new List<double>(Math.Max(0, endIndex - startIndex + 1));
        for (int eventIndex = startIndex; eventIndex <= endIndex; eventIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, eventIndex - startIndex);
            samples.Add(isXAxis ? index.GetDeltaX(eventIndex) : index.GetDeltaY(eventIndex));
        }
        return samples.Count == 0 ? Array.Empty<double>() : samples.ToArray();
    }

    private static IReadOnlyList<double> ExtractDeltaMagnitudeValues(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, CancellationToken cancellationToken)
    {
        List<double> samples = new List<double>(Math.Max(0, endIndex - startIndex + 1));
        for (int eventIndex = startIndex; eventIndex <= endIndex; eventIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, eventIndex - startIndex);
            int deltaX = index.GetDeltaX(eventIndex);
            int deltaY = index.GetDeltaY(eventIndex);
            samples.Add(Math.Sqrt((double)deltaX * deltaX + (double)deltaY * deltaY));
        }
        return samples.Count == 0 ? Array.Empty<double>() : samples.ToArray();
    }

    private static IReadOnlyList<double> ExtractAbsolutePointValues(IReadOnlyList<MousePerformanceChartPoint> points, CancellationToken cancellationToken)
    {
        if (points == null || points.Count == 0)
        {
            return Array.Empty<double>();
        }
        List<double> samples = new List<double>(points.Count);
        for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, pointIndex);
            double value = points[pointIndex].Y;
            if (IsFinite(value))
            {
                samples.Add(Math.Abs(value));
            }
        }
        return samples.Count == 0 ? Array.Empty<double>() : samples.ToArray();
    }

    private static IReadOnlyList<double> ExtractNonNegativePointValues(IReadOnlyList<MousePerformanceChartPoint> points, CancellationToken cancellationToken)
    {
        if (points == null || points.Count == 0)
        {
            return Array.Empty<double>();
        }
        List<double> samples = new List<double>(points.Count);
        for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, pointIndex);
            double value = points[pointIndex].Y;
            if (IsFinite(value))
            {
                samples.Add(Math.Max(0.0, value));
            }
        }
        return samples.Count == 0 ? Array.Empty<double>() : samples.ToArray();
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
