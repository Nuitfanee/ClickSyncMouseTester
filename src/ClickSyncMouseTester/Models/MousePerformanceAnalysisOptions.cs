using System;

namespace ClickSyncMouseTester.Models;

public sealed class MousePerformanceAnalysisOptions
{
    public static readonly MousePerformanceAnalysisOptions Default = new MousePerformanceAnalysisOptions(12.0, 3, 30.0, 75.0, 7, 0.1, 60.0);

    public double TrendWindowMs { get; }

    public int MinimumTrendSamples { get; }

    public double CurrentVelocityWindowMs { get; }

    public double TimingSeriesRecommendedWindowMs { get; }

    public int TimingSeriesRecommendedMinimumSamples { get; }

    public double TimingSeriesTrimRatio { get; }

    public double TimingSeriesEmaTimeConstantMs { get; }

    public MousePerformanceAnalysisOptions(double trendWindowMs, int minimumTrendSamples)
        : this(trendWindowMs, minimumTrendSamples, 30.0, 75.0, 7, 0.1, 60.0)
    {
    }

    public MousePerformanceAnalysisOptions(double trendWindowMs, int minimumTrendSamples, double currentVelocityWindowMs, double timingSeriesRecommendedWindowMs, int timingSeriesRecommendedMinimumSamples, double timingSeriesTrimRatio, double timingSeriesEmaTimeConstantMs)
    {
        TrendWindowMs = ((double.IsNaN(trendWindowMs) || double.IsInfinity(trendWindowMs)) ? 12.0 : Math.Max(1.0, trendWindowMs));
        MinimumTrendSamples = Math.Max(3, minimumTrendSamples);
        CurrentVelocityWindowMs = NormalizePositive(currentVelocityWindowMs, 30.0);
        TimingSeriesRecommendedWindowMs = NormalizePositive(timingSeriesRecommendedWindowMs, 75.0);
        TimingSeriesRecommendedMinimumSamples = Math.Max(3, timingSeriesRecommendedMinimumSamples);
        TimingSeriesTrimRatio = NormalizeRatio(timingSeriesTrimRatio, 0.1);
        TimingSeriesEmaTimeConstantMs = NormalizePositive(timingSeriesEmaTimeConstantMs, 60.0);
    }

    private static double NormalizePositive(double value, double fallback)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0 ? fallback : value;
    }

    private static double NormalizeRatio(double value, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return fallback;
        }
        return Math.Max(0.0, Math.Min(0.49, value));
    }
}





