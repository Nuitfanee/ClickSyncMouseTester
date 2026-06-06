using System;

namespace ClickSyncMouseTester.Services;

internal readonly struct MousePerformanceHistogramBinLayout
{
    public double MinimumX { get; }

    public double MaximumX { get; }

    public double BinWidth { get; }

    public int BinCount { get; }

    public MousePerformanceHistogramBinLayout(double minimumX, double maximumX, double binWidth, int binCount)
    {
        MinimumX = minimumX;
        MaximumX = maximumX;
        BinWidth = binWidth;
        BinCount = Math.Max(0, binCount);
    }

    public bool IsValid => BinCount > 0 && BinWidth > 0.0 && MaximumX > MinimumX;
}
