using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClickSyncMouseTester.Services;

internal sealed class MousePerformanceChartAnalysisCache
{
    private struct FrameCacheKey
    {
        public string SessionIdentity { get; }

        public int SessionRevision { get; }

        public MousePerformancePlotType PlotType { get; }

        public int StartIndex { get; }

        public int EndIndex { get; }

        public MousePerformanceTimeBasis TimeBasis { get; }

        public double? EffectiveCpi { get; }

        public FrameCacheKey(string sessionIdentity, int sessionRevision, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, double? effectiveCpi)
        {
            SessionIdentity = sessionIdentity ?? string.Empty;
            SessionRevision = Math.Max(0, sessionRevision);
            PlotType = plotType;
            StartIndex = Math.Max(0, startIndex);
            EndIndex = Math.Max(0, endIndex);
            TimeBasis = timeBasis;
            EffectiveCpi = effectiveCpi;
        }
    }

    private struct ComparisonHistogramFrameCacheKey
    {
        public string DatasetSignature { get; }

        public MousePerformancePlotType PlotType { get; }

        public int StartIndex { get; }

        public int EndIndex { get; }

        public MousePerformanceTimeBasis TimeBasis { get; }

        public ComparisonHistogramFrameCacheKey(string datasetSignature, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis)
        {
            DatasetSignature = datasetSignature ?? string.Empty;
            PlotType = plotType;
            StartIndex = Math.Max(0, startIndex);
            EndIndex = Math.Max(0, endIndex);
            TimeBasis = timeBasis;
        }
    }

    private struct VelocityStatisticsCacheKey
    {
        public string SessionIdentity { get; }

        public int SessionRevision { get; }

        public MousePerformancePlotType PlotType { get; }

        public int StartIndex { get; }

        public int EndIndex { get; }

        public MousePerformanceTimeBasis TimeBasis { get; }

        public double? EffectiveCpi { get; }

        public VelocityStatisticsCacheKey(string sessionIdentity, int sessionRevision, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, double? effectiveCpi)
        {
            SessionIdentity = sessionIdentity ?? string.Empty;
            SessionRevision = Math.Max(0, sessionRevision);
            PlotType = plotType;
            StartIndex = Math.Max(0, startIndex);
            EndIndex = Math.Max(0, endIndex);
            TimeBasis = timeBasis;
            EffectiveCpi = effectiveCpi;
        }
    }

    private struct ResidualDispersionCacheKey
    {
        public string SessionIdentity { get; }

        public int SessionRevision { get; }

        public MousePerformancePlotType PlotType { get; }

        public int StartIndex { get; }

        public int EndIndex { get; }

        public MousePerformanceTimeBasis TimeBasis { get; }

        public ResidualDispersionCacheKey(string sessionIdentity, int sessionRevision, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis)
        {
            SessionIdentity = sessionIdentity ?? string.Empty;
            SessionRevision = Math.Max(0, sessionRevision);
            PlotType = plotType;
            StartIndex = Math.Max(0, startIndex);
            EndIndex = Math.Max(0, endIndex);
            TimeBasis = timeBasis;
        }
    }

    private struct TimingStatisticsCacheKey
    {
        public string SessionIdentity { get; }

        public int SessionRevision { get; }

        public MousePerformancePlotType PlotType { get; }

        public int StartIndex { get; }

        public int EndIndex { get; }

        public MousePerformanceTimeBasis TimeBasis { get; }

        public TimingStatisticsCacheKey(string sessionIdentity, int sessionRevision, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis)
        {
            SessionIdentity = sessionIdentity ?? string.Empty;
            SessionRevision = Math.Max(0, sessionRevision);
            PlotType = plotType;
            StartIndex = Math.Max(0, startIndex);
            EndIndex = Math.Max(0, endIndex);
            TimeBasis = timeBasis;
        }
    }

    private struct GapSourceCacheKey
    {
        public string SessionIdentity { get; }

        public int SessionRevision { get; }

        public int StartIndex { get; }

        public int EndIndex { get; }

        public MousePerformanceTimeBasis TimeBasis { get; }

        public GapSourceCacheKey(string sessionIdentity, int sessionRevision, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis)
        {
            SessionIdentity = sessionIdentity ?? string.Empty;
            SessionRevision = Math.Max(0, sessionRevision);
            StartIndex = Math.Max(0, startIndex);
            EndIndex = Math.Max(0, endIndex);
            TimeBasis = timeBasis;
        }
    }

    private const int MaxFrameCacheEntries = 48;

    private const int MaxComparisonHistogramFrameCacheEntries = 24;

    private const int MaxGapSourceCacheEntries = 64;

    private const int MaxVelocityStatisticsCacheEntries = 128;

    private const int MaxResidualDispersionCacheEntries = 128;

    private const int MaxTimingStatisticsCacheEntries = 128;

    private static readonly MousePerformanceChartAnalysisCache _instance = new MousePerformanceChartAnalysisCache();

    private readonly object _syncRoot;

    private readonly SemaphoreSlim _warmupGate;

    private readonly Dictionary<FrameCacheKey, MousePerformanceChartRenderFrame> _frameCache;

    private readonly Queue<FrameCacheKey> _frameCacheOrder;

    private readonly Dictionary<FrameCacheKey, Task<MousePerformanceChartRenderFrame>> _frameTasks;

    private readonly Dictionary<ComparisonHistogramFrameCacheKey, MousePerformanceChartRenderFrame> _comparisonHistogramFrameCache;

    private readonly Queue<ComparisonHistogramFrameCacheKey> _comparisonHistogramFrameCacheOrder;

    private readonly Dictionary<ComparisonHistogramFrameCacheKey, Task<MousePerformanceChartRenderFrame>> _comparisonHistogramFrameTasks;

    private readonly Dictionary<GapSourceCacheKey, IReadOnlyList<MousePerformanceChartGapSource>> _gapSourceCache;

    private readonly Queue<GapSourceCacheKey> _gapSourceCacheOrder;

    private readonly Dictionary<VelocityStatisticsCacheKey, MousePerformanceVelocityStatisticsSummary> _velocityStatisticsCache;

    private readonly Queue<VelocityStatisticsCacheKey> _velocityStatisticsCacheOrder;

    private readonly Dictionary<VelocityStatisticsCacheKey, Task<MousePerformanceVelocityStatisticsSummary>> _velocityStatisticsTasks;

    private readonly Dictionary<ResidualDispersionCacheKey, MousePerformanceResidualDispersionSummary> _residualDispersionCache;

    private readonly Queue<ResidualDispersionCacheKey> _residualDispersionCacheOrder;

    private readonly Dictionary<ResidualDispersionCacheKey, Task<MousePerformanceResidualDispersionSummary>> _residualDispersionTasks;

    private readonly Dictionary<TimingStatisticsCacheKey, MousePerformanceTimingStatisticsSummary> _timingStatisticsCache;

    private readonly Queue<TimingStatisticsCacheKey> _timingStatisticsCacheOrder;

