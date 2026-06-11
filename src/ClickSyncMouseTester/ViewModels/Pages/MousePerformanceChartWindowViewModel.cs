#define TRACE
using ClickSyncMouseTester.Infrastructure;
using ClickSyncMouseTester.Models;
using ClickSyncMouseTester.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace ClickSyncMouseTester.ViewModels.Pages;

[SupportedOSPlatform("windows")]
public class MousePerformanceChartWindowViewModel : BindableBase, IDisposable
{
    public sealed class MousePerformancePlotTypeOption : BindableBase
    {
        private string _displayName;

        public MousePerformancePlotType PlotType { get; }

        public string DisplayName
        {
            get
            {
                return _displayName;
            }
            set
            {
                SetProperty(ref _displayName, value ?? string.Empty, "DisplayName");
            }
        }

        public MousePerformancePlotTypeOption(MousePerformancePlotType plotType, string displayName)
        {
            PlotType = plotType;
            _displayName = displayName ?? string.Empty;
        }
    }

    public sealed class MousePerformanceTimeBasisOption : BindableBase
    {
        private string _displayName;

        public MousePerformanceTimeBasis TimeBasis { get; }

        public string DisplayName
        {
            get
            {
                return _displayName;
            }
            set
            {
                SetProperty(ref _displayName, value ?? string.Empty, "DisplayName");
            }
        }

        public MousePerformanceTimeBasisOption(MousePerformanceTimeBasis timeBasis, string displayName)
        {
            TimeBasis = timeBasis;
            _displayName = displayName ?? string.Empty;
        }
    }

    public sealed class MousePerformanceChartGapAnalysisTargetOption : BindableBase
    {
        private string _displayName;

        public MousePerformanceChartDatasetSlot DatasetSlot { get; }

        public string DisplayName
        {
            get
            {
                return _displayName;
            }
            set
            {
                SetProperty(ref _displayName, value ?? string.Empty, "DisplayName");
            }
        }

        public MousePerformanceChartGapAnalysisTargetOption(MousePerformanceChartDatasetSlot datasetSlot, string displayName)
        {
            DatasetSlot = datasetSlot;
            _displayName = displayName ?? string.Empty;
        }
    }

    public sealed class MousePerformanceChartLegendItem : BindableBase
    {
        private readonly Action<MousePerformanceChartDatasetSlot, bool> _visibilityChanged;

        private MousePerformanceChartDatasetSlot _datasetSlot;

        private string _text;

        private Brush _primaryBrush;

        private Brush _secondaryBrush;

        private Brush _accentBrush;

        private bool _isVisible;

        private bool _canToggleVisibility;

        public MousePerformanceChartDatasetSlot DatasetSlot => _datasetSlot;

        public string Text
        {
            get
            {
                return _text;
            }
            set
            {
                SetProperty(ref _text, value ?? string.Empty, "Text");
            }
        }

        public Brush PrimaryBrush
        {
            get
            {
                return _primaryBrush;
            }
            set
            {
                SetProperty(ref _primaryBrush, value, "PrimaryBrush");
            }
        }

        public Brush SecondaryBrush
        {
            get
            {
                return _secondaryBrush;
            }
            set
            {
                SetProperty(ref _secondaryBrush, value, "SecondaryBrush");
            }
        }

        public Brush AccentBrush
        {
            get
            {
                return _accentBrush;
            }
            set
            {
                SetProperty(ref _accentBrush, value, "AccentBrush");
            }
        }

        public bool IsVisible
        {
            get
            {
                return _isVisible;
            }
            set
            {
                if (SetProperty(ref _isVisible, value, "IsVisible"))
                {
                    _visibilityChanged?.Invoke(_datasetSlot, _isVisible);
                }
            }
        }

        public bool CanToggleVisibility
        {
            get
            {
                return _canToggleVisibility;
            }
            set
            {
                SetProperty(ref _canToggleVisibility, value, "CanToggleVisibility");
            }
        }

        public MousePerformanceChartLegendItem(MousePerformanceChartDatasetSlot datasetSlot, string labelText, Brush primaryBrush, Brush secondaryBrush, Brush accentBrush, bool isVisible, bool canToggleVisibility, Action<MousePerformanceChartDatasetSlot, bool> visibilityChanged)
        {
            _datasetSlot = datasetSlot;
            _text = labelText ?? string.Empty;
            _primaryBrush = primaryBrush;
            _secondaryBrush = secondaryBrush;
            _accentBrush = accentBrush;
            _isVisible = isVisible;
            _canToggleVisibility = canToggleVisibility;
            _visibilityChanged = visibilityChanged;
        }
    }

    private sealed class MousePerformanceChartDatasetPresentationState
    {
        public string SessionIdentity { get; set; }

        public bool IsVisible { get; set; }

        public long ActivationOrder { get; set; }

        public MousePerformanceChartDatasetPresentationState(string sessionIdentity, bool isVisible, long activationOrder)
        {
            SessionIdentity = sessionIdentity ?? string.Empty;
            IsVisible = isVisible;
            ActivationOrder = activationOrder;
        }
    }


    private sealed class DatasetSessionEntry
    {
        public MousePerformanceChartDatasetSlot DatasetSlot { get; }

        public MousePerformanceSessionArchive Session { get; }

        public DatasetSessionEntry(MousePerformanceChartDatasetSlot datasetSlot, MousePerformanceSessionArchive session)
        {
            DatasetSlot = datasetSlot;
            Session = session;
        }
    }


    private sealed class DatasetRenderFrameEntry
    {
        public MousePerformanceChartDatasetSlot DatasetSlot { get; }

        public MousePerformanceChartRenderFrame Frame { get; }

        public DatasetRenderFrameEntry(MousePerformanceChartDatasetSlot datasetSlot, MousePerformanceChartRenderFrame frame)
        {
            DatasetSlot = datasetSlot;
            Frame = frame;
        }
    }

    private sealed class RenderRequestState
    {
        public MousePerformancePlotType PlotType { get; }

        public MousePerformanceTimeBasis TimeBasis { get; }

        public int StartIndex { get; }

        public int EndIndex { get; }

        public bool ShowStem { get; }

        public bool ShowLines { get; }

        public int ActiveDatasetCount { get; }

        public IReadOnlyList<DatasetSessionEntry> VisibleEntries { get; }

        public RenderRequestState(MousePerformancePlotType plotType, MousePerformanceTimeBasis timeBasis, int startIndex, int endIndex, bool showStem, bool showLines, int activeDatasetCount, IReadOnlyList<DatasetSessionEntry> visibleEntries)
        {
            PlotType = plotType;
            TimeBasis = timeBasis;
            StartIndex = Math.Max(0, startIndex);
            EndIndex = Math.Max(StartIndex, endIndex);
            ShowStem = showStem;
            ShowLines = showLines;
            ActiveDatasetCount = Math.Max(0, activeDatasetCount);
            VisibleEntries = visibleEntries ?? Array.Empty<DatasetSessionEntry>();
        }
    }


    private const MousePerformanceSessionStatus VelocityStatisticsNotReadySessionStatus = MousePerformanceSessionStatus.Collecting;

    private const int LoadingFrameDisplayDelayMilliseconds = 120;

    private readonly LocalizationManager _localization;

    private readonly IMousePerformancePreferencesStore _preferencesStore;

    private readonly MousePerformanceChartAnalysisCache _analysisCache;

    private readonly ObservableCollection<MousePerformancePlotTypeOption> _plotTypeOptions;

    private readonly ObservableCollection<MousePerformanceTimeBasisOption> _timeBasisOptions;

    private readonly ObservableCollection<MousePerformanceChartGapAnalysisTargetOption> _gapAnalysisTargetOptions;

    private readonly ObservableCollection<MousePerformanceChartLegendItem> _legendItems;

    private readonly List<MousePerformanceSessionArchive> _comparisonSessions;

    private readonly Dictionary<MousePerformanceChartDatasetSlot, MousePerformanceChartDatasetPresentationState> _datasetPresentationStates;

    private MousePerformanceSessionArchive _baselineSession;

    private MousePerformanceSnapshot _snapshot;

    private MousePerformancePlotType _selectedPlotType;

    private MousePerformanceTimeBasis _selectedTimeBasis;

    private MousePerformanceChartDatasetSlot _selectedGapAnalysisDatasetSlot;

    private string _rangeStartText;

    private string _rangeEndText;

    private int _maximumIndex;

    private bool _showStem;

    private bool _showLines;

    private bool _showGapOverlay;

    private MousePerformanceChartRenderFrame _renderFrame;

    private string _toolbarSupplementText;

    private string _toolbarDiagnosticsText;

    private int _visibleGapCount;

    private double? _visibleGapAverageDurationMs;

    private bool _isChartRendererAvailable;

    private string _windowTitleText;

    private long _nextDatasetActivationOrder;

    private const bool SuspendChartOptionPersistence = false;

    private int _renderBuildVersion;

    private CancellationTokenSource _renderBuildCancellation;

    private bool _disposed;

    internal IMousePerformancePreferencesStore PreferencesStore => _preferencesStore;

    public ObservableCollection<MousePerformancePlotTypeOption> PlotTypeOptions => _plotTypeOptions;

    public ObservableCollection<MousePerformanceTimeBasisOption> TimeBasisOptions => _timeBasisOptions;

    public ObservableCollection<MousePerformanceChartGapAnalysisTargetOption> GapAnalysisTargetOptions => _gapAnalysisTargetOptions;

    public ObservableCollection<MousePerformanceChartLegendItem> LegendItems => _legendItems;

    public int ComparisonSessionCount => _comparisonSessions.Count;

    public bool HasLegendItems => _comparisonSessions.Count > 0;

    public bool IsTimeBasisSelectionEnabled => _selectedPlotType != MousePerformancePlotType.XVsY;

    public bool IsGapAnalysisSelectionVisible
    {
        get
        {
            if (_showGapOverlay && CanUseGapOverlay)
            {
                return _gapAnalysisTargetOptions.Count > 1;
            }
            return false;
        }
    }

    public bool CanUseGapOverlay => _selectedPlotType != MousePerformancePlotType.XVsY && !MousePerformancePlotTraits.IsHistogramPlot(_selectedPlotType);

    public bool CanExportBaselineSession
    {
        get
        {
            if (HasSingleLiveBaselineSession())
            {
                return !IsBaselineCollecting();
            }
            return false;
        }
    }

    public bool CanImportComparisonSession
    {
        get
        {
            if (_baselineSession != null && _baselineSession.HasData)
            {
                return !IsBaselineCollecting();
            }
            return false;
        }
    }

    public bool CanSavePng
    {
        get
        {
            if (_baselineSession != null && _baselineSession.HasData && !IsBaselineCollecting())
            {
                return _isChartRendererAvailable;
            }
            return false;
        }
    }

    public MousePerformancePlotType SelectedPlotType
    {
        get
        {
            return _selectedPlotType;
        }
        set
        {
            if (SetProperty(ref _selectedPlotType, value, "SelectedPlotType"))
            {
                NormalizeGapOverlayForPlotType();
                RaisePropertyChanged("IsTimeBasisSelectionEnabled");
                RaisePropertyChanged("ShowStem");
                RaisePropertyChanged("CanToggleStem");
                RaisePropertyChanged("ShowLines");
                RaisePropertyChanged("CanToggleLines");
                RaisePropertyChanged("CanUseGapOverlay");
                RaisePropertyChanged("IsGapAnalysisSelectionVisible");
                PersistChartOptions();
                RebuildRenderFrame();
            }
        }
    }

    public MousePerformanceTimeBasis SelectedTimeBasis
    {
        get
        {
            return _selectedTimeBasis;
        }
        set
        {
            if (SetProperty(ref _selectedTimeBasis, value, "SelectedTimeBasis"))
            {
                PersistChartOptions();
                RebuildRenderFrame();
            }
        }
    }

    public MousePerformanceChartDatasetSlot SelectedGapAnalysisDatasetSlot
    {
        get
        {
            return _selectedGapAnalysisDatasetSlot;
        }
        set
        {
            if (ContainsGapAnalysisTargetOption(value) && SetProperty(ref _selectedGapAnalysisDatasetSlot, value, "SelectedGapAnalysisDatasetSlot"))
            {
                ResetVisibleGapMetrics();
                RefreshToolbarDiagnostics();
            }
        }
    }

    public string RangeStartText
    {
        get
        {
            return _rangeStartText;
        }
        set
        {
            SetProperty(ref _rangeStartText, NormalizeIndexText(value), "RangeStartText");
        }
    }

    public string RangeEndText
    {
        get
        {
            return _rangeEndText;
        }
        set
        {
            SetProperty(ref _rangeEndText, NormalizeIndexText(value), "RangeEndText");
        }
    }

    public int MaximumIndex
    {
        get
        {
            return _maximumIndex;
        }
        private set
        {
            if (SetProperty(ref _maximumIndex, Math.Max(0, value), "MaximumIndex"))
            {
                RaisePropertyChanged("MaximumIndexText");
            }
        }
    }

    public string MaximumIndexText => MaximumIndex.ToString(CultureInfo.InvariantCulture);

    public bool ShowStem
    {
        get
        {
            return ResolveEffectiveShowStem();
        }
        set
        {
            if (SetProperty(ref _showStem, value, "ShowStem"))
            {
                PersistChartOptions();
                RebuildRenderFrame();
            }
        }
    }

    public bool CanToggleStem => MousePerformancePlotPresentationPolicy.Resolve(_selectedPlotType).CanToggleStem;

