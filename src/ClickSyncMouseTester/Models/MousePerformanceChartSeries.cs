using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Models;

public class MousePerformanceChartSeries
{
    private readonly MousePerformanceChartSeriesKind _kind;

    private readonly MousePerformanceChartSeriesPalette _palette;

    private readonly IReadOnlyList<MousePerformanceChartPoint> _points;

    private readonly IReadOnlyList<MousePerformanceHistogramBin> _histogramBins;

    private readonly MousePerformanceChartDatasetSlot _datasetSlot;

    private readonly double _xOffset;

    private readonly double _groupScale;

    private readonly MousePerformanceSampleBasis _sampleBasis;

    public MousePerformanceChartSeriesKind Kind => _kind;

    public MousePerformanceChartSeriesPalette Palette => _palette;

    public IReadOnlyList<MousePerformanceChartPoint> Points => _points;

    public IReadOnlyList<MousePerformanceHistogramBin> HistogramBins => _histogramBins;

    public MousePerformanceChartDatasetSlot DatasetSlot => _datasetSlot;

    public double XOffset => _xOffset;

    public double GroupScale => _groupScale;

    public MousePerformanceSampleBasis SampleBasis => _sampleBasis;

    public MousePerformanceChartSeries(MousePerformanceChartSeriesKind kind, MousePerformanceChartSeriesPalette palette, IReadOnlyList<MousePerformanceChartPoint> points, MousePerformanceChartDatasetSlot datasetSlot = MousePerformanceChartDatasetSlot.Baseline, double xOffset = 0.0, MousePerformanceSampleBasis sampleBasis = MousePerformanceSampleBasis.RawReport, double groupScale = 1.0)
    {
        _kind = kind;
        _palette = palette;
        _points = points ?? Array.Empty<MousePerformanceChartPoint>();
        _histogramBins = Array.Empty<MousePerformanceHistogramBin>();
        _datasetSlot = datasetSlot;
        _xOffset = xOffset;
        _groupScale = SanitizeGroupScale(groupScale);
        _sampleBasis = sampleBasis;
    }

    public MousePerformanceChartSeries(MousePerformanceChartSeriesPalette palette, IReadOnlyList<MousePerformanceHistogramBin> histogramBins, MousePerformanceChartDatasetSlot datasetSlot = MousePerformanceChartDatasetSlot.Baseline, double xOffset = 0.0, MousePerformanceSampleBasis sampleBasis = MousePerformanceSampleBasis.RawReport, double groupScale = 1.0)
    {
        _kind = MousePerformanceChartSeriesKind.Histogram;
        _palette = palette;
        _points = Array.Empty<MousePerformanceChartPoint>();
        _histogramBins = histogramBins ?? Array.Empty<MousePerformanceHistogramBin>();
        _datasetSlot = datasetSlot;
        _xOffset = xOffset;
        _groupScale = SanitizeGroupScale(groupScale);
        _sampleBasis = sampleBasis;
    }

    private static double SanitizeGroupScale(double groupScale)
    {
        if (double.IsNaN(groupScale) || double.IsInfinity(groupScale) || groupScale <= 0.0)
        {
            return 1.0;
        }
        return Math.Min(1.0, groupScale);
    }
}