    private readonly Dictionary<TimingStatisticsCacheKey, Task<MousePerformanceTimingStatisticsSummary>> _timingStatisticsTasks;

    public static MousePerformanceChartAnalysisCache Instance => _instance;

    private MousePerformanceChartAnalysisCache()
    {
        _syncRoot = new object();
        _warmupGate = new SemaphoreSlim(1, 1);
        _frameCache = new Dictionary<FrameCacheKey, MousePerformanceChartRenderFrame>();
        _frameCacheOrder = new Queue<FrameCacheKey>();
        _frameTasks = new Dictionary<FrameCacheKey, Task<MousePerformanceChartRenderFrame>>();
        _comparisonHistogramFrameCache = new Dictionary<ComparisonHistogramFrameCacheKey, MousePerformanceChartRenderFrame>();
        _comparisonHistogramFrameCacheOrder = new Queue<ComparisonHistogramFrameCacheKey>();
        _comparisonHistogramFrameTasks = new Dictionary<ComparisonHistogramFrameCacheKey, Task<MousePerformanceChartRenderFrame>>();
        _gapSourceCache = new Dictionary<GapSourceCacheKey, IReadOnlyList<MousePerformanceChartGapSource>>();
        _gapSourceCacheOrder = new Queue<GapSourceCacheKey>();
        _velocityStatisticsCache = new Dictionary<VelocityStatisticsCacheKey, MousePerformanceVelocityStatisticsSummary>();
        _velocityStatisticsCacheOrder = new Queue<VelocityStatisticsCacheKey>();
        _velocityStatisticsTasks = new Dictionary<VelocityStatisticsCacheKey, Task<MousePerformanceVelocityStatisticsSummary>>();
        _residualDispersionCache = new Dictionary<ResidualDispersionCacheKey, MousePerformanceResidualDispersionSummary>();
        _residualDispersionCacheOrder = new Queue<ResidualDispersionCacheKey>();
        _residualDispersionTasks = new Dictionary<ResidualDispersionCacheKey, Task<MousePerformanceResidualDispersionSummary>>();
        _timingStatisticsCache = new Dictionary<TimingStatisticsCacheKey, MousePerformanceTimingStatisticsSummary>();
        _timingStatisticsCacheOrder = new Queue<TimingStatisticsCacheKey>();
        _timingStatisticsTasks = new Dictionary<TimingStatisticsCacheKey, Task<MousePerformanceTimingStatisticsSummary>>();
    }

