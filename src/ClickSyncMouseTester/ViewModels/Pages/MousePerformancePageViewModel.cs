using ClickSyncMouseTester.Infrastructure;
using ClickSyncMouseTester.Models;
using ClickSyncMouseTester.Navigation;
using ClickSyncMouseTester.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace ClickSyncMouseTester.ViewModels.Pages;

[SupportedOSPlatform("windows")]
public class MousePerformancePageViewModel : BindableBase, IDisposable, ICaptureSessionPageViewModel, ICaptureKeyboardShortcutHandler, INavigationResettablePageViewModel
{
    private enum StatusScenario
    {
        Ready,
        NeedDevice,
        Collecting,
        Paused,
        NoDevice,
        DeviceDisconnected,
        Imported
    }


    private const double DefaultCpiValue = 800.0;

    private const double UiRefreshIntervalMilliseconds = 33.333333333333336;

    private const int EscapeVirtualKey = 27;

    private readonly Dispatcher _dispatcher;

    private readonly LocalizationManager _localization;

    private readonly IMousePerformancePreferencesStore _preferencesStore;

    private readonly IRawKeyboardInputSource _keyboardInputSource;

    private readonly IRawMouseControlInputSource _mouseControlInputSource;

    private readonly MousePerformanceCaptureService _captureService;

    private readonly MousePerformanceChartAnalysisCache _chartAnalysisCache;

    private readonly DispatcherTimer _uiTimer;

    private readonly ObservableCollection<RawMouseDeviceInfo> _devices;

    private readonly List<MousePerformanceSessionArchive> _importedComparisonSessions;

    private readonly DelegateCommand _collectCommand;

    private readonly DelegateCommand _resetCommand;

    private readonly DelegateCommand _plotCommand;

    private MousePerformanceSessionSourceMode _sessionSourceMode;

    private MousePerformanceSessionArchive _importedSession;

    private MousePerformanceSessionMetadata _liveSessionMetadata;

    private RawMouseDeviceInfo _selectedDevice;

    private string _selectedDeviceTitle;

    private string _selectedDeviceMetaText;

    private string _selectedDevicePathText;

    private string _cpiText;

    private double _effectiveCpiValue;

    private string _statusPillText;

    private string _statusMessage;

    private string _hintText;

    private string _qualityText;

    private string _eventCountText;

    private string _sumXCountText;

    private string _sumXCmText;

    private string _sumYCountText;

    private string _sumYCmText;

    private string _pathCountText;

    private string _pathCmText;

    private string _speedText;

    private string _collectActionText;

    private bool _isLocked;

    private bool _isPlotHighlighted;

    private bool _isPageActive;

    private bool _isChartWindowAttached;

    private int _plotOpenRequestVersion;

    private int _chartWindowCloseRequestVersion;

    private MousePerformanceSnapshot _latestSnapshot;

    private MousePerformanceSnapshot _latestChartSnapshot;

    private string _pendingDeviceId;

    private bool _pendingStartFresh;

    private int _pauseGesturePending;

    private bool _isPauseGestureInputSubscribed;

    private string _lastChartWarmupSignature;

    private bool _disposed;

    internal IMousePerformancePreferencesStore PreferencesStore => _preferencesStore;

    public ObservableCollection<RawMouseDeviceInfo> Devices => _devices;

    public RawMouseDeviceInfo SelectedDevice
    {
        get
        {
            return _selectedDevice;
        }
        set
        {
            if (SetProperty(ref _selectedDevice, value, "SelectedDevice"))
            {
                UpdateSelectedDeviceSummary();
                UpdateCollectActionText();
                RaiseCanExecuteChanges();
                ApplyStatusScenario(ResolveStatusScenario(_latestSnapshot));
            }
        }
    }

    public string SelectedDeviceTitle
    {
        get
        {
            return _selectedDeviceTitle;
        }
        private set
        {
            SetProperty(ref _selectedDeviceTitle, value, "SelectedDeviceTitle");
        }
    }

    public string SelectedDeviceMetaText
    {
        get
        {
            return _selectedDeviceMetaText;
        }
        private set
        {
            SetProperty(ref _selectedDeviceMetaText, value, "SelectedDeviceMetaText");
        }
    }

    public string SelectedDevicePathText
    {
        get
        {
            return _selectedDevicePathText;
        }
        private set
        {
            SetProperty(ref _selectedDevicePathText, value, "SelectedDevicePathText");
        }
    }

    public string CpiText
    {
        get
        {
            return _cpiText;
        }
        set
        {
            string normalizedCpiText = NormalizeCpiText(value);
            if (SetProperty(ref _cpiText, normalizedCpiText, "CpiText"))
            {
                UpdateCpiStateFromText();
            }
        }
    }

    public string StatusPillText
    {
        get
        {
            return _statusPillText;
        }
        private set
        {
            SetProperty(ref _statusPillText, value, "StatusPillText");
        }
    }

    public string StatusMessage
    {
        get
        {
            return _statusMessage;
        }
        private set
        {
            SetProperty(ref _statusMessage, value, "StatusMessage");
        }
    }

    public string HintText
    {
        get
        {
            return _hintText;
        }
        private set
        {
            SetProperty(ref _hintText, value, "HintText");
        }
    }

    public string QualityText
    {
        get
        {
            return _qualityText;
        }
        private set
        {
            SetProperty(ref _qualityText, value, "QualityText");
        }
    }

    public bool IsQualityVisible
    {
        get
        {
            if (_sessionSourceMode == MousePerformanceSessionSourceMode.Live && _latestSnapshot != null)
            {
                return _latestSnapshot.HasData;
            }
            return false;
        }
    }

    public string EventCountText
    {
        get
        {
            return _eventCountText;
        }
        private set
        {
            SetProperty(ref _eventCountText, value, "EventCountText");
        }
    }

    public string SumXCountText
    {
        get
        {
            return _sumXCountText;
        }
        private set
        {
            SetProperty(ref _sumXCountText, value, "SumXCountText");
        }
    }

    public string SumXCmText
    {
        get
        {
            return _sumXCmText;
        }
        private set
        {
            SetProperty(ref _sumXCmText, value, "SumXCmText");
        }
    }

    public string SumYCountText
    {
        get
        {
            return _sumYCountText;
        }
        private set
        {
            SetProperty(ref _sumYCountText, value, "SumYCountText");
        }
    }

    public string SumYCmText
    {
        get
        {
            return _sumYCmText;
        }
        private set
        {
            SetProperty(ref _sumYCmText, value, "SumYCmText");
        }
    }

    public string PathCountText
    {
        get
        {
            return _pathCountText;
        }
        private set
        {
            SetProperty(ref _pathCountText, value, "PathCountText");
        }
    }

    public string PathCmText
    {
        get
        {
            return _pathCmText;
        }
        private set
        {
            SetProperty(ref _pathCmText, value, "PathCmText");
        }
    }

    public string SpeedText
    {
        get
        {
            return _speedText;
        }
        private set
        {
            SetProperty(ref _speedText, value, "SpeedText");
        }
    }

