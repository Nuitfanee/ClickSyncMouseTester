using System;

namespace ClickSyncMouseTester.Models;

public sealed class MousePerformanceDistributionStatisticsSummary
{
    public int SampleCount { get; }

    public double AverageValue { get; }

    public double StandardDeviationValue { get; }

    public double P50Value { get; }

    public double P95Value { get; }

    public double P99Value { get; }

    public double? P999Value { get; }

    public double MadValue { get; }

    public double IqrValue { get; }

    public MousePerformanceDistributionStatisticsSummary(int sampleCount, double averageValue, double standardDeviationValue, double p50Value, double p95Value, double p99Value, double? p999Value, double madValue, double iqrValue)
    {
        SampleCount = Math.Max(0, sampleCount);
        AverageValue = SanitizeFinite(averageValue);
        StandardDeviationValue = SanitizeNonNegative(standardDeviationValue);
        P50Value = SanitizeFinite(p50Value);
        P95Value = SanitizeFinite(p95Value);
        P99Value = SanitizeFinite(p99Value);
        P999Value = p999Value.HasValue ? SanitizeFinite(p999Value.Value) : null;
        MadValue = SanitizeNonNegative(madValue);
        IqrValue = SanitizeNonNegative(iqrValue);
    }

    private static double SanitizeFinite(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : value;
    }

    private static double SanitizeNonNegative(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : Math.Max(0.0, value);
    }
}
