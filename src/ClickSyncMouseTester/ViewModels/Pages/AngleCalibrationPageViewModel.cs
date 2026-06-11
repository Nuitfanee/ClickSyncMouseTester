using ClickSyncMouseTester.Infrastructure;
using ClickSyncMouseTester.Models;
using ClickSyncMouseTester.Navigation;
using ClickSyncMouseTester.Services;
using System;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;

namespace ClickSyncMouseTester.ViewModels.Pages;

[SupportedOSPlatform("windows")]
public class AngleCalibrationPageViewModel : BindableBase, IDisposable, ICaptureSessionPageViewModel, INavigationResettablePageViewModel
{
    private enum StatusScenario
    {
        Ready,
        AwaitingInput,
        Measuring,
        Paused,
        ResultReady,
        NoDevice,
        DeviceDisconnected
    }

    private const int ResultSwipeThreshold = 30;

    private const double UiRefreshIntervalMilliseconds = 16.666666666666668;

    private readonly Dispatcher _dispatcher;

    private readonly IRawInputBroker _rawInputBroker;

    private readonly LocalizationManager _localization;

    private readonly DispatcherTimer _uiTimer;

    private readonly DelegateCommand _captureSurfaceCommand;

    private readonly DelegateCommand _copyAngleCommand;

    private readonly DelegateCommand _resetCommand;

    private AngleCalibrationCaptureService _captureService;

    private AngleCalibrationRenderFrame _renderFrame;

    private string _recommendedAngleText;

    private string _swipeCountText;

    private string _sampleCountText;

    private string _stabilityText;

    private string _topHintText;

    private string _qualityHintText;

    private bool _isLocked;

    private bool _isPageActive;

    private bool _isDeviceDisconnected;

    private bool _disposed;

    public AngleCalibrationRenderFrame RenderFrame
    {
        get
        {
            return _renderFrame;
        }
        private set
        {
            if (value == null)
            {
                value = CreateEmptyRenderFrame();
            }
            SetProperty(ref _renderFrame, value, "RenderFrame");
        }
    }

    public string RecommendedAngleText
    {
        get
        {
            return _recommendedAngleText;
        }
        private set
        {
            SetProperty(ref _recommendedAngleText, value, "RecommendedAngleText");
        }
    }

    public string SwipeCountText
    {
        get
        {
            return _swipeCountText;
        }
        private set
        {
            SetProperty(ref _swipeCountText, value, "SwipeCountText");
        }
    }

    public string SampleCountText
    {
        get
        {
            return _sampleCountText;
        }
        private set
        {
            SetProperty(ref _sampleCountText, value, "SampleCountText");
        }
    }

    public string StabilityText
    {
        get
        {
            return _stabilityText;
        }
        private set
        {
            SetProperty(ref _stabilityText, value, "StabilityText");
        }
    }

    public string TopHintText
    {
        get
        {
            return _topHintText;
        }
        private set
        {
            SetProperty(ref _topHintText, value, "TopHintText");
        }
    }

    public string QualityHintText
    {
        get
        {
            return _qualityHintText;
        }
        private set
        {
            SetProperty(ref _qualityHintText, value, "QualityHintText");
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
            SetProperty(ref _isLocked, value, "IsLocked");
        }
    }

    public DelegateCommand CaptureSurfaceCommand => _captureSurfaceCommand;

    public DelegateCommand ResetCommand => _resetCommand;

    public DelegateCommand CopyAngleCommand => _copyAngleCommand;

    public bool HasRecommendedAngle
    {
        get
        {
            if (RenderFrame != null)
            {
                return RenderFrame.HasRecommendedAngle;
            }
            return false;
        }
    }

    public bool HasQualityHint => !string.IsNullOrWhiteSpace(QualityHintText);

    public event EventHandler EnterLockRequested;

    public event EventHandler<CaptureLockRequestEventArgs> ExitLockRequested;

