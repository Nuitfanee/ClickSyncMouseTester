using ClickSyncMouseTester.Infrastructure;
using ClickSyncMouseTester.Models;
using ClickSyncMouseTester.Navigation;
using ClickSyncMouseTester.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClickSyncMouseTester.ViewModels;

[SupportedOSPlatform("windows")]
public class MainWindowViewModel : BindableBase, IDisposable, ICaptureSessionPageViewModel, INavigationResettablePageViewModel, IDisplayRefreshRateAwarePageViewModel
{
    private enum StatusScenario
    {
        Ready,
        Measuring,
        Paused,
        Cleared,
        NeedDevice,
        DeviceDisconnected,
        NoDevices,
        RefreshFeedback
    }

    private sealed class MetricTextState
    {
        public double LastUpdateMilliseconds { get; set; }

        public bool HasDisplayedValue { get; set; }

        public MetricTextState()
        {
            LastUpdateMilliseconds = double.NaN;
        }
    }


    private const double MetricTextUpdateIntervalMilliseconds = 1000.0 / 10.0;

    private const double RefreshFeedbackDurationMilliseconds = 1200.0;

    private const double LowRefreshDisplayChartFrameRateHz = 60.0;

    private const double HighRefreshDisplayChartFrameRateHz = 120.0;

    private const double HighRefreshDisplayThresholdHz = 60.5;

    private readonly Dispatcher _dispatcher;

    private readonly PollingRateCaptureService _captureService;

    private readonly DeviceSelectionCoordinator _deviceSelectionCoordinator;

    private readonly LocalizationManager _localization;

    private readonly ThemeManager _themeManager;

    private readonly DispatcherTimer _statusRestoreTimer;

    private readonly ObservableCollection<RawMouseDeviceInfo> _devices;

    private readonly ObservableCollection<LanguageOption> _languageOptions;

    private readonly PollingMetricCardsPresenter _metricCardsPresenter;

    private readonly DelegateCommand _toggleLanguageCommand;

    private readonly DelegateCommand _toggleThemeCommand;

    private readonly DelegateCommand _refreshDevicesCommand;

    private readonly DelegateCommand _commitManualDeviceSelectionCommand;

    private readonly DelegateCommand _startCommand;

    private readonly DelegateCommand _stopCommand;

    private RawMouseDeviceInfo _selectedDevice;

    private LanguageOption _selectedLanguage;

    private PollingRateMode _selectedPollingRateMode;

    private string _languageToggleText;

    private string _languageToggleKeyText;

    private string _themeToggleText;

    private string _themeToggleKeyText;

    private string _rateModeDescriptionText;

    private double _rawCurrentRate;

    private int _currentRate;

    private int _peakRate;

    private double? _emptyPacketPercent;

    private long _droppedPacketCount;

    private IReadOnlyList<PollingHistoryPoint> _historyPoints;

    private string _statusPillText;

    private string _statusMessage;

    private string _hintText;

    private string _currentRateText;

    private string _peakRateText;

    private string _emptyPacketText;

    private string _droppedPacketCountText;

    private string _startButtonText;

    private string _selectedDeviceTitle;

    private string _selectedDeviceMetaText;

    private string _selectedDevicePathText;

    private bool _isLocked;

    private bool _hasSessionData;

    private string _sessionDeviceId;

    private string _pendingDeviceId;

    private bool _pendingSessionReset;

    private bool _clearSelectionOnNextRefresh;

    private PollingMetricsSnapshot _latestMetricsSnapshot;

    private PollingChartRenderFrame _latestChartRenderFrame;

    private readonly MetricTextState _currentRateTextState;

    private readonly MetricTextState _emptyPacketTextState;

    private StatusScenario _statusScenario;

    private StatusScenario _restoreStatusScenario;

    private bool _isPollingUiRenderingSubscribed;

    private bool _isPageActive;

    private double _chartRenderFrameRateHz;

    private bool _disposed;

    public ObservableCollection<RawMouseDeviceInfo> Devices => _devices;

    public ObservableCollection<LanguageOption> LanguageOptions => _languageOptions;

    public ObservableCollection<PollingMetricCardViewModel> MetricCards => _metricCardsPresenter.Cards;

    public string LanguageToggleText
    {
        get
        {
            return _languageToggleText;
        }
        private set
        {
            SetProperty(ref _languageToggleText, value, "LanguageToggleText");
        }
    }

    public string LanguageToggleKeyText
    {
        get
        {
            return _languageToggleKeyText;
        }
        private set
        {
            SetProperty(ref _languageToggleKeyText, value, "LanguageToggleKeyText");
        }
    }

    public string ThemeToggleText
    {
        get
        {
            return _themeToggleText;
        }
        private set
        {
            SetProperty(ref _themeToggleText, value, "ThemeToggleText");
        }
    }

    public string ThemeToggleKeyText
    {
        get
        {
            return _themeToggleKeyText;
        }
        private set
        {
            SetProperty(ref _themeToggleKeyText, value, "ThemeToggleKeyText");
        }
    }

