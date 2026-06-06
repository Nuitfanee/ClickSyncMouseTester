using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ClickSyncMouseTester.Services;

internal static class MousePerformanceHistogramBuilder
{
    private const int CancellationCheckMask = 1023;
    private const int MinimumBinCount = 8;
    private const int MaximumBinCount = 96;

    public static MousePerformanceHistogramBinLayout ResolveLayout(IReadOnlyList<double> samples, MousePerformanceScalarSampleKind sampleKind, CancellationToken cancellationToken = default)
    {
        return MousePerformanceHistogramLayoutPolicy.ResolveLayout(samples, sampleKind, cancellationToken);
    }

    public static MousePerformanceHistogramBinLayout ResolveContinuousLayout(IReadOnlyList<double> samples, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<double> sortedSamples = MousePerformanceDistributionCalculator.ToSortedFiniteSamples(samples, cancellationToken);
        if (sortedSamples.Count == 0)
        {
            return default;
        }

        double minimum = sortedSamples[0];
        double maximum = sortedSamples[sortedSamples.Count - 1];
        if (minimum.Equals(maximum))
        {
            double padding = Math.Max(1.0, Math.Abs(minimum) * 0.1);
            minimum -= padding;
            maximum += padding;
        }

        double? firstQuartile = MousePerformanceDistributionCalculator.ResolvePercentileFromSortedValues(sortedSamples, 0.25);
        double? thirdQuartile = MousePerformanceDistributionCalculator.ResolvePercentileFromSortedValues(sortedSamples, 0.75);
        double iqr = firstQuartile.HasValue && thirdQuartile.HasValue ? Math.Max(0.0, thirdQuartile.Value - firstQuartile.Value) : 0.0;
        double range = maximum - minimum;
        double binWidth = 0.0;
        if (iqr > 0.0 && sortedSamples.Count > 1)
        {
            binWidth = 2.0 * iqr / Math.Pow(sortedSamples.Count, 1.0 / 3.0);
        }

        int binCount = binWidth > 0.0
            ? (int)Math.Ceiling(range / binWidth)
            : (int)Math.Ceiling(Math.Sqrt(sortedSamples.Count));
        binCount = Math.Max(MinimumBinCount, Math.Min(MaximumBinCount, binCount));
        binWidth = range / binCount;
        if (binWidth <= 0.0)
        {
            return default;
        }

        return new MousePerformanceHistogramBinLayout(minimum, maximum, binWidth, binCount);
    }

    public static IReadOnlyList<MousePerformanceHistogramBin> Build(IReadOnlyList<double> samples, MousePerformanceHistogramScale scale, MousePerformanceHistogramBinLayout layout, CancellationToken cancellationToken = default)
    {
        if (samples == null || samples.Count == 0 || !layout.IsValid)
        {
            return Array.Empty<MousePerformanceHistogramBin>();
        }

        int[] counts = new int[layout.BinCount];
        int finiteSampleCount = 0;
        for (int sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, sampleIndex);
            double sample = samples[sampleIndex];
            if (double.IsNaN(sample) || double.IsInfinity(sample))
            {
                continue;
            }

            int binIndex = (int)Math.Floor((sample - layout.MinimumX) / layout.BinWidth);
            if (binIndex < 0)
            {
                binIndex = 0;
            }
            else if (binIndex >= layout.BinCount)
            {
                binIndex = layout.BinCount - 1;
            }
            counts[binIndex]++;
            finiteSampleCount++;
        }

        if (finiteSampleCount == 0)
        {
            return Array.Empty<MousePerformanceHistogramBin>();
        }

        MousePerformanceHistogramBin[] bins = new MousePerformanceHistogramBin[layout.BinCount];
        for (int binIndex = 0; binIndex < layout.BinCount; binIndex++)
        {
            double minimumX = layout.MinimumX + binIndex * layout.BinWidth;
            double maximumX = binIndex == layout.BinCount - 1 ? layout.MaximumX : minimumX + layout.BinWidth;
            double value = scale == MousePerformanceHistogramScale.Percent ? (double)counts[binIndex] * 100.0 / finiteSampleCount : counts[binIndex];
            bins[binIndex] = new MousePerformanceHistogramBin(minimumX, maximumX, value, counts[binIndex]);
        }

        return bins;
    }

    private static void ThrowIfCancellationRequested(CancellationToken cancellationToken, int iteration)
    {
        if (cancellationToken.CanBeCanceled && (Math.Max(0, iteration) & CancellationCheckMask) == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
