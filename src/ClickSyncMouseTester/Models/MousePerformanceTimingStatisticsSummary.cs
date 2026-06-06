using System;

namespace ClickSyncMouseTester.Models;

public sealed class MousePerformanceTimingStatisticsSummary
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

    public MousePerformanceTimingStatisticsSummary(int sampleCount, double averageValue, double standardDeviationValue, double p50Value, double p95Value, double p99Value, double? p999Value, double madValue, double iqrValue)
    {
        SampleCount = Math.Max(0, sampleCount);
        AverageValue = Math.Max(0.0, averageValue);
        StandardDeviationValue = Math.Max(0.0, standardDeviationValue);
        P50Value = Math.Max(0.0, p50Value);
        P95Value = Math.Max(0.0, p95Value);
        P99Value = Math.Max(0.0, p99Value);
        P999Value = p999Value.HasValue ? Math.Max(0.0, p999Value.Value) : null;
        MadValue = Math.Max(0.0, madValue);
        IqrValue = Math.Max(0.0, iqrValue);
    }
}