    public bool ShowLines
    {
        get
        {
            return ResolveEffectiveShowLines();
        }
        set
        {
            if (SetProperty(ref _showLines, value, "ShowLines"))
            {
                PersistChartOptions();
                RebuildRenderFrame();
            }
        }
    }

    public bool CanToggleLines => MousePerformancePlotPresentationPolicy.Resolve(_selectedPlotType).CanToggleLines;

    public bool ShowGapOverlay
    {
        get
        {
            return _showGapOverlay;
        }
        set
        {
            bool enabledGapOverlay = value && CanUseGapOverlay;
            if (SetProperty(ref _showGapOverlay, enabledGapOverlay, "ShowGapOverlay"))
            {
                if (_showGapOverlay)
                {
                    SelectFirstGapAnalysisTargetOption();
                }
                RaisePropertyChanged("IsGapAnalysisSelectionVisible");
                RefreshToolbarDiagnostics();
            }
        }
    }

    public MousePerformanceChartRenderFrame RenderFrame
    {
        get
        {
            return _renderFrame;
        }
        private set
        {
            SetProperty(ref _renderFrame, value, "RenderFrame");
        }
    }

    public string ToolbarSupplementText
    {
        get
        {
            return _toolbarSupplementText;
        }
        private set
        {
            SetProperty(ref _toolbarSupplementText, value ?? string.Empty, "ToolbarSupplementText");
        }
    }

    public string ToolbarDiagnosticsText
    {
        get
        {
            return _toolbarDiagnosticsText;
        }
        private set
        {
            string diagnosticsText = value ?? string.Empty;
            if (SetProperty(ref _toolbarDiagnosticsText, diagnosticsText, "ToolbarDiagnosticsText"))
            {
                RaisePropertyChanged("HasToolbarDiagnostics");
            }
        }
    }

    public bool HasToolbarDiagnostics => !string.IsNullOrWhiteSpace(_toolbarDiagnosticsText);

    public string WindowTitleText
    {
        get
        {
            return _windowTitleText;
        }
        private set
        {
            SetProperty(ref _windowTitleText, value, "WindowTitleText");
        }
    }

    public MousePerformanceChartWindowViewModel(IMousePerformancePreferencesStore preferencesStore = null)
    {
        _localization = LocalizationManager.Instance;
        _preferencesStore = preferencesStore ?? MousePerformancePreferencesStore.Instance;
        _analysisCache = MousePerformanceChartAnalysisCache.Instance;
        _localization.Initialize();
        _plotTypeOptions = new ObservableCollection<MousePerformancePlotTypeOption>();
        _timeBasisOptions = new ObservableCollection<MousePerformanceTimeBasisOption>();
        _gapAnalysisTargetOptions = new ObservableCollection<MousePerformanceChartGapAnalysisTargetOption>();
        _legendItems = new ObservableCollection<MousePerformanceChartLegendItem>();
        _comparisonSessions = new List<MousePerformanceSessionArchive>();
        _datasetPresentationStates = new Dictionary<MousePerformanceChartDatasetSlot, MousePerformanceChartDatasetPresentationState>();
        _rangeStartText = "0";
        _rangeEndText = "0";
        MousePerformancePreferences mousePerformancePreferences = _preferencesStore.LoadPreferences();
        _selectedPlotType = mousePerformancePreferences.ChartPlotType;
        _showStem = mousePerformancePreferences.ChartShowStem;
        _showLines = mousePerformancePreferences.ChartShowLines;
        _selectedTimeBasis = MousePerformanceTimeBasis.RawCapture;
        _selectedGapAnalysisDatasetSlot = MousePerformanceChartDatasetSlot.Baseline;
        _showGapOverlay = false;
        _toolbarSupplementText = string.Empty;
        _toolbarDiagnosticsText = string.Empty;
        _visibleGapCount = 0;
        _visibleGapAverageDurationMs = null;
        _isChartRendererAvailable = false;
        _nextDatasetActivationOrder = 0L;
        _renderFrame = MousePerformanceEngine.CreateChartRenderFrame(null, _selectedPlotType, 0, 0, _showStem, _showLines, _selectedTimeBasis);
        _localization.LanguageChanged += OnLanguageChanged;
        RefreshWindowText();
        RefreshPlotOptions();
        RefreshTimeBasisOptions();
        RebuildRenderFrame();
    }

    private void NormalizeGapOverlayForPlotType()
    {
        if (!CanUseGapOverlay && _showGapOverlay)
        {
            _showGapOverlay = false;
            ResetVisibleGapMetrics();
            RaisePropertyChanged("ShowGapOverlay");
            RaisePropertyChanged("IsGapAnalysisSelectionVisible");
            RefreshToolbarDiagnostics();
        }
    }

    public void UpdateVisibleGapMetrics(int count, double? averageDurationMs)
    {
        int sanitizedGapCount = Math.Max(0, count);
        double? sanitizedAverageDurationMs = null;
        if (averageDurationMs.HasValue && !double.IsNaN(averageDurationMs.Value) && !double.IsInfinity(averageDurationMs.Value) && averageDurationMs.Value > 0.0)
        {
            sanitizedAverageDurationMs = averageDurationMs.Value;
        }

        if (_visibleGapCount != sanitizedGapCount || !Nullable.Equals(_visibleGapAverageDurationMs, sanitizedAverageDurationMs))
        {
            _visibleGapCount = sanitizedGapCount;
            _visibleGapAverageDurationMs = sanitizedAverageDurationMs;
            RefreshToolbarDiagnostics();
        }
    }

    public void UpdateChartRendererAvailability(bool isAvailable)
    {
        if (_isChartRendererAvailable != isAvailable)
        {
            _isChartRendererAvailable = isAvailable;
            RaisePropertyChanged("CanSavePng");
        }
    }

    public void UpdateSessionGroup(MousePerformanceSessionArchive session, IEnumerable<MousePerformanceSessionArchive> comparisonSessions)
    {
        MousePerformanceSnapshot previousSnapshot = _snapshot;
        int previousMaximumIndex = _maximumIndex;
        int previousStartIndex = ParseIndex(_rangeStartText, 0);
        int previousEndIndex = ParseIndex(_rangeEndText, previousMaximumIndex);
        bool shouldFollowLatest = previousEndIndex >= previousMaximumIndex;

        _baselineSession = session;
        _snapshot = session?.Snapshot;
        _comparisonSessions.Clear();
        if (_baselineSession != null && _baselineSession.HasData)
        {
            foreach (MousePerformanceSessionArchive comparisonSession in NormalizeComparisonSessions(_baselineSession, comparisonSessions))
            {
                _comparisonSessions.Add(comparisonSession);
            }
        }

        RefreshSessionState(previousSnapshot, previousStartIndex, previousEndIndex, shouldFollowLatest);
    }

    public MousePerformanceSessionArchive CreateBaselineExportSession()
    {
        return _baselineSession;
    }

    public void CommitRangeInputs()
    {
        int startIndex = ParseIndex(_rangeStartText, 0);
        int endIndex = ParseIndex(_rangeEndText, MaximumIndex);
        startIndex = Math.Max(0, Math.Min(startIndex, MaximumIndex));
        endIndex = Math.Max(startIndex, Math.Min(endIndex, MaximumIndex));
        SetProperty(ref _rangeStartText, startIndex.ToString(CultureInfo.InvariantCulture), "RangeStartText");
        SetProperty(ref _rangeEndText, endIndex.ToString(CultureInfo.InvariantCulture), "RangeEndText");
        RebuildRenderFrame();
    }

    private void RefreshSessionState(MousePerformanceSnapshot previousSnapshot, int previousStartIndex, int previousEndIndex, bool shouldFollowLatest)
    {
        SynchronizeDatasetPresentationStates();
        RefreshGapAnalysisTargetOptions();
        RefreshLegendItems();
        RaiseSessionCapabilityStateChanged();
        MaximumIndex = ResolveMaximumIndex();

        int startIndex = Math.Max(0, Math.Min(previousStartIndex, MaximumIndex));
        int endIndex = Math.Max(startIndex, Math.Min(previousEndIndex, MaximumIndex));
        if (shouldFollowLatest)
        {
            endIndex = MaximumIndex;
        }

        SetProperty(ref _rangeStartText, startIndex.ToString(CultureInfo.InvariantCulture), "RangeStartText");
        SetProperty(ref _rangeEndText, endIndex.ToString(CultureInfo.InvariantCulture), "RangeEndText");
        if (CanReuseRenderFrameData(previousSnapshot, _snapshot, startIndex, endIndex))
        {
            if (RequiresLocalizedFrameRefresh(previousSnapshot, _snapshot))
            {
                RefreshLocalizedFrameOnly();
            }
        }
        else
        {
            RebuildRenderFrame();
        }
    }

    private int ResolveMaximumIndex()
    {
        int maximumEventIndex = -1;
        foreach (MousePerformanceSessionArchive session in EnumerateVisibleSessions())
        {
            if (session != null && session.Snapshot != null && session.Snapshot.EventCount > 0)
            {
                maximumEventIndex = Math.Max(maximumEventIndex, session.Snapshot.EventCount - 1);
            }
        }

        return maximumEventIndex < 0 ? 0 : maximumEventIndex;
    }

    private IEnumerable<MousePerformanceSessionArchive> EnumerateActiveSessions()
    {
        if (_baselineSession != null)
        {
            yield return _baselineSession;
        }
        foreach (MousePerformanceSessionArchive comparisonSession in _comparisonSessions)
        {
            if (comparisonSession != null)
            {
                yield return comparisonSession;
            }
        }
    }

    private IEnumerable<MousePerformanceSessionArchive> EnumerateVisibleSessions()
    {
        foreach (MousePerformanceChartDatasetSlot datasetSlot in ResolveVisibleDatasetSlots())
        {
            MousePerformanceSessionArchive session = ResolveSessionByDatasetSlot(datasetSlot);
            if (session != null)
            {
                yield return session;
            }
        }
    }

    private void RefreshLegendItems()
    {
        _legendItems.Clear();
        if (_comparisonSessions.Count == 0 || _baselineSession == null)
        {
            RaisePropertyChanged("HasLegendItems");
            return;
        }

        int visibleDatasetCount = ResolveVisibleDatasetCount();
        int nextImportedOrdinal = 0;
        if (_baselineSession.SourceMode == MousePerformanceSessionSourceMode.Live)
        {
            _legendItems.Add(CreateLegendItem(MousePerformanceChartDatasetSlot.Baseline, L("MousePerformance.Chart.Legend.LiveBaseline", _baselineSession.DisplayName), visibleDatasetCount));
        }
        else
        {
            _legendItems.Add(CreateLegendItem(MousePerformanceChartDatasetSlot.Baseline, ResolveImportedLegendText(0, _baselineSession.DisplayName), visibleDatasetCount));
            nextImportedOrdinal = 1;
        }

        if (_comparisonSessions.Count >= 1)
        {
            _legendItems.Add(CreateLegendItem(MousePerformanceChartDatasetSlot.CompareA, ResolveImportedLegendText(nextImportedOrdinal, _comparisonSessions[0].DisplayName), visibleDatasetCount));
        }
        if (_comparisonSessions.Count >= 2)
        {
            _legendItems.Add(CreateLegendItem(MousePerformanceChartDatasetSlot.CompareB, ResolveImportedLegendText(nextImportedOrdinal + 1, _comparisonSessions[1].DisplayName), visibleDatasetCount));
        }

        RaisePropertyChanged("HasLegendItems");
    }

    private void RefreshGapAnalysisTargetOptions()
    {
        List<MousePerformanceChartGapAnalysisTargetOption> availableOptions = new List<MousePerformanceChartGapAnalysisTargetOption>();
        if (IsDatasetVisible(MousePerformanceChartDatasetSlot.Baseline))
        {
            availableOptions.Add(new MousePerformanceChartGapAnalysisTargetOption(MousePerformanceChartDatasetSlot.Baseline, ResolveGapAnalysisTargetDisplayName(MousePerformanceChartDatasetSlot.Baseline)));
        }
        if (IsDatasetVisible(MousePerformanceChartDatasetSlot.CompareA))
        {
            availableOptions.Add(new MousePerformanceChartGapAnalysisTargetOption(MousePerformanceChartDatasetSlot.CompareA, ResolveGapAnalysisTargetDisplayName(MousePerformanceChartDatasetSlot.CompareA)));
        }
        if (IsDatasetVisible(MousePerformanceChartDatasetSlot.CompareB))
        {
            availableOptions.Add(new MousePerformanceChartGapAnalysisTargetOption(MousePerformanceChartDatasetSlot.CompareB, ResolveGapAnalysisTargetDisplayName(MousePerformanceChartDatasetSlot.CompareB)));
        }

        _gapAnalysisTargetOptions.Clear();
        foreach (MousePerformanceChartGapAnalysisTargetOption option in availableOptions)
        {
            _gapAnalysisTargetOptions.Add(option);
        }

        RaisePropertyChanged("IsGapAnalysisSelectionVisible");
        EnsureGapAnalysisTargetSelection();
    }

    private void EnsureGapAnalysisTargetSelection()
    {
        if (_gapAnalysisTargetOptions.Count == 0)
        {
            if (_selectedGapAnalysisDatasetSlot != MousePerformanceChartDatasetSlot.Baseline)
            {
                _selectedGapAnalysisDatasetSlot = MousePerformanceChartDatasetSlot.Baseline;
                RaisePropertyChanged("SelectedGapAnalysisDatasetSlot");
            }
            ResetVisibleGapMetrics();
        }
        else if (ContainsGapAnalysisTargetOption(_selectedGapAnalysisDatasetSlot))
        {
            RaisePropertyChanged("SelectedGapAnalysisDatasetSlot");
        }
        else
        {
            _selectedGapAnalysisDatasetSlot = _gapAnalysisTargetOptions[0].DatasetSlot;
            RaisePropertyChanged("SelectedGapAnalysisDatasetSlot");
            ResetVisibleGapMetrics();
        }
    }

