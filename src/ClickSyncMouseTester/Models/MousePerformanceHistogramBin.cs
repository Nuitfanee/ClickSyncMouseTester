using System;

namespace ClickSyncMouseTester.Models;

public readonly struct MousePerformanceHistogramBin : IEquatable<MousePerformanceHistogramBin>
{
    public double MinimumX { get; }

    public double MaximumX { get; }

    public double Value { get; }

    public int Count { get; }

    public MousePerformanceHistogramBin(double minimumX, double maximumX, double value, int count)
    {
        MinimumX = minimumX;
        MaximumX = maximumX;
        Value = Math.Max(0.0, value);
        Count = Math.Max(0, count);
    }

    public double CenterX => (MinimumX + MaximumX) / 2.0;

    public bool Equals(MousePerformanceHistogramBin other)
    {
        return MinimumX.Equals(other.MinimumX)
            && MaximumX.Equals(other.MaximumX)
            && Value.Equals(other.Value)
            && Count == other.Count;
    }

    public override bool Equals(object obj)
    {
        return obj is MousePerformanceHistogramBin other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(MinimumX, MaximumX, Value, Count);
    }
}
