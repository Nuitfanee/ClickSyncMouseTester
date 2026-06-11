using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Models;

public class MousePerformanceChartRenderFrame
{
    private readonly MousePerformancePlotType _plotType;

    private readonly string _title;

    private readonly string _subtitle;

    private readonly string _description;

    private readonly string _xAxisTitle;

    private readonly string _yAxisTitle;

    private readonly bool _isAvailable;

    private readonly string _message;

    private readonly int _startIndex;

    private readonly int _endIndex;

    private readonly bool _showStem;

    private readonly bool _showLines;

    private readonly MousePerformanceTimeBasis _timeBasis;

    private readonly double _xMinimum;

    private readonly double _xMaximum;

    private readonly double _yMinimum;

    private readonly double _yMaximum;

    private readonly IReadOnlyList<MousePerformanceChartSeries> _series;

    private readonly IReadOnlyList<MousePerformanceChartGapSource> _gapSources;

    private readonly bool _hasComparisonDatasets;

    public MousePerformancePlotType PlotType => _plotType;

    public string Title => _title;

    public string Subtitle => _subtitle;

    public string Description => _description;

    public string XAxisTitle => _xAxisTitle;

    public string YAxisTitle => _yAxisTitle;

    public bool IsAvailable => _isAvailable;

    public string Message => _message;

    public int StartIndex => _startIndex;

    public int EndIndex => _endIndex;

    public bool ShowStem => _showStem;

    public bool ShowLines => _showLines;

    public MousePerformanceTimeBasis TimeBasis => _timeBasis;

    public double XMinimum => _xMinimum;

    public double XMaximum => _xMaximum;

    public double YMinimum => _yMinimum;

    public double YMaximum => _yMaximum;

    public IReadOnlyList<MousePerformanceChartSeries> Series => _series;

    public IReadOnlyList<MousePerformanceChartGapSource> GapSources => _gapSources;

    public bool HasComparisonDatasets => _hasComparisonDatasets;

    public MousePerformanceChartRenderFrame(MousePerformancePlotType plotType, string title, string subtitle, string description, string xAxisTitle, string yAxisTitle, bool isAvailable, string message, int startIndex, int endIndex, bool showStem, bool showLines, MousePerformanceTimeBasis timeBasis, double xMinimum, double xMaximum, double yMinimum, double yMaximum, IReadOnlyList<MousePerformanceChartSeries> series, bool hasComparisonDatasets = false, IReadOnlyList<MousePerformanceChartGapSource> gapSources = null)
    {
        _plotType = plotType;
        _title = title ?? string.Empty;
        _subtitle = subtitle ?? string.Empty;
        _description = description ?? string.Empty;
        _xAxisTitle = xAxisTitle ?? string.Empty;
        _yAxisTitle = yAxisTitle ?? string.Empty;
        _isAvailable = isAvailable;
        _message = message ?? string.Empty;
        _startIndex = Math.Max(0, startIndex);
        _endIndex = Math.Max(0, endIndex);
        _showStem = showStem;
        _showLines = showLines;
        _timeBasis = timeBasis;
        _xMinimum = xMinimum;
        _xMaximum = xMaximum;
        _yMinimum = yMinimum;
        _yMaximum = yMaximum;
        _series = series ?? Array.Empty<MousePerformanceChartSeries>();
        _gapSources = gapSources ?? Array.Empty<MousePerformanceChartGapSource>();
        _hasComparisonDatasets = hasComparisonDatasets;
    }
}