    public bool IsLocked
    {
        get
        {
            return _isLocked;
        }
        private set
        {
            if (SetProperty(ref _isLocked, value, "IsLocked"))
            {
                RaisePropertyChanged("IsDeviceSelectionEnabled");
                RaisePropertyChanged("IsCpiInputEnabled");
                RaisePropertyChanged("AreActionButtonsEnabled");
                UpdateCollectActionText();
                UpdatePlotHighlightState();
                RaiseCanExecuteChanges();
                UpdateUiTimer();
                UpdatePauseGestureInputSubscription();
            }
        }
    }

    public bool IsDeviceSelectionEnabled
    {
        get
        {
            if (!IsLocked && _sessionSourceMode == MousePerformanceSessionSourceMode.Live)
            {
                return _importedComparisonSessions.Count == 0;
            }
            return false;
        }
    }

    public bool IsCpiInputEnabled
    {
        get
        {
            if (!IsLocked && _sessionSourceMode == MousePerformanceSessionSourceMode.Live)
            {
                return ShouldDisplaySingleSessionMetrics();
            }
            return false;
        }
    }

    public bool AreActionButtonsEnabled => !IsLocked;

    public string CollectActionText
    {
        get
        {
            return _collectActionText;
        }
        private set
        {
            SetProperty(ref _collectActionText, value, "CollectActionText");
        }
    }

    internal bool HasCurrentSessionData => HasPrimarySessionData();

    public bool HasImportedSessions
    {
        get
        {
            if (_sessionSourceMode != MousePerformanceSessionSourceMode.Imported || _importedSession == null || !_importedSession.HasData)
            {
                return _importedComparisonSessions.Count > 0;
            }
            return true;
        }
    }

    public bool CanDeleteImportedSessions => HasImportedSessions;

