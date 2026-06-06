using System;

namespace ClickSyncMouseTester.Models;

public readonly struct MousePerformanceChartPoint : IEquatable<MousePerformanceChartPoint>
{
    public double X { get; }

    public double Y { get; }

    public MousePerformanceChartPoint(double x, double y)
    {
        X = x;
        Y = y;
    }

    public bool Equals(MousePerformanceChartPoint other)
    {
        return X.Equals(other.X) && Y.Equals(other.Y);
    }

    public override bool Equals(object obj)
    {
        return obj is MousePerformanceChartPoint other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(X, Y);
    }
}