    private void SelectFirstGapAnalysisTargetOption()
    {
        if (_gapAnalysisTargetOptions.Count != 0)
        {
            MousePerformanceChartDatasetSlot datasetSlot = _gapAnalysisTargetOptions[0].DatasetSlot;
            _selectedGapAnalysisDatasetSlot = datasetSlot;
            RaisePropertyChanged("SelectedGapAnalysisDatasetSlot");
            ResetVisibleGapMetrics();
        }
    }

    private bool ContainsGapAnalysisTargetOption(MousePerformanceChartDatasetSlot datasetSlot)
    {
        return _gapAnalysisTargetOptions.Any((MousePerformanceChartGapAnalysisTargetOption optionItem) => optionItem != null && optionItem.DatasetSlot == datasetSlot);
    }

    private string ResolveGapAnalysisTargetDisplayName(MousePerformanceChartDatasetSlot datasetSlot)
    {
        MousePerformanceSessionArchive session = ResolveSessionByDatasetSlot(datasetSlot);
        if (session == null)
        {
            return string.Empty;
        }
        if (datasetSlot == MousePerformanceChartDatasetSlot.Baseline && session.SourceMode == MousePerformanceSessionSourceMode.Live)
        {
            return L("MousePerformance.Chart.GapTarget.LiveBaseline", session.DisplayName);
        }

        char importedLetter = ResolveImportedDatasetLetter(datasetSlot);
        return L("MousePerformance.Chart.GapTarget.Imported", importedLetter.ToString(), session.DisplayName);
    }

    private int ResolveImportedOrdinalForDatasetSlot(MousePerformanceChartDatasetSlot datasetSlot)
    {
        int firstComparisonOrdinal = (_baselineSession == null || _baselineSession.SourceMode != MousePerformanceSessionSourceMode.Live) ? 1 : 0;
        return datasetSlot switch
        {
            MousePerformanceChartDatasetSlot.Baseline => 0,
            MousePerformanceChartDatasetSlot.CompareA => firstComparisonOrdinal,
            MousePerformanceChartDatasetSlot.CompareB => firstComparisonOrdinal + 1,
            _ => 0
        };
    }

    private char ResolveImportedDatasetLetter(MousePerformanceChartDatasetSlot datasetSlot)
    {
        return (char)('A' + Math.Max(0, ResolveImportedOrdinalForDatasetSlot(datasetSlot)));
    }

    private MousePerformanceSessionArchive ResolveSessionByDatasetSlot(MousePerformanceChartDatasetSlot datasetSlot)
    {
        switch (datasetSlot)
        {
            case MousePerformanceChartDatasetSlot.Baseline:
                return _baselineSession;
            case MousePerformanceChartDatasetSlot.CompareA:
                if (_comparisonSessions.Count >= 1)
                {
                    return _comparisonSessions[0];
                }
                break;
            case MousePerformanceChartDatasetSlot.CompareB:
                if (_comparisonSessions.Count >= 2)
                {
                    return _comparisonSessions[1];
                }
                break;
        }
        return null;
    }

    private string ResolveImportedLegendText(int importedOrdinal, string displayName)
    {
        char importedLetter = (char)('A' + Math.Max(0, importedOrdinal));
        return L("MousePerformance.Chart.Legend.Imported", importedLetter.ToString(), displayName ?? string.Empty);
    }

    private MousePerformanceChartLegendItem CreateLegendItem(MousePerformanceChartDatasetSlot datasetSlot, string legendText, int visibleDatasetCount)
    {
        bool isVisible = IsDatasetVisible(datasetSlot);
        bool canToggleVisibility = !isVisible || visibleDatasetCount > 1;
        return new MousePerformanceChartLegendItem(
            datasetSlot,
            legendText,
            MousePerformanceChartColorPalette.CreateBrush(datasetSlot, MousePerformanceChartSeriesPalette.Primary, 1.0),
            MousePerformanceChartColorPalette.CreateBrush(datasetSlot, MousePerformanceChartSeriesPalette.Secondary, 1.0),
            MousePerformanceChartColorPalette.CreateBrush(datasetSlot, MousePerformanceChartSeriesPalette.Accent, 1.0),
            isVisible,
            canToggleVisibility,
            HandleLegendItemVisibilityChanged);
    }

    private void SynchronizeDatasetPresentationStates()
    {
        IReadOnlyList<DatasetSessionEntry> activeEntries = ResolveActiveDatasetEntries();
        HashSet<MousePerformanceChartDatasetSlot> activeSlots = new HashSet<MousePerformanceChartDatasetSlot>(activeEntries.Select(entry => entry.DatasetSlot));
        foreach (DatasetSessionEntry activeEntry in activeEntries)
        {
            MousePerformanceChartDatasetSlot datasetSlot = activeEntry.DatasetSlot;
            string sessionIdentity = ResolveSessionIdentity(activeEntry.Session);
            MousePerformanceChartDatasetPresentationState value = null;
            if (!_datasetPresentationStates.TryGetValue(datasetSlot, out value) || !string.Equals(value.SessionIdentity, sessionIdentity, StringComparison.Ordinal))
            {
                ActivateDatasetSlot(datasetSlot, sessionIdentity);
            }
        }
        MousePerformanceChartDatasetSlot[] inactiveSlots = _datasetPresentationStates.Keys.Where(slot => !activeSlots.Contains(slot)).ToArray();
        foreach (MousePerformanceChartDatasetSlot inactiveSlot in inactiveSlots)
        {
            _datasetPresentationStates.Remove(inactiveSlot);
        }
        EnsureAtLeastOneVisibleDataset();
    }

    private IReadOnlyList<DatasetSessionEntry> ResolveActiveDatasetEntries()
    {
        List<DatasetSessionEntry> activeEntries = new List<DatasetSessionEntry>();
        if (_baselineSession != null && _baselineSession.HasData)
        {
            activeEntries.Add(new DatasetSessionEntry(MousePerformanceChartDatasetSlot.Baseline, _baselineSession));
        }
        if (_comparisonSessions.Count >= 1 && _comparisonSessions[0] != null && _comparisonSessions[0].HasData)
        {
            activeEntries.Add(new DatasetSessionEntry(MousePerformanceChartDatasetSlot.CompareA, _comparisonSessions[0]));
        }
        if (_comparisonSessions.Count >= 2 && _comparisonSessions[1] != null && _comparisonSessions[1].HasData)
        {
            activeEntries.Add(new DatasetSessionEntry(MousePerformanceChartDatasetSlot.CompareB, _comparisonSessions[1]));
        }
        return activeEntries;
    }

    private int ResolveVisibleDatasetCount()
    {
        return ResolveVisibleDatasetSlots().Count;
    }

    private IReadOnlyList<MousePerformanceChartDatasetSlot> ResolveVisibleDatasetSlots()
    {
        return (from entry in ResolveActiveDatasetEntries()
                select entry.DatasetSlot into datasetSlot
                where IsDatasetVisible(datasetSlot)
                orderby ResolveDatasetActivationOrder(datasetSlot), (int)datasetSlot
                select datasetSlot).ToArray();
    }

    private bool IsDatasetVisible(MousePerformanceChartDatasetSlot datasetSlot)
    {
        MousePerformanceChartDatasetPresentationState value = null;
        if (!_datasetPresentationStates.TryGetValue(datasetSlot, out value))
        {
            return false;
        }
        return value != null && value.IsVisible && ResolveSessionByDatasetSlot(datasetSlot) != null && ResolveSessionByDatasetSlot(datasetSlot).HasData;
    }

    private long ResolveDatasetActivationOrder(MousePerformanceChartDatasetSlot datasetSlot)
    {
        MousePerformanceChartDatasetPresentationState value = null;
        if (!_datasetPresentationStates.TryGetValue(datasetSlot, out value) || value == null)
        {
            return long.MinValue;
        }
        return value.ActivationOrder;
    }

    private void ActivateDatasetSlot(MousePerformanceChartDatasetSlot datasetSlot, string sessionIdentity = null)
    {
        string resolvedSessionIdentity = sessionIdentity ?? ResolveSessionIdentity(ResolveSessionByDatasetSlot(datasetSlot));
        if (!_datasetPresentationStates.TryGetValue(datasetSlot, out MousePerformanceChartDatasetPresentationState state) || state == null)
        {
            state = new MousePerformanceChartDatasetPresentationState(resolvedSessionIdentity, isVisible: true, 0L);
            _datasetPresentationStates[datasetSlot] = state;
        }

        state.SessionIdentity = resolvedSessionIdentity;
        state.IsVisible = true;
        state.ActivationOrder = _nextDatasetActivationOrder;
        _nextDatasetActivationOrder++;
    }

    private void EnsureAtLeastOneVisibleDataset()
    {
        if (ResolveVisibleDatasetCount() <= 0)
        {
            DatasetSessionEntry firstActiveEntry = ResolveActiveDatasetEntries().FirstOrDefault();
            if (firstActiveEntry != null)
            {
                ActivateDatasetSlot(firstActiveEntry.DatasetSlot, ResolveSessionIdentity(firstActiveEntry.Session));
            }
        }
    }

    private void HandleLegendItemVisibilityChanged(MousePerformanceChartDatasetSlot datasetSlot, bool isVisible)
    {
        MousePerformanceChartDatasetPresentationState value = null;
        if (!_datasetPresentationStates.TryGetValue(datasetSlot, out value) || value == null)
        {
            return;
        }
        if (isVisible)
        {
            ActivateDatasetSlot(datasetSlot);
        }
        else
        {
            if (ResolveVisibleDatasetCount() <= 1)
            {
                RefreshLegendItems();
                return;
            }
            value.IsVisible = false;
        }
        EnsureAtLeastOneVisibleDataset();
        RefreshVisibleSessionRangeState();
        RefreshGapAnalysisTargetOptions();
        RefreshLegendItems();
        RebuildRenderFrame();
    }

    private void RefreshVisibleSessionRangeState()
    {
        int previousMaximumIndex = _maximumIndex;
        int previousStartIndex = ParseIndex(_rangeStartText, 0);
        int previousEndIndex = ParseIndex(_rangeEndText, previousMaximumIndex);
        bool shouldFollowLatest = previousEndIndex >= previousMaximumIndex;

        MaximumIndex = ResolveMaximumIndex();
        int startIndex = Math.Max(0, Math.Min(previousStartIndex, MaximumIndex));
        int endIndex = Math.Max(startIndex, Math.Min(previousEndIndex, MaximumIndex));
        if (shouldFollowLatest)
        {
            endIndex = MaximumIndex;
        }

        SetProperty(ref _rangeStartText, startIndex.ToString(CultureInfo.InvariantCulture), "RangeStartText");
        SetProperty(ref _rangeEndText, endIndex.ToString(CultureInfo.InvariantCulture), "RangeEndText");
    }

    private void RaiseSessionCapabilityStateChanged()
    {
        RaisePropertyChanged("ComparisonSessionCount");
        RaisePropertyChanged("HasLegendItems");
        RaisePropertyChanged("CanExportBaselineSession");
        RaisePropertyChanged("CanImportComparisonSession");
        RaisePropertyChanged("CanSavePng");
        RaisePropertyChanged("IsGapAnalysisSelectionVisible");
    }

    private bool HasSingleLiveBaselineSession()
    {
        if (_baselineSession != null && _baselineSession.HasData && _baselineSession.SourceMode == MousePerformanceSessionSourceMode.Live)
        {
            return _comparisonSessions.Count == 0;
        }
        return false;
    }

    private bool IsBaselineCollecting()
    {
        if (_baselineSession != null && _baselineSession.Snapshot != null)
        {
            return _baselineSession.Snapshot.Status == MousePerformanceSessionStatus.Collecting;
        }
        return false;
    }

    private static IReadOnlyList<MousePerformanceSessionArchive> NormalizeComparisonSessions(MousePerformanceSessionArchive baselineSession, IEnumerable<MousePerformanceSessionArchive> comparisonSessions)
    {
        if (baselineSession == null || !baselineSession.HasData || comparisonSessions == null)
        {
            return Array.Empty<MousePerformanceSessionArchive>();
        }

        List<MousePerformanceSessionArchive> normalizedSessions = new List<MousePerformanceSessionArchive>();
        foreach (MousePerformanceSessionArchive comparisonSession in comparisonSessions)
        {
            if (comparisonSession != null && comparisonSession.HasData)
            {
                normalizedSessions.Add(comparisonSession);
                if (normalizedSessions.Count >= 2)
                {
                    break;
                }
            }
        }

        return normalizedSessions;
    }

    private static string ResolveSessionIdentity(MousePerformanceSessionArchive session)
    {
        return MousePerformanceSessionIdentityResolver.ResolveSessionInstanceIdentity(session);
    }

    private static string ResolveSessionContentIdentity(MousePerformanceSessionArchive session)
    {
        return MousePerformanceSessionIdentityResolver.ResolveSessionContentIdentity(session);
    }

    private MousePerformanceTimeBasis ResolveEffectiveTimeBasis()
    {
        return _selectedTimeBasis;
    }

