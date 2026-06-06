using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ClickSyncMouseTester.Services;

internal enum MousePerformanceVelocityTrendComponent
{
    XAxis,
    YAxis,
    PathSpeed
}

internal readonly struct MousePerformanceVelocityTrendPair
{
    public IReadOnlyList<MousePerformanceChartPoint> Primary { get; }

    public IReadOnlyList<MousePerformanceChartPoint> Secondary { get; }

    public MousePerformanceVelocityTrendPair(IReadOnlyList<MousePerformanceChartPoint> primary, IReadOnlyList<MousePerformanceChartPoint> secondary)
    {
        Primary = primary ?? Array.Empty<MousePerformanceChartPoint>();
        Secondary = secondary ?? Array.Empty<MousePerformanceChartPoint>();
    }
}

internal static class MousePerformanceVelocityTrendEstimator
{
    private const int CancellationCheckMask = 1023;

    public static IReadOnlyList<MousePerformanceChartPoint> Estimate(
        MousePerformanceSessionAnalysisIndex index,
        int startIndex,
        int endIndex,
        double? cpi,
        MousePerformanceVelocityTrendComponent component,
        MousePerformanceAnalysisOptions analysisOptions,
        MousePerformanceTimeBasis timeBasis,
        CancellationToken cancellationToken = default)
    {
        List<MousePerformanceChartPoint> rawEstimates = new List<MousePerformanceChartPoint>(ResolveEstimatedCapacity(startIndex, endIndex));
        if (!CanEstimate(index, startIndex, endIndex, cpi))
        {
            return rawEstimates;
        }

        MousePerformanceAnalysisOptions options = analysisOptions ?? MousePerformanceAnalysisOptions.Default;
        double countsToMillimeters = 25.4 / cpi.Value;
        foreach (VelocitySegment segment in EnumerateVelocitySegments(index, startIndex, endIndex, timeBasis))
        {
            List<MousePerformanceChartPoint> segmentRawEstimates = new List<MousePerformanceChartPoint>(segment.Length);
            AppendSegmentRawEstimates(index, segment.StartIndex, segment.EndIndex, countsToMillimeters, component, options, timeBasis, segmentRawEstimates, cancellationToken);
            AddRange(rawEstimates, SmoothSegment(segmentRawEstimates, options, nonNegative: component == MousePerformanceVelocityTrendComponent.PathSpeed, cancellationToken));
        }
        return rawEstimates;
    }

    public static MousePerformanceVelocityTrendPair EstimateDualAxis(
        MousePerformanceSessionAnalysisIndex index,
        int startIndex,
        int endIndex,
        double? cpi,
        MousePerformanceAnalysisOptions analysisOptions,
        MousePerformanceTimeBasis timeBasis,
        CancellationToken cancellationToken = default)
    {
        int capacity = ResolveEstimatedCapacity(startIndex, endIndex);
        List<MousePerformanceChartPoint> rawPrimary = new List<MousePerformanceChartPoint>(capacity);
        List<MousePerformanceChartPoint> rawSecondary = new List<MousePerformanceChartPoint>(capacity);
        if (!CanEstimate(index, startIndex, endIndex, cpi))
        {
            return new MousePerformanceVelocityTrendPair(rawPrimary, rawSecondary);
        }

        MousePerformanceAnalysisOptions options = analysisOptions ?? MousePerformanceAnalysisOptions.Default;
        double countsToMillimeters = 25.4 / cpi.Value;
        foreach (VelocitySegment segment in EnumerateVelocitySegments(index, startIndex, endIndex, timeBasis))
        {
            List<MousePerformanceChartPoint> segmentRawPrimary = new List<MousePerformanceChartPoint>(segment.Length);
            List<MousePerformanceChartPoint> segmentRawSecondary = new List<MousePerformanceChartPoint>(segment.Length);
            AppendSegmentDualAxisRawEstimates(index, segment.StartIndex, segment.EndIndex, countsToMillimeters, options, timeBasis, segmentRawPrimary, segmentRawSecondary, cancellationToken);
            AddRange(rawPrimary, SmoothSegment(segmentRawPrimary, options, nonNegative: false, cancellationToken));
            AddRange(rawSecondary, SmoothSegment(segmentRawSecondary, options, nonNegative: false, cancellationToken));
        }
        return new MousePerformanceVelocityTrendPair(rawPrimary, rawSecondary);
    }

