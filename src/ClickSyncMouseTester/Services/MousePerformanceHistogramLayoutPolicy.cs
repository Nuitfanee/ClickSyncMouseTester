using System;
using System.Collections.Generic;
using System.Threading;

namespace ClickSyncMouseTester.Services;

internal static class MousePerformanceHistogramLayoutPolicy
{
    private const int MaximumIntegerBinCount = 96;
    private const int CancellationCheckMask = 1023;
    private const double UnitBinHalfWidth = 0.5;

    public static MousePerformanceHistogramBinLayout ResolveLayout(IReadOnlyList<double> samples, MousePerformanceScalarSampleKind sampleKind, CancellationToken cancellationToken = default)
    {
        return sampleKind switch
        {
            MousePerformanceScalarSampleKind.DeltaX or MousePerformanceScalarSampleKind.DeltaY => ResolveIntegerCountLayout(samples, cancellationToken),
            _ => MousePerformanceHistogramBuilder.ResolveContinuousLayout(samples, cancellationToken)
        };
    }

    private static MousePerformanceHistogramBinLayout ResolveIntegerCountLayout(IReadOnlyList<double> samples, CancellationToken cancellationToken)
    {
        if (samples == null || samples.Count == 0)
        {
            return default;
        }

        int minimum = int.MaxValue;
        int maximum = int.MinValue;
        for (int sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, sampleIndex);
            double sample = samples[sampleIndex];
            if (double.IsNaN(sample) || double.IsInfinity(sample))
            {
                continue;
            }

            int integerSample = (int)Math.Round(sample, MidpointRounding.AwayFromZero);
            minimum = Math.Min(minimum, integerSample);
            maximum = Math.Max(maximum, integerSample);
        }

        if (minimum == int.MaxValue || maximum == int.MinValue)
        {
            return default;
        }

        long valueSpan = (long)maximum - minimum + 1L;
        if (valueSpan <= 0L)
        {
            return default;
        }

        if (valueSpan <= MaximumIntegerBinCount)
        {
            return new MousePerformanceHistogramBinLayout(minimum - UnitBinHalfWidth, maximum + UnitBinHalfWidth, 1.0, (int)valueSpan);
        }

        int binWidth = Math.Max(1, (int)Math.Ceiling((double)valueSpan / MaximumIntegerBinCount));
        int binCount = (int)Math.Ceiling((double)valueSpan / binWidth);
        return new MousePerformanceHistogramBinLayout(minimum, minimum + (double)binCount * binWidth, binWidth, binCount);
    }

    private static void ThrowIfCancellationRequested(CancellationToken cancellationToken, int iteration)
    {
        if (cancellationToken.CanBeCanceled && (Math.Max(0, iteration) & CancellationCheckMask) == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