    private void RebuildRenderFrame()
    {
        int startIndex = ParseIndex(_rangeStartText, 0);
        int endIndex = ParseIndex(_rangeEndText, MaximumIndex);
        startIndex = Math.Max(0, Math.Min(startIndex, MaximumIndex));
        endIndex = Math.Max(startIndex, Math.Min(endIndex, MaximumIndex));
        RenderRequestState request = CreateCurrentRenderRequest(startIndex, endIndex);
        int requestVersion = Interlocked.Increment(ref _renderBuildVersion);
        CancellationTokenSource previousCancellation = _renderBuildCancellation;
        _renderBuildCancellation = new CancellationTokenSource();
        if (previousCancellation != null)
        {
            previousCancellation.Cancel();
            previousCancellation.Dispose();
        }

        ResetVisibleGapMetrics();
        MousePerformanceChartRenderFrame previousFrame = _renderFrame;
        MousePerformanceChartRenderFrame cachedFrame = null;
        if (TryCreateMergedRenderFrameFromCache(request, ref cachedFrame))
        {
            ScheduleStatisticsWarmup(cachedFrame, _renderBuildCancellation.Token);
            RenderFrame = LocalizeFrame(cachedFrame);
            RefreshToolbarDiagnostics();
            return;
        }

        if (!CanKeepCurrentRenderFrameVisibleDuringBuild(previousFrame))
        {
            _ = ShowLoadingFrameIfBuildIsStillPendingAsync(request, requestVersion, previousFrame, _renderBuildCancellation.Token);
        }
        CancellationToken token = _renderBuildCancellation.Token;
        _ = BuildAndApplyRenderFrameAsync(request, requestVersion, token);
    }

    private static bool CanKeepCurrentRenderFrameVisibleDuringBuild(MousePerformanceChartRenderFrame frame)
    {
        return frame?.IsAvailable ?? false;
    }

