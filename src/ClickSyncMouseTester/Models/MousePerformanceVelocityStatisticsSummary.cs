using System;

namespace ClickSyncMouseTester.Models;

public sealed class MousePerformanceVelocityStatisticsSummary
{
    public int SampleCount { get; }

    public double AverageMetersPerSecond { get; }

    public double StandardDeviationMetersPerSecond { get; }

    public double P50MetersPerSecond { get; }

    public double P95MetersPerSecond { get; }

    public double P99MetersPerSecond { get; }

    public double? P999MetersPerSecond { get; }

    public double MadMetersPerSecond { get; }

    public double IqrMetersPerSecond { get; }

    public MousePerformanceVelocityStatisticsSummary(int sampleCount, double averageMetersPerSecond, double standardDeviationMetersPerSecond, double p50MetersPerSecond, double p95MetersPerSecond, double p99MetersPerSecond, double? p999MetersPerSecond, double madMetersPerSecond, double iqrMetersPerSecond)
    {
        SampleCount = Math.Max(0, sampleCount);
        AverageMetersPerSecond = Math.Max(0.0, averageMetersPerSecond);
        StandardDeviationMetersPerSecond = Math.Max(0.0, standardDeviationMetersPerSecond);
        P50MetersPerSecond = Math.Max(0.0, p50MetersPerSecond);
        P95MetersPerSecond = Math.Max(0.0, p95MetersPerSecond);
        P99MetersPerSecond = Math.Max(0.0, p99MetersPerSecond);
        P999MetersPerSecond = p999MetersPerSecond.HasValue ? Math.Max(0.0, p999MetersPerSecond.Value) : null;
        MadMetersPerSecond = Math.Max(0.0, madMetersPerSecond);
        IqrMetersPerSecond = Math.Max(0.0, iqrMetersPerSecond);
    }
}