    public bool TryGetFrame(MousePerformanceSessionArchive session, MousePerformancePlotType plotType, int startIndex, int endIndex, bool showStem, bool showLines, MousePerformanceTimeBasis timeBasis, ref MousePerformanceChartRenderFrame frame)
    {
        frame = null;
        FrameCacheKey cacheKey = default;
        if (!TryCreateFrameCacheKey(session, plotType, startIndex, endIndex, timeBasis, ref cacheKey))
        {
            return false;
        }
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            if (_frameCache.TryGetValue(cacheKey, out MousePerformanceChartRenderFrame cachedDataFrame))
            {
                frame = CreatePresentationFrame(cachedDataFrame, showStem, showLines);
                return true;
            }
            return false;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    public bool TryGetVelocityStatistics(MousePerformanceSessionArchive session, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, ref MousePerformanceVelocityStatisticsSummary statistics)
    {
        statistics = null;
        VelocityStatisticsCacheKey cacheKey = default;
        if (!TryCreateVelocityStatisticsCacheKey(session, plotType, startIndex, endIndex, timeBasis, ref cacheKey))
        {
            return false;
        }
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            return _velocityStatisticsCache.TryGetValue(cacheKey, out statistics);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    public bool TryGetResidualDispersion(MousePerformanceSessionArchive session, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, ref MousePerformanceResidualDispersionSummary statistics)
    {
        statistics = null;
        ResidualDispersionCacheKey cacheKey = default;
        if (!TryCreateResidualDispersionCacheKey(session, plotType, startIndex, endIndex, timeBasis, ref cacheKey))
        {
            return false;
        }
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            return _residualDispersionCache.TryGetValue(cacheKey, out statistics);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    public bool TryGetTimingStatistics(MousePerformanceSessionArchive session, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, ref MousePerformanceTimingStatisticsSummary statistics)
    {
        statistics = null;
        TimingStatisticsCacheKey cacheKey = default;
        if (!TryCreateTimingStatisticsCacheKey(session, plotType, startIndex, endIndex, timeBasis, ref cacheKey))
        {
            return false;
        }
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            return _timingStatisticsCache.TryGetValue(cacheKey, out statistics);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    public Task<MousePerformanceChartRenderFrame> GetOrBuildFrameAsync(MousePerformanceSessionArchive session, MousePerformancePlotType plotType, int startIndex, int endIndex, bool showStem, bool showLines, MousePerformanceTimeBasis timeBasis, CancellationToken cancellationToken)
    {
        FrameCacheKey cacheKey = default;
        if (!TryCreateFrameCacheKey(session, plotType, startIndex, endIndex, timeBasis, ref cacheKey))
        {
            return Task.FromResult(MousePerformanceEngine.CreateChartRenderFrame(session?.Snapshot ?? null, plotType, startIndex, endIndex, showStem, showLines, timeBasis));
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<MousePerformanceChartRenderFrame>(cancellationToken);
        }
        MousePerformanceChartRenderFrame cachedFrame = null;
        Task<MousePerformanceChartRenderFrame> frameBuildTask = null;
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            if (_frameCache.TryGetValue(cacheKey, out cachedFrame))
            {
                return Task.FromResult(CreatePresentationFrame(cachedFrame, showStem, showLines));
            }
            if (cancellationToken.CanBeCanceled)
            {
                frameBuildTask = BuildFrameAsync(cacheKey, session.Snapshot, plotType, startIndex, endIndex, showStem: false, showLines: false, timeBasis, ResolveCachedGapSources(cacheKey, session.Snapshot), cancellationToken);
                return CreatePresentationFrameAsync(frameBuildTask, showStem, showLines, cancellationToken);
            }
            if (!_frameTasks.TryGetValue(cacheKey, out frameBuildTask))
            {
                frameBuildTask = BuildFrameAsync(cacheKey, session.Snapshot, plotType, startIndex, endIndex, showStem: false, showLines: false, timeBasis, ResolveCachedGapSources(cacheKey, session.Snapshot), CancellationToken.None, ownsTaskRegistration: true);
                _frameTasks[cacheKey] = frameBuildTask;
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
        return CreatePresentationFrameAsync(frameBuildTask, showStem, showLines, cancellationToken);
    }

    public Task<MousePerformanceChartRenderFrame> GetOrBuildComparisonHistogramFrameAsync(IReadOnlyList<MousePerformanceChartDatasetSession> sessions, MousePerformancePlotType plotType, int startIndex, int endIndex, bool showStem, bool showLines, MousePerformanceTimeBasis timeBasis, CancellationToken cancellationToken)
    {
        IReadOnlyList<MousePerformanceHistogramDataset> datasets = CreateHistogramDatasets(sessions);
        ComparisonHistogramFrameCacheKey cacheKey = default;
        ComparisonHistogramFrameCacheKey? cacheKeyOverride = TryCreateComparisonHistogramFrameCacheKey(sessions, plotType, startIndex, endIndex, timeBasis, ref cacheKey) ? cacheKey : null;
        return GetOrBuildComparisonHistogramFrameAsync(datasets, plotType, startIndex, endIndex, showStem, showLines, timeBasis, cancellationToken, cacheKeyOverride);
    }

    private Task<MousePerformanceChartRenderFrame> GetOrBuildComparisonHistogramFrameAsync(IReadOnlyList<MousePerformanceHistogramDataset> datasets, MousePerformancePlotType plotType, int startIndex, int endIndex, bool showStem, bool showLines, MousePerformanceTimeBasis timeBasis, CancellationToken cancellationToken, ComparisonHistogramFrameCacheKey? cacheKey)
    {
        if (!cacheKey.HasValue)
        {
            return Task.FromResult(MousePerformanceChartFrameBuilder.CreateComparisonHistogramRenderFrame(datasets, plotType, startIndex, endIndex, showStem, showLines, timeBasis, cancellationToken: cancellationToken));
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<MousePerformanceChartRenderFrame>(cancellationToken);
        }

        MousePerformanceChartRenderFrame cachedFrame = null;
        Task<MousePerformanceChartRenderFrame> frameBuildTask = null;
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            if (_comparisonHistogramFrameCache.TryGetValue(cacheKey.Value, out cachedFrame))
            {
                return Task.FromResult(CreatePresentationFrame(cachedFrame, showStem, showLines));
            }
            if (cancellationToken.CanBeCanceled)
            {
                frameBuildTask = BuildComparisonHistogramFrameAsync(cacheKey.Value, datasets, plotType, startIndex, endIndex, showStem: false, showLines: false, timeBasis, cancellationToken);
                return CreatePresentationFrameAsync(frameBuildTask, showStem, showLines, cancellationToken);
            }
            if (!_comparisonHistogramFrameTasks.TryGetValue(cacheKey.Value, out frameBuildTask))
            {
                frameBuildTask = BuildComparisonHistogramFrameAsync(cacheKey.Value, datasets, plotType, startIndex, endIndex, showStem: false, showLines: false, timeBasis, CancellationToken.None, ownsTaskRegistration: true);
                _comparisonHistogramFrameTasks[cacheKey.Value] = frameBuildTask;
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }

        return CreatePresentationFrameAsync(frameBuildTask, showStem, showLines, cancellationToken);
    }

    private static async Task<MousePerformanceChartRenderFrame> CreatePresentationFrameAsync(Task<MousePerformanceChartRenderFrame> frameTask, bool showStem, bool showLines, CancellationToken cancellationToken)
    {
        MousePerformanceChartRenderFrame dataFrame = cancellationToken.CanBeCanceled
            ? await frameTask.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false)
            : await frameTask.ConfigureAwait(continueOnCapturedContext: false);
        return CreatePresentationFrame(dataFrame, showStem, showLines);
    }

    private static MousePerformanceChartRenderFrame CreatePresentationFrame(MousePerformanceChartRenderFrame dataFrame, bool showStem, bool showLines)
    {
        if (dataFrame == null)
        {
            return dataFrame;
        }

        MousePerformancePlotPresentationPolicy presentationPolicy = MousePerformancePlotPresentationPolicy.Resolve(dataFrame.PlotType);
        MousePerformancePlotPresentationState presentationState = presentationPolicy.ResolveState(showStem, showLines);
        if (dataFrame.ShowStem == presentationState.ShowStem && dataFrame.ShowLines == presentationState.ShowLines)
        {
            return dataFrame;
        }

        IReadOnlyList<MousePerformanceChartSeries> series = dataFrame.Series;
        if (dataFrame.IsAvailable && dataFrame.Series != null && MousePerformancePlotTraits.IsHistogramPlot(dataFrame.PlotType))
        {
            series = dataFrame.Series;
        }
        else if (dataFrame.IsAvailable && dataFrame.Series != null && presentationPolicy.UsesContinuousEstimateLine)
        {
            series = CreateContinuousEstimatedPresentationSeries(dataFrame);
        }
        else if (dataFrame.IsAvailable && dataFrame.PlotType != MousePerformancePlotType.XVsY && dataFrame.Series != null)
        {
            List<MousePerformanceChartSeries> presentationSeries = new List<MousePerformanceChartSeries>(dataFrame.Series.Count + 2);
            MousePerformanceChartSeries scatterPrimary = ResolveSeries(dataFrame, MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Primary);
            MousePerformanceChartSeries scatterSecondary = ResolveSeries(dataFrame, MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Secondary);

            if (presentationPolicy.ShouldShowConnectedRawLine(presentationState.ShowLines))
            {
                if (scatterPrimary?.Points?.Count > 1)
                {
                    presentationSeries.Add(new MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Accent, scatterPrimary.Points, scatterPrimary.DatasetSlot, scatterPrimary.XOffset, scatterPrimary.SampleBasis));
                }
                if (scatterSecondary?.Points?.Count > 1)
                {
                    presentationSeries.Add(new MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Secondary, scatterSecondary.Points, scatterSecondary.DatasetSlot, scatterSecondary.XOffset, scatterSecondary.SampleBasis));
                }
            }
            else if (presentationPolicy.ShouldUseTrendLine(presentationState.ShowLines))
            {
                MousePerformanceChartSeries trendPrimary = ResolveSeries(dataFrame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Primary);
                MousePerformanceChartSeries trendSecondary = ResolveSeries(dataFrame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Secondary);
                if (trendPrimary?.Points?.Count > 1)
                {
                    presentationSeries.Add(new MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Primary, trendPrimary.Points, trendPrimary.DatasetSlot, trendPrimary.XOffset, trendPrimary.SampleBasis));
                }
                if (trendSecondary?.Points?.Count > 1)
                {
                    presentationSeries.Add(new MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Secondary, trendSecondary.Points, trendSecondary.DatasetSlot, trendSecondary.XOffset, trendSecondary.SampleBasis));
                }
            }

            if (presentationState.ShowStem)
            {
                if (scatterPrimary?.Points?.Count > 0)
                {
                    presentationSeries.Add(new MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Stem, MousePerformanceChartSeriesPalette.Primary, scatterPrimary.Points, scatterPrimary.DatasetSlot, scatterPrimary.XOffset, scatterPrimary.SampleBasis));
                }
                if (scatterSecondary?.Points?.Count > 0)
                {
                    presentationSeries.Add(new MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Stem, MousePerformanceChartSeriesPalette.Secondary, scatterSecondary.Points, scatterSecondary.DatasetSlot, scatterSecondary.XOffset, scatterSecondary.SampleBasis));
                }
            }

            if (scatterPrimary != null)
            {
                presentationSeries.Add(scatterPrimary);
            }
            if (scatterSecondary != null)
            {
                presentationSeries.Add(scatterSecondary);
            }