    private async Task ShowLoadingFrameIfBuildIsStillPendingAsync(RenderRequestState request, int requestVersion, MousePerformanceChartRenderFrame previousFrame, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(120, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        RunOnUiThread(() =>
        {
            if (!_disposed && !cancellationToken.IsCancellationRequested && requestVersion == _renderBuildVersion)
            {
                if (previousFrame == null)
                {
                    if (_renderFrame != null)
                    {
                        return;
                    }
                }
                else if (!ReferenceEquals(_renderFrame, previousFrame))
                {
                    return;
                }
                RenderFrame = LocalizeFrame(CreateLoadingFrame(request));
                RefreshToolbarDiagnostics();
            }
        });
    }

    private RenderRequestState CreateCurrentRenderRequest(int startIndex, int endIndex)
    {
        DatasetSessionEntry[] visibleEntries = (from entry in ResolveVisibleDatasetSlots().Select(datasetSlot =>
            {
                MousePerformanceSessionArchive session = ResolveSessionByDatasetSlot(datasetSlot);
                return (session != null && session.HasData) ? new DatasetSessionEntry(datasetSlot, session) : null;
            })
                                                where entry != null
                                                select entry).ToArray();
        return new RenderRequestState(_selectedPlotType, ResolveEffectiveTimeBasis(), startIndex, endIndex, ResolveEffectiveShowStem(), ResolveEffectiveShowLines(), ResolveActiveDatasetEntries().Count, visibleEntries);
    }

    private bool TryCreateMergedRenderFrameFromCache(RenderRequestState request, ref MousePerformanceChartRenderFrame frame)
    {
        frame = null;
        if (request == null)
        {
            return false;
        }
        if (request.VisibleEntries.Count == 0)
        {
            frame = MousePerformanceEngine.CreateChartRenderFrame(null, request.PlotType, request.StartIndex, request.EndIndex, request.ShowStem, request.ShowLines, request.TimeBasis);
            return true;
        }
        if (MousePerformancePlotTraits.IsHistogramPlot(request.PlotType) && request.VisibleEntries.Count > 1)
        {
            return false;
        }
        Dictionary<MousePerformanceChartDatasetSlot, MousePerformanceChartRenderFrame> framesByDatasetSlot = new Dictionary<MousePerformanceChartDatasetSlot, MousePerformanceChartRenderFrame>();
        foreach (DatasetSessionEntry visibleEntry in request.VisibleEntries)
        {
            MousePerformanceChartRenderFrame datasetFrame = null;
            if (!_analysisCache.TryGetFrame(visibleEntry.Session, request.PlotType, request.StartIndex, request.EndIndex, request.ShowStem, request.ShowLines, request.TimeBasis, ref datasetFrame))
            {
                return false;
            }
            framesByDatasetSlot[visibleEntry.DatasetSlot] = datasetFrame;
        }
        frame = CreateMergedRenderFrameFromResolvedFrames(request, framesByDatasetSlot);
        return true;
    }

    private async Task BuildAndApplyRenderFrameAsync(RenderRequestState request, int requestVersion, CancellationToken cancellationToken)
    {
        try
        {
            MousePerformanceChartRenderFrame result = await BuildMergedRenderFrameAsync(request, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            RunOnUiThread(() =>
            {
                if (!_disposed && !cancellationToken.IsCancellationRequested && requestVersion == _renderBuildVersion)
                {
                    ResetVisibleGapMetrics();
                    ScheduleStatisticsWarmup(result, cancellationToken);
                    RenderFrame = LocalizeFrame(result);
                    RefreshToolbarDiagnostics();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Exception arg = ex;
            Trace.TraceError($"Mouse performance chart build failed: {arg}");
            RunOnUiThread(() =>
            {
                if (!_disposed && !cancellationToken.IsCancellationRequested && requestVersion == _renderBuildVersion)
                {
                    ResetVisibleGapMetrics();
                    RenderFrame = LocalizeFrame(CreateUnavailableFrame(request));
                    RefreshToolbarDiagnostics();
                }
            });
        }
    }

    private async Task<MousePerformanceChartRenderFrame> BuildMergedRenderFrameAsync(RenderRequestState request, CancellationToken cancellationToken)
    {
        if (request == null || request.VisibleEntries.Count == 0)
        {
            MousePerformancePlotType plotType = MousePerformancePlotType.XCountVsTime;
            int startIndex = 0;
            int endIndex = 0;
            bool showStem = false;
            bool showLines = false;
            MousePerformanceTimeBasis timeBasis = MousePerformanceTimeBasis.RawCapture;
            if (request != null)
            {
                plotType = request.PlotType;
                startIndex = request.StartIndex;
                endIndex = request.EndIndex;
                showStem = request.ShowStem;
                showLines = request.ShowLines;
                timeBasis = request.TimeBasis;
            }
            return MousePerformanceEngine.CreateChartRenderFrame(null, plotType, startIndex, endIndex, showStem, showLines, timeBasis);
        }

        if (MousePerformancePlotTraits.IsHistogramPlot(request.PlotType) && request.VisibleEntries.Count > 1)
        {
            return await BuildMergedHistogramRenderFrameAsync(request, cancellationToken);
        }

        List<MousePerformanceChartDatasetSlot> datasetSlots = new List<MousePerformanceChartDatasetSlot>();
        List<Task<MousePerformanceChartRenderFrame>> frameBuildTasks = new List<Task<MousePerformanceChartRenderFrame>>();
        foreach (DatasetSessionEntry visibleEntry in request.VisibleEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            datasetSlots.Add(visibleEntry.DatasetSlot);
            frameBuildTasks.Add(_analysisCache.GetOrBuildFrameAsync(visibleEntry.Session, request.PlotType, request.StartIndex, request.EndIndex, request.ShowStem, request.ShowLines, request.TimeBasis, cancellationToken));
        }

        MousePerformanceChartRenderFrame[] resolvedFrames = await Task.WhenAll(frameBuildTasks);
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<MousePerformanceChartDatasetSlot, MousePerformanceChartRenderFrame> framesBySlot = new Dictionary<MousePerformanceChartDatasetSlot, MousePerformanceChartRenderFrame>();
        for (int datasetIndex = 0; datasetIndex < datasetSlots.Count; datasetIndex++)
        {
            framesBySlot[datasetSlots[datasetIndex]] = resolvedFrames[datasetIndex];
        }

        return CreateMergedRenderFrameFromResolvedFrames(request, framesBySlot);
    }

    private static Task<MousePerformanceChartRenderFrame> BuildMergedHistogramRenderFrameAsync(RenderRequestState request, CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return Task.FromResult<MousePerformanceChartRenderFrame>(null);
        }

        List<MousePerformanceChartDatasetSession> sessions = new List<MousePerformanceChartDatasetSession>(request.VisibleEntries.Count);
        foreach (DatasetSessionEntry visibleEntry in request.VisibleEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visibleEntry?.Session?.Snapshot != null)
            {
                sessions.Add(new MousePerformanceChartDatasetSession(visibleEntry.DatasetSlot, visibleEntry.Session));
            }
        }

        if (sessions.Count == 0)
        {
            return Task.FromResult<MousePerformanceChartRenderFrame>(null);
        }

        return MousePerformanceChartAnalysisCache.Instance.GetOrBuildComparisonHistogramFrameAsync(sessions, request.PlotType, request.StartIndex, request.EndIndex, request.ShowStem, request.ShowLines, request.TimeBasis, cancellationToken);
    }

    private MousePerformanceChartRenderFrame CreateMergedRenderFrameFromResolvedFrames(RenderRequestState request, IReadOnlyDictionary<MousePerformanceChartDatasetSlot, MousePerformanceChartRenderFrame> frameBySlot)
    {
        if (request == null)
        {
            return MousePerformanceEngine.CreateChartRenderFrame(null, MousePerformancePlotType.XCountVsTime, 0, 0, showStem: false, showLines: false, MousePerformanceTimeBasis.RawCapture);
        }
        if (frameBySlot == null || frameBySlot.Count == 0)
        {
            return MousePerformanceEngine.CreateChartRenderFrame(null, request.PlotType, request.StartIndex, request.EndIndex, request.ShowStem, request.ShowLines, request.TimeBasis);
        }

        List<DatasetRenderFrameEntry> availableFrames = new List<DatasetRenderFrameEntry>();
        foreach (DatasetSessionEntry visibleEntry in request.VisibleEntries)
        {
            if (frameBySlot.TryGetValue(visibleEntry.DatasetSlot, out MousePerformanceChartRenderFrame resolvedFrame) && resolvedFrame != null && resolvedFrame.IsAvailable)
            {
                availableFrames.Add(new DatasetRenderFrameEntry(visibleEntry.DatasetSlot, resolvedFrame));
            }
        }

        if (availableFrames.Count == 0)
        {
            DatasetRenderFrameEntry fallbackFrame = null;
            foreach (DatasetSessionEntry visibleEntry in request.VisibleEntries)
            {
                if (frameBySlot.TryGetValue(visibleEntry.DatasetSlot, out MousePerformanceChartRenderFrame resolvedFrame) && resolvedFrame != null)
                {
                    fallbackFrame = new DatasetRenderFrameEntry(visibleEntry.DatasetSlot, resolvedFrame);
                    break;
                }
            }
            if (fallbackFrame == null)
            {
                return MousePerformanceEngine.CreateChartRenderFrame(null, request.PlotType, request.StartIndex, request.EndIndex, request.ShowStem, request.ShowLines, request.TimeBasis);
            }
            return CloneRenderFrameForDisplay(fallbackFrame.Frame, fallbackFrame.DatasetSlot, request.TimeBasis, request.ActiveDatasetCount > 1);
        }

        if (MousePerformancePlotTraits.IsHistogramPlot(request.PlotType) && availableFrames.Count > 1)
        {
            MousePerformanceChartRenderFrame histogramFrame = CreateMergedHistogramRenderFrame(request, availableFrames, cancellationToken: default);
            if (histogramFrame != null)
            {
                return histogramFrame;
            }
        }

        if (availableFrames.Count == 1)
        {
            return CloneRenderFrameForDisplay(availableFrames[0].Frame, availableFrames[0].DatasetSlot, request.TimeBasis, request.ActiveDatasetCount > 1);
        }

        IReadOnlyList<DatasetRenderFrameEntry> alignedFrames = availableFrames;
        if (request.TimeBasis == MousePerformanceTimeBasis.RawCapture && request.PlotType != MousePerformancePlotType.XVsY)
        {
            alignedFrames = AlignFramesToRawCaptureOrigin(availableFrames);
        }

        MousePerformanceChartRenderFrame[] frames = alignedFrames.Select(entry => entry.Frame).ToArray();
        double xMinimum = frames.Min(datasetFrame => datasetFrame.XMinimum);
        double xMaximum = frames.Max(datasetFrame => datasetFrame.XMaximum);
        double yMinimum = frames.Min(datasetFrame => datasetFrame.YMinimum);
        double yMaximum = frames.Max(datasetFrame => datasetFrame.YMaximum);
        List<MousePerformanceChartSeries> mergedSeries = new List<MousePerformanceChartSeries>();
        List<MousePerformanceChartGapSource> mergedGapSources = new List<MousePerformanceChartGapSource>();
        foreach (DatasetRenderFrameEntry entry in alignedFrames)
        {
            AppendDatasetSeries(mergedSeries, entry.Frame, entry.DatasetSlot);
            AppendDatasetGapSources(mergedGapSources, entry.Frame, entry.DatasetSlot);
        }

        return new MousePerformanceChartRenderFrame(request.PlotType, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, isAvailable: true, string.Empty, request.StartIndex, request.EndIndex, request.ShowStem, request.ShowLines, request.TimeBasis, xMinimum, xMaximum, yMinimum, yMaximum, mergedSeries.ToArray(), request.ActiveDatasetCount > 1, mergedGapSources.ToArray());
    }

    private static MousePerformanceChartRenderFrame CreateMergedHistogramRenderFrame(RenderRequestState request, IReadOnlyList<DatasetRenderFrameEntry> availableFrames)
    {
        return CreateMergedHistogramRenderFrame(request, availableFrames, CancellationToken.None);
    }

    private static MousePerformanceChartRenderFrame CreateMergedHistogramRenderFrame(RenderRequestState request, IReadOnlyList<DatasetRenderFrameEntry> availableFrames, CancellationToken cancellationToken)
    {
        if (request == null || availableFrames == null || availableFrames.Count == 0)
        {
            return null;
        }

        List<MousePerformanceHistogramDataset> datasets = new List<MousePerformanceHistogramDataset>(availableFrames.Count);
        foreach (DatasetRenderFrameEntry entry in availableFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MousePerformanceSnapshot snapshot = entry?.Frame != null ? ResolveSnapshotFromSessionEntry(request, entry.DatasetSlot) : null;
            if (snapshot == null)
            {
                continue;
            }
            datasets.Add(new MousePerformanceHistogramDataset(entry.DatasetSlot, snapshot));
        }

        if (datasets.Count == 0)
        {
            return null;
        }

        return MousePerformanceChartFrameBuilder.CreateComparisonHistogramRenderFrame(datasets, request.PlotType, request.StartIndex, request.EndIndex, request.ShowStem, request.ShowLines, request.TimeBasis, cancellationToken: cancellationToken);
    }

    private static MousePerformanceSnapshot ResolveSnapshotFromSessionEntry(RenderRequestState request, MousePerformanceChartDatasetSlot datasetSlot)
    {
        if (request?.VisibleEntries == null)
        {
            return null;
        }
        DatasetSessionEntry entry = request.VisibleEntries.FirstOrDefault(candidate => candidate != null && candidate.DatasetSlot == datasetSlot);
        return entry?.Session?.Snapshot;
    }

    private MousePerformanceChartRenderFrame CreateLoadingFrame(RenderRequestState request)
    {
        if (request == null)
        {
            return MousePerformanceEngine.CreateChartRenderFrame(null, MousePerformancePlotType.XCountVsTime, 0, 0, showStem: false, showLines: false, MousePerformanceTimeBasis.RawCapture);
        }
        return new MousePerformanceChartRenderFrame(request.PlotType, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, isAvailable: false, L("MousePerformance.Chart.Loading.Frame"), request.StartIndex, request.EndIndex, request.ShowStem, request.ShowLines, request.TimeBasis, 0.0, 1.0, -1.0, 1.0, Array.Empty<MousePerformanceChartSeries>(), request.ActiveDatasetCount > 1);
    }

    private MousePerformanceChartRenderFrame CreateUnavailableFrame(RenderRequestState request)
    {
        if (request == null)
        {
            return MousePerformanceEngine.CreateChartRenderFrame(null, MousePerformancePlotType.XCountVsTime, 0, 0, showStem: false, showLines: false, MousePerformanceTimeBasis.RawCapture);
        }
        return new MousePerformanceChartRenderFrame(request.PlotType, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, isAvailable: false, string.Empty, request.StartIndex, request.EndIndex, request.ShowStem, request.ShowLines, request.TimeBasis, 0.0, 1.0, -1.0, 1.0, Array.Empty<MousePerformanceChartSeries>(), request.ActiveDatasetCount > 1);
    }

    private static IReadOnlyList<DatasetRenderFrameEntry> AlignFramesToRawCaptureOrigin(IReadOnlyList<DatasetRenderFrameEntry> entries)
    {
        if (entries == null || entries.Count == 0)
        {
            return Array.Empty<DatasetRenderFrameEntry>();
        }

        List<DatasetRenderFrameEntry> alignedEntries = new List<DatasetRenderFrameEntry>(entries.Count);
        foreach (DatasetRenderFrameEntry entry in entries)
        {
            if (entry == null || entry.Frame == null)
            {
                continue;
            }

            double? minimumX = ResolveFrameSeriesMinimumX(entry.Frame);
            if (!minimumX.HasValue || Math.Abs(minimumX.Value) < double.Epsilon)
            {
                alignedEntries.Add(entry);
            }
            else
            {
                alignedEntries.Add(new DatasetRenderFrameEntry(entry.DatasetSlot, TranslateFrameAlongX(entry.Frame, -minimumX.Value)));
            }
        }

        return alignedEntries;
    }

    private static void AppendDatasetSeries(ICollection<MousePerformanceChartSeries> target, MousePerformanceChartRenderFrame frame, MousePerformanceChartDatasetSlot datasetSlot)
    {
        if (target == null || frame == null || frame.Series == null || frame.Series.Count == 0)
        {
            return;
        }

        foreach (MousePerformanceChartSeries series in frame.Series)
        {
            if (series != null)
            {
                target.Add(CloneSeriesForDataset(series, datasetSlot, series.XOffset));
            }
        }
    }

    private static MousePerformanceChartSeries CloneSeriesForDataset(MousePerformanceChartSeries series, MousePerformanceChartDatasetSlot datasetSlot, double xOffset)
    {
        if (series == null)
        {
            return null;
        }
        if (series.Kind == MousePerformanceChartSeriesKind.Histogram)
        {
            return new MousePerformanceChartSeries(series.Palette, series.HistogramBins, datasetSlot, xOffset, series.SampleBasis);
        }
        return new MousePerformanceChartSeries(series.Kind, series.Palette, series.Points, datasetSlot, xOffset, series.SampleBasis);
    }

    private static void AppendDatasetGapSources(ICollection<MousePerformanceChartGapSource> target, MousePerformanceChartRenderFrame frame, MousePerformanceChartDatasetSlot datasetSlot)
    {
        if (target == null || frame == null || frame.GapSources == null || frame.GapSources.Count == 0)
        {
            return;
        }

        foreach (MousePerformanceChartGapSource gapSource in frame.GapSources)
        {
            if (gapSource != null)
            {
                target.Add(gapSource.WithDatasetSlot(datasetSlot));
            }
        }
    }

    private static MousePerformanceChartRenderFrame TranslateFrameAlongX(MousePerformanceChartRenderFrame sourceFrame, double deltaX)
    {
        if (sourceFrame == null || Math.Abs(deltaX) < double.Epsilon)
        {
            return sourceFrame;
        }

        List<MousePerformanceChartSeries> translatedSeries = new List<MousePerformanceChartSeries>();
        if (sourceFrame.Series != null)
        {
            foreach (MousePerformanceChartSeries series in sourceFrame.Series)
            {
                if (series != null)
                {
                    translatedSeries.Add(CloneSeriesForDataset(series, series.DatasetSlot, series.XOffset + deltaX));
                }
            }
        }

        List<MousePerformanceChartGapSource> translatedGapSources = new List<MousePerformanceChartGapSource>();
        if (sourceFrame.GapSources != null)
        {
            foreach (MousePerformanceChartGapSource gapSource in sourceFrame.GapSources)
            {
                if (gapSource != null)
                {
                    translatedGapSources.Add(gapSource.WithXOffset(gapSource.XOffset + deltaX));
                }
            }
        }

        return new MousePerformanceChartRenderFrame(sourceFrame.PlotType, sourceFrame.Title, sourceFrame.Subtitle, sourceFrame.Description, sourceFrame.XAxisTitle, sourceFrame.YAxisTitle, sourceFrame.IsAvailable, sourceFrame.Message, sourceFrame.StartIndex, sourceFrame.EndIndex, sourceFrame.ShowStem, sourceFrame.ShowLines, sourceFrame.TimeBasis, sourceFrame.XMinimum + deltaX, sourceFrame.XMaximum + deltaX, sourceFrame.YMinimum, sourceFrame.YMaximum, translatedSeries.ToArray(), sourceFrame.HasComparisonDatasets, translatedGapSources.ToArray());
    }

    private static double? ResolveFrameSeriesMinimumX(MousePerformanceChartRenderFrame frame)
    {
        if (frame == null || frame.Series == null)
        {
            return null;
        }

        bool hasPoint = false;
        double minimumX = double.MaxValue;
        foreach (MousePerformanceChartSeries series in frame.Series)
        {
            if (series == null)
            {
                continue;
            }
            if (series.Kind == MousePerformanceChartSeriesKind.Histogram)
            {
                if (series.HistogramBins == null)
                {
                    continue;
                }
                foreach (MousePerformanceHistogramBin bin in series.HistogramBins)
                {
                    if (!double.IsNaN(bin.MinimumX) && !double.IsInfinity(bin.MinimumX))
                    {
                        double absoluteX = bin.MinimumX + series.XOffset;
                        if (absoluteX < minimumX)
                        {
                            minimumX = absoluteX;
                        }
                        hasPoint = true;
                    }
                }
                continue;
            }
            if (series.Points == null)
            {
                continue;
            }
            foreach (MousePerformanceChartPoint point in series.Points)
            {
                if (!double.IsNaN(point.X) && !double.IsInfinity(point.X))
                {
                    double absoluteX = point.X + series.XOffset;
                    if (absoluteX < minimumX)
                    {
                        minimumX = absoluteX;
                    }
                    hasPoint = true;
                }
            }
        }

        return hasPoint ? minimumX : null;
    }

    private static MousePerformanceChartRenderFrame CloneRenderFrameForDisplay(MousePerformanceChartRenderFrame sourceFrame, MousePerformanceChartDatasetSlot datasetSlot, MousePerformanceTimeBasis effectiveTimeBasis, bool hasComparisonDatasets)
    {
        if (sourceFrame == null)
        {
            return null;
        }

        List<MousePerformanceChartSeries> displaySeries = new List<MousePerformanceChartSeries>();
        AppendDatasetSeries(displaySeries, sourceFrame, datasetSlot);
        List<MousePerformanceChartGapSource> displayGapSources = new List<MousePerformanceChartGapSource>();
        AppendDatasetGapSources(displayGapSources, sourceFrame, datasetSlot);
        return new MousePerformanceChartRenderFrame(sourceFrame.PlotType, sourceFrame.Title, sourceFrame.Subtitle, sourceFrame.Description, sourceFrame.XAxisTitle, sourceFrame.YAxisTitle, sourceFrame.IsAvailable, sourceFrame.Message, sourceFrame.StartIndex, sourceFrame.EndIndex, sourceFrame.ShowStem, sourceFrame.ShowLines, effectiveTimeBasis, sourceFrame.XMinimum, sourceFrame.XMaximum, sourceFrame.YMinimum, sourceFrame.YMaximum, displaySeries.ToArray(), hasComparisonDatasets, displayGapSources.ToArray());
    }

    private bool CanReuseRenderFrameData(MousePerformanceSnapshot previousSnapshot, MousePerformanceSnapshot nextSnapshot, int startIndex, int endIndex)
    {
        if (ResolveActiveDatasetEntries().Count > 1 || ResolveDisplayReferenceDatasetSlot() != MousePerformanceChartDatasetSlot.Baseline)
        {
            return false;
        }
        if (_renderFrame == null || nextSnapshot == null || !_renderFrame.IsAvailable)
        {
            return false;
        }
        if (_renderFrame.HasComparisonDatasets)
        {
            return false;
        }
        if (_renderFrame.PlotType != _selectedPlotType || _renderFrame.ShowStem != ResolveEffectiveShowStem() || _renderFrame.ShowLines != ResolveEffectiveShowLines() || _renderFrame.TimeBasis != ResolveEffectiveTimeBasis() || _renderFrame.StartIndex != startIndex || _renderFrame.EndIndex != endIndex)
        {
            return false;
        }
        if (!IsVelocityPlot(_selectedPlotType))
        {
            return true;
        }
        if (previousSnapshot == null)
        {
            return false;
        }
        return previousSnapshot.CanComputeVelocity == nextSnapshot.CanComputeVelocity && Nullable.Equals(previousSnapshot.EffectiveCpi, nextSnapshot.EffectiveCpi);
    }

    private static bool RequiresLocalizedFrameRefresh(MousePerformanceSnapshot previousSnapshot, MousePerformanceSnapshot nextSnapshot)
    {
        if (previousSnapshot == null || nextSnapshot == null)
        {
            return true;
        }
        return previousSnapshot.Status != nextSnapshot.Status || !Nullable.Equals(previousSnapshot.EffectiveCpi, nextSnapshot.EffectiveCpi);
    }

    private void RefreshLocalizedFrameOnly()
    {
        if (_renderFrame != null)
        {
            RenderFrame = LocalizeFrame(_renderFrame);
        }
        RefreshToolbarDiagnostics();
    }

    private MousePerformanceChartRenderFrame LocalizeFrame(MousePerformanceChartRenderFrame frame)
    {
        if (frame == null)
        {
            return null;
        }
        MousePerformanceSnapshot snapshot = ResolveDisplayReferenceSnapshot();
        string title = ResolvePlotDisplayName(frame.PlotType);
        string subtitle = ResolveSubtitle();
        string description = ResolvePlotDescription(frame);
        string xAxisTitle = ResolveXAxisTitle(frame.PlotType, frame.TimeBasis);
        string yAxisTitle = ResolveYAxisTitle(frame.PlotType);
        string message = (string.IsNullOrWhiteSpace(frame.Message) ? ResolveUnavailableMessage(frame.PlotType, snapshot) : frame.Message);
        if (frame.IsAvailable)
        {
            message = string.Empty;
        }
        return new MousePerformanceChartRenderFrame(frame.PlotType, title, subtitle, description, xAxisTitle, yAxisTitle, frame.IsAvailable, message, frame.StartIndex, frame.EndIndex, frame.ShowStem, frame.ShowLines, frame.TimeBasis, frame.XMinimum, frame.XMaximum, frame.YMinimum, frame.YMaximum, frame.Series, frame.HasComparisonDatasets, frame.GapSources);
    }

    private void RefreshPlotOptions()
    {
        IReadOnlyList<MousePerformancePlotType> displayOrder = MousePerformancePlotTraits.ResolvePlotDisplayOrder();
        for (int plotIndex = 0; plotIndex < displayOrder.Count; plotIndex++)
        {
            MousePerformancePlotType value = displayOrder[plotIndex];
            MousePerformancePlotTypeOption plotTypeOption = _plotTypeOptions.FirstOrDefault(option => option.PlotType == value);
            if (plotTypeOption == null)
            {
                _plotTypeOptions.Insert(plotIndex, new MousePerformancePlotTypeOption(value, ResolvePlotDisplayName(value)));
            }
            else
            {
                plotTypeOption.DisplayName = ResolvePlotDisplayName(value);
                int currentIndex = _plotTypeOptions.IndexOf(plotTypeOption);
                if (currentIndex != plotIndex)
                {
                    _plotTypeOptions.Move(currentIndex, plotIndex);
                }
            }
        }

        for (int optionIndex = _plotTypeOptions.Count - 1; optionIndex >= displayOrder.Count; optionIndex--)
        {
            _plotTypeOptions.RemoveAt(optionIndex);
        }
    }

    private bool ResolveEffectiveShowStem()
    {
        return MousePerformancePlotPresentationPolicy.Resolve(_selectedPlotType).ResolveState(_showStem, _showLines).ShowStem;
    }

    private bool ResolveEffectiveShowLines()
    {
        return MousePerformancePlotPresentationPolicy.Resolve(_selectedPlotType).ResolveState(_showStem, _showLines).ShowLines;
    }

    private void RefreshTimeBasisOptions()
    {
        foreach (MousePerformanceTimeBasis value in Enum.GetValues(typeof(MousePerformanceTimeBasis)))
        {
            MousePerformanceTimeBasisOption timeBasisOption = _timeBasisOptions.FirstOrDefault(option => option.TimeBasis == value);
            if (timeBasisOption == null)
            {
                _timeBasisOptions.Add(new MousePerformanceTimeBasisOption(value, ResolveTimeBasisDisplayName(value)));
            }
            else
            {
                timeBasisOption.DisplayName = ResolveTimeBasisDisplayName(value);
            }
        }
    }

    private void RefreshWindowText()
    {
        WindowTitleText = L("MousePerformance.Chart.WindowTitle");
    }

    private string ResolvePlotDisplayName(MousePerformancePlotType plotType)
    {
        return plotType switch
        {
            MousePerformancePlotType.XCountVsTime => L("MousePerformance.Chart.Plot.XCountVsTime"),
            MousePerformancePlotType.YCountVsTime => L("MousePerformance.Chart.Plot.YCountVsTime"),
            MousePerformancePlotType.XYCountVsTime => L("MousePerformance.Chart.Plot.XYCountVsTime"),
            MousePerformancePlotType.IntervalVsTime => L("MousePerformance.Chart.Plot.IntervalVsTime"),
            MousePerformancePlotType.XVelocityVsTime => L("MousePerformance.Chart.Plot.XVelocityVsTime"),
            MousePerformancePlotType.YVelocityVsTime => L("MousePerformance.Chart.Plot.YVelocityVsTime"),
            MousePerformancePlotType.XYVelocityVsTime => L("MousePerformance.Chart.Plot.XYVelocityVsTime"),
            MousePerformancePlotType.FrequencyVsTime => L("MousePerformance.Chart.Plot.FrequencyVsTime"),
            MousePerformancePlotType.XSumVsTime => L("MousePerformance.Chart.Plot.XSumVsTime"),
            MousePerformancePlotType.YSumVsTime => L("MousePerformance.Chart.Plot.YSumVsTime"),
            MousePerformancePlotType.XYSumVsTime => L("MousePerformance.Chart.Plot.XYSumVsTime"),
            MousePerformancePlotType.PathSpeedVsTime => L("MousePerformance.Chart.Plot.PathSpeedVsTime"),
            MousePerformancePlotType.IntervalHistogram => L("MousePerformance.Chart.Plot.IntervalHistogram"),
            MousePerformancePlotType.DeltaXHistogram => L("MousePerformance.Chart.Plot.DeltaXHistogram"),
            MousePerformancePlotType.DeltaYHistogram => L("MousePerformance.Chart.Plot.DeltaYHistogram"),
            MousePerformancePlotType.DeltaMagnitudeHistogram => L("MousePerformance.Chart.Plot.DeltaMagnitudeHistogram"),
            _ => L("MousePerformance.Chart.Plot.XVsY"),
        };
    }

    private string ResolveTimeBasisDisplayName(MousePerformanceTimeBasis timeBasis)
    {
        if (timeBasis == MousePerformanceTimeBasis.RawCapture)
        {
            return L("MousePerformance.Chart.Option.TimeBasis.RawCapture");
        }
        return L("MousePerformance.Chart.Option.TimeBasis.LogicalSession");
    }

    private string ResolveXAxisTitle(MousePerformancePlotType plotType, MousePerformanceTimeBasis timeBasis)
    {
        if (plotType == MousePerformancePlotType.XVsY)
        {
            return L("MousePerformance.Chart.Axis.XCounts");
        }
        if (plotType == MousePerformancePlotType.IntervalHistogram)
        {
            return L("MousePerformance.Chart.Axis.Interval");
        }
        if (plotType == MousePerformancePlotType.DeltaXHistogram)
        {
            return L("MousePerformance.Chart.Axis.DeltaX");
        }
        if (plotType == MousePerformancePlotType.DeltaYHistogram)
        {
            return L("MousePerformance.Chart.Axis.DeltaY");
        }
        if (plotType == MousePerformancePlotType.DeltaMagnitudeHistogram)
        {
            return L("MousePerformance.Chart.Axis.DeltaMagnitude");
        }
        if (timeBasis == MousePerformanceTimeBasis.RawCapture)
        {
            return L("MousePerformance.Chart.Axis.Time.RawCapture");
        }
        return L("MousePerformance.Chart.Axis.Time.LogicalSession");
    }

    private string ResolveYAxisTitle(MousePerformancePlotType plotType)
    {
        return plotType switch
        {
            MousePerformancePlotType.XCountVsTime => L("MousePerformance.Chart.Axis.XCounts"),
            MousePerformancePlotType.YCountVsTime => L("MousePerformance.Chart.Axis.YCounts"),
            MousePerformancePlotType.XYCountVsTime => L("MousePerformance.Chart.Axis.XYCounts"),
            MousePerformancePlotType.IntervalVsTime => L("MousePerformance.Chart.Axis.Interval"),
            MousePerformancePlotType.FrequencyVsTime => L("MousePerformance.Chart.Axis.Frequency"),
            MousePerformancePlotType.XVelocityVsTime => L("MousePerformance.Chart.Axis.XVelocity"),
            MousePerformancePlotType.YVelocityVsTime => L("MousePerformance.Chart.Axis.YVelocity"),
            MousePerformancePlotType.XYVelocityVsTime => L("MousePerformance.Chart.Axis.XYVelocity"),
            MousePerformancePlotType.XSumVsTime => L("MousePerformance.Chart.Axis.XSum"),
            MousePerformancePlotType.YSumVsTime => L("MousePerformance.Chart.Axis.YSum"),
            MousePerformancePlotType.XYSumVsTime => L("MousePerformance.Chart.Axis.XYSum"),
            MousePerformancePlotType.PathSpeedVsTime => L("MousePerformance.Chart.Axis.PathSpeed"),
            MousePerformancePlotType.IntervalHistogram or MousePerformancePlotType.DeltaXHistogram or MousePerformancePlotType.DeltaYHistogram or MousePerformancePlotType.DeltaMagnitudeHistogram => L("MousePerformance.Chart.Axis.HistogramPercent"),
            _ => L("MousePerformance.Chart.Axis.YCounts"),
        };
    }

    private string ResolvePlotDescription(MousePerformanceChartRenderFrame frame)
    {
        MousePerformancePlotType plotType = frame?.PlotType ?? MousePerformancePlotType.XCountVsTime;
        List<string> descriptionSegments = new List<string>();
        switch (plotType)
        {
            case MousePerformancePlotType.XCountVsTime:
                descriptionSegments.Add(L("MousePerformance.Chart.Description.XCountVsTime"));
                break;
            case MousePerformancePlotType.YCountVsTime:
                descriptionSegments.Add(L("MousePerformance.Chart.Description.YCountVsTime"));
                break;
            case MousePerformancePlotType.XYCountVsTime:
                descriptionSegments.Add(L("MousePerformance.Chart.Description.XYCountVsTime"));
                break;
            case MousePerformancePlotType.IntervalVsTime:
                descriptionSegments.Add(L("MousePerformance.Chart.Description.IntervalVsTime"));
                break;
            case MousePerformancePlotType.FrequencyVsTime:
                descriptionSegments.Add(L("MousePerformance.Chart.Description.FrequencyVsTime"));
                break;
            case MousePerformancePlotType.XVelocityVsTime:
                descriptionSegments.Add(L("MousePerformance.Chart.Description.XVelocityVsTime"));
                break;
            case MousePerformancePlotType.YVelocityVsTime:
                descriptionSegments.Add(L("MousePerformance.Chart.Description.YVelocityVsTime"));
                break;
            case MousePerformancePlotType.XYVelocityVsTime:
                descriptionSegments.Add(L("MousePerformance.Chart.Description.XYVelocityVsTime"));
                break;
            case MousePerformancePlotType.PathSpeedVsTime:
                descriptionSegments.Add(L("MousePerformance.Chart.Description.PathSpeedVsTime"));
                break;
            case MousePerformancePlotType.IntervalHistogram:
                descriptionSegments.Add(L("MousePerformance.Chart.Description.IntervalHistogram"));
                break;
            case MousePerformancePlotType.DeltaXHistogram:
                descriptionSegments.Add(L("MousePerformance.Chart.Description.DeltaXHistogram"));
                break;
            case MousePerformancePlotType.DeltaYHistogram:
                descriptionSegments.Add(L("MousePerformance.Chart.Description.DeltaYHistogram"));
                break;
            case MousePerformancePlotType.DeltaMagnitudeHistogram:
                descriptionSegments.Add(L("MousePerformance.Chart.Description.DeltaMagnitudeHistogram"));
                break;
            case MousePerformancePlotType.XSumVsTime:
                descriptionSegments.Add(L("MousePerformance.Chart.Description.XSumVsTime"));
                break;
            case MousePerformancePlotType.YSumVsTime:
                descriptionSegments.Add(L("MousePerformance.Chart.Description.YSumVsTime"));
                break;
            case MousePerformancePlotType.XYSumVsTime:
                descriptionSegments.Add(L("MousePerformance.Chart.Description.XYSumVsTime"));
                break;
            default:
                descriptionSegments.Add(L("MousePerformance.Chart.Description.XVsY"));
                break;
        }

        string velocityStatisticsDescription = ResolveVelocityStatisticsDescription(frame);
        if (!string.IsNullOrWhiteSpace(velocityStatisticsDescription))
        {
            descriptionSegments.Add(velocityStatisticsDescription);
        }
        string timingStatisticsDescription = ResolveTimingStatisticsDescription(frame);
        if (!string.IsNullOrWhiteSpace(timingStatisticsDescription))
        {
            descriptionSegments.Add(timingStatisticsDescription);
        }
        string residualDispersionDescription = ResolveResidualDispersionDescription(frame);
        if (!string.IsNullOrWhiteSpace(residualDispersionDescription))
        {
            descriptionSegments.Add(residualDispersionDescription);
        }
        if (!string.IsNullOrWhiteSpace(velocityStatisticsDescription) || !string.IsNullOrWhiteSpace(timingStatisticsDescription) || !string.IsNullOrWhiteSpace(residualDispersionDescription))
        {
            descriptionSegments.Add(L("MousePerformance.Chart.Description.Semantics.RobustStats"));
        }

        return string.Join(" | ", descriptionSegments.Where(segment => !string.IsNullOrWhiteSpace(segment)));
    }

    private string ResolveVelocityStatisticsDescription(MousePerformanceChartRenderFrame frame)
    {
        IReadOnlyList<MousePerformanceChartDatasetSlot> visibleDatasetSlots = ResolveVisibleDatasetSlotsForStatistics();
        if (visibleDatasetSlots.Count == 0)
        {
            return string.Empty;
        }

        bool includeLabel = visibleDatasetSlots.Count > 1;
        List<string> averageSegments = new List<string>();
        List<string> p50Segments = new List<string>();
        List<string> p95Segments = new List<string>();
        List<string> p99Segments = new List<string>();
        List<string> p999Segments = new List<string>();
        List<string> stdDevSegments = new List<string>();
        List<string> madSegments = new List<string>();
        List<string> iqrSegments = new List<string>();
        bool hasStatistics = false;
        foreach (MousePerformanceChartDatasetSlot datasetSlot in visibleDatasetSlots)
        {
            string label = ResolveCompactDatasetLabel(datasetSlot);
            MousePerformanceVelocityStatisticsSummary statistics = ResolveVelocityStatistics(frame, datasetSlot);
            string averageText = "--";
            string p50Text = "--";
            string p95Text = "--";
            string p99Text = "--";
            string p999Text = "--";
            string stdDevText = "--";
            string madText = "--";
            string iqrText = "--";
            if (statistics != null)
            {
                averageText = FormatVelocityValue(statistics.AverageMetersPerSecond);
                p50Text = FormatVelocityValue(statistics.P50MetersPerSecond);
                p95Text = FormatVelocityValue(statistics.P95MetersPerSecond);
                p99Text = FormatVelocityValue(statistics.P99MetersPerSecond);
                p999Text = FormatNullableVelocityValue(statistics.P999MetersPerSecond);
                stdDevText = FormatVelocityValue(statistics.StandardDeviationMetersPerSecond);
                madText = FormatVelocityValue(statistics.MadMetersPerSecond);
                iqrText = FormatVelocityValue(statistics.IqrMetersPerSecond);
                hasStatistics = true;
            }

            averageSegments.Add(ResolveStatisticDatasetValueText(label, averageText, includeLabel));
            p50Segments.Add(ResolveStatisticDatasetValueText(label, p50Text, includeLabel));
            p95Segments.Add(ResolveStatisticDatasetValueText(label, p95Text, includeLabel));
            p99Segments.Add(ResolveStatisticDatasetValueText(label, p99Text, includeLabel));
            p999Segments.Add(ResolveStatisticDatasetValueText(label, p999Text, includeLabel));
            stdDevSegments.Add(ResolveStatisticDatasetValueText(label, stdDevText, includeLabel));
            madSegments.Add(ResolveStatisticDatasetValueText(label, madText, includeLabel));
            iqrSegments.Add(ResolveStatisticDatasetValueText(label, iqrText, includeLabel));
        }

        if (!hasStatistics)
        {
            return string.Empty;
        }

        return L(
            "MousePerformance.Chart.Description.VelocityStats",
            JoinDescriptionSegments(averageSegments),
            JoinDescriptionSegments(p50Segments),
            JoinDescriptionSegments(p95Segments),
            JoinDescriptionSegments(p99Segments),
            JoinDescriptionSegments(p999Segments),
            JoinDescriptionSegments(stdDevSegments),
            JoinDescriptionSegments(madSegments),
            JoinDescriptionSegments(iqrSegments));
    }

    private MousePerformanceVelocityStatisticsSummary ResolveVelocityStatistics(MousePerformanceChartRenderFrame frame, MousePerformanceChartDatasetSlot datasetSlot)
    {
        MousePerformanceSnapshot snapshot = ResolveSnapshotByDatasetSlot(datasetSlot);
        if (!CanResolveVelocityStatistics(frame, datasetSlot, snapshot))
        {
            return null;
        }
        MousePerformanceSessionArchive session = ResolveSessionByDatasetSlot(datasetSlot);
        MousePerformanceVelocityStatisticsSummary statistics = null;
        if (!_analysisCache.TryGetVelocityStatistics(session, frame.PlotType, frame.StartIndex, frame.EndIndex, frame.TimeBasis, ref statistics))
        {
            return null;
        }
        if (statistics == null)
        {
            return null;
        }
        return statistics;
    }

    private bool CanResolveVelocityStatistics(MousePerformanceChartRenderFrame frame, MousePerformanceChartDatasetSlot datasetSlot, MousePerformanceSnapshot snapshot = null)
    {
        if (frame == null || !frame.IsAvailable || !IsVelocityPlot(frame.PlotType))
        {
            return false;
        }
        if (snapshot == null)
        {
            snapshot = ResolveSnapshotByDatasetSlot(datasetSlot);
        }
        if (snapshot == null || snapshot.Status == MousePerformanceSessionStatus.Collecting || !snapshot.CanComputeVelocity)
        {
            return false;
        }
        return true;
    }

    private string ResolveTimingStatisticsDescription(MousePerformanceChartRenderFrame frame)
    {
        if (frame == null || !IsTimingPlot(frame.PlotType))
        {
            return string.Empty;
        }

        bool isIntervalPlot = frame.PlotType == MousePerformancePlotType.IntervalVsTime;
        IReadOnlyList<MousePerformanceChartDatasetSlot> visibleDatasetSlots = ResolveVisibleDatasetSlotsForStatistics();
        if (visibleDatasetSlots.Count == 0)
        {
            return string.Empty;
        }

        bool includeLabel = visibleDatasetSlots.Count > 1;
        List<string> averageSegments = new List<string>();
        List<string> p50Segments = new List<string>();
        List<string> p95Segments = new List<string>();
        List<string> p99Segments = new List<string>();
        List<string> p999Segments = new List<string>();
        List<string> stdDevSegments = new List<string>();
        List<string> madSegments = new List<string>();
        List<string> iqrSegments = new List<string>();
        bool hasStatistics = false;
        foreach (MousePerformanceChartDatasetSlot datasetSlot in visibleDatasetSlots)
        {
            string label = ResolveCompactDatasetLabel(datasetSlot);
            MousePerformanceTimingStatisticsSummary statistics = ResolveTimingStatistics(frame, datasetSlot);
            string averageText = "--";
            string p50Text = "--";
            string p95Text = "--";
            string p99Text = "--";
            string p999Text = "--";
            string stdDevText = "--";
            string madText = "--";
            string iqrText = "--";
            if (statistics != null)
            {
                if (isIntervalPlot)
                {
                    averageText = FormatIntervalTimingValue(statistics.AverageValue);
                    p50Text = FormatIntervalTimingValue(statistics.P50Value);
                    p95Text = FormatIntervalTimingValue(statistics.P95Value);
                    p99Text = FormatIntervalTimingValue(statistics.P99Value);
                    p999Text = FormatNullableIntervalTimingValue(statistics.P999Value);
                    stdDevText = FormatIntervalTimingValue(statistics.StandardDeviationValue);
                    madText = FormatIntervalTimingValue(statistics.MadValue);
                    iqrText = FormatIntervalTimingValue(statistics.IqrValue);
                }
                else
                {
                    averageText = FormatTimingValue(statistics.AverageValue);
                    p50Text = FormatTimingValue(statistics.P50Value);
                    p95Text = FormatTimingValue(statistics.P95Value);
                    p99Text = FormatTimingValue(statistics.P99Value);
                    p999Text = FormatNullableTimingValue(statistics.P999Value);
                    stdDevText = FormatTimingValue(statistics.StandardDeviationValue);
                    madText = FormatTimingValue(statistics.MadValue);
                    iqrText = FormatTimingValue(statistics.IqrValue);
                }
                hasStatistics = true;
            }

            averageSegments.Add(ResolveStatisticDatasetValueText(label, averageText, includeLabel));
            p50Segments.Add(ResolveStatisticDatasetValueText(label, p50Text, includeLabel));
            p95Segments.Add(ResolveStatisticDatasetValueText(label, p95Text, includeLabel));
            p99Segments.Add(ResolveStatisticDatasetValueText(label, p99Text, includeLabel));
            p999Segments.Add(ResolveStatisticDatasetValueText(label, p999Text, includeLabel));
            stdDevSegments.Add(ResolveStatisticDatasetValueText(label, stdDevText, includeLabel));
            if (isIntervalPlot)
            {
                madSegments.Add(ResolveStatisticDatasetValueText(label, madText, includeLabel));
                iqrSegments.Add(ResolveStatisticDatasetValueText(label, iqrText, includeLabel));
            }
        }

        if (!hasStatistics)
        {
            return string.Empty;
        }
        if (isIntervalPlot)
        {
            return L("MousePerformance.Chart.Description.IntervalStats", JoinDescriptionSegments(averageSegments), JoinDescriptionSegments(p50Segments), JoinDescriptionSegments(p95Segments), JoinDescriptionSegments(p99Segments), JoinDescriptionSegments(p999Segments), JoinDescriptionSegments(stdDevSegments), JoinDescriptionSegments(madSegments), JoinDescriptionSegments(iqrSegments));
        }
        return L("MousePerformance.Chart.Description.FrequencyStats", JoinDescriptionSegments(averageSegments), JoinDescriptionSegments(p50Segments), JoinDescriptionSegments(p95Segments), JoinDescriptionSegments(p99Segments), JoinDescriptionSegments(stdDevSegments));
    }

    private MousePerformanceTimingStatisticsSummary ResolveTimingStatistics(MousePerformanceChartRenderFrame frame, MousePerformanceChartDatasetSlot datasetSlot)
    {
        MousePerformanceSnapshot snapshot = ResolveSnapshotByDatasetSlot(datasetSlot);
        if (!CanResolveTimingStatistics(frame, datasetSlot, snapshot))
        {
            return null;
        }
        MousePerformanceSessionArchive session = ResolveSessionByDatasetSlot(datasetSlot);
        MousePerformanceTimingStatisticsSummary statistics = null;
        if (!_analysisCache.TryGetTimingStatistics(session, frame.PlotType, frame.StartIndex, frame.EndIndex, frame.TimeBasis, ref statistics))
        {
            return null;
        }
        return statistics;
    }

    private bool CanResolveTimingStatistics(MousePerformanceChartRenderFrame frame, MousePerformanceChartDatasetSlot datasetSlot, MousePerformanceSnapshot snapshot = null)
    {
        if (frame == null || !frame.IsAvailable || !IsTimingPlot(frame.PlotType))
        {
            return false;
        }
        if (snapshot == null)
        {
            snapshot = ResolveSnapshotByDatasetSlot(datasetSlot);
        }
        if (snapshot == null || snapshot.Status == MousePerformanceSessionStatus.Collecting)
        {
            return false;
        }
        return true;
    }

    private string ResolveResidualDispersionDescription(MousePerformanceChartRenderFrame frame)
    {
        IReadOnlyList<MousePerformanceChartDatasetSlot> visibleDatasetSlots = ResolveVisibleDatasetSlotsForStatistics();
        if (visibleDatasetSlots.Count == 0)
        {
            return string.Empty;
        }

        bool includeLabel = visibleDatasetSlots.Count > 1;
        List<string> madSegments = new List<string>();
        List<string> iqrSegments = new List<string>();
        bool hasStatistics = false;
        foreach (MousePerformanceChartDatasetSlot datasetSlot in visibleDatasetSlots)
        {
            string label = ResolveCompactDatasetLabel(datasetSlot);
            MousePerformanceResidualDispersionSummary statistics = ResolveResidualDispersion(frame, datasetSlot);
            string madText = "--";
            string iqrText = "--";
            if (statistics != null)
            {
                madText = FormatCountValue(statistics.MadCounts);
                iqrText = FormatCountValue(statistics.IqrCounts);
                hasStatistics = true;
            }

            madSegments.Add(ResolveStatisticDatasetValueText(label, madText, includeLabel));
            iqrSegments.Add(ResolveStatisticDatasetValueText(label, iqrText, includeLabel));
        }

        if (!hasStatistics)
        {
            return string.Empty;
        }

        return L("MousePerformance.Chart.Description.CountDispersion", JoinDescriptionSegments(madSegments), JoinDescriptionSegments(iqrSegments));
    }

    private MousePerformanceResidualDispersionSummary ResolveResidualDispersion(MousePerformanceChartRenderFrame frame, MousePerformanceChartDatasetSlot datasetSlot)
    {
        MousePerformanceSnapshot snapshot = ResolveSnapshotByDatasetSlot(datasetSlot);
        if (!CanResolveResidualDispersion(frame, datasetSlot, snapshot))
        {
            return null;
        }
        MousePerformanceSessionArchive session = ResolveSessionByDatasetSlot(datasetSlot);
        MousePerformanceResidualDispersionSummary statistics = null;
        if (!_analysisCache.TryGetResidualDispersion(session, frame.PlotType, frame.StartIndex, frame.EndIndex, frame.TimeBasis, ref statistics))
        {
            return null;
        }
        if (statistics == null)
        {
            return null;
        }
        return statistics;
    }

    private void ScheduleStatisticsWarmup(MousePerformanceChartRenderFrame frame, CancellationToken cancellationToken)
    {
        if (frame == null || !frame.IsAvailable)
        {
            return;
        }

        int requestVersion = _renderBuildVersion;
        List<Task> warmupTasks = new List<Task>();
        foreach (MousePerformanceChartDatasetSlot datasetSlot in ResolveVisibleDatasetSlotsForStatistics())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            MousePerformanceSessionArchive session = ResolveSessionByDatasetSlot(datasetSlot);
            if (session == null || session.Snapshot == null)
            {
                continue;
            }

            if (IsVelocityPlot(frame.PlotType))
            {
                MousePerformanceVelocityStatisticsSummary statistics = null;
                if (!_analysisCache.TryGetVelocityStatistics(session, frame.PlotType, frame.StartIndex, frame.EndIndex, frame.TimeBasis, ref statistics))
                {
                    warmupTasks.Add(_analysisCache.GetOrBuildVelocityStatisticsAsync(session, frame.PlotType, frame.StartIndex, frame.EndIndex, frame.TimeBasis, cancellationToken));
                }
            }
            else if (IsCountPlot(frame.PlotType))
            {
                MousePerformanceResidualDispersionSummary statistics = null;
                if (!_analysisCache.TryGetResidualDispersion(session, frame.PlotType, frame.StartIndex, frame.EndIndex, frame.TimeBasis, ref statistics))
                {
                    warmupTasks.Add(_analysisCache.GetOrBuildResidualDispersionAsync(session, frame.PlotType, frame.StartIndex, frame.EndIndex, frame.TimeBasis, cancellationToken));
                }
            }
            else if (IsTimingPlot(frame.PlotType))
            {
                MousePerformanceTimingStatisticsSummary statistics = null;
                if (!_analysisCache.TryGetTimingStatistics(session, frame.PlotType, frame.StartIndex, frame.EndIndex, frame.TimeBasis, ref statistics))
                {
                    warmupTasks.Add(_analysisCache.GetOrBuildTimingStatisticsAsync(session, frame.PlotType, frame.StartIndex, frame.EndIndex, frame.TimeBasis, cancellationToken));
                }
            }
        }

        if (warmupTasks.Count == 0)
        {
            return;
        }

        Task.WhenAll(warmupTasks).ContinueWith((Task completedTask) =>
        {
            if (!completedTask.IsCanceled)
            {
                if (completedTask.IsFaulted)
                {
                    Trace.TraceWarning($"Mouse performance statistics warmup failed: {completedTask.Exception}");
                }
                else
                {
                    RunOnUiThread(() =>
                    {
                        if (!_disposed && requestVersion == _renderBuildVersion)
                        {
                            RefreshLocalizedFrameOnly();
                        }
                    });
                }
            }
        }, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private bool CanResolveResidualDispersion(MousePerformanceChartRenderFrame frame, MousePerformanceChartDatasetSlot datasetSlot, MousePerformanceSnapshot snapshot = null)
    {
        if (frame == null || !frame.IsAvailable || !IsCountPlot(frame.PlotType))
        {
            return false;
        }
        if (snapshot == null)
        {
            snapshot = ResolveSnapshotByDatasetSlot(datasetSlot);
        }
        if (snapshot == null || snapshot.Status == MousePerformanceSessionStatus.Collecting)
        {
            return false;
        }
        return true;
    }

    private static string FormatVelocityValue(double value)
    {
        if (!IsFinite(value))
        {
            return "--";
        }
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string FormatNullableVelocityValue(double? value)
    {
        return value.HasValue ? FormatVelocityValue(value.Value) : "--";
    }

    private static string FormatCountValue(double value)
    {
        if (!IsFinite(value))
        {
            return "--";
        }
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string FormatTimingValue(double value)
    {
        if (!IsFinite(value))
        {
            return "--";
        }
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string FormatNullableTimingValue(double? value)
    {
        return value.HasValue ? FormatTimingValue(value.Value) : "--";
    }

    private static string FormatIntervalTimingValue(double value)
    {
        if (!IsFinite(value))
        {
            return "--";
        }
        if (Math.Abs(value) < 1.0)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.00} us", value * 1000.0);
        }
        return string.Format(CultureInfo.InvariantCulture, "{0:0.00} ms", value);
    }

    private static string FormatNullableIntervalTimingValue(double? value)
    {
        return value.HasValue ? FormatIntervalTimingValue(value.Value) : "--";
    }

    private static bool IsFinite(double value)
    {
        if (!double.IsNaN(value))
        {
            return !double.IsInfinity(value);
        }
        return false;
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

    private bool IsComparisonDisplayActive()
    {
        return ResolveVisibleDatasetCount() > 1;
    }

    private MousePerformanceChartDatasetSlot ResolveDisplayReferenceDatasetSlot()
    {
        IReadOnlyList<MousePerformanceChartDatasetSlot> readOnlyList = ResolveVisibleDatasetSlots();
        if (readOnlyList.Count == 1)
        {
            return readOnlyList[0];
        }
        if (readOnlyList.Count > 1)
        {
            if (readOnlyList.Contains(MousePerformanceChartDatasetSlot.Baseline))
            {
                return MousePerformanceChartDatasetSlot.Baseline;
            }
            return readOnlyList[0];
        }
        IReadOnlyList<DatasetSessionEntry> activeEntries = ResolveActiveDatasetEntries();
        if (activeEntries.Count == 0)
        {
            return MousePerformanceChartDatasetSlot.Baseline;
        }
        if (activeEntries.FirstOrDefault(entry => entry != null && entry.DatasetSlot == MousePerformanceChartDatasetSlot.Baseline) != null)
        {
            return MousePerformanceChartDatasetSlot.Baseline;
        }
        return activeEntries[0].DatasetSlot;
    }

    private MousePerformanceSessionArchive ResolveDisplayReferenceSession()
    {
        return ResolveSessionByDatasetSlot(ResolveDisplayReferenceDatasetSlot());
    }

    private MousePerformanceSnapshot ResolveDisplayReferenceSnapshot()
    {
        return ResolveDisplayReferenceSession()?.Snapshot;
    }

    private string ResolveToolbarSupplementDescription(MousePerformancePlotType plotType, MousePerformanceTimeBasis timeBasis, bool showGapOverlay)
    {
        List<string> segments = new List<string>();
        MousePerformancePlotPresentationPolicy presentationPolicy = MousePerformancePlotPresentationPolicy.Resolve(plotType);
        segments.Add(L(presentationPolicy.ToolbarSemanticsResourceKey));
        if (plotType != MousePerformancePlotType.XVsY)
        {
            segments.Add(ResolveTimeBasisSupplement(timeBasis));
            if (showGapOverlay)
            {
                segments.Add(L("MousePerformance.Chart.Description.Semantics.GapOverlay"));
            }
        }

        return JoinDescriptionSegments(segments, " | ");
    }

    private static string JoinDescriptionSegments(IEnumerable<string> segments, string separator = " ")
    {
        return string.Join(separator, (segments ?? Array.Empty<string>()).Where(segment => !string.IsNullOrWhiteSpace(segment)));
    }

    private string ResolveTimeBasisSupplement(MousePerformanceTimeBasis timeBasis)
    {
        if (timeBasis == MousePerformanceTimeBasis.RawCapture)
        {
            return L("MousePerformance.Chart.Description.Semantics.TimeBasis.RawCapture");
        }
        return L("MousePerformance.Chart.Description.Semantics.TimeBasis.LogicalSession");
    }

    private IReadOnlyList<MousePerformanceChartDatasetSlot> ResolveVisibleDatasetSlotsForStatistics()
    {
        return new MousePerformanceChartDatasetSlot[3]
        {
            MousePerformanceChartDatasetSlot.Baseline,
            MousePerformanceChartDatasetSlot.CompareA,
            MousePerformanceChartDatasetSlot.CompareB
        }.Where((MousePerformanceChartDatasetSlot datasetSlot) => IsDatasetVisible(datasetSlot)).ToArray();
    }

    private MousePerformanceSnapshot ResolveSnapshotByDatasetSlot(MousePerformanceChartDatasetSlot datasetSlot)
    {
        return ResolveSessionByDatasetSlot(datasetSlot)?.Snapshot;
    }

    private string ResolveCompactDatasetLabel(MousePerformanceChartDatasetSlot datasetSlot)
    {
        MousePerformanceSessionArchive session = ResolveSessionByDatasetSlot(datasetSlot);
        if (session == null)
        {
            return string.Empty;
        }
        if (datasetSlot == MousePerformanceChartDatasetSlot.Baseline && session.SourceMode == MousePerformanceSessionSourceMode.Live)
        {
            return L("MousePerformance.Chart.CompactLabel.LiveBaseline");
        }
        char importedLetter = ResolveImportedDatasetLetter(datasetSlot);
        return L("MousePerformance.Chart.CompactLabel.Imported", importedLetter.ToString());
    }

    private static string ResolveStatisticDatasetValueText(string label, string valueText, bool includeLabel = true)
    {
        string trimmedValueText = (valueText ?? "--").Trim();
        if (!includeLabel || string.IsNullOrWhiteSpace(label))
        {
            return trimmedValueText;
        }
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1}", label.Trim(), trimmedValueText);
    }

    private string ResolveSubtitle()
    {
        IReadOnlyList<MousePerformanceChartDatasetSlot> visibleDatasetSlots = ResolveVisibleDatasetSlotsForStatistics();
        if (visibleDatasetSlots.Count == 0)
        {
            return L("MousePerformance.Chart.Subtitle.NoCpi");
        }

        bool includeLabel = visibleDatasetSlots.Count > 1;
        List<string> cpiSegments = new List<string>();
        bool hasCpi = false;
        foreach (MousePerformanceChartDatasetSlot datasetSlot in visibleDatasetSlots)
        {
            string label = ResolveCompactDatasetLabel(datasetSlot);
            MousePerformanceSnapshot snapshot = ResolveSnapshotByDatasetSlot(datasetSlot);
            string valueText = "--";
            if (snapshot != null && snapshot.EffectiveCpi.HasValue)
            {
                valueText = snapshot.EffectiveCpi.Value.ToString("0.##", CultureInfo.InvariantCulture);
                hasCpi = true;
            }
            cpiSegments.Add(ResolveStatisticDatasetValueText(label, valueText, includeLabel));
        }

        if (!hasCpi)
        {
            return L("MousePerformance.Chart.Subtitle.NoCpi");
        }

        return L("MousePerformance.Chart.Subtitle.WithCpi", JoinDescriptionSegments(cpiSegments));
    }

    private string ResolveUnavailableMessage(MousePerformancePlotType plotType, MousePerformanceSnapshot snapshot)
    {
        if (snapshot == null || snapshot.Events == null || snapshot.Events.Count == 0)
        {
            return L("MousePerformance.Chart.Unavailable.NoData");
        }
        if (plotType != MousePerformancePlotType.XCountVsTime && plotType != MousePerformancePlotType.YCountVsTime && plotType != MousePerformancePlotType.XYCountVsTime && plotType != MousePerformancePlotType.XSumVsTime && plotType != MousePerformancePlotType.YSumVsTime && plotType != MousePerformancePlotType.XYSumVsTime && plotType != MousePerformancePlotType.XVsY && snapshot.Events.Count < 2)
        {
            return L("MousePerformance.Chart.Unavailable.InsufficientMotion");
        }
        if (IsVelocityPlot(plotType) && (snapshot == null || !snapshot.CanComputeVelocity))
        {
            return L("MousePerformance.Chart.Unavailable.InvalidCpi");
        }
        return L("MousePerformance.Chart.Unavailable.Generic");
    }

    private void RefreshToolbarDiagnostics()
    {
        ToolbarDiagnosticsText = ResolveToolbarDiagnostics(_visibleGapCount, _visibleGapAverageDurationMs);
        RefreshToolbarSupplementText();
    }

    private void RefreshToolbarSupplementText()
    {
        string toolbarSupplementText = ResolveToolbarSupplementDescription(_selectedPlotType, ResolveEffectiveTimeBasis(), _showGapOverlay);
        ToolbarSupplementText = toolbarSupplementText ?? string.Empty;
    }

    private string ResolveToolbarDiagnostics(int gapCount, double? gapAverageDurationMs)
    {
        if (!_showGapOverlay)
        {
            return string.Empty;
        }
        if (IsGapAnalysisSelectionVisible)
        {
            return L("MousePerformance.Chart.Diagnostics.Summary.WithTarget", ResolveGapAnalysisTargetSummaryText(), Math.Max(0, gapCount).ToString(CultureInfo.InvariantCulture), FormatNullable(gapAverageDurationMs));
        }
        return L("MousePerformance.Chart.Diagnostics.Summary", Math.Max(0, gapCount).ToString(CultureInfo.InvariantCulture), FormatNullable(gapAverageDurationMs));
    }

    private string ResolveGapAnalysisTargetSummaryText()
    {
        MousePerformanceChartGapAnalysisTargetOption selectedGapTargetOption = _gapAnalysisTargetOptions.FirstOrDefault(option => option != null && option.DatasetSlot == _selectedGapAnalysisDatasetSlot);
        if (selectedGapTargetOption == null)
        {
            return string.Empty;
        }
        return selectedGapTargetOption.DisplayName;
    }

    private void ResetVisibleGapMetrics()
    {
        _visibleGapCount = 0;
        _visibleGapAverageDurationMs = null;
    }

    private void PersistChartOptions()
    {
        if (!SuspendChartOptionPersistence)
        {
            _preferencesStore.SaveChartOptions(_selectedPlotType, _showStem, _showLines);
        }
    }

    private void OnLanguageChanged(object sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            MousePerformancePlotType selectedPlotType = _selectedPlotType;
            MousePerformanceTimeBasis selectedTimeBasis = _selectedTimeBasis;
            RefreshWindowText();
            RefreshPlotOptions();
            RefreshTimeBasisOptions();
            RefreshGapAnalysisTargetOptions();
            RefreshLegendItems();
            _selectedPlotType = selectedPlotType;
            _selectedTimeBasis = selectedTimeBasis;
            RaisePropertyChanged("SelectedPlotType");
            RaisePropertyChanged("SelectedTimeBasis");
            RaisePropertyChanged("SelectedGapAnalysisDatasetSlot");
            RefreshLocalizedFrameOnly();
        });
    }

    private static void RunOnUiThread(Action action)
    {
        if (action == null)
        {
            return;
        }
        if (System.Windows.Application.Current == null || System.Windows.Application.Current.Dispatcher == null || System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            action();
            return;
        }
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Exception arg = ex;
                Trace.TraceError($"Mouse performance chart UI dispatch failed: {arg}");
            }
        }));
    }

    private string L(string key, params object[] args)
    {
        return _localization.GetString(key, args);
    }

    private static string NormalizeIndexText(string inputText)
    {
        if (string.IsNullOrEmpty(inputText))
        {
            return string.Empty;
        }
        StringBuilder digits = new StringBuilder(inputText.Length);
        foreach (char character in inputText)
        {
            if (char.IsDigit(character))
            {
                digits.Append(character);
            }
        }
        return digits.ToString();
    }

    private static int ParseIndex(string inputText, int fallback)
    {
        int result = 0;
        if (string.IsNullOrWhiteSpace(inputText) || !int.TryParse(inputText, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
        {
            return fallback;
        }
        return result;
    }

    private static string FormatNullable(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return "--";
        }
        return value.Value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_renderBuildCancellation != null)
            {
                _renderBuildCancellation.Cancel();
                _renderBuildCancellation.Dispose();
            }
            _renderBuildCancellation = null;
            _localization.LanguageChanged -= OnLanguageChanged;
        }
    }

    void IDisposable.Dispose()
    {
        this.Dispose();
    }
}







