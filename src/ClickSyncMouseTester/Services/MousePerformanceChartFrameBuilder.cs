using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ClickSyncMouseTester.Services;

internal static class MousePerformanceChartFrameBuilder
{
    private const int CancellationCheckMask = 1023;

    public static MousePerformanceChartRenderFrame CreateChartRenderFrame(MousePerformanceSnapshot snapshot, MousePerformancePlotType plotType, int startIndex, int endIndex, bool showStem, bool showLines, MousePerformanceTimeBasis timeBasis, MousePerformanceAnalysisOptions analysisOptions = null, CancellationToken cancellationToken = default, IReadOnlyList<MousePerformanceChartGapSource> gapSources = null)
    {
        MousePerformanceAnalysisOptions options = analysisOptions ?? MousePerformanceAnalysisOptions.Default;
        IReadOnlyList<MousePerformanceEvent> events = snapshot?.Events ?? Array.Empty<MousePerformanceEvent>();
        if (events.Count == 0)
        {
            return CreateUnavailableFrame(plotType, showStem, showLines, timeBasis);
        }

        MousePerformanceSessionAnalysisIndex index = new MousePerformanceSessionAnalysisIndex(events, cancellationToken);
        int clampedStartIndex = index.ClampStartIndex(startIndex);
        int clampedEndIndex = index.ClampEndIndex(clampedStartIndex, endIndex);
        if (MousePerformancePlotTraits.IsVelocityPlot(plotType) && (snapshot == null || !snapshot.CanComputeVelocity))
        {
            return CreateUnavailableFrame(plotType, showStem, showLines, timeBasis, clampedStartIndex, clampedEndIndex);
        }
        if (MousePerformancePlotTraits.IsHistogramPlot(plotType))
        {
            return CreateHistogramFrame(snapshot, index, plotType, clampedStartIndex, clampedEndIndex, showStem, showLines, timeBasis, options, cancellationToken);
        }

        List<MousePerformanceChartPoint> scatterPrimary = new List<MousePerformanceChartPoint>();
        List<MousePerformanceChartPoint> scatterSecondary = new List<MousePerformanceChartPoint>();
        List<MousePerformanceChartPoint> rawLinePrimary = new List<MousePerformanceChartPoint>();
        List<MousePerformanceChartPoint> rawLineSecondary = new List<MousePerformanceChartPoint>();
        List<MousePerformanceChartPoint> trendPrimary = new List<MousePerformanceChartPoint>();
        List<MousePerformanceChartPoint> trendSecondary = new List<MousePerformanceChartPoint>();
        List<MousePerformanceChartPoint> stemPrimary = new List<MousePerformanceChartPoint>();
        List<MousePerformanceChartPoint> stemSecondary = new List<MousePerformanceChartPoint>();
        double xMin = double.MaxValue;
        double xMax = double.MinValue;
        double yMin = double.MaxValue;
        double yMax = double.MinValue;
        MousePerformancePlotPresentationPolicy presentationPolicy = MousePerformancePlotPresentationPolicy.Resolve(plotType);
        MousePerformancePlotPresentationState presentationState = presentationPolicy.ResolveState(showStem, showLines);
        bool usesContinuousEstimatedPresentation = presentationPolicy.UsesContinuousEstimateLine;
        bool shouldShowConnectedRawLine = presentationPolicy.ShouldShowConnectedRawLine(presentationState.ShowLines);
        bool shouldUseTrendLine = presentationPolicy.ShouldUseTrendLine(presentationState.ShowLines);
        MousePerformanceSampleBasis scatterPrimaryBasis = ResolveScatterPrimaryBasis(plotType);
        MousePerformanceSampleBasis scatterSecondaryBasis = ResolveScatterSecondaryBasis(plotType);

        switch (plotType)
        {
            case MousePerformancePlotType.XCountVsTime:
                MousePerformanceSeriesBuilder.BuildSingleAxisCountSeries(index, clampedStartIndex, clampedEndIndex, isXAxis: true, timeBasis, scatterPrimary, rawLinePrimary, presentationState.ShowStem ? stemPrimary : null, ref xMin, ref xMax, ref yMin, ref yMax, cancellationToken);
                if (shouldUseTrendLine)
                {
                    trendPrimary.AddRange(MousePerformanceSeriesBuilder.BuildMovingAverageTrend(rawLinePrimary, options, cancellationToken));
                }
                break;
            case MousePerformancePlotType.YCountVsTime:
                MousePerformanceSeriesBuilder.BuildSingleAxisCountSeries(index, clampedStartIndex, clampedEndIndex, isXAxis: false, timeBasis, scatterPrimary, rawLinePrimary, presentationState.ShowStem ? stemPrimary : null, ref xMin, ref xMax, ref yMin, ref yMax, cancellationToken);
                if (shouldUseTrendLine)
                {
                    trendPrimary.AddRange(MousePerformanceSeriesBuilder.BuildMovingAverageTrend(rawLinePrimary, options, cancellationToken));
                }
                break;
            case MousePerformancePlotType.XYCountVsTime:
                MousePerformanceSeriesBuilder.BuildDualAxisCountSeries(index, clampedStartIndex, clampedEndIndex, timeBasis, scatterPrimary, scatterSecondary, rawLinePrimary, rawLineSecondary, presentationState.ShowStem ? stemPrimary : null, presentationState.ShowStem ? stemSecondary : null, ref xMin, ref xMax, ref yMin, ref yMax, cancellationToken);
                if (shouldUseTrendLine)
                {
                    trendPrimary.AddRange(MousePerformanceSeriesBuilder.BuildMovingAverageTrend(rawLinePrimary, options, cancellationToken));
                    trendSecondary.AddRange(MousePerformanceSeriesBuilder.BuildMovingAverageTrend(rawLineSecondary, options, cancellationToken));
                }
                break;
            case MousePerformancePlotType.IntervalVsTime:
                {
                    IReadOnlyList<MousePerformanceChartPoint> scatterPoints = MousePerformanceSeriesBuilder.BuildRawTimingSeriesPoints(index, clampedStartIndex, clampedEndIndex, timeBasis, MousePerformanceTimingSeriesMode.Interval, cancellationToken);
                    MousePerformanceSeriesBuilder.AppendTimingSeriesPoints(scatterPoints, shouldShowConnectedRawLine ? scatterPoints : null, scatterPrimary, shouldShowConnectedRawLine ? rawLinePrimary : null, shouldShowConnectedRawLine && presentationState.ShowStem ? stemPrimary : null, ref xMin, ref xMax, ref yMin, ref yMax);
                    break;
                }
            case MousePerformancePlotType.FrequencyVsTime:
                {
                    MousePerformanceTimingSeriesSamples timingSamples = MousePerformanceSeriesBuilder.BuildTimingSeriesSamples(index, clampedStartIndex, clampedEndIndex, timeBasis, options, MousePerformanceTimingSeriesMode.Frequency, cancellationToken);
                    trendPrimary.AddRange(ResolveTimingTrendPoints(timingSamples));
                    MousePerformanceSeriesBuilder.UpdateRangeFromPoints(trendPrimary, ref xMin, ref xMax, ref yMin, ref yMax);
                    break;
                }
            case MousePerformancePlotType.XVelocityVsTime:
            case MousePerformancePlotType.YVelocityVsTime:
                trendPrimary.AddRange(MousePerformanceSeriesBuilder.BuildVelocityTrend(index, clampedStartIndex, clampedEndIndex, snapshot.EffectiveCpi, plotType, options, timeBasis, cancellationToken));
                MousePerformanceSeriesBuilder.UpdateRangeFromPoints(trendPrimary, ref xMin, ref xMax, ref yMin, ref yMax);
                break;
            case MousePerformancePlotType.XYVelocityVsTime:
                MousePerformanceSeriesBuilder.BuildDualAxisVelocityTrend(index, clampedStartIndex, clampedEndIndex, snapshot.EffectiveCpi, options, timeBasis, trendPrimary, trendSecondary, cancellationToken);
                MousePerformanceSeriesBuilder.UpdateRangeFromPoints(trendPrimary, ref xMin, ref xMax, ref yMin, ref yMax);
                MousePerformanceSeriesBuilder.UpdateRangeFromPoints(trendSecondary, ref xMin, ref xMax, ref yMin, ref yMax);
                break;
            case MousePerformancePlotType.PathSpeedVsTime:
                trendPrimary.AddRange(MousePerformanceSeriesBuilder.BuildPathSpeedTrend(index, clampedStartIndex, clampedEndIndex, snapshot.EffectiveCpi, options, timeBasis, cancellationToken));
                MousePerformanceSeriesBuilder.UpdateRangeFromPoints(trendPrimary, ref xMin, ref xMax, ref yMin, ref yMax);
                break;
            case MousePerformancePlotType.XSumVsTime:
                MousePerformanceSeriesBuilder.BuildSingleAxisSumSeries(index, clampedStartIndex, clampedEndIndex, isXAxis: true, timeBasis, scatterPrimary, rawLinePrimary, presentationState.ShowStem ? stemPrimary : null, ref xMin, ref xMax, ref yMin, ref yMax, cancellationToken);
                if (shouldUseTrendLine)
                {
                    trendPrimary.AddRange(MousePerformanceSeriesBuilder.BuildMovingAverageTrend(rawLinePrimary, options, cancellationToken));
                }
                break;
            case MousePerformancePlotType.YSumVsTime:
                MousePerformanceSeriesBuilder.BuildSingleAxisSumSeries(index, clampedStartIndex, clampedEndIndex, isXAxis: false, timeBasis, scatterPrimary, rawLinePrimary, presentationState.ShowStem ? stemPrimary : null, ref xMin, ref xMax, ref yMin, ref yMax, cancellationToken);
                if (shouldUseTrendLine)
                {
                    trendPrimary.AddRange(MousePerformanceSeriesBuilder.BuildMovingAverageTrend(rawLinePrimary, options, cancellationToken));
                }
                break;
            case MousePerformancePlotType.XYSumVsTime:
                MousePerformanceSeriesBuilder.BuildDualAxisSumSeries(index, clampedStartIndex, clampedEndIndex, timeBasis, scatterPrimary, scatterSecondary, rawLinePrimary, rawLineSecondary, presentationState.ShowStem ? stemPrimary : null, presentationState.ShowStem ? stemSecondary : null, ref xMin, ref xMax, ref yMin, ref yMax, cancellationToken);
                if (shouldUseTrendLine)
                {
                    trendPrimary.AddRange(MousePerformanceSeriesBuilder.BuildMovingAverageTrend(rawLinePrimary, options, cancellationToken));
                    trendSecondary.AddRange(MousePerformanceSeriesBuilder.BuildMovingAverageTrend(rawLineSecondary, options, cancellationToken));
                }
                break;
            default:
                MousePerformanceSeriesBuilder.BuildTrajectorySeries(index, clampedStartIndex, clampedEndIndex, scatterPrimary, rawLinePrimary, ref xMin, ref xMax, ref yMin, ref yMax, cancellationToken);
                break;
        }

        if (scatterPrimary.Count == 0 && scatterSecondary.Count == 0 && rawLinePrimary.Count <= 1 && rawLineSecondary.Count <= 1 && trendPrimary.Count <= 1 && trendSecondary.Count <= 1)
        {
            return CreateUnavailableFrame(plotType, showStem, showLines, timeBasis, clampedStartIndex, clampedEndIndex);
        }

        List<MousePerformanceChartSeries> series = new List<MousePerformanceChartSeries>();
        MousePerformanceChartPoint[] scatterPrimaryPoints = MousePerformanceSeriesBuilder.ToArrayOrEmpty(scatterPrimary);
        MousePerformanceChartPoint[] scatterSecondaryPoints = MousePerformanceSeriesBuilder.ToArrayOrEmpty(scatterSecondary);
        if (plotType == MousePerformancePlotType.XVsY)
        {
            if (rawLinePrimary.Count > 1)
            {
                series.Add(new MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Accent, MousePerformanceSeriesBuilder.ResolveSharedSeriesPoints(rawLinePrimary, scatterPrimary, scatterPrimaryPoints), sampleBasis: MousePerformanceSampleBasis.CumulativeMotion));
            }
        }
        else if (shouldUseTrendLine || usesContinuousEstimatedPresentation)
        {
            if (trendPrimary.Count > 1)
            {
                series.Add(new MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Primary, MousePerformanceSeriesBuilder.ToArrayOrEmpty(trendPrimary), sampleBasis: ResolveTrendPrimaryBasis(plotType)));
            }
            if (trendSecondary.Count > 1)
            {
                series.Add(new MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Secondary, MousePerformanceSeriesBuilder.ToArrayOrEmpty(trendSecondary), sampleBasis: ResolveTrendSecondaryBasis(plotType)));
            }
        }
        else if (shouldShowConnectedRawLine)
        {
            if (rawLinePrimary.Count > 1)
            {
                series.Add(new MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Accent, MousePerformanceSeriesBuilder.ResolveSharedSeriesPoints(rawLinePrimary, scatterPrimary, scatterPrimaryPoints), sampleBasis: scatterPrimaryBasis));
            }
            if (rawLineSecondary.Count > 1)
            {
                series.Add(new MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Secondary, MousePerformanceSeriesBuilder.ResolveSharedSeriesPoints(rawLineSecondary, scatterSecondary, scatterSecondaryPoints), sampleBasis: scatterSecondaryBasis));
            }
        }

        if (presentationState.ShowStem && shouldShowConnectedRawLine)
        {
            if (stemPrimary.Count > 0)
            {
                series.Add(new MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Stem, MousePerformanceChartSeriesPalette.Primary, MousePerformanceSeriesBuilder.ResolveSharedSeriesPoints(stemPrimary, scatterPrimary, scatterPrimaryPoints), sampleBasis: scatterPrimaryBasis));
            }
            if (stemSecondary.Count > 0)
            {
                series.Add(new MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Stem, MousePerformanceChartSeriesPalette.Secondary, MousePerformanceSeriesBuilder.ResolveSharedSeriesPoints(stemSecondary, scatterSecondary, scatterSecondaryPoints), sampleBasis: scatterSecondaryBasis));
            }
        }

        if (scatterPrimary.Count > 0)
        {
            series.Add(new MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Primary, scatterPrimaryPoints, sampleBasis: scatterPrimaryBasis));
        }
        if (scatterSecondary.Count > 0)
        {
            series.Add(new MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Secondary, scatterSecondaryPoints, sampleBasis: scatterSecondaryBasis));
        }

        MousePerformanceChartViewportPolicy.ExpandAxisRange(ref xMin, ref xMax, MousePerformanceChartViewportPolicy.ResolveHorizontalAxisPaddingRatio(plotType), MousePerformanceChartViewportPolicy.ResolveSinglePointAxisPaddingRatio(plotType, isHorizontalAxis: true));
        MousePerformanceChartViewportPolicy.ExpandYAxisRange(plotType, ref yMin, ref yMax);
        IReadOnlyList<MousePerformanceChartGapSource> resolvedGapSources = plotType == MousePerformancePlotType.XVsY
            ? Array.Empty<MousePerformanceChartGapSource>()
            : gapSources ?? CreateReportGapSources(events, clampedStartIndex, clampedEndIndex, timeBasis);
        return new MousePerformanceChartRenderFrame(plotType, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, isAvailable: true, string.Empty, clampedStartIndex, clampedEndIndex, presentationState.ShowStem, presentationState.ShowLines, timeBasis, xMin, xMax, yMin, yMax, series.ToArray(), gapSources: resolvedGapSources);
    }

    private static MousePerformanceChartRenderFrame CreateHistogramFrame(MousePerformanceSnapshot snapshot, MousePerformanceSessionAnalysisIndex index, MousePerformancePlotType plotType, int startIndex, int endIndex, bool showStem, bool showLines, MousePerformanceTimeBasis timeBasis, MousePerformanceAnalysisOptions options, CancellationToken cancellationToken)
    {
        MousePerformancePlotPresentationPolicy presentationPolicy = MousePerformancePlotPresentationPolicy.Resolve(plotType);
        MousePerformancePlotPresentationState presentationState = presentationPolicy.ResolveState(showStem, showLines);
        MousePerformanceScalarSampleKind sampleKind = MousePerformancePlotTraits.ResolveHistogramSampleKind(plotType);
        IReadOnlyList<double> samples = MousePerformanceScalarSampleExtractor.Extract(index, sampleKind, startIndex, endIndex, snapshot?.EffectiveCpi, timeBasis, options, cancellationToken);
        MousePerformanceHistogramBinLayout layout = MousePerformanceHistogramBuilder.ResolveLayout(samples, sampleKind, cancellationToken);
        IReadOnlyList<MousePerformanceHistogramBin> bins = MousePerformanceHistogramBuilder.Build(samples, MousePerformanceHistogramScale.Percent, layout, cancellationToken);
        if (bins.Count == 0)
        {
            return CreateUnavailableFrame(plotType, showStem, showLines, timeBasis, startIndex, endIndex);
        }

        double xMin = double.MaxValue;
        double xMax = double.MinValue;
        double yMin = double.MaxValue;
        double yMax = double.MinValue;
        MousePerformanceSeriesBuilder.UpdateRangeFromHistogramBins(bins, ref xMin, ref xMax, ref yMin, ref yMax);
        MousePerformanceChartViewportPolicy.ExpandAxisRange(ref xMin, ref xMax, MousePerformanceChartViewportPolicy.ResolveHorizontalAxisPaddingRatio(plotType), MousePerformanceChartViewportPolicy.ResolveSinglePointAxisPaddingRatio(plotType, isHorizontalAxis: true));
        MousePerformanceChartViewportPolicy.ExpandYAxisRange(plotType, ref yMin, ref yMax);
        MousePerformanceChartSeries[] series =
        {
            new MousePerformanceChartSeries(MousePerformanceChartSeriesPalette.Primary, bins, sampleBasis: MousePerformancePlotTraits.ResolveHistogramSampleBasis(plotType))
        };
        return new MousePerformanceChartRenderFrame(plotType, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, isAvailable: true, string.Empty, startIndex, endIndex, presentationState.ShowStem, presentationState.ShowLines, timeBasis, xMin, xMax, yMin, yMax, series, gapSources: Array.Empty<MousePerformanceChartGapSource>());
    }

    public static MousePerformanceChartRenderFrame CreateComparisonHistogramRenderFrame(IReadOnlyList<MousePerformanceHistogramDataset> datasets, MousePerformancePlotType plotType, int startIndex, int endIndex, bool showStem, bool showLines, MousePerformanceTimeBasis timeBasis, MousePerformanceAnalysisOptions analysisOptions = null, CancellationToken cancellationToken = default)
    {
        MousePerformanceAnalysisOptions options = analysisOptions ?? MousePerformanceAnalysisOptions.Default;
        MousePerformancePlotPresentationPolicy presentationPolicy = MousePerformancePlotPresentationPolicy.Resolve(plotType);
        MousePerformancePlotPresentationState presentationState = presentationPolicy.ResolveState(showStem, showLines);
        if (datasets == null || datasets.Count == 0 || !MousePerformancePlotTraits.IsHistogramPlot(plotType))
        {
            return CreateUnavailableFrame(plotType, showStem, showLines, timeBasis, startIndex, endIndex);
        }

        MousePerformanceScalarSampleKind sampleKind = MousePerformancePlotTraits.ResolveHistogramSampleKind(plotType);
        List<DatasetHistogramSamples> datasetSamples = new List<DatasetHistogramSamples>(datasets.Count);
        List<double> combinedSamples = new List<double>();
        for (int datasetIndex = 0; datasetIndex < datasets.Count; datasetIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, datasetIndex);
            MousePerformanceHistogramDataset dataset = datasets[datasetIndex];
            MousePerformanceSnapshot snapshot = dataset.Snapshot;
            if (snapshot == null)
            {
                continue;
            }

            IReadOnlyList<double> samples = MousePerformanceScalarSampleExtractor.Extract(snapshot, sampleKind, startIndex, endIndex, timeBasis, options, cancellationToken);
            if (samples.Count == 0)
            {
                continue;
            }

            datasetSamples.Add(new DatasetHistogramSamples(dataset.DatasetSlot, samples));
            combinedSamples.AddRange(samples);
        }

        if (datasetSamples.Count == 0 || combinedSamples.Count == 0)
        {
            return CreateUnavailableFrame(plotType, showStem, showLines, timeBasis, startIndex, endIndex);
        }

        MousePerformanceHistogramBinLayout layout = MousePerformanceHistogramBuilder.ResolveLayout(combinedSamples, sampleKind, cancellationToken);
        if (!layout.IsValid)
        {
            return CreateUnavailableFrame(plotType, showStem, showLines, timeBasis, startIndex, endIndex);
        }

        List<MousePerformanceChartSeries> series = new List<MousePerformanceChartSeries>(datasetSamples.Count);
        double xMinimum = double.MaxValue;
        double xMaximum = double.MinValue;
        double yMinimum = double.MaxValue;
        double yMaximum = double.MinValue;
        double groupScale = ResolveHistogramGroupScale(datasetSamples.Count);
        for (int datasetIndex = 0; datasetIndex < datasetSamples.Count; datasetIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, datasetIndex);
            DatasetHistogramSamples samples = datasetSamples[datasetIndex];
            IReadOnlyList<MousePerformanceHistogramBin> bins = MousePerformanceHistogramBuilder.Build(samples.Samples, MousePerformanceHistogramScale.Percent, layout, cancellationToken);
            if (bins.Count == 0)
            {
                continue;
            }

            double xOffset = ResolveHistogramGroupOffset(layout.BinWidth, datasetIndex, datasetSamples.Count);
            MousePerformanceSeriesBuilder.UpdateRangeFromHistogramBins(bins, xOffset, groupScale, ref xMinimum, ref xMaximum, ref yMinimum, ref yMaximum);
            series.Add(new MousePerformanceChartSeries(MousePerformanceChartSeriesPalette.Primary, bins, samples.DatasetSlot, xOffset, MousePerformancePlotTraits.ResolveHistogramSampleBasis(plotType), groupScale));
        }

        if (series.Count == 0)
        {
            return CreateUnavailableFrame(plotType, showStem, showLines, timeBasis, startIndex, endIndex);
        }

        MousePerformanceChartViewportPolicy.ExpandAxisRange(ref xMinimum, ref xMaximum, MousePerformanceChartViewportPolicy.ResolveHorizontalAxisPaddingRatio(plotType), MousePerformanceChartViewportPolicy.ResolveSinglePointAxisPaddingRatio(plotType, isHorizontalAxis: true));
        MousePerformanceChartViewportPolicy.ExpandYAxisRange(plotType, ref yMinimum, ref yMaximum);
        return new MousePerformanceChartRenderFrame(plotType, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, isAvailable: true, string.Empty, startIndex, endIndex, presentationState.ShowStem, presentationState.ShowLines, timeBasis, xMinimum, xMaximum, yMinimum, yMaximum, series.ToArray(), datasets.Count > 1, Array.Empty<MousePerformanceChartGapSource>());
    }

    private readonly struct DatasetHistogramSamples
    {
        public MousePerformanceChartDatasetSlot DatasetSlot { get; }

        public IReadOnlyList<double> Samples { get; }

        public DatasetHistogramSamples(MousePerformanceChartDatasetSlot datasetSlot, IReadOnlyList<double> samples)
        {
            DatasetSlot = datasetSlot;
            Samples = samples ?? Array.Empty<double>();
        }
    }

    private static double ResolveHistogramGroupScale(int datasetCount)
    {
        return datasetCount > 1 ? 0.82 / datasetCount : 1.0;
    }

    private static double ResolveHistogramGroupOffset(double binWidth, int datasetIndex, int datasetCount)
    {
        if (datasetCount <= 1 || binWidth <= 0.0)
        {
            return 0.0;
        }
        double groupScale = ResolveHistogramGroupScale(datasetCount);
        double slotWidth = binWidth / datasetCount;
        return (datasetIndex + 0.5) * slotWidth - binWidth / 2.0 - groupScale * binWidth / 2.0;
    }

    public static MousePerformanceChartRenderFrame CreateUnavailableFrame(MousePerformancePlotType plotType, bool showStem, bool showLines, MousePerformanceTimeBasis timeBasis, int startIndex = 0, int endIndex = 0)
    {
        MousePerformancePlotPresentationPolicy presentationPolicy = MousePerformancePlotPresentationPolicy.Resolve(plotType);
        MousePerformancePlotPresentationState presentationState = presentationPolicy.ResolveState(showStem, showLines);
        return new MousePerformanceChartRenderFrame(plotType, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, isAvailable: false, string.Empty, startIndex, endIndex, presentationState.ShowStem, presentationState.ShowLines, timeBasis, -1.0, 1.0, -1.0, 1.0, Array.Empty<MousePerformanceChartSeries>());
    }

    private static MousePerformanceSampleBasis ResolveScatterPrimaryBasis(MousePerformancePlotType plotType)
    {
        return plotType switch
        {
            MousePerformancePlotType.IntervalVsTime or MousePerformancePlotType.FrequencyVsTime => MousePerformanceSampleBasis.ReportTiming,
            MousePerformancePlotType.XVelocityVsTime or MousePerformancePlotType.YVelocityVsTime or MousePerformancePlotType.XYVelocityVsTime or MousePerformancePlotType.PathSpeedVsTime => MousePerformanceSampleBasis.RelativeMotion,
            MousePerformancePlotType.XSumVsTime or MousePerformancePlotType.YSumVsTime or MousePerformancePlotType.XYSumVsTime or MousePerformancePlotType.XVsY => MousePerformanceSampleBasis.CumulativeMotion,
            _ => MousePerformanceSampleBasis.RawReport
        };
    }

    private static MousePerformanceSampleBasis ResolveScatterSecondaryBasis(MousePerformancePlotType plotType)
    {
        return plotType == MousePerformancePlotType.XYVelocityVsTime ? MousePerformanceSampleBasis.RelativeMotion : ResolveScatterPrimaryBasis(plotType);
    }

    private static MousePerformanceSampleBasis ResolveTrendPrimaryBasis(MousePerformancePlotType plotType)
    {
        return plotType == MousePerformancePlotType.FrequencyVsTime
            ? MousePerformanceSampleBasis.ReportTiming
            : MousePerformanceSampleBasis.TrendEstimate;
    }

    private static MousePerformanceSampleBasis ResolveTrendSecondaryBasis(MousePerformancePlotType plotType)
    {
        return ResolveTrendPrimaryBasis(plotType);
    }

    private static IReadOnlyList<MousePerformanceChartPoint> ResolveTimingTrendPoints(MousePerformanceTimingSeriesSamples timingSamples)
    {
        return timingSamples?.DisplayPoints ?? Array.Empty<MousePerformanceChartPoint>();
    }

    public static IReadOnlyList<MousePerformanceChartGapSource> CreateReportGapSources(IReadOnlyList<MousePerformanceEvent> events, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis)
    {
        if (events == null || events.Count <= 1 || startIndex >= endIndex)
        {
            return Array.Empty<MousePerformanceChartGapSource>();
        }

        return new[] { new MousePerformanceChartGapSource(MousePerformanceChartDatasetSlot.Baseline, () => BuildReportGapIntervals(events, startIndex, endIndex, timeBasis)) };
    }

    private static IReadOnlyList<MousePerformanceChartGapInterval> BuildReportGapIntervals(IReadOnlyList<MousePerformanceEvent> events, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis)
    {
        if (events == null || events.Count <= 1 || startIndex >= endIndex)
        {
            return Array.Empty<MousePerformanceChartGapInterval>();
        }

        int clampedStartIndex = Math.Max(0, Math.Min(startIndex, events.Count - 1));
        int clampedEndIndex = Math.Max(clampedStartIndex, Math.Min(endIndex, events.Count - 1));
        List<MousePerformanceChartGapInterval> intervals = new List<MousePerformanceChartGapInterval>(Math.Max(0, clampedEndIndex - Math.Max(1, clampedStartIndex) + 1));
        int firstIntervalEventIndex = Math.Max(1, clampedStartIndex);
        for (int eventIndex = firstIntervalEventIndex; eventIndex <= clampedEndIndex; eventIndex++)
        {
            MousePerformanceEvent previousEvent = events[eventIndex - 1];
            MousePerformanceEvent currentEvent = events[eventIndex];
            if (previousEvent == null || currentEvent == null)
            {
                continue;
            }

            if (timeBasis == MousePerformanceTimeBasis.RawCapture && previousEvent.SessionSegmentId != currentEvent.SessionSegmentId)
            {
                continue;
            }

            long previousTicks = ResolveTimeTicks(previousEvent, timeBasis);
            long currentTicks = ResolveTimeTicks(currentEvent, timeBasis);
            if (currentTicks <= previousTicks)
            {
                continue;
            }

            double previousX = MousePerformanceSessionAnalysisIndex.TicksToMilliseconds(previousTicks);
            double currentX = MousePerformanceSessionAnalysisIndex.TicksToMilliseconds(currentTicks);
            intervals.Add(new MousePerformanceChartGapInterval(previousX, currentX));
        }

        return intervals.Count == 0 ? Array.Empty<MousePerformanceChartGapInterval>() : intervals.ToArray();
    }

    private static long ResolveTimeTicks(MousePerformanceEvent mouseEvent, MousePerformanceTimeBasis timeBasis)
    {
        return mouseEvent == null ? 0L : timeBasis == MousePerformanceTimeBasis.RawCapture ? mouseEvent.RawRelativeTicks : mouseEvent.LogicalTicks;
    }

    private static void ThrowIfCancellationRequested(CancellationToken cancellationToken, int iteration)
    {
        if (cancellationToken.CanBeCanceled && (Math.Max(0, iteration) & CancellationCheckMask) == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}

