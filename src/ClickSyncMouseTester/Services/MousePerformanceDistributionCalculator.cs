using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ClickSyncMouseTester.Services;

internal static class MousePerformanceDistributionCalculator
{
    private const int CancellationCheckMask = 1023;
    private const int MinimumP999SampleCount = 1000;

    public static MousePerformanceDistributionStatisticsSummary Compute(IReadOnlyList<double> samples, bool includeDispersionMetrics = true, CancellationToken cancellationToken = default)
    {
        if (samples == null || samples.Count == 0)
        {
            return null;
        }

        List<double> finiteSamples = new List<double>(samples.Count);
        for (int sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, sampleIndex);
            double sample = samples[sampleIndex];
            if (!double.IsNaN(sample) && !double.IsInfinity(sample))
            {
                finiteSamples.Add(sample);
            }
        }

        if (finiteSamples.Count == 0)
        {
            return null;
        }

        double[] sortedSamples = finiteSamples.ToArray();
        Array.Sort(sortedSamples);
        double sum = 0.0;
        for (int sampleIndex = 0; sampleIndex < sortedSamples.Length; sampleIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, sampleIndex);
            sum += sortedSamples[sampleIndex];
        }

        double average = sum / sortedSamples.Length;
        double varianceSum = 0.0;
        for (int sampleIndex = 0; sampleIndex < sortedSamples.Length; sampleIndex++)
        {
            double deviation = sortedSamples[sampleIndex] - average;
            varianceSum += deviation * deviation;
        }

        double standardDeviation = Math.Sqrt(varianceSum / sortedSamples.Length);
        double p50 = ResolvePercentileFromSortedValues(sortedSamples, 0.5) ?? 0.0;
        double p95 = ResolvePercentileFromSortedValues(sortedSamples, 0.95) ?? 0.0;
        double p99 = ResolvePercentileFromSortedValues(sortedSamples, 0.99) ?? 0.0;
        double? p999 = sortedSamples.Length >= MinimumP999SampleCount
            ? ResolvePercentileFromSortedValues(sortedSamples, 0.999)
            : null;
        double mad = 0.0;
        double iqr = 0.0;
        if (includeDispersionMetrics)
        {
            double p25 = ResolvePercentileFromSortedValues(sortedSamples, 0.25) ?? 0.0;
            double p75 = ResolvePercentileFromSortedValues(sortedSamples, 0.75) ?? 0.0;
            iqr = Math.Max(0.0, p75 - p25);
            double[] absoluteDeviations = new double[sortedSamples.Length];
            for (int sampleIndex = 0; sampleIndex < sortedSamples.Length; sampleIndex++)
            {
                absoluteDeviations[sampleIndex] = Math.Abs(sortedSamples[sampleIndex] - p50);
            }
            Array.Sort(absoluteDeviations);
            mad = ResolvePercentileFromSortedValues(absoluteDeviations, 0.5) ?? 0.0;
        }

        return new MousePerformanceDistributionStatisticsSummary(sortedSamples.Length, average, standardDeviation, p50, p95, p99, p999, mad, iqr);
    }

    public static double? ResolvePercentileFromSortedValues(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues == null || sortedValues.Count == 0)
        {
            return null;
        }
        double percentilePosition = Math.Max(0.0, Math.Min(1.0, percentile)) * (sortedValues.Count - 1);
        int lowerIndex = (int)Math.Floor(percentilePosition);
        int upperIndex = (int)Math.Ceiling(percentilePosition);
        if (lowerIndex == upperIndex)
        {
            return sortedValues[lowerIndex];
        }
        double interpolationRatio = percentilePosition - lowerIndex;
        return sortedValues[lowerIndex] + (sortedValues[upperIndex] - sortedValues[lowerIndex]) * interpolationRatio;
    }

    public static IReadOnlyList<double> ToSortedFiniteSamples(IReadOnlyList<double> samples, CancellationToken cancellationToken = default)
    {
        if (samples == null || samples.Count == 0)
        {
            return Array.Empty<double>();
        }

        List<double> finiteSamples = new List<double>(samples.Count);
        for (int sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, sampleIndex);
            double sample = samples[sampleIndex];
            if (!double.IsNaN(sample) && !double.IsInfinity(sample))
            {
                finiteSamples.Add(sample);
            }
        }

        finiteSamples.Sort();
        return finiteSamples.Count == 0 ? Array.Empty<double>() : finiteSamples.ToArray();
    }

    private static void ThrowIfCancellationRequested(CancellationToken cancellationToken, int iteration)
    {
        if (cancellationToken.CanBeCanceled && (Math.Max(0, iteration) & CancellationCheckMask) == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