    public AngleCalibrationPageViewModel(IRawInputBroker rawInputBroker)
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
        _rawInputBroker = rawInputBroker;
        _localization = LocalizationManager.Instance;
        _localization.Initialize();
        _uiTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher);
        _uiTimer.Interval = TimeSpan.FromMilliseconds(16.666666666666668);
        _captureSurfaceCommand = new DelegateCommand(RequestCaptureSurfaceInteraction);
        _copyAngleCommand = new DelegateCommand(RequestCopyAngle, CanCopyAngle);
        _resetCommand = new DelegateCommand(RequestReset);
        _renderFrame = CreateEmptyRenderFrame();
        _localization.LanguageChanged += OnLanguageChanged;
        _uiTimer.Tick += OnUiTimerTick;
        RefreshFrameText(_renderFrame);
        ApplyStatusScenario(StatusScenario.Ready);
    }

    public void SetPageActive(bool isActive)
    {
        if (_isPageActive != isActive)
        {
            _isPageActive = isActive;
            if (isActive)
            {
                EnsureCaptureService();
                PullLatestRenderFrame(forceCapture: true, updateScenario: true);
                RefreshAvailableDeviceState();
                UpdateUiTimer();
            }
            else
            {
                _uiTimer.Stop();
            }
        }
    }

    public void ResetToDefaultState()
    {
        _uiTimer.Stop();
        if (_captureService != null)
        {
            _captureService.StopSession();
            _captureService.ResetSession();
        }
        ReleaseCaptureService();
        _isPageActive = false;
        _isDeviceDisconnected = false;
        IsLocked = false;
        RenderFrame = CreateEmptyRenderFrame();
        RefreshFrameText(RenderFrame);
        ApplyStatusScenario(StatusScenario.Ready);
    }

    void INavigationResettablePageViewModel.ResetToDefaultState()
    {
        this.ResetToDefaultState();
    }

    public void OnLockEntered()
    {
        EnsureCaptureService();
        if (_captureService != null)
        {
            if (!_captureService.BeginSession())
            {
                IsLocked = false;
                _isDeviceDisconnected = false;
                ApplyStatusScenario(StatusScenario.NoDevice);
                ExitLockRequested?.Invoke(this, new CaptureLockRequestEventArgs(CaptureUnlockReason.PauseSession));
            }
            else
            {
                _isDeviceDisconnected = false;
                IsLocked = true;
                PullLatestRenderFrame(forceCapture: true, updateScenario: true);
                UpdateUiTimer();
            }
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
        IsLocked = false;
        if (_captureService == null)
        {
            ApplyScenarioFromFrame();
            return;
        }
        switch (reason)
        {
            case CaptureUnlockReason.ClearSession:
                _captureService.StopSession();
                _captureService.ResetSession();
                _isDeviceDisconnected = false;
                PullLatestRenderFrame(forceCapture: true, updateScenario: false);
                RefreshAvailableDeviceState();
                break;
            case CaptureUnlockReason.DeviceDisconnected:
                _captureService.StopSession();
                _isDeviceDisconnected = true;
                PullLatestRenderFrame(forceCapture: true, updateScenario: false);
                ApplyStatusScenario(StatusScenario.DeviceDisconnected);
                break;
            default:
                _captureService.PauseSession();
                _isDeviceDisconnected = false;
                PullLatestRenderFrame(forceCapture: true, updateScenario: true);
                break;
        }
        UpdateUiTimer();
    }

    void ICaptureSessionPageViewModel.OnViewUnlockCompleted(CaptureUnlockReason reason)
    {
        this.OnViewUnlockCompleted(reason);
    }

    private void EnsureCaptureService()
    {
        if (_captureService == null)
        {
            AngleCalibrationCaptureService angleCalibrationCaptureService = new AngleCalibrationCaptureService(_rawInputBroker);
            angleCalibrationCaptureService.DevicesChanged += OnDevicesChanged;
            angleCalibrationCaptureService.SelectedDeviceDisconnected += OnSelectedDeviceDisconnected;
            _captureService = angleCalibrationCaptureService;
        }
    }

    private void ReleaseCaptureService()
    {
        if (_captureService != null)
        {
            _captureService.DevicesChanged -= OnDevicesChanged;
            _captureService.SelectedDeviceDisconnected -= OnSelectedDeviceDisconnected;
            _captureService.Dispose();
            _captureService = null;
        }
    }

    private void RequestCaptureSurfaceInteraction()
    {
        if (IsLocked)
        {
            return;
        }
        EnsureCaptureService();
        if (_captureService != null)
        {
            if (!_captureService.HasAvailableMouseDevice())
            {
                ApplyStatusScenario(StatusScenario.NoDevice);
                return;
            }
            _isDeviceDisconnected = false;
            EnterLockRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RequestReset()
    {
        if (IsLocked)
        {
            ExitLockRequested?.Invoke(this, new CaptureLockRequestEventArgs(CaptureUnlockReason.ClearSession));
            return;
        }
        EnsureCaptureService();
        if (_captureService != null)
        {
            _captureService.StopSession();
            _captureService.ResetSession();
            _isDeviceDisconnected = false;
            PullLatestRenderFrame(forceCapture: true, updateScenario: false);
        }
        else
        {
            RenderFrame = CreateEmptyRenderFrame();
            RefreshFrameText(RenderFrame);
        }
        RefreshAvailableDeviceState();
    }

    private bool CanCopyAngle()
    {
        return HasRecommendedAngle;
    }

    private void RequestCopyAngle()
    {
        if (CanCopyAngle())
        {
            Clipboard.SetText(RecommendedAngleText);
        }
    }

    private void OnDevicesChanged(object sender, EventArgs e)
    {
        RunOnUiThread(RefreshAvailableDeviceState);
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
                _isDeviceDisconnected = true;
                PullLatestRenderFrame(forceCapture: true, updateScenario: false);
                ApplyStatusScenario(StatusScenario.DeviceDisconnected);
            }
        });
    }

    private void OnLanguageChanged(object sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            RefreshFrameText(RenderFrame);
            ApplyScenarioFromFrame();
        });
    }

    private void OnUiTimerTick(object sender, EventArgs e)
    {
        if (_captureService != null)
        {
            PullLatestRenderFrame(IsLocked, !_isDeviceDisconnected);
        }
    }

    private void PullLatestRenderFrame(bool forceCapture, bool updateScenario)
    {
        if (_captureService == null)
        {
            return;
        }
        AngleCalibrationRenderFrame renderFrame = null;
        if (!_captureService.TryReadLatestRenderFrame(ref renderFrame) && forceCapture)
        {
            renderFrame = _captureService.CaptureRenderFrame();
        }
        if (renderFrame != null)
        {
            RenderFrame = renderFrame;
            RefreshFrameText(renderFrame);
            if (updateScenario)
            {
                ApplyScenarioFromFrame();
            }
        }
    }

    private void RefreshAvailableDeviceState()
    {
        if (_captureService != null && !IsLocked)
        {
            bool hasAvailableDevice = _captureService.HasAvailableMouseDevice();
            if (_isDeviceDisconnected && hasAvailableDevice)
            {
                _isDeviceDisconnected = false;
            }
            if (_isDeviceDisconnected)
            {
                ApplyStatusScenario(StatusScenario.DeviceDisconnected);
            }
            else if (!hasAvailableDevice && (RenderFrame == null || !RenderFrame.HasData))
            {
                ApplyStatusScenario(StatusScenario.NoDevice);
            }
            else if (hasAvailableDevice && (RenderFrame == null || !RenderFrame.HasData))
            {
                ApplyStatusScenario(StatusScenario.Ready);
            }
            else
            {
                ApplyScenarioFromFrame();
            }
        }
    }

    private void ApplyScenarioFromFrame()
    {
        if (_isDeviceDisconnected)
        {
            ApplyStatusScenario(StatusScenario.DeviceDisconnected);
            return;
        }
        AngleCalibrationRenderFrame renderFrame = RenderFrame;
        bool hasTraceData = renderFrame?.HasData ?? false;
        bool hasAvailableDevice = _captureService != null && _captureService.HasAvailableMouseDevice();
        bool hasActiveSessionDevice = _captureService != null && !string.IsNullOrWhiteSpace(_captureService.CurrentSessionDeviceId);
        if (!hasTraceData)
        {
            if (IsLocked)
            {
                ApplyStatusScenario(!hasActiveSessionDevice ? StatusScenario.AwaitingInput : StatusScenario.Measuring);
            }
            else
            {
                ApplyStatusScenario(!hasAvailableDevice ? StatusScenario.NoDevice : StatusScenario.Ready);
            }
        }
        else if (renderFrame.HasRecommendedAngle)
        {
            ApplyStatusScenario(StatusScenario.ResultReady);
        }
        else if (IsLocked)
        {
            ApplyStatusScenario(StatusScenario.Measuring);
        }
        else
        {
            ApplyStatusScenario(StatusScenario.Paused);
        }
    }

    private void ApplyStatusScenario(StatusScenario scenario)
    {
        switch (scenario)
        {
            case StatusScenario.Ready:
                TopHintText = L("AngleCalibration.Hint.Ready");
                break;
            case StatusScenario.AwaitingInput:
                TopHintText = L("AngleCalibration.Hint.AwaitingInput");
                break;
            case StatusScenario.Measuring:
                TopHintText = L("AngleCalibration.Hint.Measuring");
                break;
            case StatusScenario.Paused:
                TopHintText = L("AngleCalibration.Hint.Paused");
                break;
            case StatusScenario.ResultReady:
                TopHintText = (IsLocked ? L("AngleCalibration.Hint.ResultReady.Active") : L("AngleCalibration.Hint.ResultReady.Paused"));
                break;
            case StatusScenario.NoDevice:
                TopHintText = L("AngleCalibration.Hint.NoDevice");
                break;
            case StatusScenario.DeviceDisconnected:
                TopHintText = L("AngleCalibration.Hint.DeviceDisconnected");
                break;
        }
    }

    private void RefreshFrameText(AngleCalibrationRenderFrame frame)
    {
        if (frame == null || !frame.HasRecommendedAngle || !frame.RecommendedAngleDegrees.HasValue)
        {
            RecommendedAngleText = "--";
        }
        else
        {
            RecommendedAngleText = frame.RecommendedAngleDegrees.Value.ToString("0.0", CultureInfo.InvariantCulture);
        }
        int swipeCount = frame?.SwipeCount ?? 0;
        int sampleCount = frame?.SampleCount ?? 0;
        double? stabilityDegrees = frame?.StabilityDegrees;
        SwipeCountText = string.Format(CultureInfo.InvariantCulture, "{0} /{1}", swipeCount, 30);
        SampleCountText = sampleCount.ToString(CultureInfo.InvariantCulture);
        if (!stabilityDegrees.HasValue || double.IsNaN(stabilityDegrees.Value) || double.IsInfinity(stabilityDegrees.Value))
        {
            StabilityText = "--";
        }
        else
        {
            StabilityText = string.Format(CultureInfo.InvariantCulture, "±{0:0.0}°", stabilityDegrees.Value);
        }
        QualityHintText = ResolveQualityHintText(frame);
        RaisePropertyChanged("HasRecommendedAngle");
        RaisePropertyChanged("HasQualityHint");
        _copyAngleCommand.RaiseCanExecuteChanged();
    }

    private string ResolveQualityHintText(AngleCalibrationRenderFrame frame)
    {
        if (frame == null || !frame.HasData)
        {
            return string.Empty;
        }
        int droppedPacketCount = _captureService != null ? _captureService.DroppedPacketCount : 0;
        if (droppedPacketCount > 0)
        {
            return L("AngleCalibration.Quality.QueueOverflow", droppedPacketCount.ToString(CultureInfo.InvariantCulture));
        }
        switch (frame.QualityReason)
        {
            case AngleCalibrationQualityReason.InsufficientProgress:
                return L("AngleCalibration.Quality.InsufficientProgress");
            case AngleCalibrationQualityReason.Imbalance:
                return L("AngleCalibration.Quality.Imbalance");
            case AngleCalibrationQualityReason.HighDispersion:
                return L("AngleCalibration.Quality.HighDispersion");
            case AngleCalibrationQualityReason.TooManyOutliers:
                return L("AngleCalibration.Quality.Outliers");
            case AngleCalibrationQualityReason.Good:
                if (frame.QualityLevel == AngleCalibrationQualityLevel.Excellent)
                {
                    return L("AngleCalibration.Quality.Excellent");
                }
                if (frame.QualityLevel == AngleCalibrationQualityLevel.Good)
                {
                    return L("AngleCalibration.Quality.Good");
                }
                return L("AngleCalibration.Quality.Fair");
            default:
                return string.Empty;
        }
    }

    private void UpdateUiTimer()
    {
        bool shouldRunTimer = _isPageActive && _captureService != null && (IsLocked || (RenderFrame != null && RenderFrame.HasData));
        if (shouldRunTimer && !_uiTimer.IsEnabled)
        {
            _uiTimer.Start();
        }
        else if (!shouldRunTimer && _uiTimer.IsEnabled)
        {
            _uiTimer.Stop();
        }
    }

    private static AngleCalibrationRenderFrame CreateEmptyRenderFrame()
    {
        return new AngleCalibrationRenderFrame(AngleCalibrationStatus.Empty, isLocked: false, hasData: false, null, 0, 0, null, Array.Empty<AngleCalibrationTraceStroke>(), AngleCalibrationQualityLevel.None, AngleCalibrationQualityReason.None, 0);
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
            _uiTimer.Stop();
            _localization.LanguageChanged -= OnLanguageChanged;
            _uiTimer.Tick -= OnUiTimerTick;
            ReleaseCaptureService();
        }
    }

    void IDisposable.Dispose()
    {
        this.Dispose();
    }
}