    private static void AppendSegmentRawEstimates(
        MousePerformanceSessionAnalysisIndex index,
        int startIndex,
        int endIndex,
        double countsToMillimeters,
        MousePerformanceVelocityTrendComponent component,
        MousePerformanceAnalysisOptions options,
        MousePerformanceTimeBasis timeBasis,
        ICollection<MousePerformanceChartPoint> target,
        CancellationToken cancellationToken)
    {
        double halfWindowMs = options.TrendWindowMs / 2.0;
        int minimumSamples = options.MinimumTrendSamples;
        int leftCursor = startIndex;
        int rightCursor = startIndex - 1;

        for (int eventIndex = startIndex; eventIndex <= endIndex; eventIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, eventIndex - startIndex);
            double eventTimeMs = index.GetTimeMs(eventIndex, timeBasis);
            MoveWindowCursors(index, startIndex, endIndex, eventTimeMs, halfWindowMs, timeBasis, ref leftCursor, ref rightCursor);

            int left = leftCursor;
            int right = rightCursor;
            ExpandEventWindow(index, startIndex, endIndex, eventIndex, minimumSamples, ref left, ref right, timeBasis);
            if (!TryResolveWindowInterval(index, left, right, timeBasis, out double intervalMs))
            {
                continue;
            }

            double velocity = ResolveVelocity(index, left, right, intervalMs, countsToMillimeters, component);
            if (IsFinite(velocity))
            {
                target.Add(new MousePerformanceChartPoint(eventTimeMs, velocity));
            }
        }
    }

    private static void AppendSegmentDualAxisRawEstimates(
        MousePerformanceSessionAnalysisIndex index,
        int startIndex,
        int endIndex,
        double countsToMillimeters,
        MousePerformanceAnalysisOptions options,
        MousePerformanceTimeBasis timeBasis,
        ICollection<MousePerformanceChartPoint> primary,
        ICollection<MousePerformanceChartPoint> secondary,
        CancellationToken cancellationToken)
    {
        double halfWindowMs = options.TrendWindowMs / 2.0;
        int minimumSamples = options.MinimumTrendSamples;
        int leftCursor = startIndex;
        int rightCursor = startIndex - 1;

        for (int eventIndex = startIndex; eventIndex <= endIndex; eventIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, eventIndex - startIndex);
            double eventTimeMs = index.GetTimeMs(eventIndex, timeBasis);
            MoveWindowCursors(index, startIndex, endIndex, eventTimeMs, halfWindowMs, timeBasis, ref leftCursor, ref rightCursor);

            int left = leftCursor;
            int right = rightCursor;
            ExpandEventWindow(index, startIndex, endIndex, eventIndex, minimumSamples, ref left, ref right, timeBasis);
            if (!TryResolveWindowInterval(index, left, right, timeBasis, out double intervalMs))
            {
                continue;
            }

            double xVelocity = ResolveVelocity(index, left, right, intervalMs, countsToMillimeters, MousePerformanceVelocityTrendComponent.XAxis);
            double yVelocity = ResolveVelocity(index, left, right, intervalMs, countsToMillimeters, MousePerformanceVelocityTrendComponent.YAxis);
            if (IsFinite(xVelocity) && IsFinite(yVelocity))
            {
                primary.Add(new MousePerformanceChartPoint(eventTimeMs, xVelocity));
                secondary.Add(new MousePerformanceChartPoint(eventTimeMs, yVelocity));
            }
        }
    }

    private static IReadOnlyList<MousePerformanceChartPoint> SmoothSegment(
        IReadOnlyList<MousePerformanceChartPoint> rawEstimates,
        MousePerformanceAnalysisOptions options,
        bool nonNegative,
        CancellationToken cancellationToken)
    {
        if (rawEstimates == null || rawEstimates.Count == 0)
        {
            return Array.Empty<MousePerformanceChartPoint>();
        }
        if (rawEstimates.Count == 1)
        {
            MousePerformanceChartPoint onlyPoint = rawEstimates[0];
            double onlyValue = nonNegative ? Math.Max(0.0, onlyPoint.Y) : onlyPoint.Y;
            return new[] { new MousePerformanceChartPoint(onlyPoint.X, onlyValue) };
        }

        double timeConstantMs = ResolveVelocitySmoothingTimeConstantMs(options);
        double[] filteredValues = FilterIsolatedOutliers(rawEstimates, nonNegative, cancellationToken);
        double[] forward = new double[rawEstimates.Count];
        double[] backward = new double[rawEstimates.Count];

        forward[0] = filteredValues[0];
        for (int pointIndex = 1; pointIndex < rawEstimates.Count; pointIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, pointIndex);
            double deltaTimeMs = Math.Max(0.0, rawEstimates[pointIndex].X - rawEstimates[pointIndex - 1].X);
            double alpha = ResolveAdaptiveEmaAlpha(deltaTimeMs, timeConstantMs);
            forward[pointIndex] = forward[pointIndex - 1] + alpha * (filteredValues[pointIndex] - forward[pointIndex - 1]);
        }

        int lastIndex = rawEstimates.Count - 1;
        backward[lastIndex] = filteredValues[lastIndex];
        for (int pointIndex = lastIndex - 1; pointIndex >= 0; pointIndex--)
        {
            ThrowIfCancellationRequested(cancellationToken, lastIndex - pointIndex);
            double deltaTimeMs = Math.Max(0.0, rawEstimates[pointIndex + 1].X - rawEstimates[pointIndex].X);
            double alpha = ResolveAdaptiveEmaAlpha(deltaTimeMs, timeConstantMs);
            backward[pointIndex] = backward[pointIndex + 1] + alpha * (filteredValues[pointIndex] - backward[pointIndex + 1]);
        }

        MousePerformanceChartPoint[] smoothed = new MousePerformanceChartPoint[rawEstimates.Count];
        for (int pointIndex = 0; pointIndex < rawEstimates.Count; pointIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, pointIndex);
            double value = (forward[pointIndex] + backward[pointIndex]) / 2.0;
            if (nonNegative)
            {
                value = Math.Max(0.0, value);
            }
            smoothed[pointIndex] = new MousePerformanceChartPoint(rawEstimates[pointIndex].X, value);
        }
        return smoothed;
    }

    private static double[] FilterIsolatedOutliers(IReadOnlyList<MousePerformanceChartPoint> rawEstimates, bool nonNegative, CancellationToken cancellationToken)
    {
        double[] filteredValues = new double[rawEstimates.Count];
        for (int pointIndex = 0; pointIndex < rawEstimates.Count; pointIndex++)
        {
            filteredValues[pointIndex] = CoerceTrendValue(rawEstimates[pointIndex].Y, nonNegative);
        }
        if (rawEstimates.Count < 5)
        {
            return filteredValues;
        }

        double[] windowValues = new double[5];
        double[] deviations = new double[5];
        for (int pointIndex = 0; pointIndex < rawEstimates.Count; pointIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, pointIndex);
            int windowStartIndex = Math.Max(0, pointIndex - 2);
            int windowEndIndex = Math.Min(rawEstimates.Count - 1, pointIndex + 2);
            int windowCount = windowEndIndex - windowStartIndex + 1;
            for (int windowIndex = 0; windowIndex < windowCount; windowIndex++)
            {
                windowValues[windowIndex] = filteredValues[windowStartIndex + windowIndex];
            }

            Array.Sort(windowValues, 0, windowCount);
            double median = ResolveMedian(windowValues, windowCount);
            for (int windowIndex = 0; windowIndex < windowCount; windowIndex++)
            {
                deviations[windowIndex] = Math.Abs(filteredValues[windowStartIndex + windowIndex] - median);
            }

            Array.Sort(deviations, 0, windowCount);
            double scaledMad = ResolveMedian(deviations, windowCount) * 1.4826;
            double residual = filteredValues[pointIndex] - median;
            double deviationLimit = Math.Max(Math.Max(scaledMad * 6.0, Math.Abs(median) * 4.0), 4.0);
            if (Math.Abs(residual) > deviationLimit)
            {
                filteredValues[pointIndex] = CoerceTrendValue(median + Math.Sign(residual) * deviationLimit, nonNegative);
            }
        }
        return filteredValues;
    }

    private static void MoveWindowCursors(
        MousePerformanceSessionAnalysisIndex index,
        int startIndex,
        int endIndex,
        double eventTimeMs,
        double halfWindowMs,
        MousePerformanceTimeBasis timeBasis,
        ref int leftCursor,
        ref int rightCursor)
    {
        while (leftCursor < endIndex && eventTimeMs - index.GetTimeMs(leftCursor, timeBasis) > halfWindowMs)
        {
            leftCursor++;
        }
        if (rightCursor < leftCursor - 1)
        {
            rightCursor = leftCursor - 1;
        }
        while (rightCursor + 1 <= endIndex && index.GetTimeMs(rightCursor + 1, timeBasis) - eventTimeMs <= halfWindowMs)
        {
            rightCursor++;
        }
        if (rightCursor < startIndex)
        {
            rightCursor = startIndex;
        }
    }

    private static void ExpandEventWindow(
        MousePerformanceSessionAnalysisIndex index,
        int rangeStart,
        int rangeEnd,
        int centerIndex,
        int minimumSamples,
        ref int left,
        ref int right,
        MousePerformanceTimeBasis timeBasis)
    {
        left = Math.Max(rangeStart, Math.Min(left, rangeEnd));
        right = Math.Max(left, Math.Min(right, rangeEnd));
        double eventTimeMs = index.GetTimeMs(centerIndex, timeBasis);
        while (right - left + 1 < minimumSamples && (left > rangeStart || right < rangeEnd))
        {
            if (left <= rangeStart)
            {
                right++;
                continue;
            }
            if (right >= rangeEnd)
            {
                left--;
                continue;
            }

            double nextLeftDistanceMs = Math.Abs(eventTimeMs - index.GetTimeMs(left - 1, timeBasis));
            double nextRightDistanceMs = Math.Abs(index.GetTimeMs(right + 1, timeBasis) - eventTimeMs);
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

    private static bool TryResolveWindowInterval(MousePerformanceSessionAnalysisIndex index, int left, int right, MousePerformanceTimeBasis timeBasis, out double intervalMs)
    {
        intervalMs = 0.0;
        return right > left && index.TryGetIntervalMs(left, right, timeBasis, ref intervalMs);
    }

    private static double ResolveVelocity(MousePerformanceSessionAnalysisIndex index, int left, int right, double intervalMs, double countsToMillimeters, MousePerformanceVelocityTrendComponent component)
    {
        return component switch
        {
            MousePerformanceVelocityTrendComponent.XAxis => (index.GetCumulativeX(right) - index.GetCumulativeX(left)) / intervalMs * countsToMillimeters,
            MousePerformanceVelocityTrendComponent.YAxis => (index.GetCumulativeY(right) - index.GetCumulativeY(left)) / intervalMs * countsToMillimeters,
            MousePerformanceVelocityTrendComponent.PathSpeed => index.GetPathCountsBetween(left + 1, right) / intervalMs * countsToMillimeters,
            _ => 0.0
        };
    }

    private static IEnumerable<VelocitySegment> EnumerateVelocitySegments(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis)
    {
        int firstVelocityEventIndex = Math.Max(1, startIndex);
        if (index == null || firstVelocityEventIndex > endIndex)
        {
            yield break;
        }

        if (timeBasis != MousePerformanceTimeBasis.RawCapture)
        {
            yield return new VelocitySegment(firstVelocityEventIndex, endIndex);
            yield break;
        }

        int segmentStart = firstVelocityEventIndex;
        while (segmentStart <= endIndex)
        {
            int segmentEnd = Math.Min(endIndex, index.GetSegmentEnd(segmentStart));
            yield return new VelocitySegment(segmentStart, segmentEnd);
            segmentStart = segmentEnd + 1;
        }
    }

    private static int ResolveEstimatedCapacity(int startIndex, int endIndex)
    {
        return Math.Max(0, endIndex - Math.Max(1, startIndex) + 1);
    }

    private static bool CanEstimate(MousePerformanceSessionAnalysisIndex index, int startIndex, int endIndex, double? cpi)
    {
        return index != null && index.Count > 1 && startIndex <= endIndex && cpi.HasValue && cpi.Value > 0.0;
    }

    private static double ResolveVelocitySmoothingTimeConstantMs(MousePerformanceAnalysisOptions options)
    {
        double trendWindowMs = (options ?? MousePerformanceAnalysisOptions.Default).TrendWindowMs;
        return Math.Max(8.0, trendWindowMs);
    }

    private static double ResolveMedian(double[] sortedValues, int count)
    {
        if (sortedValues == null || count <= 0)
        {
            return 0.0;
        }
        int middleIndex = count / 2;
        if ((count & 1) != 0)
        {
            return sortedValues[middleIndex];
        }
        return (sortedValues[middleIndex - 1] + sortedValues[middleIndex]) / 2.0;
    }

    private static double CoerceTrendValue(double value, bool nonNegative)
    {
        if (!IsFinite(value))
        {
            return 0.0;
        }
        return nonNegative ? Math.Max(0.0, value) : value;
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

    private readonly struct VelocitySegment
    {
        public int StartIndex { get; }

        public int EndIndex { get; }

        public int Length => EndIndex - StartIndex + 1;

        public VelocitySegment(int startIndex, int endIndex)
        {
            StartIndex = startIndex;
            EndIndex = Math.Max(startIndex, endIndex);
        }
    }
}