            series = presentationSeries.ToArray();
        }

        return new MousePerformanceChartRenderFrame(dataFrame.PlotType, dataFrame.Title, dataFrame.Subtitle, dataFrame.Description, dataFrame.XAxisTitle, dataFrame.YAxisTitle, dataFrame.IsAvailable, dataFrame.Message, dataFrame.StartIndex, dataFrame.EndIndex, presentationState.ShowStem, presentationState.ShowLines, dataFrame.TimeBasis, dataFrame.XMinimum, dataFrame.XMaximum, dataFrame.YMinimum, dataFrame.YMaximum, series, dataFrame.HasComparisonDatasets, dataFrame.GapSources);
    }

    private static IReadOnlyList<MousePerformanceChartSeries> CreateContinuousEstimatedPresentationSeries(MousePerformanceChartRenderFrame dataFrame)
    {
        List<MousePerformanceChartSeries> presentationSeries = new List<MousePerformanceChartSeries>(dataFrame.Series.Count);
        foreach (MousePerformanceChartSeries series in dataFrame.Series)
        {
            if (series != null && series.Kind == MousePerformanceChartSeriesKind.Line && series.Points != null && series.Points.Count > 1)
            {
                presentationSeries.Add(series);
            }
        }
        return presentationSeries.ToArray();
    }

    public Task<MousePerformanceVelocityStatisticsSummary> GetOrBuildVelocityStatisticsAsync(MousePerformanceSessionArchive session, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, CancellationToken cancellationToken)
    {
        VelocityStatisticsCacheKey cacheKey = default;
        if (!TryCreateVelocityStatisticsCacheKey(session, plotType, startIndex, endIndex, timeBasis, ref cacheKey))
        {
            return Task.FromResult<MousePerformanceVelocityStatisticsSummary>(null);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<MousePerformanceVelocityStatisticsSummary>(cancellationToken);
        }
        MousePerformanceVelocityStatisticsSummary cachedStatistics = null;
        Task<MousePerformanceVelocityStatisticsSummary> statisticsBuildTask = null;
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            if (_velocityStatisticsCache.TryGetValue(cacheKey, out cachedStatistics))
            {
                return Task.FromResult(cachedStatistics);
            }
            if (cancellationToken.CanBeCanceled)
            {
                return BuildVelocityStatisticsAsync(cacheKey, session.Snapshot, plotType, startIndex, endIndex, timeBasis, cancellationToken);
            }
            if (!_velocityStatisticsTasks.TryGetValue(cacheKey, out statisticsBuildTask))
            {
                statisticsBuildTask = BuildVelocityStatisticsAsync(cacheKey, session.Snapshot, plotType, startIndex, endIndex, timeBasis, CancellationToken.None, ownsTaskRegistration: true);
                _velocityStatisticsTasks[cacheKey] = statisticsBuildTask;
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
        if (!cancellationToken.CanBeCanceled)
        {
            return statisticsBuildTask;
        }
        return statisticsBuildTask.WaitAsync(cancellationToken);
    }

    public Task<MousePerformanceResidualDispersionSummary> GetOrBuildResidualDispersionAsync(MousePerformanceSessionArchive session, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, CancellationToken cancellationToken)
    {
        ResidualDispersionCacheKey cacheKey = default;
        if (!TryCreateResidualDispersionCacheKey(session, plotType, startIndex, endIndex, timeBasis, ref cacheKey))
        {
            return Task.FromResult<MousePerformanceResidualDispersionSummary>(null);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<MousePerformanceResidualDispersionSummary>(cancellationToken);
        }
        MousePerformanceResidualDispersionSummary cachedDispersion = null;
        Task<MousePerformanceResidualDispersionSummary> dispersionBuildTask = null;
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            if (_residualDispersionCache.TryGetValue(cacheKey, out cachedDispersion))
            {
                return Task.FromResult(cachedDispersion);
            }
            if (cancellationToken.CanBeCanceled)
            {
                return BuildResidualDispersionAsync(cacheKey, session.Snapshot, plotType, startIndex, endIndex, timeBasis, cancellationToken);
            }
            if (!_residualDispersionTasks.TryGetValue(cacheKey, out dispersionBuildTask))
            {
                dispersionBuildTask = BuildResidualDispersionAsync(cacheKey, session.Snapshot, plotType, startIndex, endIndex, timeBasis, CancellationToken.None, ownsTaskRegistration: true);
                _residualDispersionTasks[cacheKey] = dispersionBuildTask;
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
        if (!cancellationToken.CanBeCanceled)
        {
            return dispersionBuildTask;
        }
        return dispersionBuildTask.WaitAsync(cancellationToken);
    }

    public Task<MousePerformanceTimingStatisticsSummary> GetOrBuildTimingStatisticsAsync(MousePerformanceSessionArchive session, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, CancellationToken cancellationToken)
    {
        TimingStatisticsCacheKey cacheKey = default;
        if (!TryCreateTimingStatisticsCacheKey(session, plotType, startIndex, endIndex, timeBasis, ref cacheKey))
        {
            return Task.FromResult<MousePerformanceTimingStatisticsSummary>(null);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<MousePerformanceTimingStatisticsSummary>(cancellationToken);
        }
        MousePerformanceTimingStatisticsSummary cachedTimingStatistics = null;
        Task<MousePerformanceTimingStatisticsSummary> timingStatisticsBuildTask = null;
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            if (_timingStatisticsCache.TryGetValue(cacheKey, out cachedTimingStatistics))
            {
                return Task.FromResult(cachedTimingStatistics);
            }
            if (cancellationToken.CanBeCanceled)
            {
                return BuildTimingStatisticsAsync(cacheKey, session.Snapshot, plotType, startIndex, endIndex, timeBasis, cancellationToken);
            }
            if (!_timingStatisticsTasks.TryGetValue(cacheKey, out timingStatisticsBuildTask))
            {
                timingStatisticsBuildTask = BuildTimingStatisticsAsync(cacheKey, session.Snapshot, plotType, startIndex, endIndex, timeBasis, CancellationToken.None, ownsTaskRegistration: true);
                _timingStatisticsTasks[cacheKey] = timingStatisticsBuildTask;
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
        if (!cancellationToken.CanBeCanceled)
        {
            return timingStatisticsBuildTask;
        }
        return timingStatisticsBuildTask.WaitAsync(cancellationToken);
    }

    public void WarmSessionAfterStop(MousePerformanceSessionArchive session, MousePerformancePlotType preferredPlotType, bool showStem, bool showLines, MousePerformanceTimeBasis timeBasis = MousePerformanceTimeBasis.RawCapture)
    {
        if (session != null && session.HasData && session.Snapshot != null && session.Snapshot.Status != MousePerformanceSessionStatus.Collecting)
        {
            int safeEndIndex = Math.Max(0, session.Snapshot.EventCount - 1);
            _ = RunSessionWarmupAsync(session, preferredPlotType, showStem, showLines, timeBasis, safeEndIndex);
        }
    }

    private async Task RunSessionWarmupAsync(MousePerformanceSessionArchive session, MousePerformancePlotType preferredPlotType, bool showStem, bool showLines, MousePerformanceTimeBasis timeBasis, int safeEndIndex)
    {
        await _warmupGate.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            foreach (MousePerformancePlotType warmupPlotType in ResolveWarmupPlotTypes(preferredPlotType, session.Snapshot.CanComputeVelocity))
            {
                await GetOrBuildFrameAsync(session, warmupPlotType, 0, safeEndIndex, showStem, showLines, timeBasis, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
            }
            if (session.Snapshot.CanComputeVelocity)
            {
                foreach (MousePerformancePlotType velocityPlotType in ResolveVelocityWarmupPlotTypes())
                {
                    await GetOrBuildVelocityStatisticsAsync(session, velocityPlotType, 0, safeEndIndex, timeBasis, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
                }
            }
            if (IsCountPlot(preferredPlotType))
            {
                await GetOrBuildResidualDispersionAsync(session, preferredPlotType, 0, safeEndIndex, timeBasis, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
            }
            if (IsTimingPlot(preferredPlotType))
            {
                await GetOrBuildTimingStatisticsAsync(session, preferredPlotType, 0, safeEndIndex, timeBasis, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            _warmupGate.Release();
        }
    }

    private async Task<MousePerformanceChartRenderFrame> BuildFrameAsync(FrameCacheKey cacheKey, MousePerformanceSnapshot snapshot, MousePerformancePlotType plotType, int startIndex, int endIndex, bool showStem, bool showLines, MousePerformanceTimeBasis timeBasis, IReadOnlyList<MousePerformanceChartGapSource> gapSources, CancellationToken cancellationToken, bool ownsTaskRegistration = false)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            MousePerformanceChartRenderFrame mousePerformanceChartRenderFrame = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return MousePerformanceEngine.CreateChartRenderFrame(snapshot, plotType, startIndex, endIndex, showStem, showLines, timeBasis, null, cancellationToken, gapSources);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            object syncRoot = _syncRoot;
            bool lockTaken = false;
            try
            {
                Monitor.Enter(syncRoot, ref lockTaken);
                StoreFrameCacheEntry(cacheKey, mousePerformanceChartRenderFrame);
                return mousePerformanceChartRenderFrame;
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(syncRoot);
                }
            }
        }
        finally
        {
            if (ownsTaskRegistration)
            {
                object syncRoot2 = _syncRoot;
                bool lockTaken2 = false;
                try
                {
                    Monitor.Enter(syncRoot2, ref lockTaken2);
                    _frameTasks.Remove(cacheKey);
                }
                finally
                {
                    if (lockTaken2)
                    {
                        Monitor.Exit(syncRoot2);
                    }
                }
            }
        }
    }

    private async Task<MousePerformanceChartRenderFrame> BuildComparisonHistogramFrameAsync(ComparisonHistogramFrameCacheKey cacheKey, IReadOnlyList<MousePerformanceHistogramDataset> datasets, MousePerformancePlotType plotType, int startIndex, int endIndex, bool showStem, bool showLines, MousePerformanceTimeBasis timeBasis, CancellationToken cancellationToken, bool ownsTaskRegistration = false)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            MousePerformanceChartRenderFrame frame = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return MousePerformanceChartFrameBuilder.CreateComparisonHistogramRenderFrame(datasets, plotType, startIndex, endIndex, showStem, showLines, timeBasis, cancellationToken: cancellationToken);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            object syncRoot = _syncRoot;
            bool lockTaken = false;
            try
            {
                Monitor.Enter(syncRoot, ref lockTaken);
                StoreComparisonHistogramFrameCacheEntry(cacheKey, frame);
                return frame;
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(syncRoot);
                }
            }
        }
        finally
        {
            if (ownsTaskRegistration)
            {
                object syncRoot2 = _syncRoot;
                bool lockTaken2 = false;
                try
                {
                    Monitor.Enter(syncRoot2, ref lockTaken2);
                    _comparisonHistogramFrameTasks.Remove(cacheKey);
                }
                finally
                {
                    if (lockTaken2)
                    {
                        Monitor.Exit(syncRoot2);
                    }
                }
            }
        }
    }

    private async Task<MousePerformanceVelocityStatisticsSummary> BuildVelocityStatisticsAsync(VelocityStatisticsCacheKey cacheKey, MousePerformanceSnapshot snapshot, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, CancellationToken cancellationToken, bool ownsTaskRegistration = false)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            MousePerformanceChartRenderFrame frame = null;
            if (TryGetCompatibleVelocityFrame(cacheKey, ref frame))
            {
                MousePerformanceVelocityStatisticsSummary mousePerformanceVelocityStatisticsSummary = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return MousePerformanceStatisticsCalculator.ComputeVelocityStatistics(frame);
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                object syncRoot = _syncRoot;
                bool lockTaken = false;
                try
                {
                    Monitor.Enter(syncRoot, ref lockTaken);
                    StoreVelocityStatisticsCacheEntry(cacheKey, mousePerformanceVelocityStatisticsSummary);
                }
                finally
                {
                    if (lockTaken)
                    {
                        Monitor.Exit(syncRoot);
                    }
                }
                return mousePerformanceVelocityStatisticsSummary;
            }
            MousePerformanceVelocityStatisticsSummary mousePerformanceVelocityStatisticsSummary2 = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return MousePerformanceStatisticsCalculator.ComputeVelocityStatistics(snapshot, plotType, startIndex, endIndex, timeBasis, cancellationToken);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            object syncRoot2 = _syncRoot;
            bool lockTaken2 = false;
            try
            {
                Monitor.Enter(syncRoot2, ref lockTaken2);
                StoreVelocityStatisticsCacheEntry(cacheKey, mousePerformanceVelocityStatisticsSummary2);
            }
            finally
            {
                if (lockTaken2)
                {
                    Monitor.Exit(syncRoot2);
                }
            }
            return mousePerformanceVelocityStatisticsSummary2;
        }
        finally
        {
            if (ownsTaskRegistration)
            {
                object syncRoot3 = _syncRoot;
                bool lockTaken3 = false;
                try
                {
                    Monitor.Enter(syncRoot3, ref lockTaken3);
                    _velocityStatisticsTasks.Remove(cacheKey);
                }
                finally
                {
                    if (lockTaken3)
                    {
                        Monitor.Exit(syncRoot3);
                    }
                }
            }
        }
    }

    private async Task<MousePerformanceTimingStatisticsSummary> BuildTimingStatisticsAsync(TimingStatisticsCacheKey cacheKey, MousePerformanceSnapshot snapshot, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, CancellationToken cancellationToken, bool ownsTaskRegistration = false)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            MousePerformanceChartRenderFrame frame = null;
            if (TryGetCompatibleTimingFrame(cacheKey, ref frame))
            {
                MousePerformanceTimingStatisticsSummary mousePerformanceTimingStatisticsSummary = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return MousePerformanceStatisticsCalculator.ComputeTimingStatistics(frame);
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                object syncRoot = _syncRoot;
                bool lockTaken = false;
                try
                {
                    Monitor.Enter(syncRoot, ref lockTaken);
                    StoreTimingStatisticsCacheEntry(cacheKey, mousePerformanceTimingStatisticsSummary);
                }
                finally
                {
                    if (lockTaken)
                    {
                        Monitor.Exit(syncRoot);
                    }
                }
                return mousePerformanceTimingStatisticsSummary;
            }
            MousePerformanceTimingStatisticsSummary mousePerformanceTimingStatisticsSummary2 = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return MousePerformanceStatisticsCalculator.ComputeTimingStatistics(snapshot, plotType, startIndex, endIndex, timeBasis, cancellationToken);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            object syncRoot2 = _syncRoot;
            bool lockTaken2 = false;
            try
            {
                Monitor.Enter(syncRoot2, ref lockTaken2);
                StoreTimingStatisticsCacheEntry(cacheKey, mousePerformanceTimingStatisticsSummary2);
            }
            finally
            {
                if (lockTaken2)
                {
                    Monitor.Exit(syncRoot2);
                }
            }
            return mousePerformanceTimingStatisticsSummary2;
        }
        finally
        {
            if (ownsTaskRegistration)
            {
                object syncRoot3 = _syncRoot;
                bool lockTaken3 = false;
                try
                {
                    Monitor.Enter(syncRoot3, ref lockTaken3);
                    _timingStatisticsTasks.Remove(cacheKey);
                }
                finally
                {
                    if (lockTaken3)
                    {
                        Monitor.Exit(syncRoot3);
                    }
                }
            }
        }
    }

    private bool TryGetCompatibleVelocityFrame(VelocityStatisticsCacheKey cacheKey, ref MousePerformanceChartRenderFrame frame)
    {
        frame = null;
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            foreach (KeyValuePair<FrameCacheKey, MousePerformanceChartRenderFrame> cacheEntry in _frameCache)
            {
                if (cacheEntry.Value != null && cacheEntry.Value.IsAvailable && string.Equals(cacheEntry.Key.SessionIdentity, cacheKey.SessionIdentity, StringComparison.Ordinal) && cacheEntry.Key.SessionRevision == cacheKey.SessionRevision && cacheEntry.Key.PlotType == cacheKey.PlotType && cacheEntry.Key.StartIndex == cacheKey.StartIndex && cacheEntry.Key.EndIndex == cacheKey.EndIndex && cacheEntry.Key.TimeBasis == cacheKey.TimeBasis && Nullable.Equals(cacheEntry.Key.EffectiveCpi, cacheKey.EffectiveCpi))
                {
                    frame = cacheEntry.Value;
                    return true;
                }
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
        return false;
    }

    private bool TryGetCompatibleTimingFrame(TimingStatisticsCacheKey cacheKey, ref MousePerformanceChartRenderFrame frame)
    {
        frame = null;
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            foreach (KeyValuePair<FrameCacheKey, MousePerformanceChartRenderFrame> cacheEntry in _frameCache)
            {
                if (cacheEntry.Value != null && cacheEntry.Value.IsAvailable && string.Equals(cacheEntry.Key.SessionIdentity, cacheKey.SessionIdentity, StringComparison.Ordinal) && cacheEntry.Key.SessionRevision == cacheKey.SessionRevision && cacheEntry.Key.PlotType == cacheKey.PlotType && cacheEntry.Key.StartIndex == cacheKey.StartIndex && cacheEntry.Key.EndIndex == cacheKey.EndIndex && cacheEntry.Key.TimeBasis == cacheKey.TimeBasis)
                {
                    frame = cacheEntry.Value;
                    return true;
                }
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
        return false;
    }

    private bool TryGetCompatibleResidualDispersionFrame(ResidualDispersionCacheKey cacheKey, ref MousePerformanceChartRenderFrame frame)
    {
        frame = null;
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            foreach (KeyValuePair<FrameCacheKey, MousePerformanceChartRenderFrame> cacheEntry in _frameCache)
            {
                if (cacheEntry.Value != null && cacheEntry.Value.IsAvailable && string.Equals(cacheEntry.Key.SessionIdentity, cacheKey.SessionIdentity, StringComparison.Ordinal) && cacheEntry.Key.SessionRevision == cacheKey.SessionRevision && cacheEntry.Key.PlotType == cacheKey.PlotType && cacheEntry.Key.StartIndex == cacheKey.StartIndex && cacheEntry.Key.EndIndex == cacheKey.EndIndex && cacheEntry.Key.TimeBasis == cacheKey.TimeBasis)
                {
                    frame = cacheEntry.Value;
                    return true;
                }
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
        return false;
    }

    private async Task<MousePerformanceResidualDispersionSummary> BuildResidualDispersionAsync(ResidualDispersionCacheKey cacheKey, MousePerformanceSnapshot snapshot, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, CancellationToken cancellationToken, bool ownsTaskRegistration = false)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            MousePerformanceChartRenderFrame frame = null;
            if (TryGetCompatibleResidualDispersionFrame(cacheKey, ref frame))
            {
                MousePerformanceResidualDispersionSummary cachedFrameDispersion = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return MousePerformanceStatisticsCalculator.ComputeResidualDispersion(frame);
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                object frameSyncRoot = _syncRoot;
                bool frameLockTaken = false;
                try
                {
                    Monitor.Enter(frameSyncRoot, ref frameLockTaken);
                    StoreResidualDispersionCacheEntry(cacheKey, cachedFrameDispersion);
                }
                finally
                {
                    if (frameLockTaken)
                    {
                        Monitor.Exit(frameSyncRoot);
                    }
                }
                return cachedFrameDispersion;
            }
            MousePerformanceResidualDispersionSummary mousePerformanceResidualDispersionSummary = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return MousePerformanceStatisticsCalculator.ComputeResidualDispersion(snapshot, plotType, startIndex, endIndex, timeBasis, cancellationToken);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            object syncRoot = _syncRoot;
            bool lockTaken = false;
            try
            {
                Monitor.Enter(syncRoot, ref lockTaken);
                StoreResidualDispersionCacheEntry(cacheKey, mousePerformanceResidualDispersionSummary);
                return mousePerformanceResidualDispersionSummary;
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(syncRoot);
                }
            }
        }
        finally
        {
            if (ownsTaskRegistration)
            {
                object syncRoot2 = _syncRoot;
                bool lockTaken2 = false;
                try
                {
                    Monitor.Enter(syncRoot2, ref lockTaken2);
                    _residualDispersionTasks.Remove(cacheKey);
                }
                finally
                {
                    if (lockTaken2)
                    {
                        Monitor.Exit(syncRoot2);
                    }
                }
            }
        }
    }

    private void StoreFrameCacheEntry(FrameCacheKey cacheKey, MousePerformanceChartRenderFrame frame)
    {
        if (!_frameCache.ContainsKey(cacheKey))
        {
            TrimFrameCacheIfNeeded();
            _frameCacheOrder.Enqueue(cacheKey);
        }
        _frameCache[cacheKey] = frame;
    }

    private void StoreComparisonHistogramFrameCacheEntry(ComparisonHistogramFrameCacheKey cacheKey, MousePerformanceChartRenderFrame frame)
    {
        if (!_comparisonHistogramFrameCache.ContainsKey(cacheKey))
        {
            TrimComparisonHistogramFrameCacheIfNeeded();
            _comparisonHistogramFrameCacheOrder.Enqueue(cacheKey);
        }
        _comparisonHistogramFrameCache[cacheKey] = frame;
    }

    private IReadOnlyList<MousePerformanceChartGapSource> ResolveCachedGapSources(FrameCacheKey frameCacheKey, MousePerformanceSnapshot snapshot)
    {
        if (snapshot == null || frameCacheKey.PlotType == MousePerformancePlotType.XVsY)
        {
            return Array.Empty<MousePerformanceChartGapSource>();
        }

        GapSourceCacheKey gapSourceCacheKey = new GapSourceCacheKey(frameCacheKey.SessionIdentity, frameCacheKey.SessionRevision, frameCacheKey.StartIndex, frameCacheKey.EndIndex, frameCacheKey.TimeBasis);
        if (_gapSourceCache.TryGetValue(gapSourceCacheKey, out IReadOnlyList<MousePerformanceChartGapSource> cachedGapSources))
        {
            return cachedGapSources;
        }

        IReadOnlyList<MousePerformanceChartGapSource> gapSources = MousePerformanceChartFrameBuilder.CreateReportGapSources(snapshot.Events, frameCacheKey.StartIndex, frameCacheKey.EndIndex, frameCacheKey.TimeBasis);
        if (!_gapSourceCache.ContainsKey(gapSourceCacheKey))
        {
            TrimGapSourceCacheIfNeeded();
            _gapSourceCacheOrder.Enqueue(gapSourceCacheKey);
        }
        _gapSourceCache[gapSourceCacheKey] = gapSources;
        return gapSources;
    }

    private void StoreVelocityStatisticsCacheEntry(VelocityStatisticsCacheKey cacheKey, MousePerformanceVelocityStatisticsSummary statistics)
    {
        if (!_velocityStatisticsCache.ContainsKey(cacheKey))
        {
            TrimVelocityStatisticsCacheIfNeeded();
            _velocityStatisticsCacheOrder.Enqueue(cacheKey);
        }
        _velocityStatisticsCache[cacheKey] = statistics;
    }

    private void StoreResidualDispersionCacheEntry(ResidualDispersionCacheKey cacheKey, MousePerformanceResidualDispersionSummary statistics)
    {
        if (!_residualDispersionCache.ContainsKey(cacheKey))
        {
            TrimResidualDispersionCacheIfNeeded();
            _residualDispersionCacheOrder.Enqueue(cacheKey);
        }
        _residualDispersionCache[cacheKey] = statistics;
    }

    private void StoreTimingStatisticsCacheEntry(TimingStatisticsCacheKey cacheKey, MousePerformanceTimingStatisticsSummary statistics)
    {
        if (!_timingStatisticsCache.ContainsKey(cacheKey))
        {
            TrimTimingStatisticsCacheIfNeeded();
            _timingStatisticsCacheOrder.Enqueue(cacheKey);
        }
        _timingStatisticsCache[cacheKey] = statistics;
    }

    private void TrimFrameCacheIfNeeded()
    {
        while (_frameCache.Count >= MaxFrameCacheEntries && _frameCacheOrder.Count > 0)
        {
            FrameCacheKey key = _frameCacheOrder.Dequeue();
            if (_frameCache.Remove(key))
            {
                break;
            }
        }
    }

    private void TrimComparisonHistogramFrameCacheIfNeeded()
    {
        while (_comparisonHistogramFrameCache.Count >= MaxComparisonHistogramFrameCacheEntries && _comparisonHistogramFrameCacheOrder.Count > 0)
        {
            ComparisonHistogramFrameCacheKey key = _comparisonHistogramFrameCacheOrder.Dequeue();
            if (_comparisonHistogramFrameCache.Remove(key))
            {
                break;
            }
        }
    }

    private void TrimGapSourceCacheIfNeeded()
    {
        while (_gapSourceCache.Count >= MaxGapSourceCacheEntries && _gapSourceCacheOrder.Count > 0)
        {
            GapSourceCacheKey key = _gapSourceCacheOrder.Dequeue();
            if (_gapSourceCache.Remove(key))
            {
                break;
            }
        }
    }

    private void TrimVelocityStatisticsCacheIfNeeded()
    {
        while (_velocityStatisticsCache.Count >= MaxVelocityStatisticsCacheEntries && _velocityStatisticsCacheOrder.Count > 0)
        {
            VelocityStatisticsCacheKey key = _velocityStatisticsCacheOrder.Dequeue();
            if (_velocityStatisticsCache.Remove(key))
            {
                break;
            }
        }
    }

    private void TrimResidualDispersionCacheIfNeeded()
    {
        while (_residualDispersionCache.Count >= MaxResidualDispersionCacheEntries && _residualDispersionCacheOrder.Count > 0)
        {
            ResidualDispersionCacheKey key = _residualDispersionCacheOrder.Dequeue();
            if (_residualDispersionCache.Remove(key))
            {
                break;
            }
        }
    }

    private void TrimTimingStatisticsCacheIfNeeded()
    {
        while (_timingStatisticsCache.Count >= MaxTimingStatisticsCacheEntries && _timingStatisticsCacheOrder.Count > 0)
        {
            TimingStatisticsCacheKey key = _timingStatisticsCacheOrder.Dequeue();
            if (_timingStatisticsCache.Remove(key))
            {
                break;
            }
        }
    }

    private static MousePerformanceChartSeries ResolveSeries(MousePerformanceChartRenderFrame frame, MousePerformanceChartSeriesKind kind, MousePerformanceChartSeriesPalette palette)
    {
        if (frame?.Series == null)
        {
            return null;
        }
        return frame.Series.FirstOrDefault(series => series != null && series.Kind == kind && series.Palette == palette && series.Points != null && series.Points.Count > 0);
    }

    private static IReadOnlyList<MousePerformancePlotType> ResolveWarmupPlotTypes(MousePerformancePlotType preferredPlotType, bool includeVelocityPlots)
    {
        List<MousePerformancePlotType> plotTypes = new List<MousePerformancePlotType>();
        AddUniquePlotType(plotTypes, preferredPlotType);
        if (includeVelocityPlots)
        {
            foreach (MousePerformancePlotType velocityPlotType in ResolveVelocityWarmupPlotTypes())
            {
                AddUniquePlotType(plotTypes, velocityPlotType);
            }
        }
        return plotTypes;
    }

    private static IReadOnlyList<MousePerformancePlotType> ResolveVelocityWarmupPlotTypes()
    {
        return new MousePerformancePlotType[3]
        {
            MousePerformancePlotType.XYVelocityVsTime,
            MousePerformancePlotType.XVelocityVsTime,
            MousePerformancePlotType.YVelocityVsTime
        };
    }

    private static void AddUniquePlotType(ICollection<MousePerformancePlotType> target, MousePerformancePlotType plotType)
    {
        if (target != null && !target.Contains(plotType))
        {
            target.Add(plotType);
        }
    }

    private static bool TryCreateFrameCacheKey(MousePerformanceSessionArchive session, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, ref FrameCacheKey cacheKey)
    {
        cacheKey = default;
        MousePerformanceSnapshot mousePerformanceSnapshot = session?.Snapshot;
        if (mousePerformanceSnapshot == null)
        {
            return false;
        }
        string sessionIdentity = ResolveSessionContentIdentity(session);
        if (string.IsNullOrWhiteSpace(sessionIdentity))
        {
            return false;
        }
        cacheKey = new FrameCacheKey(sessionIdentity, mousePerformanceSnapshot.SessionRevision, plotType, startIndex, endIndex, timeBasis, IsVelocityPlot(plotType) ? mousePerformanceSnapshot.EffectiveCpi : null);
        return true;
    }

    private static IReadOnlyList<MousePerformanceHistogramDataset> CreateHistogramDatasets(IReadOnlyList<MousePerformanceChartDatasetSession> sessions)
    {
        if (sessions == null || sessions.Count == 0)
        {
            return Array.Empty<MousePerformanceHistogramDataset>();
        }

        List<MousePerformanceHistogramDataset> datasets = new List<MousePerformanceHistogramDataset>(sessions.Count);
        for (int sessionIndex = 0; sessionIndex < sessions.Count; sessionIndex++)
        {
            MousePerformanceChartDatasetSession sessionEntry = sessions[sessionIndex];
            MousePerformanceSnapshot snapshot = sessionEntry?.Session?.Snapshot;
            if (snapshot != null)
            {
                datasets.Add(new MousePerformanceHistogramDataset(sessionEntry.DatasetSlot, snapshot));
            }
        }
        return datasets.Count == 0 ? Array.Empty<MousePerformanceHistogramDataset>() : datasets.ToArray();
    }

    private static bool TryCreateComparisonHistogramFrameCacheKey(IReadOnlyList<MousePerformanceChartDatasetSession> sessions, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, ref ComparisonHistogramFrameCacheKey cacheKey)
    {
        cacheKey = default;
        if (sessions == null || sessions.Count == 0 || !MousePerformancePlotTraits.IsHistogramPlot(plotType))
        {
            return false;
        }

        List<string> datasetSegments = new List<string>(sessions.Count);
        for (int sessionIndex = 0; sessionIndex < sessions.Count; sessionIndex++)
        {
            MousePerformanceChartDatasetSession sessionEntry = sessions[sessionIndex];
            MousePerformanceSessionArchive session = sessionEntry?.Session;
            MousePerformanceSnapshot snapshot = session?.Snapshot;
            if (session == null || snapshot == null)
            {
                return false;
            }

            string contentIdentity = ResolveSessionContentIdentity(session);
            if (string.IsNullOrWhiteSpace(contentIdentity))
            {
                return false;
            }

            string cpiSegment = snapshot.EffectiveCpi.HasValue ? snapshot.EffectiveCpi.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            datasetSegments.Add($"{(int)sessionEntry.DatasetSlot}:{contentIdentity}:{snapshot.SessionRevision}:{cpiSegment}");
        }

        string datasetSignature = string.Join("|", datasetSegments);
        cacheKey = new ComparisonHistogramFrameCacheKey(datasetSignature, plotType, startIndex, endIndex, timeBasis);
        return true;
    }

    private static bool TryCreateVelocityStatisticsCacheKey(MousePerformanceSessionArchive session, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, ref VelocityStatisticsCacheKey cacheKey)
    {
        cacheKey = default;
        MousePerformanceSnapshot mousePerformanceSnapshot = session?.Snapshot;
        if (mousePerformanceSnapshot == null || !IsVelocityPlot(plotType) || !mousePerformanceSnapshot.CanComputeVelocity || !mousePerformanceSnapshot.EffectiveCpi.HasValue || mousePerformanceSnapshot.EffectiveCpi.Value <= 0.0)
        {
            return false;
        }
        string sessionIdentity = ResolveSessionContentIdentity(session);
        if (string.IsNullOrWhiteSpace(sessionIdentity))
        {
            return false;
        }
        cacheKey = new VelocityStatisticsCacheKey(sessionIdentity, mousePerformanceSnapshot.SessionRevision, plotType, startIndex, endIndex, timeBasis, mousePerformanceSnapshot.EffectiveCpi);
        return true;
    }

    private static bool TryCreateResidualDispersionCacheKey(MousePerformanceSessionArchive session, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, ref ResidualDispersionCacheKey cacheKey)
    {
        cacheKey = default;
        MousePerformanceSnapshot mousePerformanceSnapshot = session?.Snapshot;
        if (mousePerformanceSnapshot == null || !IsCountPlot(plotType))
        {
            return false;
        }
        string sessionIdentity = ResolveSessionContentIdentity(session);
        if (string.IsNullOrWhiteSpace(sessionIdentity))
        {
            return false;
        }
        cacheKey = new ResidualDispersionCacheKey(sessionIdentity, mousePerformanceSnapshot.SessionRevision, plotType, startIndex, endIndex, timeBasis);
        return true;
    }

    private static bool TryCreateTimingStatisticsCacheKey(MousePerformanceSessionArchive session, MousePerformancePlotType plotType, int startIndex, int endIndex, MousePerformanceTimeBasis timeBasis, ref TimingStatisticsCacheKey cacheKey)
    {
        cacheKey = default;
        MousePerformanceSnapshot mousePerformanceSnapshot = session?.Snapshot;
        if (mousePerformanceSnapshot == null || !IsTimingPlot(plotType))
        {
            return false;
        }
        string sessionIdentity = ResolveSessionContentIdentity(session);
        if (string.IsNullOrWhiteSpace(sessionIdentity))
        {
            return false;
        }
        cacheKey = new TimingStatisticsCacheKey(sessionIdentity, mousePerformanceSnapshot.SessionRevision, plotType, startIndex, endIndex, timeBasis);
        return true;
    }

    private static string ResolveSessionContentIdentity(MousePerformanceSessionArchive session)
    {
        string contentIdentity = MousePerformanceSessionIdentityResolver.ResolveSessionContentIdentity(session);
        if (!string.IsNullOrWhiteSpace(contentIdentity))
        {
            return contentIdentity;
        }
        return MousePerformanceSessionIdentityResolver.ResolveSessionInstanceIdentity(session);
    }

    private static bool IsCountPlot(MousePerformancePlotType plotType)
    {
        return MousePerformancePlotTraits.IsCountPlot(plotType);
    }

    private static bool IsVelocityPlot(MousePerformancePlotType plotType)
    {
        return MousePerformancePlotTraits.IsVelocityPlot(plotType);
    }

    private static bool IsTimingPlot(MousePerformancePlotType plotType)
    {
        return MousePerformancePlotTraits.IsTimingPlot(plotType);
    }
}