    public bool IsImportedSessionActive
    {
        get
        {
            if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported && _importedSession != null)
            {
                return _importedSession.HasData;
            }
            return false;
        }
    }

    internal int ImportedComparisonSessionCount => _importedComparisonSessions.Count;

    public bool IsPlotHighlighted => _isPlotHighlighted;

    public MousePerformanceSnapshot LatestChartSnapshot
    {
        get
        {
            return _latestChartSnapshot;
        }
        private set
        {
            SetProperty(ref _latestChartSnapshot, value, "LatestChartSnapshot");
        }
    }

    public int PlotOpenRequestVersion
    {
        get
        {
            return _plotOpenRequestVersion;
        }
        private set
        {
            SetProperty(ref _plotOpenRequestVersion, value, "PlotOpenRequestVersion");
        }
    }

    public int ChartWindowCloseRequestVersion
    {
        get
        {
            return _chartWindowCloseRequestVersion;
        }
        private set
        {
            SetProperty(ref _chartWindowCloseRequestVersion, value, "ChartWindowCloseRequestVersion");
        }
    }

    public DelegateCommand CollectCommand => _collectCommand;

    public DelegateCommand ResetCommand => _resetCommand;

    public DelegateCommand PlotCommand => _plotCommand;

    public event EventHandler EnterLockRequested;

    public event EventHandler<CaptureLockRequestEventArgs> ExitLockRequested;

    public MousePerformancePageViewModel(IRawInputBroker rawInputBroker, IMousePerformancePreferencesStore preferencesStore = null)
    {
        if (System.Windows.Application.Current != null)
        {
            _dispatcher = System.Windows.Application.Current.Dispatcher;
        }
        else
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
        }
        if (rawInputBroker == null)
        {
            throw new ArgumentNullException("rawInputBroker");
        }
        _localization = LocalizationManager.Instance;
        _preferencesStore = preferencesStore ?? MousePerformancePreferencesStore.Instance;
        _localization.Initialize();
        _keyboardInputSource = rawInputBroker;
        _mouseControlInputSource = rawInputBroker;
        _captureService = new MousePerformanceCaptureService(rawInputBroker);
        _chartAnalysisCache = MousePerformanceChartAnalysisCache.Instance;
        _uiTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher);
        _uiTimer.Interval = TimeSpan.FromMilliseconds(33.333333333333336);
        _devices = new ObservableCollection<RawMouseDeviceInfo>();
        _importedComparisonSessions = new List<MousePerformanceSessionArchive>();
        _collectCommand = new DelegateCommand(RequestCollect, CanCollect);
        _resetCommand = new DelegateCommand(RequestReset, CanReset);
        _plotCommand = new DelegateCommand(RequestPlot, CanPlot);
        _sessionSourceMode = MousePerformanceSessionSourceMode.Live;
        _effectiveCpiValue = ResolveStoredCpiOrDefault(_preferencesStore.LoadPreferences());
        _cpiText = FormatCpi(_effectiveCpiValue);
        _uiTimer.Tick += OnUiTimerTick;
        _localization.LanguageChanged += OnLanguageChanged;
        _captureService.DevicesChanged += OnDevicesChanged;
        _captureService.SelectedDeviceDisconnected += OnSelectedDeviceDisconnected;
        _captureService.SetCpiState(_effectiveCpiValue, canComputeVelocity: true);
        SetSummaryPlaceholder();
        UpdateCollectActionText();
        RefreshDevices();
        RefreshSnapshot(refreshAnalysisSnapshot: false);
    }

    internal MousePerformanceSessionArchive CreateCurrentChartBaselineSession()
    {
        if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported)
        {
            return _importedSession;
        }
        MousePerformanceSnapshot mousePerformanceSnapshot = null;
        mousePerformanceSnapshot = ((!IsLocked) ? (LatestChartSnapshot ?? _captureService.CaptureAnalysisSnapshot()) : _captureService.CaptureAnalysisSnapshot());
        if (mousePerformanceSnapshot == null)
        {
            return null;
        }
        return BuildLiveSessionArchive(mousePerformanceSnapshot);
    }

    internal IReadOnlyList<MousePerformanceSessionArchive> CreateCurrentChartComparisonSessions()
    {
        if (_importedComparisonSessions.Count == 0)
        {
            return Array.Empty<MousePerformanceSessionArchive>();
        }
        return _importedComparisonSessions.ToArray();
    }

    internal void ReplaceWithImportedSession(MousePerformanceSessionArchive session)
    {
        if (session != null && session.HasData)
        {
            _captureService.ResetSession();
            _importedComparisonSessions.Clear();
            _liveSessionMetadata = null;
            _pendingDeviceId = null;
            _pendingStartFresh = false;
            _sessionSourceMode = MousePerformanceSessionSourceMode.Imported;
            _importedSession = session;
            IsLocked = false;
            LoadImportedDisplayDevice(session);
            RefreshDisplayedCpiText();
            RefreshSnapshot(_isChartWindowAttached, forceAnalysisRefresh: true);
        }
    }

    internal void AddImportedComparisonSession(MousePerformanceSessionArchive session, bool replaceOldest = false)
    {
        if (session == null || !session.HasData || !HasPrimarySessionData())
        {
            return;
        }
        if (_importedComparisonSessions.Count >= 2)
        {
            if (!replaceOldest)
            {
                return;
            }
            _importedComparisonSessions.RemoveAt(0);
        }
        _importedComparisonSessions.Add(session);
        RefreshDisplayedCpiText();
        RefreshSnapshot(_isChartWindowAttached, forceAnalysisRefresh: true);
    }

    internal bool ContainsEquivalentSession(MousePerformanceSessionArchive session)
    {
        if (session == null || !session.HasData)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(MousePerformanceSessionIdentityResolver.ResolveSessionContentIdentity(session)))
        {
            return false;
        }
        MousePerformanceSessionArchive mousePerformanceSessionArchive = ResolvePrimarySessionArchive();
        if (mousePerformanceSessionArchive != null && MousePerformanceSessionIdentityResolver.AreEquivalentSessionContent(mousePerformanceSessionArchive, session))
        {
            return true;
        }
        foreach (MousePerformanceSessionArchive importedComparisonSession in _importedComparisonSessions)
        {
            if (importedComparisonSession != null && MousePerformanceSessionIdentityResolver.AreEquivalentSessionContent(importedComparisonSession, session))
            {
                return true;
            }
        }
        return false;
    }

    internal void DeleteImportedSessions()
    {
        if (!HasImportedSessions)
        {
            return;
        }
        if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported)
        {
            bool isChartWindowAttached = _isChartWindowAttached;
            if (isChartWindowAttached)
            {
                _isChartWindowAttached = false;
            }
            ExitImportedSession();
            RefreshDevices();
            RefreshSnapshot(refreshAnalysisSnapshot: false);
            if (isChartWindowAttached)
            {
                RaiseChartWindowCloseRequest();
            }
        }
        else
        {
            _importedComparisonSessions.Clear();
            RefreshDisplayedCpiText();
            RefreshSnapshot(_isChartWindowAttached, forceAnalysisRefresh: true);
        }
    }

    public void SetPageActive(bool isActive)
    {
        if (_isPageActive == isActive)
        {
            return;
        }
        _isPageActive = isActive;
        if (isActive)
        {
            if (_sessionSourceMode == MousePerformanceSessionSourceMode.Live)
            {
                RefreshDevices();
            }
            RefreshSnapshot(_isChartWindowAttached);
        }
        else
        {
            _uiTimer.Stop();
        }
        UpdateUiTimer();
    }

    public void SetChartWindowAttached(bool isAttached)
    {
        if (_isChartWindowAttached != isAttached)
        {
            _isChartWindowAttached = isAttached;
            if (isAttached)
            {
                RefreshSnapshot(refreshAnalysisSnapshot: true);
            }
        }
    }

    public void CommitCpiInput()
    {
        if (!ShouldDisplaySingleSessionMetrics())
        {
            RefreshDisplayedCpiText();
            return;
        }
        if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported)
        {
            RefreshDisplayedCpiText();
            return;
        }
        double value = 0.0;
        if (TryParsePositiveCpi(_cpiText, ref value))
        {
            _effectiveCpiValue = value;
            _preferencesStore.SaveLastCpi(value);
        }
        else
        {
            SetProperty(ref _cpiText, FormatCpi(_effectiveCpiValue), "CpiText");
        }
        _captureService.SetCpiState(_effectiveCpiValue, canComputeVelocity: true);
        RefreshSnapshot(_isChartWindowAttached || (_latestSnapshot != null && _latestSnapshot.HasData), forceAnalysisRefresh: true);
    }

    public void ResetToDefaultState()
    {
        _uiTimer.Stop();
        _captureService.ResetSession();
        _sessionSourceMode = MousePerformanceSessionSourceMode.Live;
        _importedSession = null;
        _importedComparisonSessions.Clear();
        _liveSessionMetadata = null;
        _pendingDeviceId = null;
        _pendingStartFresh = false;
        _isPageActive = false;
        _isChartWindowAttached = false;
        _lastChartWarmupSignature = string.Empty;
        IsLocked = false;
        _effectiveCpiValue = ResolveStoredCpiOrDefault(_preferencesStore.LoadPreferences());
        _captureService.SetCpiState(_effectiveCpiValue, canComputeVelocity: true);
        SelectedDevice = null;
        SetProperty(ref _cpiText, FormatCpi(_effectiveCpiValue), "CpiText");
        RaiseChartWindowCloseRequest();
        RefreshDevices();
        RefreshSnapshot(refreshAnalysisSnapshot: false);
    }

    void INavigationResettablePageViewModel.ResetToDefaultState()
    {
        this.ResetToDefaultState();
    }

    public void OnLockEntered()
    {
        if (_sessionSourceMode != MousePerformanceSessionSourceMode.Live || string.IsNullOrWhiteSpace(_pendingDeviceId))
        {
            return;
        }
        if (!_captureService.BeginSession(_pendingDeviceId, _pendingStartFresh))
        {
            IsLocked = false;
            RefreshDevices();
            RefreshSnapshot(_isChartWindowAttached);
            ExitLockRequested?.Invoke(this, new CaptureLockRequestEventArgs(CaptureUnlockReason.PauseSession));
            return;
        }
        if (_pendingStartFresh || _liveSessionMetadata == null || !string.Equals(_liveSessionMetadata.DeviceId, _pendingDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            _liveSessionMetadata = ResolveMetadataForLiveDevice(_pendingDeviceId);
        }
        IsLocked = true;
        _pendingStartFresh = false;
        RefreshSnapshot(refreshAnalysisSnapshot: false);
    }

    void ICaptureSessionPageViewModel.OnLockEntered()
    {
        this.OnLockEntered();
    }

    public void RequestPauseFromView()
    {
        if (TryBeginPauseRequest())
        {
            try
            {
                RaisePauseExitRequestIfLocked();
            }
            finally
            {
                Interlocked.Exchange(ref _pauseGesturePending, 0);
            }
        }
    }

    void ICaptureSessionPageViewModel.RequestPauseFromView()
    {
        this.RequestPauseFromView();
    }

    public void RequestCollectOrPauseFromShortcut()
    {
        if (IsLocked)
        {
            RequestPauseFromView();
            return;
        }

        RequestCollect();
    }

    bool ICaptureKeyboardShortcutHandler.TryHandleCaptureKeyboardShortcut(CaptureKeyboardShortcut shortcut)
    {
        if (shortcut != CaptureKeyboardShortcut.StartOrPause)
        {
            return false;
        }

        if (IsLocked || CanCollect())
        {
            RequestCollectOrPauseFromShortcut();
            return true;
        }

        return false;
    }

    public void OnViewUnlockCompleted(CaptureUnlockReason reason)
    {
        IsLocked = false;
        Interlocked.Exchange(ref _pauseGesturePending, 0);
        switch (reason)
        {
            case CaptureUnlockReason.ClearSession:
                _captureService.ResetSession();
                _importedComparisonSessions.Clear();
                _liveSessionMetadata = null;
                _isChartWindowAttached = false;
                RefreshDisplayedCpiText();
                RaiseChartWindowCloseRequest();
                break;
            default:
                _captureService.PauseSession();
                break;
            case CaptureUnlockReason.DeviceDisconnected:
                break;
        }
        RefreshDevices();
        bool shouldPreserveAnalysisSnapshot = reason != CaptureUnlockReason.ClearSession;
        RefreshSnapshot(shouldPreserveAnalysisSnapshot || _isChartWindowAttached, shouldPreserveAnalysisSnapshot);
    }

    void ICaptureSessionPageViewModel.OnViewUnlockCompleted(CaptureUnlockReason reason)
    {
        this.OnViewUnlockCompleted(reason);
    }

    private void RequestCollect()
    {
        if (CanCollect())
        {
            if (SelectedDevice == null || string.IsNullOrWhiteSpace(SelectedDevice.DeviceId))
            {
                ApplyStatusScenario(ResolveStatusScenario(_latestSnapshot));
                return;
            }
            _captureService.RequestDeviceRefresh(force: true);
            bool canContinueSelectedSession = _latestSnapshot != null && _latestSnapshot.CanContinue && string.Equals(_latestSnapshot.SessionDeviceId, SelectedDevice.DeviceId, StringComparison.OrdinalIgnoreCase);
            _pendingDeviceId = SelectedDevice.DeviceId;
            _pendingStartFresh = !canContinueSelectedSession;
            EnterLockRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool CanCollect()
    {
        if (_sessionSourceMode == MousePerformanceSessionSourceMode.Live && !IsLocked)
        {
            return SelectedDevice != null;
        }
        return false;
    }

    private void UpdateCollectActionText()
    {
        if (IsLocked)
        {
            CollectActionText = L("MousePerformance.Action.PauseCapture");
            return;
        }

        if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported)
        {
            CollectActionText = L("MousePerformance.Action.StartCapture");
            return;
        }

        if (CanContinueSelectedSession())
        {
            CollectActionText = L("MousePerformance.Action.ResumeCapture");
            return;
        }

        CollectActionText = L("MousePerformance.Action.StartCapture");
    }

    private bool CanContinueSelectedSession()
    {
        return _latestSnapshot != null
            && _latestSnapshot.CanContinue
            && SelectedDevice != null
            && string.Equals(_latestSnapshot.SessionDeviceId, SelectedDevice.DeviceId, StringComparison.OrdinalIgnoreCase);
    }

    private void RequestReset()
    {
        if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported)
        {
            bool isChartWindowAttached = _isChartWindowAttached;
            if (isChartWindowAttached)
            {
                _isChartWindowAttached = false;
            }
            ExitImportedSession();
            RefreshDevices();
            RefreshSnapshot(refreshAnalysisSnapshot: false, forceAnalysisRefresh: true);
            if (isChartWindowAttached)
            {
                RaiseChartWindowCloseRequest();
            }
        }
        else if (IsLocked)
        {
            ExitLockRequested?.Invoke(this, new CaptureLockRequestEventArgs(CaptureUnlockReason.ClearSession));
        }
        else
        {
            _importedComparisonSessions.Clear();
            _captureService.ResetSession();
            _liveSessionMetadata = null;
            _isChartWindowAttached = false;
            RefreshDisplayedCpiText();
            RaiseChartWindowCloseRequest();
            RefreshDevices();
            RefreshSnapshot(refreshAnalysisSnapshot: false);
        }
    }

    private bool CanReset()
    {
        if (_sessionSourceMode != MousePerformanceSessionSourceMode.Imported && !IsLocked)
        {
            if (_latestSnapshot != null)
            {
                return _latestSnapshot.HasData;
            }
            return false;
        }
        return true;
    }

    private void RequestPlot()
    {
        if (!CanPlot())
        {
            return;
        }

        RefreshSnapshot(refreshAnalysisSnapshot: true, forceAnalysisRefresh: true);
        PlotOpenRequestVersion++;
    }

    private bool CanPlot()
    {
        if (_latestSnapshot != null)
        {
            return _latestSnapshot.HasData;
        }
        return false;
    }

    private void RefreshDevices()
    {
        if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported)
        {
            LoadImportedDisplayDevice(_importedSession);
            RaiseCanExecuteChanges();
            return;
        }
        _captureService.RequestDeviceRefresh();
        string previousSelectedId = SelectedDevice?.DeviceId ?? string.Empty;
        IReadOnlyList<RawMouseDeviceInfo> devices = _captureService.GetDevices();
        _devices.Clear();
        if (devices != null)
        {
            foreach (RawMouseDeviceInfo device in devices)
            {
                if (device != null)
                {
                    _devices.Add(device);
                }
            }
        }
        RawMouseDeviceInfo selectedDevice = RawMouseDeviceSelectionPolicy.ResolveSelectionAfterRefresh(_devices, previousSelectedId);
        SelectedDevice = selectedDevice;
        RaiseCanExecuteChanges();
    }

    private void RefreshSnapshot(bool refreshAnalysisSnapshot, bool forceAnalysisRefresh = false)
    {
        if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported)
        {
            MousePerformanceSnapshot displayedSnapshot = (_latestSnapshot = ResolveDisplayedSnapshot());
            UpdatePlotHighlightState();
            ApplySnapshot(displayedSnapshot);
            if (forceAnalysisRefresh || refreshAnalysisSnapshot || _isChartWindowAttached)
            {
                LatestChartSnapshot = displayedSnapshot;
                ScheduleChartWarmupFromCurrentSessions();
            }
            RaiseDisplayStateChanged();
        }
        else
        {
            MousePerformanceSnapshot summarySnapshot = (_latestSnapshot = _captureService.CaptureSummarySnapshot());
            UpdatePlotHighlightState();
            ApplySnapshot(summarySnapshot);
            if (refreshAnalysisSnapshot || _isChartWindowAttached)
            {
                RefreshChartSnapshot(summarySnapshot, forceAnalysisRefresh);
            }
            RaiseDisplayStateChanged();
        }
    }

    private void ApplySnapshot(MousePerformanceSnapshot snapshot)
    {
        if (snapshot == null)
        {
            SetSummaryPlaceholder();
            ApplyStatusScenario(ResolveStatusScenario(null));
            return;
        }
        if (ShouldDisplaySingleSessionMetrics())
        {
            ApplySummary(snapshot.Summary);
        }
        else
        {
            SetSummaryPlaceholder();
        }
        ApplyQuality(snapshot);
        ApplyStatusScenario(ResolveStatusScenario(snapshot));
        UpdateCollectActionText();
        RaiseCanExecuteChanges();
    }

    private void ApplySummary(MousePerformanceSummary summary)
    {
        if (summary == null)
        {
            SetSummaryPlaceholder();
            return;
        }
        EventCountText = summary.EventCount.ToString(CultureInfo.InvariantCulture);
        SumXCountText = summary.SumX.ToString(CultureInfo.InvariantCulture);
        SumYCountText = summary.SumY.ToString(CultureInfo.InvariantCulture);
        PathCountText = summary.PathCounts.ToString("0", CultureInfo.InvariantCulture);
        SumXCmText = FormatDistance(summary.SumXCm);
        SumYCmText = FormatDistance(summary.SumYCm);
        PathCmText = FormatDistance(summary.PathCm);
        SpeedText = FormatSpeed(ResolveSummarySpeed(summary));
    }

    private void SetSummaryPlaceholder()
    {
        EventCountText = "--";
        SumXCountText = "--";
        SumXCmText = "--";
        SumYCountText = "--";
        SumYCmText = "--";
        PathCountText = "--";
        PathCmText = "--";
        SpeedText = "--";
        QualityText = L("MousePerformance.Quality.Placeholder");
    }

    private void ApplyQuality(MousePerformanceSnapshot snapshot)
    {
        if (snapshot == null)
        {
            QualityText = L("MousePerformance.Quality.Placeholder");
            return;
        }
        MousePerformanceDataQuality dataQuality = snapshot.DataQuality;
        if (dataQuality == null)
        {
            QualityText = L("MousePerformance.Quality.Placeholder");
            return;
        }
        string qualityLevelText = ResolveDisplayedQualityLevelText(dataQuality);
        string queuePressureText = ResolveQueuePressureText(dataQuality);
        QualityText = L("MousePerformance.Quality.Format.RawReport.WithQueue", snapshot.EventCount.ToString(CultureInfo.InvariantCulture), dataQuality.ZeroMotionReportCount.ToString(CultureInfo.InvariantCulture), dataQuality.ControlReportCount.ToString(CultureInfo.InvariantCulture), dataQuality.DroppedPacketCount.ToString(CultureInfo.InvariantCulture), qualityLevelText, queuePressureText);
    }

    private void RefreshChartSnapshot(MousePerformanceSnapshot summarySnapshot, bool forceRefresh)
    {
        if ((forceRefresh || _isChartWindowAttached) && summarySnapshot != null && summarySnapshot.Status != MousePerformanceSessionStatus.Collecting && (forceRefresh || ShouldRefreshAnalysisSnapshot(summarySnapshot)))
        {
            LatestChartSnapshot = _captureService.CaptureAnalysisSnapshot();
            ScheduleChartWarmupFromCurrentSessions();
        }
    }

    private bool ShouldRefreshAnalysisSnapshot(MousePerformanceSnapshot summarySnapshot)
    {
        if (summarySnapshot == null)
        {
            return false;
        }
        if (LatestChartSnapshot == null)
        {
            return true;
        }
        return LatestChartSnapshot.SessionRevision != summarySnapshot.SessionRevision || LatestChartSnapshot.Status != summarySnapshot.Status || LatestChartSnapshot.IsFinalized != summarySnapshot.IsFinalized || LatestChartSnapshot.CanContinue != summarySnapshot.CanContinue || LatestChartSnapshot.CanComputeVelocity != summarySnapshot.CanComputeVelocity || !Nullable.Equals(LatestChartSnapshot.EffectiveCpi, summarySnapshot.EffectiveCpi);
    }

    private void ScheduleChartWarmupFromCurrentSessions()
    {
        MousePerformanceSessionArchive baselineSession = CreateCurrentChartBaselineSession();
        IReadOnlyList<MousePerformanceSessionArchive> comparisonSessions = CreateCurrentChartComparisonSessions();
        List<MousePerformanceSessionArchive> sessionsToWarm = new List<MousePerformanceSessionArchive>();
        if (baselineSession != null && baselineSession.HasData)
        {
            sessionsToWarm.Add(baselineSession);
        }
        if (comparisonSessions != null)
        {
            foreach (MousePerformanceSessionArchive comparisonSession in comparisonSessions)
            {
                if (comparisonSession != null && comparisonSession.HasData)
                {
                    sessionsToWarm.Add(comparisonSession);
                }
            }
        }
        if (sessionsToWarm.Count == 0)
        {
            _lastChartWarmupSignature = string.Empty;
            return;
        }
        MousePerformancePreferences preferences = _preferencesStore.LoadPreferences();
        MousePerformanceTimeBasis timeBasis = MousePerformanceTimeBasis.RawCapture;
        string warmupSignature = BuildChartWarmupSignature(sessionsToWarm, preferences.ChartPlotType, preferences.ChartShowStem, preferences.ChartShowLines, timeBasis);
        if (string.Equals(_lastChartWarmupSignature, warmupSignature, StringComparison.Ordinal))
        {
            return;
        }
        _lastChartWarmupSignature = warmupSignature;
        foreach (MousePerformanceSessionArchive sessionToWarm in sessionsToWarm)
        {
            _chartAnalysisCache.WarmSessionAfterStop(sessionToWarm, preferences.ChartPlotType, preferences.ChartShowStem, preferences.ChartShowLines, timeBasis);
        }
    }

    private static string BuildChartWarmupSignature(IEnumerable<MousePerformanceSessionArchive> sessions, MousePerformancePlotType plotType, bool showStem, bool showLines, MousePerformanceTimeBasis timeBasis)
    {
        if (sessions == null)
        {
            return string.Empty;
        }
        string[] sessionIdentities = sessions.Where((MousePerformanceSessionArchive session) => session != null && session.Snapshot != null).Select((MousePerformanceSessionArchive session) =>
        {
            string contentIdentity = MousePerformanceSessionIdentityResolver.ResolveSessionContentIdentity(session);
            return string.Join("|", new string[3]
            {
                contentIdentity,
                session.Snapshot.SessionRevision.ToString(CultureInfo.InvariantCulture),
                FormatNullableDouble(session.Snapshot.EffectiveCpi)
            });
        }).ToArray();
        return string.Join("::", new string[5]
        {
            plotType.ToString(),
            showStem.ToString(),
            showLines.ToString(),
            timeBasis.ToString(),
            string.Join(";", sessionIdentities)
        });
    }

    private static string FormatNullableDouble(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return string.Empty;
        }
        return value.Value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private void UpdatePlotHighlightState()
    {
        bool shouldHighlightPlot = !IsLocked && _latestSnapshot != null && _latestSnapshot.HasData;
        if (_isPlotHighlighted != shouldHighlightPlot)
        {
            _isPlotHighlighted = shouldHighlightPlot;
            RaisePropertyChanged("IsPlotHighlighted");
        }
    }

    private static string FormatDistance(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return "--";
        }
        return string.Format(CultureInfo.InvariantCulture, "{0:0.0} cm", value.Value);
    }

    private static string FormatSpeed(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return "--";
        }
        return value.Value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private double? ResolveSummarySpeed(MousePerformanceSummary summary)
    {
        if (summary == null)
        {
            return null;
        }
        if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported)
        {
            if (summary.SessionAverageVelocityMetersPerSecond.HasValue)
            {
                return summary.SessionAverageVelocityMetersPerSecond;
            }
            return summary.CurrentVelocityMetersPerSecond;
        }
        if (_latestSnapshot != null && _latestSnapshot.Status == MousePerformanceSessionStatus.Collecting)
        {
            return summary.CurrentVelocityMetersPerSecond;
        }
        return summary.SessionAverageVelocityMetersPerSecond;
    }

    private StatusScenario ResolveStatusScenario(MousePerformanceSnapshot snapshot)
    {
        if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported && _importedSession != null && _importedSession.HasData)
        {
            return StatusScenario.Imported;
        }
        if (snapshot != null)
        {
            switch (snapshot.Status)
            {
                case MousePerformanceSessionStatus.Collecting:
                    return StatusScenario.Collecting;
                case MousePerformanceSessionStatus.Paused:
                    return StatusScenario.Paused;
                case MousePerformanceSessionStatus.Stopped:
                    return StatusScenario.Paused;
                case MousePerformanceSessionStatus.NoDevice:
                    return StatusScenario.NoDevice;
                case MousePerformanceSessionStatus.DeviceDisconnected:
                    return StatusScenario.DeviceDisconnected;
            }
        }
        if (SelectedDevice == null)
        {
            if (_devices.Count == 0)
            {
                return StatusScenario.NoDevice;
            }
            return StatusScenario.NeedDevice;
        }
        return StatusScenario.Ready;
    }

    private void ApplyStatusScenario(StatusScenario scenario)
    {
        switch (scenario)
        {
            case StatusScenario.Ready:
                ApplyStatusResources("MousePerformance.Status.Ready.Pill", "MousePerformance.Status.Ready.Message", "MousePerformance.Status.Ready.Hint");
                break;
            case StatusScenario.NeedDevice:
                ApplyStatusResources("MousePerformance.Status.NeedDevice.Pill", "MousePerformance.Status.NeedDevice.Message", "MousePerformance.Status.NeedDevice.Hint");
                break;
            case StatusScenario.Collecting:
                ApplyStatusResources("MousePerformance.Status.Collecting.Pill", "MousePerformance.Status.Collecting.Message", "MousePerformance.Status.Collecting.Hint");
                break;
            case StatusScenario.Paused:
                ApplyStatusResources("MousePerformance.Status.Paused.Pill", "MousePerformance.Status.Paused.Message", "MousePerformance.Status.Paused.Hint");
                break;
            case StatusScenario.NoDevice:
                ApplyStatusResources("MousePerformance.Status.NoDevice.Pill", "MousePerformance.Status.NoDevice.Message", "MousePerformance.Status.NoDevice.Hint");
                break;
            case StatusScenario.Imported:
                ApplyStatusResources("MousePerformance.Status.Imported.Pill", "MousePerformance.Status.Imported.Message", "MousePerformance.Status.Imported.Hint");
                break;
            default:
                ApplyStatusResources("MousePerformance.Status.DeviceDisconnected.Pill", "MousePerformance.Status.DeviceDisconnected.Message", "MousePerformance.Status.DeviceDisconnected.Hint");
                break;
        }
    }

    private void ApplyStatusResources(string pillKey, string messageKey, string hintKey)
    {
        SetStatus(L(pillKey), ResolveStatusMessage(messageKey), ResolveStatusHint(hintKey));
    }

    private void SetStatus(string pill, string message, string hint)
    {
        StatusPillText = pill;
        StatusMessage = message;
        HintText = hint;
    }

    private string ResolveStatusMessage(string defaultMessageKey)
    {
        string importedStatusMessage = ResolveImportedStatusMessage();
        if (!string.IsNullOrWhiteSpace(importedStatusMessage))
        {
            return importedStatusMessage;
        }
        return L(defaultMessageKey);
    }

    private string ResolveStatusHint(string defaultHintKey)
    {
        if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported && _importedSession != null && _importedSession.HasData)
        {
            return L("MousePerformance.Status.Imported.Hint");
        }
        return L(defaultHintKey);
    }

    private string ResolveImportedStatusMessage()
    {
        IReadOnlyList<string> importedMouseLabels = ResolveImportedStatusMouseLabels();
        if (importedMouseLabels.Count == 0)
        {
            return string.Empty;
        }

        string importedMouseLabelText = string.Join("?", importedMouseLabels);
        if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported)
        {
            if (importedMouseLabels.Count == 1)
            {
                return L("MousePerformance.Status.Imported.Message.Single", importedMouseLabelText);
            }
            return L("MousePerformance.Status.Imported.Message.Multi", importedMouseLabelText);
        }
        return L("MousePerformance.Status.LiveCompare.Message", importedMouseLabelText);
    }

    private IReadOnlyList<string> ResolveImportedStatusMouseLabels()
    {
        List<string> labels = new List<string>();
        int importedOrdinal = 0;
        if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported && _importedSession != null && _importedSession.HasData)
        {
            labels.Add(ResolveImportedStatusMouseLabel(importedOrdinal, _importedSession));
            importedOrdinal++;
        }
        foreach (MousePerformanceSessionArchive importedComparisonSession in _importedComparisonSessions)
        {
            if (importedComparisonSession != null && importedComparisonSession.HasData)
            {
                labels.Add(ResolveImportedStatusMouseLabel(importedOrdinal, importedComparisonSession));
                importedOrdinal++;
            }
        }
        return labels;
    }

    private string ResolveImportedStatusMouseLabel(int importedOrdinal, MousePerformanceSessionArchive session)
    {
        char importedLabelLetter = (char)('A' + Math.Max(0, importedOrdinal));
        return L("MousePerformance.Status.Imported.MouseLabel", importedLabelLetter.ToString(), ResolveSessionDisplayName(session));
    }

    private string ResolveSessionDisplayName(MousePerformanceSessionArchive session)
    {
        if (session == null)
        {
            return L("MousePerformance.Export.FileNameFallback");
        }
        if (session.Metadata != null && !string.IsNullOrWhiteSpace(session.Metadata.DisplayName))
        {
            return session.Metadata.DisplayName;
        }
        if (!string.IsNullOrWhiteSpace(session.DisplayName))
        {
            return session.DisplayName;
        }
        return L("MousePerformance.Export.FileNameFallback");
    }

    private MousePerformanceSnapshot ResolveDisplayedSnapshot()
    {
        if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported)
        {
            return (_importedSession == null) ? null : _importedSession.Snapshot;
        }
        return _latestSnapshot;
    }

    private bool ShouldDisplaySingleSessionMetrics()
    {
        return ResolveActiveSessionCount() <= 1;
    }

    private int ResolveActiveSessionCount()
    {
        int primarySessionCount = HasPrimarySessionData() ? 1 : 0;
        return primarySessionCount + _importedComparisonSessions.Count;
    }

    private bool HasPrimarySessionData()
    {
        if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported)
        {
            return _importedSession != null && _importedSession.HasData;
        }
        return _latestSnapshot != null && _latestSnapshot.HasData;
    }

    private MousePerformanceSessionArchive ResolvePrimarySessionArchive()
    {
        if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported)
        {
            return _importedSession;
        }
        if (_latestSnapshot == null || !_latestSnapshot.HasData)
        {
            return null;
        }
        return BuildLiveSessionArchive(_latestSnapshot);
    }

    private MousePerformanceSessionArchive BuildLiveSessionArchive(MousePerformanceSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return null;
        }
        return new MousePerformanceSessionArchive(MousePerformanceSessionSourceMode.Live, ResolveLiveSessionMetadata(snapshot), snapshot);
    }

    private MousePerformanceSessionMetadata ResolveLiveSessionMetadata(MousePerformanceSnapshot snapshot)
    {
        string sessionDeviceId = snapshot?.SessionDeviceId ?? string.Empty;
        if (_liveSessionMetadata != null && (string.IsNullOrWhiteSpace(sessionDeviceId) || string.Equals(_liveSessionMetadata.DeviceId, sessionDeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            return _liveSessionMetadata;
        }
        MousePerformanceSessionMetadata metadata = ResolveMetadataForLiveDevice(sessionDeviceId);
        if (snapshot != null && snapshot.HasData)
        {
            _liveSessionMetadata = metadata;
        }
        return metadata;
    }

    private MousePerformanceSessionMetadata ResolveMetadataForLiveDevice(string deviceId)
    {
        RawMouseDeviceInfo deviceInfo = null;
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            deviceInfo = _captureService.GetDevices().FirstOrDefault(device => device != null && string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        }
        if (deviceInfo == null && SelectedDevice != null && (string.IsNullOrWhiteSpace(deviceId) || string.Equals(SelectedDevice.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)))
        {
            deviceInfo = SelectedDevice;
        }
        if (deviceInfo == null)
        {
            string displayName = L("MousePerformance.Export.FileNameFallback");
            deviceInfo = new RawMouseDeviceInfo(deviceId, displayName, null, null, 0, isVirtual: false);
        }
        return new MousePerformanceSessionMetadata(deviceInfo.DisplayName, deviceInfo.DeviceId, deviceInfo.VendorId, deviceInfo.ProductId, deviceInfo.ButtonCount, deviceInfo.IsVirtual, deviceInfo.PathSummary);
    }

    private void LoadImportedDisplayDevice(MousePerformanceSessionArchive session)
    {
        _devices.Clear();
        if (session == null || session.Metadata == null)
        {
            SelectedDevice = null;
            return;
        }
        MousePerformanceSessionMetadata metadata = session.Metadata;
        RawMouseDeviceInfo importedDevice = new RawMouseDeviceInfo(metadata.DeviceId, string.IsNullOrWhiteSpace(metadata.DisplayName) ? L("MousePerformance.Export.FileNameFallback") : metadata.DisplayName, metadata.VendorId, metadata.ProductId, metadata.ButtonCount, metadata.IsVirtual);
        _devices.Add(importedDevice);
        SelectedDevice = importedDevice;
    }

    private string ResolveImportedCpiText(MousePerformanceSessionArchive session)
    {
        if (session == null || session.Snapshot == null || !session.Snapshot.EffectiveCpi.HasValue || session.Snapshot.EffectiveCpi.Value <= 0.0)
        {
            return string.Empty;
        }
        return FormatCpi(session.Snapshot.EffectiveCpi.Value);
    }

    private string ResolveDisplayedImportedCpiText()
    {
        if (!ShouldDisplaySingleSessionMetrics())
        {
            return "--";
        }
        return ResolveImportedCpiText(_importedSession);
    }

    private string ResolveDisplayedLiveCpiText()
    {
        if (!ShouldDisplaySingleSessionMetrics())
        {
            return "--";
        }
        return FormatCpi(_effectiveCpiValue);
    }

    private void RefreshDisplayedCpiText()
    {
        if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported)
        {
            SetProperty(ref _cpiText, ResolveDisplayedImportedCpiText(), "CpiText");
        }
        else
        {
            SetProperty(ref _cpiText, ResolveDisplayedLiveCpiText(), "CpiText");
        }
    }

    private void ExitImportedSession()
    {
        _sessionSourceMode = MousePerformanceSessionSourceMode.Live;
        _importedSession = null;
        _importedComparisonSessions.Clear();
        _liveSessionMetadata = null;
        _captureService.ResetSession();
        _pendingDeviceId = null;
        _pendingStartFresh = false;
        _latestSnapshot = null;
        LatestChartSnapshot = null;
        _lastChartWarmupSignature = string.Empty;
        _effectiveCpiValue = ResolveStoredCpiOrDefault(_preferencesStore.LoadPreferences());
        _captureService.SetCpiState(_effectiveCpiValue, canComputeVelocity: true);
        SetProperty(ref _cpiText, FormatCpi(_effectiveCpiValue), "CpiText");
    }

    private void UpdateSelectedDeviceSummary()
    {
        if (SelectedDevice == null)
        {
            SelectedDeviceTitle = L("Device.None.Title");
            SelectedDeviceMetaText = L("Device.None.Meta");
            SelectedDevicePathText = "--";
            return;
        }
        MousePerformanceSessionMetadata importedMetadata = ((_sessionSourceMode != MousePerformanceSessionSourceMode.Imported) ? null : _importedSession?.Metadata);
        if (importedMetadata != null && !string.IsNullOrWhiteSpace(importedMetadata.DisplayName))
        {
            SelectedDeviceTitle = importedMetadata.DisplayName;
        }
        else
        {
            SelectedDeviceTitle = SelectedDevice.SelectionDisplayName;
        }
        List<string> deviceMetaParts = new List<string>();
        string vendorProductLabel = importedMetadata?.VendorProductLabel ?? SelectedDevice.VendorProductLabel;
        if (!string.IsNullOrWhiteSpace(vendorProductLabel))
        {
            deviceMetaParts.Add(vendorProductLabel);
        }
        int buttonCount = importedMetadata?.ButtonCount ?? SelectedDevice.ButtonCount;
        if (buttonCount > 0)
        {
            deviceMetaParts.Add(L("Device.Detail.Buttons", buttonCount));
        }
        if (importedMetadata?.IsVirtual ?? SelectedDevice.IsVirtual)
        {
            deviceMetaParts.Add(L("Device.Detail.Virtual"));
        }
        else
        {
            deviceMetaParts.Add(importedMetadata != null ? L("Device.Detail.RawInput") : RawMouseEndpointKindLocalization.Resolve(SelectedDevice.EndpointKind, key => L(key)));
            if (importedMetadata == null && !string.IsNullOrWhiteSpace(SelectedDevice.EndpointToken))
            {
                deviceMetaParts.Add(SelectedDevice.EndpointToken);
            }
        }
        SelectedDeviceMetaText = string.Join("  /  ", deviceMetaParts);
        if (importedMetadata != null && !string.IsNullOrWhiteSpace(importedMetadata.PathSummary))
        {
            SelectedDevicePathText = importedMetadata.PathSummary;
        }
        else
        {
            SelectedDevicePathText = SelectedDevice.PathSummary;
        }
    }

    private void UpdateCpiStateFromText()
    {
        if (_sessionSourceMode != MousePerformanceSessionSourceMode.Imported)
        {
            double parsedCpi = 0.0;
            bool hasValidCpi = TryParsePositiveCpi(_cpiText, ref parsedCpi);
            if (hasValidCpi)
            {
                _effectiveCpiValue = parsedCpi;
            }
            _captureService.SetCpiState(_effectiveCpiValue, hasValidCpi);
            RefreshSnapshot(_isChartWindowAttached);
        }
    }

    private void OnUiTimerTick(object sender, EventArgs e)
    {
        RefreshSnapshot(refreshAnalysisSnapshot: false);
    }

    private void OnDevicesChanged(object sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            if (_sessionSourceMode != MousePerformanceSessionSourceMode.Imported)
            {
                RefreshDevices();
                RefreshSnapshot(_isChartWindowAttached);
            }
        });
    }

    private void OnSelectedDeviceDisconnected(object sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            if (_sessionSourceMode != MousePerformanceSessionSourceMode.Imported)
            {
                if (IsLocked)
                {
                    ExitLockRequested?.Invoke(this, new CaptureLockRequestEventArgs(CaptureUnlockReason.DeviceDisconnected));
                }
                else
                {
                    RefreshDevices();
                    RefreshSnapshot(_isChartWindowAttached);
                }
            }
        });
    }

    private void OnRawKeyboardInput(object sender, RawKeyboardInputEventArgs e)
    {
        if (e == null || e.Input == null || !e.Input.IsKeyDown)
        {
            return;
        }

        if (e.Input.VirtualKey == EscapeVirtualKey)
        {
            RequestPauseFromInputGesture();
        }
    }

    private void OnRawMouseButtonInput(object sender, RawMouseButtonInputEventArgs e)
    {
        if (e != null && e.Input != null && e.Input.IsButtonDown && e.Input.ButtonKind == MouseButtonKind.RightButton)
        {
            RequestPauseFromInputGesture();
        }
    }

    private void RequestPauseFromInputGesture()
    {
        if (!TryBeginPauseRequest())
        {
            return;
        }
        RunOnUiThread(() =>
        {
            try
            {
                RaisePauseExitRequestIfLocked();
            }
            finally
            {
                Interlocked.Exchange(ref _pauseGesturePending, 0);
            }
        });
    }

    private bool TryBeginPauseRequest()
    {
        if (!IsLocked)
        {
            return false;
        }
        return Interlocked.Exchange(ref _pauseGesturePending, 1) == 0;
    }

    private void RaisePauseExitRequestIfLocked()
    {
        if (IsLocked)
        {
            ExitLockRequested?.Invoke(this, new CaptureLockRequestEventArgs(CaptureUnlockReason.PauseSession));
        }
    }

    private void OnLanguageChanged(object sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            if (_sessionSourceMode == MousePerformanceSessionSourceMode.Imported)
            {
                LoadImportedDisplayDevice(_importedSession);
            }
            UpdateSelectedDeviceSummary();
            ApplyStatusScenario(ResolveStatusScenario(_latestSnapshot));
            ApplyQuality(_latestSnapshot);
            UpdateCollectActionText();
        });
    }

    private void RaiseCanExecuteChanges()
    {
        _collectCommand.RaiseCanExecuteChanged();
        _resetCommand.RaiseCanExecuteChanged();
        _plotCommand.RaiseCanExecuteChanged();
    }

    private void RaiseDisplayStateChanged()
    {
        RaiseCanExecuteChanges();
        RaisePropertyChanged("IsDeviceSelectionEnabled");
        RaisePropertyChanged("IsCpiInputEnabled");
        RaisePropertyChanged("HasImportedSessions");
        RaisePropertyChanged("CanDeleteImportedSessions");
        RaisePropertyChanged("IsImportedSessionActive");
        RaisePropertyChanged("IsQualityVisible");
    }

    private void RaiseChartWindowCloseRequest()
    {
        ChartWindowCloseRequestVersion++;
        LatestChartSnapshot = null;
    }

    private void UpdateUiTimer()
    {
        if (_isPageActive && IsLocked)
        {
            if (!_uiTimer.IsEnabled)
            {
                _uiTimer.Start();
            }
        }
        else if (_uiTimer.IsEnabled)
        {
            _uiTimer.Stop();
        }
    }

    private void UpdatePauseGestureInputSubscription(bool forceDetach = false)
    {
        if (!forceDetach && !_disposed && _sessionSourceMode == MousePerformanceSessionSourceMode.Live && IsLocked)
        {
            if (!_isPauseGestureInputSubscribed)
            {
                _keyboardInputSource.KeyboardInput += OnRawKeyboardInput;
                _mouseControlInputSource.MouseButtonInput += OnRawMouseButtonInput;
                _isPauseGestureInputSubscribed = true;
            }
        }
        else if (_isPauseGestureInputSubscribed)
        {
            _keyboardInputSource.KeyboardInput -= OnRawKeyboardInput;
            _mouseControlInputSource.MouseButtonInput -= OnRawMouseButtonInput;
            _isPauseGestureInputSubscribed = false;
        }
    }

    private static string NormalizeCpiText(string inputText)
    {
        if (string.IsNullOrEmpty(inputText))
        {
            return string.Empty;
        }
        StringBuilder normalizedText = new StringBuilder(inputText.Length);
        bool hasDecimalPoint = false;
        foreach (char character in inputText)
        {
            if (char.IsDigit(character))
            {
                normalizedText.Append(character);
            }
            else if (character == '.' && !hasDecimalPoint)
            {
                normalizedText.Append(character);
                hasDecimalPoint = true;
            }
        }
        return normalizedText.ToString();
    }

    private static bool TryParsePositiveCpi(string inputText, ref double value)
    {
        value = 0.0;
        if (string.IsNullOrWhiteSpace(inputText))
        {
            return false;
        }
        if (!double.TryParse(inputText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }
        return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static string FormatCpi(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static double ResolveStoredCpiOrDefault(MousePerformancePreferences preferences)
    {
        if (preferences != null && preferences.LastCpi.HasValue && preferences.LastCpi.Value > 0.0 && !double.IsNaN(preferences.LastCpi.Value) && !double.IsInfinity(preferences.LastCpi.Value))
        {
            return preferences.LastCpi.Value;
        }
        return 800.0;
    }

    private void RunOnUiThread(Action action)
    {
        if (action != null)
        {
            if (_dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                _dispatcher.BeginInvoke(action);
            }
        }
    }

    private string L(string key, params object[] args)
    {
        return _localization.GetString(key, args);
    }

    private string ResolveQualityLevelText(MousePerformanceDataQualityLevel level)
    {
        return level switch
        {
            MousePerformanceDataQualityLevel.Good => L("MousePerformance.Quality.Level.Good"),
            MousePerformanceDataQualityLevel.Degraded => L("MousePerformance.Quality.Level.Degraded"),
            _ => L("MousePerformance.Quality.Level.None"),
        };
    }

    private string ResolveDisplayedQualityLevelText(MousePerformanceDataQuality quality)
    {
        if (quality == null)
        {
            return L("MousePerformance.Quality.Level.None");
        }
        MousePerformanceDataQualityLevel mousePerformanceDataQualityLevel = quality.QualityLevel;
        if (mousePerformanceDataQualityLevel == MousePerformanceDataQualityLevel.Good && !quality.IsStrictTimingFaithful)
        {
            mousePerformanceDataQualityLevel = MousePerformanceDataQualityLevel.Degraded;
        }
        return ResolveQualityLevelText(mousePerformanceDataQualityLevel);
    }

    private string ResolveQueuePressureText(MousePerformanceDataQuality quality)
    {
        if (quality == null || quality.QueueCapacity <= 0)
        {
            return L("MousePerformance.Quality.Queue.Placeholder");
        }
        return L("MousePerformance.Quality.Queue.Format", quality.QueueHighWatermarkCount.ToString(CultureInfo.InvariantCulture), quality.QueueCapacity.ToString(CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _uiTimer.Stop();
            _uiTimer.Tick -= OnUiTimerTick;
            _localization.LanguageChanged -= OnLanguageChanged;
            _captureService.DevicesChanged -= OnDevicesChanged;
            _captureService.SelectedDeviceDisconnected -= OnSelectedDeviceDisconnected;
            UpdatePauseGestureInputSubscription(forceDetach: true);
            _captureService.Dispose();
        }
    }

    void IDisposable.Dispose()
    {
        this.Dispose();
    }

}
