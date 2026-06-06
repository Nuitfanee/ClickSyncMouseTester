using System;

namespace ClickSyncMouseTester.Models;

public sealed class MousePerformanceResidualDispersionSummary
{
    public double MadCounts { get; }

    public double IqrCounts { get; }

    public MousePerformanceResidualDispersionSummary(double madCounts, double iqrCounts)
    {
        MadCounts = Math.Max(0.0, madCounts);
        IqrCounts = Math.Max(0.0, iqrCounts);
    }
}