    public string RateModeDescriptionText
    {
        get
        {
            return _rateModeDescriptionText;
        }
        private set
        {
            SetProperty(ref _rateModeDescriptionText, value, "RateModeDescriptionText");
        }
    }

    public LanguageOption SelectedLanguage
    {
        get
        {
            return _selectedLanguage;
        }
        set
        {
            if (value != null && SetProperty(ref _selectedLanguage, value, "SelectedLanguage"))
            {
                _localization.SetLanguage(value.CultureName);
            }
        }
    }

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
                UpdateStartButtonText();
                RaiseCanExecuteChanges();
            }
        }
    }

    public bool IsRawPacketRateMode
    {
        get
        {
            return _selectedPollingRateMode == PollingRateMode.RawPacketRate;
        }
        set
        {
            PollingRateMode pollingRateMode = ((!value) ? PollingRateMode.MotionReportRate : PollingRateMode.RawPacketRate);
            SetPollingRateMode(pollingRateMode);
        }
    }

    public double RawCurrentRate
    {
        get
        {
            return _rawCurrentRate;
        }
        private set
        {
            SetProperty(ref _rawCurrentRate, value, "RawCurrentRate");
        }
    }

    public int CurrentRate
    {
        get
        {
            return _currentRate;
        }
        private set
        {
            SetProperty(ref _currentRate, value, "CurrentRate");
        }
    }

    public int PeakRate
    {
        get
        {
            return _peakRate;
        }
        private set
        {
            SetProperty(ref _peakRate, value, "PeakRate");
        }
    }

    public bool IsMotionReportRateMode => _selectedPollingRateMode == PollingRateMode.MotionReportRate;

    public double? EmptyPacketPercent
    {
        get
        {
            return _emptyPacketPercent;
        }
        private set
        {
            SetProperty(ref _emptyPacketPercent, value, "EmptyPacketPercent");
        }
    }

    public long DroppedPacketCount
    {
        get
        {
            return _droppedPacketCount;
        }
        private set
        {
            SetProperty(ref _droppedPacketCount, value, "DroppedPacketCount");
        }
    }

    public IReadOnlyList<PollingHistoryPoint> HistoryPoints
    {
        get
        {
            return _historyPoints;
        }
        private set
        {
            if (value == null)
            {
                value = new List<PollingHistoryPoint>();
            }
            SetProperty(ref _historyPoints, value, "HistoryPoints");
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

    public string CurrentRateText
    {
        get
        {
            return _currentRateText;
        }
        private set
        {
            SetProperty(ref _currentRateText, value, "CurrentRateText");
        }
    }

    public string PeakRateText
    {
        get
        {
            return _peakRateText;
        }
        private set
        {
            SetProperty(ref _peakRateText, value, "PeakRateText");
        }
    }

    public string EmptyPacketText
    {
        get
        {
            return _emptyPacketText;
        }
        private set
        {
            SetProperty(ref _emptyPacketText, value, "EmptyPacketText");
        }
    }

    public string DroppedPacketCountText
    {
        get
        {
            return _droppedPacketCountText;
        }
        private set
        {
            SetProperty(ref _droppedPacketCountText, value, "DroppedPacketCountText");
        }
    }

    public string StartButtonText
    {
        get
        {
            return _startButtonText;
        }
        private set
        {
            SetProperty(ref _startButtonText, value, "StartButtonText");
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
                RaisePropertyChanged("IsRateModeSwitchEnabled");
            }
        }
    }

    public bool IsDeviceSelectionEnabled => !IsLocked;

    public bool IsRateModeSwitchEnabled
    {
        get
        {
            if (!IsLocked)
            {
                return !_hasSessionData;
            }
            return false;
        }
    }

    public DelegateCommand ToggleLanguageCommand => _toggleLanguageCommand;

    public DelegateCommand RefreshDevicesCommand => _refreshDevicesCommand;

    public DelegateCommand CommitManualDeviceSelectionCommand => _commitManualDeviceSelectionCommand;

    public DelegateCommand ToggleThemeCommand => _toggleThemeCommand;

    public DelegateCommand StartCommand => _startCommand;

    public DelegateCommand StopCommand => _stopCommand;

    public event EventHandler EnterLockRequested;

    public event EventHandler<CaptureLockRequestEventArgs> ExitLockRequested;

    public MainWindowViewModel(IRawInputBroker rawInputBroker)
        : this(rawInputBroker, null)
    {
    }

    internal MainWindowViewModel(IRawInputBroker rawInputBroker, DeviceSelectionCoordinator deviceSelectionCoordinator)
    {
        _currentRateTextState = new MetricTextState();
        _emptyPacketTextState = new MetricTextState();
        _metricCardsPresenter = new PollingMetricCardsPresenter();
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
        _deviceSelectionCoordinator = deviceSelectionCoordinator;
        _captureService = new PollingRateCaptureService(rawInputBroker);
        _localization = LocalizationManager.Instance;
        _themeManager = ThemeManager.Instance;
        _localization.Initialize();
        _statusRestoreTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher);
        _statusRestoreTimer.Interval = TimeSpan.FromMilliseconds(RefreshFeedbackDurationMilliseconds);
        _devices = new ObservableCollection<RawMouseDeviceInfo>();
        _languageOptions = new ObservableCollection<LanguageOption>(_localization.AvailableLanguages);
        _historyPoints = new List<PollingHistoryPoint>();
        _selectedPollingRateMode = PollingRateMode.MotionReportRate;
        _chartRenderFrameRateHz = HighRefreshDisplayChartFrameRateHz;
        RefreshMetricCardTitles();
        SetMetricCardMode(_selectedPollingRateMode);
        _captureService.SetChartRenderFrameRateHz(_chartRenderFrameRateHz);
        _captureService.SetMode(_selectedPollingRateMode);
        _toggleLanguageCommand = new DelegateCommand(RequestToggleLanguage);
        _toggleThemeCommand = new DelegateCommand(RequestToggleTheme);
        _refreshDevicesCommand = new DelegateCommand(RequestRefreshDevices, CanRefreshDevices);
        _commitManualDeviceSelectionCommand = new DelegateCommand(CommitManualDeviceSelection, CanCommitManualDeviceSelection);
        _startCommand = new DelegateCommand(RequestStart, CanStart);
        _stopCommand = new DelegateCommand(RequestStop, CanStop);
        _statusRestoreTimer.Tick += OnStatusRestoreTimerTick;
        _captureService.DevicesChanged += OnDevicesChanged;
        _captureService.SelectedDeviceDisconnected += OnSelectedDeviceDisconnected;
        if (_deviceSelectionCoordinator != null)
        {
            _deviceSelectionCoordinator.PreferredDeviceChanged += OnPreferredDeviceChanged;
        }
        _localization.LanguageChanged += OnLanguageChanged;
        _themeManager.ThemeChanged += OnThemeChanged;
        _selectedLanguage = _localization.CurrentLanguage;
        UpdateLanguageToggleText();
        UpdateThemeToggleText();
        UpdateRateModeDescriptionText();
        _restoreStatusScenario = StatusScenario.Ready;
        SetMetricsToPlaceholder();
        UpdateSelectedDeviceSummary();
        ApplyStatusScenario(StatusScenario.Ready);
        UpdateStartButtonText();
        RefreshDevices(updateStatus: true);
    }

    public void SetPageActive(bool isActive)
    {
        if (_isPageActive != isActive)
        {
            _isPageActive = isActive;
        }
    }

    public void SetHostDisplayRefreshRate(double? refreshRateHz)
    {
        double targetChartFrameRateHz = ResolveChartFrameRate(refreshRateHz);
        if (Math.Abs(_chartRenderFrameRateHz - targetChartFrameRateHz) < 0.001)
        {
            return;
        }

        _chartRenderFrameRateHz = targetChartFrameRateHz;
        _captureService.SetChartRenderFrameRateHz(_chartRenderFrameRateHz);
    }

    public void ResetToDefaultState()
    {
        UpdatePollingUiRenderingSubscription(forceDetach: true);
        _statusRestoreTimer.Stop();
        _captureService.StopSession();
        _captureService.SetMode(PollingRateMode.MotionReportRate);
        _captureService.ResetStatistics();
        _selectedPollingRateMode = PollingRateMode.MotionReportRate;
        SetHasSessionData(value: false);
        _sessionDeviceId = null;
        _pendingDeviceId = null;
        _pendingSessionReset = false;
        _clearSelectionOnNextRefresh = false;
        _latestMetricsSnapshot = null;
        _latestChartRenderFrame = null;
        _restoreStatusScenario = StatusScenario.Ready;
        SetMetricsToPlaceholder();
        UpdateRateModeDescriptionText();
        SetMetricCardMode(_selectedPollingRateMode);
        RaisePropertyChanged("IsRawPacketRateMode");
        RaisePropertyChanged("IsMotionReportRateMode");
        SelectedDevice = null;
        RefreshDevices(updateStatus: true);
        UpdateStartButtonText();
        RaiseCanExecuteChanges();
    }

    void INavigationResettablePageViewModel.ResetToDefaultState()
    {
        this.ResetToDefaultState();
    }

    void IDisplayRefreshRateAwarePageViewModel.SetHostDisplayRefreshRate(double? refreshRateHz)
    {
        SetHostDisplayRefreshRate(refreshRateHz);
    }

    public void OnLockEntered()
    {
        string pendingDeviceId = _pendingDeviceId;
        if (!string.IsNullOrWhiteSpace(pendingDeviceId))
        {
            if (_pendingSessionReset)
            {
                _captureService.ResetStatistics();
                SetHasSessionData(value: true);
                _latestMetricsSnapshot = null;
            }
            _sessionDeviceId = pendingDeviceId;
            _captureService.BeginSession(pendingDeviceId);
            IsLocked = true;
            UpdatePollingUiRenderingSubscription();
            ApplyLatestBufferedMetricsSnapshot();
            ApplyLatestBufferedChartRenderFrame();
            ApplyStatusScenario(StatusScenario.Measuring);
            UpdateStartButtonText();
            RaiseCanExecuteChanges();
        }
    }

    void ICaptureSessionPageViewModel.OnLockEntered()
    {
        this.OnLockEntered();
    }

    public void RequestPauseFromView()
    {
        if (IsLocked)
        {
            ExitLockRequested?.Invoke(this, new CaptureLockRequestEventArgs(CaptureUnlockReason.PauseSession));
        }
    }

    void ICaptureSessionPageViewModel.RequestPauseFromView()
    {
        this.RequestPauseFromView();
    }

    public void OnViewUnlockCompleted(CaptureUnlockReason reason)
    {
        UpdatePollingUiRenderingSubscription(forceDetach: true);
        IsLocked = false;
        switch (reason)
        {
            case CaptureUnlockReason.ClearSession:
                _captureService.StopSession();
                ClearSession();
                break;
            case CaptureUnlockReason.DeviceDisconnected:
                _captureService.StopSession();
                _clearSelectionOnNextRefresh = true;
                RefreshDevices(updateStatus: false);
                ApplyStatusScenario(StatusScenario.DeviceDisconnected);
                break;
            default:
                _captureService.PauseSession();
                ApplyLatestBufferedMetricsSnapshot();
                ApplyLatestBufferedChartRenderFrame();
                ApplyStatusScenario(StatusScenario.Paused);
                break;
        }
        UpdateStartButtonText();
        RaiseCanExecuteChanges();
    }

    void ICaptureSessionPageViewModel.OnViewUnlockCompleted(CaptureUnlockReason reason)
    {
        this.OnViewUnlockCompleted(reason);
    }

    private bool CanStart()
    {
        if (SelectedDevice != null)
        {
            return !IsLocked;
        }
        return false;
    }

    private bool CanStop()
    {
        if (!IsLocked)
        {
            return _hasSessionData;
        }
        return true;
    }

    private bool CanRefreshDevices()
    {
        return !IsLocked;
    }

    private void RequestStart()
    {
        if (SelectedDevice == null)
        {
            ApplyStatusScenario(StatusScenario.NeedDevice);
            return;
        }
        _pendingDeviceId = SelectedDevice.DeviceId;
        _pendingSessionReset = !_hasSessionData || !string.Equals(_sessionDeviceId, _pendingDeviceId, StringComparison.OrdinalIgnoreCase);
        EnterLockRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RequestStop()
    {
        if (IsLocked)
        {
            ExitLockRequested?.Invoke(this, new CaptureLockRequestEventArgs(CaptureUnlockReason.ClearSession));
            return;
        }
        _captureService.StopSession();
        ClearSession();
    }

    private void RequestToggleLanguage()
    {
        LanguageOption nextLanguage = GetNextLanguage();
        if (nextLanguage != null)
        {
            SelectedLanguage = nextLanguage;
        }
    }

    private void RequestToggleTheme()
    {
        _themeManager.ToggleTheme();
    }

    private void OnPollingUiRendering(object sender, EventArgs e)
    {
        if (_disposed || !IsLocked)
        {
            UpdatePollingUiRenderingSubscription(forceDetach: true);
            return;
        }
        PollingMetricsSnapshot metricsSnapshot = null;
        if (_captureService.TryReadLatestMetricsSnapshot(ref metricsSnapshot) && !ReferenceEquals(metricsSnapshot, _latestMetricsSnapshot))
        {
            _latestMetricsSnapshot = metricsSnapshot;
            ApplyMetricsSnapshot(metricsSnapshot);
        }
        PollingChartRenderFrame chartRenderFrame = null;
        if (_captureService.TryReadLatestChartRenderFrame(ref chartRenderFrame) && !ReferenceEquals(chartRenderFrame, _latestChartRenderFrame))
        {
            _latestChartRenderFrame = chartRenderFrame;
            ApplyChartRenderFrame(chartRenderFrame);
        }
    }

    private void UpdatePollingUiRenderingSubscription(bool forceDetach = false)
    {
        if (forceDetach || _disposed || !IsLocked)
        {
            if (_isPollingUiRenderingSubscribed)
            {
                CompositionTarget.Rendering -= OnPollingUiRendering;
                _isPollingUiRenderingSubscribed = false;
            }
        }
        else if (!_isPollingUiRenderingSubscribed)
        {
            CompositionTarget.Rendering += OnPollingUiRendering;
            _isPollingUiRenderingSubscribed = true;
        }
    }

    private void OnDevicesChanged(object sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            RefreshDevices(updateStatus: true);
        });
    }

    private void OnPreferredDeviceChanged(object sender, EventArgs e)
    {
        RunOnUiThread(ApplyPreferredDeviceSelection);
    }

    private void OnSelectedDeviceDisconnected(object sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            if (IsLocked)
            {
                ExitLockRequested?.Invoke(this, new CaptureLockRequestEventArgs(CaptureUnlockReason.DeviceDisconnected));
            }
            else
            {
                _captureService.StopSession();
                _clearSelectionOnNextRefresh = true;
                RefreshDevices(updateStatus: false);
                ApplyStatusScenario(StatusScenario.DeviceDisconnected);
                UpdateStartButtonText();
                RaiseCanExecuteChanges();
            }
        });
    }

    private void OnLanguageChanged(object sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            UpdateSelectedLanguageSelection();
            UpdateLanguageToggleText();
            UpdateThemeToggleText();
            UpdateRateModeDescriptionText();
            RefreshMetricCardTitles();
            UpdateMetricCardValues();
            UpdateSelectedDeviceSummary();
            UpdateStartButtonText();
            ApplyStatusScenario(_statusScenario);
        });
    }

    private void OnThemeChanged(object sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            UpdateThemeToggleText();
        });
    }

    private void RequestRefreshDevices()
    {
        if (CanRefreshDevices())
        {
            RefreshDevices(updateStatus: false);
            ShowRefreshFeedback();
        }
    }

    private void RefreshDevices(bool updateStatus)
    {
        IReadOnlyList<RawMouseDeviceInfo> devices = _captureService.GetDevices();
        string previousSelectedId = SelectedDevice?.DeviceId;
        _devices.Clear();
        foreach (RawMouseDeviceInfo device in devices)
        {
            _devices.Add(device);
        }
        RawMouseDeviceInfo selectedDevice = null;
        if (_clearSelectionOnNextRefresh)
        {
            _clearSelectionOnNextRefresh = false;
        }
        else
        {
            selectedDevice = ResolveDeviceSelectionAfterRefresh(previousSelectedId);
        }
        SelectedDevice = selectedDevice;
        if (updateStatus)
        {
            if (_devices.Count == 0)
            {
                ApplyStatusScenario(StatusScenario.NoDevices);
            }
            else if (!_hasSessionData && !IsLocked)
            {
                ApplyStatusScenario(StatusScenario.Ready);
            }
        }
        RaiseCanExecuteChanges();
    }

    private RawMouseDeviceInfo ResolveDeviceSelectionAfterRefresh(string previousSelectedId)
    {
        bool allowInitialSelection = string.IsNullOrWhiteSpace(_sessionDeviceId);
        if (_deviceSelectionCoordinator != null)
        {
            return _deviceSelectionCoordinator.ResolveSelectionAfterRefresh(_devices, previousSelectedId, allowInitialSelection, preferManualSelection: !IsLocked);
        }

        return RawMouseDeviceSelectionPolicy.ResolveSelectionAfterRefresh(_devices, previousSelectedId, allowInitialSelection);
    }

    private void ApplyPreferredDeviceSelection()
    {
        if (_deviceSelectionCoordinator == null || IsLocked)
        {
            return;
        }

        RawMouseDeviceInfo preferredDevice = _deviceSelectionCoordinator.FindPreferredDevice(_devices);
        if (preferredDevice == null || string.Equals(SelectedDevice?.DeviceId, preferredDevice.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SelectedDevice = preferredDevice;
    }

    public void CommitManualDeviceSelection()
    {
        if (!CanCommitManualDeviceSelection())
        {
            return;
        }

        _deviceSelectionCoordinator?.CommitManualSelection(SelectedDevice);
    }

    private bool CanCommitManualDeviceSelection()
    {
        return !IsLocked && SelectedDevice != null;
    }

    private void ApplyMetricsSnapshot(PollingMetricsSnapshot metricsSnapshot, bool forceMetricTextUpdate = false)
    {
        if (metricsSnapshot != null)
        {
            CurrentRate = metricsSnapshot.CurrentRate;
            PeakRate = metricsSnapshot.PeakRate;
            EmptyPacketPercent = metricsSnapshot.EmptyPacketPercent;
            DroppedPacketCount = metricsSnapshot.DroppedPacketCount;
            UpdateCurrentRateText(metricsSnapshot.CurrentRate, forceMetricTextUpdate);
            UpdateEmptyPacketText(metricsSnapshot.EmptyPacketPercent, forceMetricTextUpdate);
            PeakRateText = metricsSnapshot.PeakRate.ToString(CultureInfo.InvariantCulture);
            DroppedPacketCountText = metricsSnapshot.DroppedPacketCount.ToString(CultureInfo.InvariantCulture);
            UpdateMetricCardValues();
        }
    }

    private void ApplyChartRenderFrame(PollingChartRenderFrame chartRenderFrame)
    {
        if (chartRenderFrame != null)
        {
            RawCurrentRate = chartRenderFrame.RawCurrentRate;
            HistoryPoints = chartRenderFrame.HistoryPoints;
        }
    }

    private void ApplyLatestBufferedMetricsSnapshot()
    {
        PollingMetricsSnapshot metricsSnapshot = null;
        if (_captureService.TryReadLatestMetricsSnapshot(ref metricsSnapshot))
        {
            _latestMetricsSnapshot = metricsSnapshot;
            ApplyMetricsSnapshot(metricsSnapshot, forceMetricTextUpdate: true);
        }
    }

    private void ApplyLatestBufferedChartRenderFrame()
    {
        PollingChartRenderFrame chartRenderFrame = null;
        if (_captureService.TryReadLatestChartRenderFrame(ref chartRenderFrame))
        {
            _latestChartRenderFrame = chartRenderFrame;
            ApplyChartRenderFrame(chartRenderFrame);
        }
    }

    private void SetMetricsToPlaceholder()
    {
        RawCurrentRate = 0.0;
        CurrentRate = 0;
        PeakRate = 0;
        EmptyPacketPercent = null;
        DroppedPacketCount = 0L;
        HistoryPoints = new List<PollingHistoryPoint>();
        CurrentRateText = "--";
        PeakRateText = "--";
        EmptyPacketText = "--";
        DroppedPacketCountText = "--";
        UpdateMetricCardValues();
        ResetMetricTextState(_currentRateTextState);
        ResetMetricTextState(_emptyPacketTextState);
    }

    private void UpdateCurrentRateText(int targetRate, bool forceUpdate)
    {
        if (ShouldUpdateMetricText(_currentRateTextState, forceUpdate))
        {
            CurrentRateText = Math.Max(0, targetRate).ToString(CultureInfo.InvariantCulture);
        }
    }

    private void UpdateEmptyPacketText(double? targetEmptyPacketPercent, bool forceUpdate)
    {
        if (ShouldUpdateMetricText(_emptyPacketTextState, forceUpdate))
        {
            EmptyPacketText = FormatPercent(targetEmptyPacketPercent);
        }
    }

    private bool ShouldUpdateMetricText(MetricTextState state, bool forceUpdate)
    {
        if (state == null)
        {
            return false;
        }
        double realtimeMilliseconds = GetRealtimeMilliseconds();
        if (forceUpdate || !state.HasDisplayedValue)
        {
            state.HasDisplayedValue = true;
            state.LastUpdateMilliseconds = realtimeMilliseconds;
            return true;
        }
        if (!double.IsNaN(state.LastUpdateMilliseconds) && realtimeMilliseconds - state.LastUpdateMilliseconds < MetricTextUpdateIntervalMilliseconds)
        {
            return false;
        }
        state.LastUpdateMilliseconds = realtimeMilliseconds;
        return true;
    }

    private static string FormatPercent(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return "--";
        }
        return value.Value.ToString("0.00", CultureInfo.InvariantCulture) + "%";
    }

    private static void ResetMetricTextState(MetricTextState state)
    {
        if (state != null)
        {
            state.LastUpdateMilliseconds = double.NaN;
            state.HasDisplayedValue = false;
        }
    }

    private void ClearSession()
    {
        UpdatePollingUiRenderingSubscription(forceDetach: true);
        _statusRestoreTimer.Stop();
        _captureService.ResetStatistics();
        SetHasSessionData(value: false);
        _sessionDeviceId = null;
        _pendingDeviceId = null;
        _pendingSessionReset = false;
        _latestMetricsSnapshot = null;
        _latestChartRenderFrame = null;
        SetMetricsToPlaceholder();
        ApplyStatusScenario(StatusScenario.Cleared);
        UpdateStartButtonText();
        RaiseCanExecuteChanges();
    }

    private void SetHasSessionData(bool value)
    {
        if (_hasSessionData != value)
        {
            _hasSessionData = value;
            RaisePropertyChanged("IsRateModeSwitchEnabled");
        }
    }

    private void SetPollingRateMode(PollingRateMode mode)
    {
        if (mode != _selectedPollingRateMode)
        {
            if (!IsRateModeSwitchEnabled)
            {
                RaisePropertyChanged("IsRawPacketRateMode");
                RaisePropertyChanged("IsMotionReportRateMode");
                return;
            }
            _selectedPollingRateMode = mode;
            _captureService.SetMode(mode);
            _captureService.ResetStatistics();
            _latestMetricsSnapshot = null;
            _latestChartRenderFrame = null;
            SetMetricsToPlaceholder();
            UpdateRateModeDescriptionText();
            SetMetricCardMode(_selectedPollingRateMode);
            RaisePropertyChanged("IsRawPacketRateMode");
            RaisePropertyChanged("IsMotionReportRateMode");
        }
    }

    private void RefreshMetricCardTitles()
    {
        _metricCardsPresenter.RefreshTitles(key => L(key));
    }

    private void SetMetricCardMode(PollingRateMode mode)
    {
        int previousColumnCount = _metricCardsPresenter.ColumnCount;
        _metricCardsPresenter.SetMode(mode);
        if (previousColumnCount != _metricCardsPresenter.ColumnCount)
        {
            RaisePropertyChanged("MetricColumnCount");
        }
        UpdateMetricCardValues();
    }

    private void UpdateMetricCardValues()
    {
        _metricCardsPresenter.UpdateValues(PeakRateText, EmptyPacketText, DroppedPacketCountText);
    }

    public int MetricColumnCount => _metricCardsPresenter.ColumnCount;

    private void UpdateRateModeDescriptionText()
    {
        if (_selectedPollingRateMode == PollingRateMode.RawPacketRate)
        {
            RateModeDescriptionText = L("Control.ReportMode.Description.RawPacket");
        }
        else
        {
            RateModeDescriptionText = L("Control.ReportMode.Description.MotionReport");
        }
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
        SelectedDeviceTitle = SelectedDevice.SelectionDisplayName;
        List<string> deviceMetaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(SelectedDevice.VendorProductLabel))
        {
            deviceMetaParts.Add(SelectedDevice.VendorProductLabel);
        }
        if (SelectedDevice.ButtonCount > 0)
        {
            deviceMetaParts.Add(L("Device.Detail.Buttons", SelectedDevice.ButtonCount));
        }
        if (SelectedDevice.IsVirtual)
        {
            deviceMetaParts.Add(L("Device.Detail.Virtual"));
        }
        else
        {
            deviceMetaParts.Add(RawMouseEndpointKindLocalization.Resolve(SelectedDevice.EndpointKind, key => L(key)));
        }
        if (!string.IsNullOrWhiteSpace(SelectedDevice.EndpointToken))
        {
            deviceMetaParts.Add(SelectedDevice.EndpointToken);
        }
        if (deviceMetaParts.Count == 0)
        {
            SelectedDeviceMetaText = L("Device.Detail.RawInput");
        }
        else
        {
            SelectedDeviceMetaText = string.Join("  /  ", deviceMetaParts);
        }
        SelectedDevicePathText = SelectedDevice.PathSummary;
    }

    private void UpdateSelectedLanguageSelection()
    {
        LanguageOption currentLanguage = _localization.CurrentLanguage;
        if (currentLanguage != null && (_selectedLanguage == null || !string.Equals(_selectedLanguage.CultureName, currentLanguage.CultureName, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedLanguage = currentLanguage;
            RaisePropertyChanged("SelectedLanguage");
        }
    }

    private void UpdateLanguageToggleText()
    {
        LanguageOption nextLanguage = GetNextLanguage();
        if (nextLanguage == null)
        {
            LanguageToggleText = string.Empty;
            LanguageToggleKeyText = string.Empty;
        }
        else if (nextLanguage.CultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            LanguageToggleText = L("Language.Toggle.ToChinese");
            LanguageToggleKeyText = LanguageToggleText;
        }
        else if (nextLanguage.CultureName.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            LanguageToggleText = L("Language.Toggle.ToEnglish");
            LanguageToggleKeyText = LanguageToggleText;
        }
        else
        {
            LanguageToggleText = nextLanguage.NativeName;
            LanguageToggleKeyText = nextLanguage.CultureName.Split('-')[0].ToUpperInvariant();
        }
    }

    private LanguageOption GetNextLanguage()
    {
        if (_languageOptions.Count == 0)
        {
            return null;
        }
        LanguageOption currentLanguage = _selectedLanguage ?? _localization.CurrentLanguage;
        if (currentLanguage == null)
        {
            return _languageOptions[0];
        }

        int currentLanguageIndex = -1;
        for (int languageIndex = 0; languageIndex < _languageOptions.Count; languageIndex++)
        {
            if (string.Equals(_languageOptions[languageIndex].CultureName, currentLanguage.CultureName, StringComparison.OrdinalIgnoreCase))
            {
                currentLanguageIndex = languageIndex;
                break;
            }
        }
        if (currentLanguageIndex < 0)
        {
            return _languageOptions[0];
        }
        return _languageOptions[(currentLanguageIndex + 1) % _languageOptions.Count];
    }

    private void UpdateThemeToggleText()
    {
        if (GetNextTheme() == AppTheme.Light)
        {
            ThemeToggleText = L("Theme.Toggle.ToLight");
            ThemeToggleKeyText = L("Theme.Toggle.KeyLight");
        }
        else
        {
            ThemeToggleText = L("Theme.Toggle.ToDark");
            ThemeToggleKeyText = L("Theme.Toggle.KeyDark");
        }
    }

    private AppTheme GetNextTheme()
    {
        if (_themeManager.CurrentTheme == AppTheme.Dark)
        {
            return AppTheme.Light;
        }
        return AppTheme.Dark;
    }

    private void UpdateStartButtonText()
    {
        if (IsLocked)
        {
            StartButtonText = L("StartButton.Running");
        }
        else if (!_hasSessionData || SelectedDevice == null)
        {
            StartButtonText = L("StartButton.Start");
        }
        else if (string.Equals(_sessionDeviceId, SelectedDevice.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            StartButtonText = L("StartButton.Continue");
        }
        else
        {
            StartButtonText = L("StartButton.NewSession");
        }
    }

    private void ApplyStatusScenario(StatusScenario scenario)
    {
        if (scenario != StatusScenario.RefreshFeedback && _statusRestoreTimer.IsEnabled)
        {
            _statusRestoreTimer.Stop();
        }
        _statusScenario = scenario;
        switch (scenario)
        {
            case StatusScenario.Ready:
                SetStatus(L("Status.Ready.Pill"), L("Status.Ready.Message"), L("Status.Ready.Hint"));
                break;
            case StatusScenario.Measuring:
                SetStatus(L("Status.Measuring.Pill"), L("Status.Measuring.Message"), L("Status.Measuring.Hint"));
                break;
            case StatusScenario.Paused:
                SetStatus(L("Status.Paused.Pill"), L("Status.Paused.Message"), L("Status.Paused.Hint"));
                break;
            case StatusScenario.Cleared:
                SetStatus(L("Status.Cleared.Pill"), L("Status.Cleared.Message"), L("Status.Cleared.Hint"));
                break;
            case StatusScenario.NeedDevice:
                SetStatus(L("Status.NeedDevice.Pill"), L("Status.NeedDevice.Message"), L("Status.NeedDevice.Hint"));
                break;
            case StatusScenario.DeviceDisconnected:
                SetStatus(L("Status.DeviceDisconnected.Pill"), L("Status.DeviceDisconnected.Message"), L("Status.DeviceDisconnected.Hint"));
                break;
            case StatusScenario.NoDevices:
                SetStatus(L("Status.NoDevices.Pill"), L("Status.NoDevices.Message"), L("Status.NoDevices.Hint"));
                break;
            case StatusScenario.RefreshFeedback:
                SetStatus(message: (Devices.Count != 0) ? L("Status.Refresh.Message.Count", Devices.Count) : L("Status.Refresh.Message.None"), hint: (SelectedDevice != null) ? L("Status.Refresh.Hint.Selected", SelectedDevice.DisplayName) : L("Status.Refresh.Hint.NoSelection"), pill: L("Status.Refresh.Pill"));
                break;
        }
    }

    private void SetStatus(string pill, string message, string hint)
    {
        StatusPillText = pill;
        StatusMessage = message;
        HintText = hint;
    }

    private void ShowRefreshFeedback()
    {
        StatusScenario restoreStatusScenario = _statusScenario;
        if (_statusRestoreTimer.IsEnabled)
        {
            restoreStatusScenario = _restoreStatusScenario;
            _statusRestoreTimer.Stop();
        }
        _restoreStatusScenario = restoreStatusScenario;
        ApplyStatusScenario(StatusScenario.RefreshFeedback);
        _statusRestoreTimer.Start();
    }

    private void OnStatusRestoreTimerTick(object sender, EventArgs e)
    {
        _statusRestoreTimer.Stop();
        ApplyStatusScenario(_restoreStatusScenario);
    }

    private void RaiseCanExecuteChanges()
    {
        _refreshDevicesCommand.RaiseCanExecuteChanged();
        _commitManualDeviceSelectionCommand.RaiseCanExecuteChanged();
        _startCommand.RaiseCanExecuteChanged();
        _stopCommand.RaiseCanExecuteChanged();
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

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            UpdatePollingUiRenderingSubscription(forceDetach: true);
            _statusRestoreTimer.Stop();
            _statusRestoreTimer.Tick -= OnStatusRestoreTimerTick;
            _captureService.DevicesChanged -= OnDevicesChanged;
            _captureService.SelectedDeviceDisconnected -= OnSelectedDeviceDisconnected;
            if (_deviceSelectionCoordinator != null)
            {
                _deviceSelectionCoordinator.PreferredDeviceChanged -= OnPreferredDeviceChanged;
            }
            _localization.LanguageChanged -= OnLanguageChanged;
            _themeManager.ThemeChanged -= OnThemeChanged;
            _captureService.Dispose();
        }
    }

    void IDisposable.Dispose()
    {
        this.Dispose();
    }

    private static double GetRealtimeMilliseconds()
    {
        return (double)Stopwatch.GetTimestamp() * 1000.0 / (double)Stopwatch.Frequency;
    }

    private static double ResolveChartFrameRate(double? displayRefreshRateHz)
    {
        if (!displayRefreshRateHz.HasValue || double.IsNaN(displayRefreshRateHz.Value) || double.IsInfinity(displayRefreshRateHz.Value))
        {
            return HighRefreshDisplayChartFrameRateHz;
        }

        return displayRefreshRateHz.Value > HighRefreshDisplayThresholdHz
            ? HighRefreshDisplayChartFrameRateHz
            : LowRefreshDisplayChartFrameRateHz;
    }
}
