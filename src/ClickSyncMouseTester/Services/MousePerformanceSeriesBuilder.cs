using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ClickSyncMouseTester.Services;

internal enum MousePerformanceTimingSeriesMode
{
    Interval,
    Frequency
}

internal sealed class MousePerformanceTimingSeriesSamples
{
    public IReadOnlyList<MousePerformanceChartPoint> RawPoints { get; }

    public IReadOnlyList<MousePerformanceChartPoint> WindowPoints { get; }

    public IReadOnlyList<MousePerformanceChartPoint> DisplayPoints { get; }

    public MousePerformanceTimingSeriesSamples(IReadOnlyList<MousePerformanceChartPoint> rawPoints, IReadOnlyList<MousePerformanceChartPoint> windowPoints, IReadOnlyList<MousePerformanceChartPoint> displayPoints)
    {
        RawPoints = rawPoints ?? Array.Empty<MousePerformanceChartPoint>();
        WindowPoints = windowPoints ?? Array.Empty<MousePerformanceChartPoint>();
        DisplayPoints = displayPoints ?? Array.Empty<MousePerformanceChartPoint>();
    }
}

internal static class MousePerformanceSeriesBuilder
{
    private const int CancellationCheckMask = 1023;

    public static void BuildSingleAxisCountSeries(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, bool isXAxis, MousePerformanceTimeBasis timeBasis, ICollection<MousePerformanceChartPoint> scatter, ICollection<MousePerformanceChartPoint> rawLine, ICollection<MousePerformanceChartPoint> stems, ref double xMin, ref double xMax, ref double yMin, ref double yMax, CancellationToken cancellationToken = default)
    {
        EnsureChartPointCapacity(scatter, Math.Max(0, endIndex - startIndex + 1));
        EnsureChartPointCapacity(rawLine, Math.Max(0, endIndex - startIndex + 1));
        EnsureChartPointCapacity(stems, Math.Max(0, endIndex - startIndex + 1));
        for (int eventIndex = startIndex; eventIndex <= endIndex; eventIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, eventIndex - startIndex);
            int axisDeltaCounts = isXAxis ? index.GetDeltaX(eventIndex) : index.GetDeltaY(eventIndex);
            AppendPoint(scatter, rawLine, stems, new MousePerformanceChartPoint(index.GetTimeMs(eventIndex, timeBasis), axisDeltaCounts), ref xMin, ref xMax, ref yMin, ref yMax);
        }
    }

    public static void BuildDualAxisCountSeries(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, ICollection<MousePerformanceChartPoint> scatterPrimary, ICollection<MousePerformanceChartPoint> scatterSecondary, ICollection<MousePerformanceChartPoint> rawLinePrimary, ICollection<MousePerformanceChartPoint> rawLineSecondary, ICollection<MousePerformanceChartPoint> stemsPrimary, ICollection<MousePerformanceChartPoint> stemsSecondary, ref double xMin, ref double xMax, ref double yMin, ref double yMax, CancellationToken cancellationToken = default)
    {
        int capacity = Math.Max(0, endIndex - startIndex + 1);
        EnsureChartPointCapacity(scatterPrimary, capacity);
        EnsureChartPointCapacity(scatterSecondary, capacity);
        EnsureChartPointCapacity(rawLinePrimary, capacity);
        EnsureChartPointCapacity(rawLineSecondary, capacity);
        EnsureChartPointCapacity(stemsPrimary, capacity);
        EnsureChartPointCapacity(stemsSecondary, capacity);
        for (int eventIndex = startIndex; eventIndex <= endIndex; eventIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, eventIndex - startIndex);
            double eventTimeMs = index.GetTimeMs(eventIndex, timeBasis);
            AppendPoint(scatterPrimary, rawLinePrimary, stemsPrimary, new MousePerformanceChartPoint(eventTimeMs, index.GetDeltaX(eventIndex)), ref xMin, ref xMax, ref yMin, ref yMax);
            AppendPoint(scatterSecondary, rawLineSecondary, stemsSecondary, new MousePerformanceChartPoint(eventTimeMs, index.GetDeltaY(eventIndex)), ref xMin, ref xMax, ref yMin, ref yMax);
        }
    }

    public static void BuildSingleAxisSumSeries(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, bool isXAxis, MousePerformanceTimeBasis timeBasis, ICollection<MousePerformanceChartPoint> scatter, ICollection<MousePerformanceChartPoint> rawLine, ICollection<MousePerformanceChartPoint> stems, ref double xMin, ref double xMax, ref double yMin, ref double yMax, CancellationToken cancellationToken = default)
    {
        int capacity = Math.Max(0, endIndex - startIndex + 1);
        EnsureChartPointCapacity(scatter, capacity);
        EnsureChartPointCapacity(rawLine, capacity);
        EnsureChartPointCapacity(stems, capacity);
        for (int eventIndex = startIndex; eventIndex <= endIndex; eventIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, eventIndex - startIndex);
            long cumulativeAxisCounts = isXAxis ? index.GetCumulativeX(eventIndex) : index.GetCumulativeY(eventIndex);
            AppendPoint(scatter, rawLine, stems, new MousePerformanceChartPoint(index.GetTimeMs(eventIndex, timeBasis), cumulativeAxisCounts), ref xMin, ref xMax, ref yMin, ref yMax);
        }
    }

    public static void BuildDualAxisSumSeries(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, ICollection<MousePerformanceChartPoint> scatterPrimary, ICollection<MousePerformanceChartPoint> scatterSecondary, ICollection<MousePerformanceChartPoint> rawLinePrimary, ICollection<MousePerformanceChartPoint> rawLineSecondary, ICollection<MousePerformanceChartPoint> stemsPrimary, ICollection<MousePerformanceChartPoint> stemsSecondary, ref double xMin, ref double xMax, ref double yMin, ref double yMax, CancellationToken cancellationToken = default)
    {
        int capacity = Math.Max(0, endIndex - startIndex + 1);
        EnsureChartPointCapacity(scatterPrimary, capacity);
        EnsureChartPointCapacity(scatterSecondary, capacity);
        EnsureChartPointCapacity(rawLinePrimary, capacity);
        EnsureChartPointCapacity(rawLineSecondary, capacity);
        EnsureChartPointCapacity(stemsPrimary, capacity);
        EnsureChartPointCapacity(stemsSecondary, capacity);
        for (int eventIndex = startIndex; eventIndex <= endIndex; eventIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, eventIndex - startIndex);
            double eventTimeMs = index.GetTimeMs(eventIndex, timeBasis);
            AppendPoint(scatterPrimary, rawLinePrimary, stemsPrimary, new MousePerformanceChartPoint(eventTimeMs, index.GetCumulativeX(eventIndex)), ref xMin, ref xMax, ref yMin, ref yMax);
            AppendPoint(scatterSecondary, rawLineSecondary, stemsSecondary, new MousePerformanceChartPoint(eventTimeMs, index.GetCumulativeY(eventIndex)), ref xMin, ref xMax, ref yMin, ref yMax);
        }
    }

    public static void BuildTrajectorySeries(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, ICollection<MousePerformanceChartPoint> scatter, ICollection<MousePerformanceChartPoint> rawLine, ref double xMin, ref double xMax, ref double yMin, ref double yMax, CancellationToken cancellationToken = default)
    {
        int capacity = Math.Max(0, endIndex - startIndex + 1);
        EnsureChartPointCapacity(scatter, capacity);
        EnsureChartPointCapacity(rawLine, capacity);
        long originXCounts = startIndex > 0 ? index.GetCumulativeX(startIndex - 1) : 0L;
        long originYCounts = startIndex > 0 ? index.GetCumulativeY(startIndex - 1) : 0L;
        for (int eventIndex = startIndex; eventIndex <= endIndex; eventIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, eventIndex - startIndex);
            MousePerformanceChartPoint trajectoryPoint = new MousePerformanceChartPoint(index.GetCumulativeX(eventIndex) - originXCounts, index.GetCumulativeY(eventIndex) - originYCounts);
            scatter?.Add(trajectoryPoint);
            rawLine?.Add(trajectoryPoint);
            UpdateRange(trajectoryPoint.X, trajectoryPoint.Y, ref xMin, ref xMax, ref yMin, ref yMax);
        }
    }

    public static MousePerformanceTimingSeriesSamples BuildTimingSeriesSamples(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, MousePerformanceAnalysisOptions analysisOptions, MousePerformanceTimingSeriesMode mode, CancellationToken cancellationToken = default)
    {
        TimingIntervalSeries intervalSeries = BuildTimingIntervalSeries(index, startIndex, endIndex, timeBasis, mode, cancellationToken);
        List<MousePerformanceChartPoint> rawPoints = intervalSeries.RawPoints;
        List<MousePerformanceChartPoint> windowPoints = new List<MousePerformanceChartPoint>(rawPoints.Count);
        List<MousePerformanceChartPoint> displayPoints = new List<MousePerformanceChartPoint>(rawPoints.Count);
        if (index == null || startIndex > endIndex)
        {
            return new MousePerformanceTimingSeriesSamples(rawPoints, windowPoints, displayPoints);
        }
        if (rawPoints.Count == 0)
        {
            return new MousePerformanceTimingSeriesSamples(rawPoints, windowPoints, displayPoints);
        }

        MousePerformanceAnalysisOptions resolvedOptions = analysisOptions ?? MousePerformanceAnalysisOptions.Default;
        double windowDurationMs = Math.Max(resolvedOptions.TimingSeriesRecommendedWindowMs, resolvedOptions.TrendWindowMs);
        int minimumSamples = Math.Max(resolvedOptions.TimingSeriesRecommendedMinimumSamples, resolvedOptions.MinimumTrendSamples);
        double halfWindowMs = windowDurationMs / 2.0;
        int leftCursor = 0;
        int rightCursor = -1;
        bool hasSmoothedValue = false;
        double smoothedValue = 0.0;
        for (int sampleIndex = 0; sampleIndex < rawPoints.Count; sampleIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, sampleIndex);
            double sampleTimeMs = rawPoints[sampleIndex].X;
            while (leftCursor < rawPoints.Count && sampleTimeMs - rawPoints[leftCursor].X > halfWindowMs)
            {
                leftCursor++;
            }
            while (rightCursor + 1 < rawPoints.Count && rawPoints[rightCursor + 1].X - sampleTimeMs <= halfWindowMs)
            {
                rightCursor++;
            }
            int windowStartIndex = leftCursor;
            int windowEndIndex = rightCursor;
            ExpandSampleWindow(rawPoints, sampleIndex, minimumSamples, ref windowStartIndex, ref windowEndIndex);
            if (windowEndIndex - windowStartIndex + 1 <= 0)
            {
                continue;
            }
            double windowValue = ResolveWindowTimingSeriesValue(mode, intervalSeries.IntervalMilliseconds, intervalSeries.IntervalPrefixSums, windowStartIndex, windowEndIndex, resolvedOptions.TimingSeriesTrimRatio);
            if (windowValue <= 0.0)
            {
                continue;
            }
            windowPoints.Add(new MousePerformanceChartPoint(sampleTimeMs, windowValue));
            if (!hasSmoothedValue)
            {
                smoothedValue = windowValue;
                hasSmoothedValue = true;
            }
            else
            {
                double previousSampleTimeMs = rawPoints[Math.Max(0, sampleIndex - 1)].X;
                double alpha = ResolveAdaptiveEmaAlpha(Math.Max(0.0, sampleTimeMs - previousSampleTimeMs), resolvedOptions.TimingSeriesEmaTimeConstantMs);
                smoothedValue += alpha * (windowValue - smoothedValue);
            }
            displayPoints.Add(new MousePerformanceChartPoint(sampleTimeMs, smoothedValue));
        }
        return new MousePerformanceTimingSeriesSamples(rawPoints, windowPoints, displayPoints);
    }

    public static IReadOnlyList<MousePerformanceChartPoint> BuildRawTimingSeriesPoints(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, MousePerformanceTimingSeriesMode mode, CancellationToken cancellationToken = default)
    {
        return BuildTimingIntervalSeries(index, startIndex, endIndex, timeBasis, mode, cancellationToken).RawPoints;
    }

    public static IReadOnlyList<MousePerformanceChartPoint> BuildMovingAverageTrend(IReadOnlyList<MousePerformanceChartPoint> points, MousePerformanceAnalysisOptions analysisOptions, CancellationToken cancellationToken = default)
    {
        List<MousePerformanceChartPoint> trendPoints = new List<MousePerformanceChartPoint>(points?.Count ?? 0);
        if (points == null || points.Count <= 1)
        {
            return trendPoints;
        }
        MousePerformanceAnalysisOptions options = analysisOptions ?? MousePerformanceAnalysisOptions.Default;
        double[] prefixSums = new double[points.Count + 1];
        for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, pointIndex);
            prefixSums[pointIndex + 1] = prefixSums[pointIndex] + points[pointIndex].Y;
        }
        double halfWindowMs = options.TrendWindowMs / 2.0;
        int leftCursor = 0;
        int rightCursor = -1;
        for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, pointIndex);
            double pointTimeMs = points[pointIndex].X;
            while (leftCursor < points.Count && pointTimeMs - points[leftCursor].X > halfWindowMs)
            {
                leftCursor++;
            }
            while (rightCursor + 1 < points.Count && points[rightCursor + 1].X - pointTimeMs <= halfWindowMs)
            {
                rightCursor++;
            }
            int windowStartIndex = leftCursor;
            int windowEndIndex = rightCursor;
            ExpandSampleWindow(points, pointIndex, options.MinimumTrendSamples, ref windowStartIndex, ref windowEndIndex);
            int windowSampleCount = windowEndIndex - windowStartIndex + 1;
            if (windowSampleCount > 0)
            {
                double averageValue = (prefixSums[windowEndIndex + 1] - prefixSums[windowStartIndex]) / windowSampleCount;
                trendPoints.Add(new MousePerformanceChartPoint(pointTimeMs, averageValue));
            }
        }
        return trendPoints;
    }

    public static IReadOnlyList<MousePerformanceChartPoint> BuildVelocityTrend(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, double? cpi, MousePerformancePlotType plotType, MousePerformanceAnalysisOptions analysisOptions, MousePerformanceTimeBasis timeBasis, CancellationToken cancellationToken = default)
    {
        MousePerformanceVelocityTrendComponent component = plotType switch
        {
            MousePerformancePlotType.XVelocityVsTime => MousePerformanceVelocityTrendComponent.XAxis,
            MousePerformancePlotType.YVelocityVsTime => MousePerformanceVelocityTrendComponent.YAxis,
            _ => MousePerformanceVelocityTrendComponent.XAxis
        };
        return MousePerformanceVelocityTrendEstimator.Estimate(index, startIndex, endIndex, cpi, component, analysisOptions, timeBasis, cancellationToken);
    }

    public static void BuildDualAxisVelocityTrend(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, double? cpi, MousePerformanceAnalysisOptions analysisOptions, MousePerformanceTimeBasis timeBasis, ICollection<MousePerformanceChartPoint> primaryTrend, ICollection<MousePerformanceChartPoint> secondaryTrend, CancellationToken cancellationToken = default)
    {
        if (index == null || index.Count <= 1 || !cpi.HasValue || cpi.Value <= 0.0 || primaryTrend == null || secondaryTrend == null)
        {
            return;
        }
        int capacity = Math.Max(0, endIndex - Math.Max(1, startIndex) + 1);
        EnsureChartPointCapacity(primaryTrend, capacity);
        EnsureChartPointCapacity(secondaryTrend, capacity);
        MousePerformanceVelocityTrendPair trendPair = MousePerformanceVelocityTrendEstimator.EstimateDualAxis(index, startIndex, endIndex, cpi, analysisOptions, timeBasis, cancellationToken);
        AddRange(primaryTrend, trendPair.Primary);
        AddRange(secondaryTrend, trendPair.Secondary);
    }

    public static IReadOnlyList<MousePerformanceChartPoint> BuildPathSpeedTrend(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, double? cpi, MousePerformanceAnalysisOptions analysisOptions, MousePerformanceTimeBasis timeBasis, CancellationToken cancellationToken = default)
    {
        return MousePerformanceVelocityTrendEstimator.Estimate(index, startIndex, endIndex, cpi, MousePerformanceVelocityTrendComponent.PathSpeed, analysisOptions, timeBasis, cancellationToken);
    }

    public static void UpdateRangeFromHistogramBins(IEnumerable<MousePerformanceHistogramBin> bins, ref double xMin, ref double xMax, ref double yMin, ref double yMax)
    {
        UpdateRangeFromHistogramBins(bins, 0.0, 1.0, ref xMin, ref xMax, ref yMin, ref yMax);
    }

    public static void UpdateRangeFromHistogramBins(IEnumerable<MousePerformanceHistogramBin> bins, double xOffset, double groupScale, ref double xMin, ref double xMax, ref double yMin, ref double yMax)
    {
        if (bins == null)
        {
            return;
        }
        double sanitizedGroupScale = double.IsNaN(groupScale) || double.IsInfinity(groupScale) || groupScale <= 0.0 ? 1.0 : Math.Min(1.0, groupScale);
        foreach (MousePerformanceHistogramBin bin in bins)
        {
            ResolveHistogramBinXRange(bin, xOffset, sanitizedGroupScale, out double minimumX, out double maximumX);
            UpdateRange(minimumX, 0.0, ref xMin, ref xMax, ref yMin, ref yMax);
            UpdateRange(maximumX, bin.Value, ref xMin, ref xMax, ref yMin, ref yMax);
        }
    }

    public static void ResolveHistogramBinXRange(MousePerformanceHistogramBin bin, double xOffset, double groupScale, out double minimumX, out double maximumX)
    {
        double binWidth = Math.Max(0.0, bin.MaximumX - bin.MinimumX);
        double sanitizedGroupScale = double.IsNaN(groupScale) || double.IsInfinity(groupScale) || groupScale <= 0.0 ? 1.0 : Math.Min(1.0, groupScale);
        double scaledWidth = binWidth * sanitizedGroupScale;
        double centerX = bin.CenterX + xOffset;
        minimumX = centerX - scaledWidth / 2.0;
        maximumX = centerX + scaledWidth / 2.0;
    }

    public static double ResolveTimingSeriesValue(MousePerformanceTimingSeriesMode mode, double totalIntervalMs, int sampleCount)
    {
        if (totalIntervalMs <= 0.0 || sampleCount <= 0)
        {
            return 0.0;
        }
        return mode == MousePerformanceTimingSeriesMode.Interval ? totalIntervalMs / sampleCount : 1000.0 * sampleCount / totalIntervalMs;
    }

    public static void AppendSeriesPoints(IEnumerable<MousePerformanceChartPoint> points, ICollection<MousePerformanceChartPoint> scatter, ICollection<MousePerformanceChartPoint> rawLine, ICollection<MousePerformanceChartPoint> stems, ref double xMin, ref double xMax, ref double yMin, ref double yMax)
    {
        if (points == null)
        {
            return;
        }
        foreach (MousePerformanceChartPoint point in points)
        {
            AppendPoint(scatter, rawLine, stems, point, ref xMin, ref xMax, ref yMin, ref yMax);
        }
    }

    public static void AppendTimingSeriesPoints(IEnumerable<MousePerformanceChartPoint> scatterPoints, IEnumerable<MousePerformanceChartPoint> connectedPoints, ICollection<MousePerformanceChartPoint> scatter, ICollection<MousePerformanceChartPoint> rawLine, ICollection<MousePerformanceChartPoint> stems, ref double xMin, ref double xMax, ref double yMin, ref double yMax)
    {
        if (scatterPoints == null && connectedPoints == null)
        {
            return;
        }
        AppendScatterPoints(scatterPoints, scatter);
        UpdateRangeFromPoints(scatterPoints, ref xMin, ref xMax, ref yMin, ref yMax);
        if (connectedPoints != null && (rawLine != null || stems != null))
        {
            AppendLineAndStemPoints(connectedPoints, rawLine, stems, ref xMin, ref xMax, ref yMin, ref yMax, updateBounds: false);
        }
    }

    public static MousePerformanceChartPoint[] ToArrayOrEmpty(IReadOnlyList<MousePerformanceChartPoint> points)
    {
        if (points == null || points.Count == 0)
        {
            return Array.Empty<MousePerformanceChartPoint>();
        }
        if (points is MousePerformanceChartPoint[] array)
        {
            return array;
        }
        MousePerformanceChartPoint[] result = new MousePerformanceChartPoint[points.Count];
        for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
        {
            result[pointIndex] = points[pointIndex];
        }
        return result;
    }

    public static MousePerformanceChartPoint[] ResolveSharedSeriesPoints(IReadOnlyList<MousePerformanceChartPoint> points, IReadOnlyList<MousePerformanceChartPoint> sharedSource, MousePerformanceChartPoint[] sharedArray)
    {
        if (points == null || points.Count == 0)
        {
            return Array.Empty<MousePerformanceChartPoint>();
        }
        if (sharedSource != null && sharedArray != null && points.Count == sharedSource.Count && points.Count == sharedArray.Length)
        {
            bool canShare = true;
            for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
            {
                if (!points[pointIndex].Equals(sharedSource[pointIndex]))
                {
                    canShare = false;
                    break;
                }
            }
            if (canShare)
            {
                return sharedArray;
            }
        }
        return ToArrayOrEmpty(points);
    }

    public static void UpdateRange(double x, double y, ref double xMin, ref double xMax, ref double yMin, ref double yMax)
    {
        if (x < xMin)
        {
            xMin = x;
        }
        if (x > xMax)
        {
            xMax = x;
        }
        if (y < yMin)
        {
            yMin = y;
        }
        if (y > yMax)
        {
            yMax = y;
        }
    }

    private static TimingIntervalSeries BuildTimingIntervalSeries(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, MousePerformanceTimingSeriesMode mode, CancellationToken cancellationToken)
    {
        int estimatedIntervalCount = Math.Max(0, endIndex - Math.Max(1, startIndex) + 1);
        List<MousePerformanceChartPoint> rawPoints = new List<MousePerformanceChartPoint>(estimatedIntervalCount);
        List<double> intervalMilliseconds = new List<double>(estimatedIntervalCount);
        List<double> intervalPrefixSums = new List<double>(estimatedIntervalCount + 1) { 0.0 };
        if (index == null || startIndex > endIndex)
        {
            return new TimingIntervalSeries(rawPoints, intervalMilliseconds, intervalPrefixSums);
        }

        int firstIntervalEventIndex = Math.Max(1, startIndex);
        for (int eventIndex = firstIntervalEventIndex; eventIndex <= endIndex; eventIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, eventIndex - firstIntervalEventIndex);
            double intervalMs = 0.0;
            if (index.TryGetIntervalMs(eventIndex - 1, eventIndex, timeBasis, ref intervalMs))
            {
                double eventTimeMs = index.GetTimeMs(eventIndex, timeBasis);
                rawPoints.Add(new MousePerformanceChartPoint(eventTimeMs, ResolveTimingSeriesValue(mode, intervalMs, 1)));
                intervalMilliseconds.Add(intervalMs);
                intervalPrefixSums.Add(intervalPrefixSums[intervalPrefixSums.Count - 1] + intervalMs);
            }
        }
        return new TimingIntervalSeries(rawPoints, intervalMilliseconds, intervalPrefixSums);
    }

    private static double ResolveWindowTimingSeriesValue(MousePerformanceTimingSeriesMode mode, IReadOnlyList<double> intervals, IReadOnlyList<double> intervalPrefixSums, int startIndex, int endIndex, double trimRatio)
    {
        if (intervals == null || startIndex > endIndex)
        {
            return 0.0;
        }
        return mode == MousePerformanceTimingSeriesMode.Interval ? ResolveTrimmedMean(intervals, startIndex, endIndex, trimRatio) : ResolveWindowReportRate(intervalPrefixSums, startIndex, endIndex);
    }

    private static double ResolveWindowReportRate(IReadOnlyList<double> intervalPrefixSums, int startIndex, int endIndex)
    {
        if (intervalPrefixSums == null || startIndex < 0 || endIndex < startIndex || endIndex + 1 >= intervalPrefixSums.Count)
        {
            return 0.0;
        }
        int sampleCount = endIndex - startIndex + 1;
        double totalIntervalMs = intervalPrefixSums[endIndex + 1] - intervalPrefixSums[startIndex];
        return ResolveTimingSeriesValue(MousePerformanceTimingSeriesMode.Frequency, totalIntervalMs, sampleCount);
    }

    private static double ResolveTrimmedMean(IReadOnlyList<double> values, int startIndex, int endIndex, double trimRatio)
    {
        if (values == null || startIndex > endIndex)
        {
            return 0.0;
        }
        int sampleCount = endIndex - startIndex + 1;
        if (sampleCount <= 0)
        {
            return 0.0;
        }
        double[] sortedSamples = new double[sampleCount];
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            sortedSamples[sampleIndex] = values[startIndex + sampleIndex];
        }
        Array.Sort(sortedSamples);
        double normalizedTrimRatio = double.IsNaN(trimRatio) || double.IsInfinity(trimRatio) ? MousePerformanceAnalysisOptions.Default.TimingSeriesTrimRatio : Math.Max(0.0, Math.Min(0.49, trimRatio));
        int trimCount = Math.Min((sampleCount - 1) / 2, (int)Math.Round(Math.Round(sampleCount * normalizedTrimRatio, MidpointRounding.AwayFromZero)));
        int trimmedStartIndex = trimCount;
        int trimmedEndIndex = sampleCount - trimCount - 1;
        if (trimmedStartIndex > trimmedEndIndex)
        {
            trimmedStartIndex = 0;
            trimmedEndIndex = sampleCount - 1;
        }
        double sum = 0.0;
        int includedSampleCount = 0;
        for (int sampleIndex = trimmedStartIndex; sampleIndex <= trimmedEndIndex; sampleIndex++)
        {
            sum += sortedSamples[sampleIndex];
            includedSampleCount++;
        }
        return includedSampleCount > 0 ? sum / includedSampleCount : 0.0;
    }

    private static double ResolveAdaptiveEmaAlpha(double deltaTimeMs, double timeConstantMs)
    {
        if (deltaTimeMs <= 0.0 || timeConstantMs <= 0.0)
        {
            return 1.0;
        }
        double alpha = 1.0 - Math.Exp(-deltaTimeMs / timeConstantMs);
        return Math.Max(0.0, Math.Min(1.0, alpha));
    }

    private static void ExpandSampleWindow(IReadOnlyList<MousePerformanceChartPoint> points, int centerIndex, int minimumSamples, ref int left, ref int right)
    {
        if (points == null || points.Count == 0)
        {
            return;
        }
        double centerTimeMs = points[centerIndex].X;
        while (right - left + 1 < minimumSamples && (left > 0 || right < points.Count - 1))
        {
            if (left <= 0)
            {
                right++;
                continue;
            }
            if (right >= points.Count - 1)
            {
                left--;
                continue;
            }
            double nextLeftDistanceMs = Math.Abs(centerTimeMs - points[left - 1].X);
            double nextRightDistanceMs = Math.Abs(points[right + 1].X - centerTimeMs);
            if (nextLeftDistanceMs <= nextRightDistanceMs)
            {
                left--;
            }
            else
            {
                right++;
            }
        }
    }

    private static void AppendScatterPoints(IEnumerable<MousePerformanceChartPoint> points, ICollection<MousePerformanceChartPoint> scatter)
    {
        if (points == null || scatter == null)
        {
            return;
        }
        foreach (MousePerformanceChartPoint point in points)
        {
            scatter.Add(point);
        }
    }

    private static void AppendLineAndStemPoints(IEnumerable<MousePerformanceChartPoint> points, ICollection<MousePerformanceChartPoint> rawLine, ICollection<MousePerformanceChartPoint> stems, ref double xMin, ref double xMax, ref double yMin, ref double yMax, bool updateBounds)
    {
        foreach (MousePerformanceChartPoint point in points)
        {
            rawLine?.Add(point);
            stems?.Add(point);
            if (updateBounds)
            {
                UpdateRange(point.X, point.Y, ref xMin, ref xMax, ref yMin, ref yMax);
            }
        }
    }

    public static void UpdateRangeFromPoints(IEnumerable<MousePerformanceChartPoint> points, ref double xMin, ref double xMax, ref double yMin, ref double yMax)
    {
        if (points == null)
        {
            return;
        }
        foreach (MousePerformanceChartPoint point in points)
        {
            UpdateRange(point.X, point.Y, ref xMin, ref xMax, ref yMin, ref yMax);
        }
    }

    private static void AppendPoint(ICollection<MousePerformanceChartPoint> scatter, ICollection<MousePerformanceChartPoint> line, ICollection<MousePerformanceChartPoint> stem, MousePerformanceChartPoint point, ref double xMin, ref double xMax, ref double yMin, ref double yMax)
    {
        scatter?.Add(point);
        line?.Add(point);
        stem?.Add(point);
        UpdateRange(point.X, point.Y, ref xMin, ref xMax, ref yMin, ref yMax);
    }

    private static void EnsureChartPointCapacity(ICollection<MousePerformanceChartPoint> points, int capacity)
    {
        if (points is List<MousePerformanceChartPoint> list && capacity > list.Capacity)
        {
            list.Capacity = capacity;
        }
    }

    private static void AddRange(ICollection<MousePerformanceChartPoint> target, IReadOnlyList<MousePerformanceChartPoint> source)
    {
        if (target == null || source == null || source.Count == 0)
        {
            return;
        }
        for (int pointIndex = 0; pointIndex < source.Count; pointIndex++)
        {
            target.Add(source[pointIndex]);
        }
    }

    private static void ThrowIfCancellationRequested(CancellationToken cancellationToken, int iteration)
    {
        if (cancellationToken.CanBeCanceled && (Math.Max(0, iteration) & CancellationCheckMask) == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class TimingIntervalSeries
    {
        public List<MousePerformanceChartPoint> RawPoints { get; }

        public IReadOnlyList<double> IntervalMilliseconds { get; }

        public IReadOnlyList<double> IntervalPrefixSums { get; }

        public TimingIntervalSeries(List<MousePerformanceChartPoint> rawPoints, IReadOnlyList<double> intervalMilliseconds, IReadOnlyList<double> intervalPrefixSums)
        {
            RawPoints = rawPoints ?? new List<MousePerformanceChartPoint>();
            IntervalMilliseconds = intervalMilliseconds ?? Array.Empty<double>();
            IntervalPrefixSums = intervalPrefixSums ?? Array.Empty<double>();
        }
    }
}
