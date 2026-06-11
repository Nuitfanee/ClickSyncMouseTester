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
using System.Windows;
using System.Windows.Threading;

namespace ClickSyncMouseTester.ViewModels.Pages;

[SupportedOSPlatform("windows")]
public class SensitivityMatchingPageViewModel : BindableBase, IDisposable, INavigationResettablePageViewModel, ICaptureKeyboardShortcutHandler
{

    private const double UiRefreshIntervalMilliseconds = 16.666666666666668;

    private const int MinimumDpi = 50;

    private const int MaximumDpi = 50000;

    private readonly Dispatcher _dispatcher;

    private readonly LocalizationManager _localization;

    private readonly SensitivityMatchCaptureService _captureService;

    private readonly DispatcherTimer _uiTimer;

    private readonly ObservableCollection<SensitivityMatchRoundCardViewModel> _rounds;

    private readonly SensitivityMatchInfoBandViewModel _sourceInfoBand;

    private readonly SensitivityMatchInfoBandViewModel _targetInfoBand;

    private readonly DelegateCommand _startSetupCommand;

    private readonly DelegateCommand _backCommand;

    private readonly DelegateCommand _continueFromSetupCommand;

    private readonly DelegateCommand _bindSlotCommand;

    private readonly DelegateCommand _startRoundCommand;

    private readonly DelegateCommand _copyRecommendedDpiCommand;

    private readonly DelegateCommand _remeasureCommand;

    private readonly DelegateCommand _rebindCommand;

    private SensitivityMatchSnapshot _latestSnapshot;

    private SensitivityMatchWizardStep _currentStep;

    private string _sourceDpiText;

    private string _targetCurrentDpiText;

    private string _statusPillText;

    private string _statusMessage;

    private string _hintText;

    private string _availableDeviceCountText;

    private string _sourceDeviceTitle;

    private string _sourceDeviceMetaText;

    private string _sourceBindingStateText;

    private string _sourceBindButtonText;

    private string _targetDeviceTitle;

    private string _targetDeviceMetaText;

    private string _targetBindingStateText;

    private string _targetBindButtonText;

    private string _recommendedTargetDpiText;

    private string _scaleText;

    private string _consistencyText;

    private string _startRoundButtonText;

    private double _overallProgressValue;

    private double _sourceProgressValue;

    private double _targetProgressValue;

    private string _overallProgressText;

    private string _sourceProgressText;

    private string _targetProgressText;

    private string _measureHeroPrimaryText;

    private string _measureHeroLabelText;

    private string _measureHeroSupportText;

    private string _measureSourceValueText;

    private string _measureTargetValueText;

    private bool _isSourceBindingPending;

    private bool _isTargetBindingPending;

    private bool _isMeasureActiveState;

    private bool _isMeasureFailureState;

    private bool _isMeasureExpiredState;

    private bool _hasMinimumDeviceCount;

    private bool _hasFinalRecommendation;

    private bool _isPageActive;

    private bool _disposed;

    public ObservableCollection<SensitivityMatchRoundCardViewModel> Rounds => _rounds;

    public SensitivityMatchInfoBandViewModel SourceInfoBand => _sourceInfoBand;

    public SensitivityMatchInfoBandViewModel TargetInfoBand => _targetInfoBand;

    public SensitivityMatchWizardStep CurrentStep
    {
        get
        {
            return _currentStep;
        }
        private set
        {
            if (SetProperty(ref _currentStep, value, "CurrentStep"))
            {
                RaisePropertyChanged("IsIntroStep");
                RaisePropertyChanged("IsSetupStep");
                RaisePropertyChanged("IsMeasureStep");
                RaiseCanExecuteChanges();
            }
        }
    }

    public bool IsIntroStep => CurrentStep == SensitivityMatchWizardStep.Intro;

    public bool IsSetupStep => CurrentStep == SensitivityMatchWizardStep.Setup;

    public bool IsMeasureStep => CurrentStep == SensitivityMatchWizardStep.Measure;

    public string SourceDpiText
    {
        get
        {
            return _sourceDpiText;
        }
        set
        {
            string normalizedDpiText = NormalizeDpiText(value);
            if (SetProperty(ref _sourceDpiText, normalizedDpiText, "SourceDpiText"))
            {
                OnDpiInputsChanged();
            }
        }
    }

    public string TargetCurrentDpiText
    {
        get
        {
            return _targetCurrentDpiText;
        }
        set
        {
            string normalizedDpiText = NormalizeDpiText(value);
            if (SetProperty(ref _targetCurrentDpiText, normalizedDpiText, "TargetCurrentDpiText"))
            {
                OnDpiInputsChanged();
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

    public string AvailableDeviceCountText
    {
        get
        {
            return _availableDeviceCountText;
        }
        private set
        {
            SetProperty(ref _availableDeviceCountText, value, "AvailableDeviceCountText");
        }
    }

    public string SourceDeviceTitle
    {
        get
        {
            return _sourceDeviceTitle;
        }
        private set
        {
            SetProperty(ref _sourceDeviceTitle, value, "SourceDeviceTitle");
        }
    }

    public string SourceDeviceMetaText
    {
        get
        {
            return _sourceDeviceMetaText;
        }
        private set
        {
            SetProperty(ref _sourceDeviceMetaText, value, "SourceDeviceMetaText");
        }
    }

    public string SourceBindingStateText
    {
        get
        {
            return _sourceBindingStateText;
        }
        private set
        {
            SetProperty(ref _sourceBindingStateText, value, "SourceBindingStateText");
        }
    }

    public string SourceBindButtonText
    {
        get
        {
            return _sourceBindButtonText;
        }
        private set
        {
            SetProperty(ref _sourceBindButtonText, value, "SourceBindButtonText");
        }
    }

    public string TargetDeviceTitle
    {
        get
        {
            return _targetDeviceTitle;
        }
        private set
        {
            SetProperty(ref _targetDeviceTitle, value, "TargetDeviceTitle");
        }
    }

    public string TargetDeviceMetaText
    {
        get
        {
            return _targetDeviceMetaText;
        }
        private set
        {
            SetProperty(ref _targetDeviceMetaText, value, "TargetDeviceMetaText");
        }
    }

    public string TargetBindingStateText
    {
        get
        {
            return _targetBindingStateText;
        }
        private set
        {
            SetProperty(ref _targetBindingStateText, value, "TargetBindingStateText");
        }
    }

    public string TargetBindButtonText
    {
        get
        {
            return _targetBindButtonText;
        }
        private set
        {
            SetProperty(ref _targetBindButtonText, value, "TargetBindButtonText");
        }
    }

    public string RecommendedTargetDpiText
    {
        get
        {
            return _recommendedTargetDpiText;
        }
        private set
        {
            SetProperty(ref _recommendedTargetDpiText, value, "RecommendedTargetDpiText");
        }
    }

    public string ScaleText
    {
        get
        {
            return _scaleText;
        }
        private set
        {
            SetProperty(ref _scaleText, value, "ScaleText");
        }
    }

    public string ConsistencyText
    {
        get
        {
            return _consistencyText;
        }
        private set
        {
            SetProperty(ref _consistencyText, value, "ConsistencyText");
        }
    }

    public string StartRoundButtonText
    {
        get
        {
            return _startRoundButtonText;
        }
        private set
        {
            SetProperty(ref _startRoundButtonText, value, "StartRoundButtonText");
        }
    }

    public double OverallProgressValue
    {
        get
        {
            return _overallProgressValue;
        }
        private set
        {
            SetProperty(ref _overallProgressValue, value, "OverallProgressValue");
        }
    }

    public double SourceProgressValue
    {
        get
        {
            return _sourceProgressValue;
        }
        private set
        {
            SetProperty(ref _sourceProgressValue, value, "SourceProgressValue");
        }
    }

    public double TargetProgressValue
    {
        get
        {
            return _targetProgressValue;
        }
        private set
        {
            SetProperty(ref _targetProgressValue, value, "TargetProgressValue");
        }
    }

    public string OverallProgressText
    {
        get
        {
            return _overallProgressText;
        }
        private set
        {
            SetProperty(ref _overallProgressText, value, "OverallProgressText");
        }
    }

    public string SourceProgressText
    {
        get
        {
            return _sourceProgressText;
        }
        private set
        {
            SetProperty(ref _sourceProgressText, value, "SourceProgressText");
        }
    }

    public string TargetProgressText
    {
        get
        {
            return _targetProgressText;
        }
        private set
        {
            SetProperty(ref _targetProgressText, value, "TargetProgressText");
        }
    }

    public string MeasureHeroPrimaryText
    {
        get
        {
            return _measureHeroPrimaryText;
        }
        private set
        {
            SetProperty(ref _measureHeroPrimaryText, value, "MeasureHeroPrimaryText");
        }
    }

    public string MeasureHeroLabelText
    {
        get
        {
            return _measureHeroLabelText;
        }
        private set
        {
            SetProperty(ref _measureHeroLabelText, value, "MeasureHeroLabelText");
        }
    }

    public string MeasureHeroSupportText
    {
        get
        {
            return _measureHeroSupportText;
        }
        private set
        {
            SetProperty(ref _measureHeroSupportText, value, "MeasureHeroSupportText");
        }
    }

    public string MeasureSourceValueText
    {
        get
        {
            return _measureSourceValueText;
        }
        private set
        {
            SetProperty(ref _measureSourceValueText, value, "MeasureSourceValueText");
        }
    }

    public string MeasureTargetValueText
    {
        get
        {
            return _measureTargetValueText;
        }
        private set
        {
            SetProperty(ref _measureTargetValueText, value, "MeasureTargetValueText");
        }
    }

    public bool IsSourceBindingPending
    {
        get
        {
            return _isSourceBindingPending;
        }
        private set
        {
            SetProperty(ref _isSourceBindingPending, value, "IsSourceBindingPending");
        }
    }

    public bool IsTargetBindingPending
    {
        get
        {
            return _isTargetBindingPending;
        }
        private set
        {
            SetProperty(ref _isTargetBindingPending, value, "IsTargetBindingPending");
        }
    }

    public bool IsMeasureActiveState
    {
        get
        {
            return _isMeasureActiveState;
        }
        private set
        {
            SetProperty(ref _isMeasureActiveState, value, "IsMeasureActiveState");
        }
    }

    public bool IsMeasureFailureState
    {
        get
        {
            return _isMeasureFailureState;
        }
        private set
        {
            SetProperty(ref _isMeasureFailureState, value, "IsMeasureFailureState");
        }
    }

    public bool IsMeasureExpiredState
    {
        get
        {
            return _isMeasureExpiredState;
        }
        private set
        {
            SetProperty(ref _isMeasureExpiredState, value, "IsMeasureExpiredState");
        }
    }

    public bool HasMinimumDeviceCount
    {
        get
        {
            return _hasMinimumDeviceCount;
        }
        private set
        {
            SetProperty(ref _hasMinimumDeviceCount, value, "HasMinimumDeviceCount");
        }
    }

    public bool HasFinalRecommendation
    {
        get
        {
            return _hasFinalRecommendation;
        }
        private set
        {
            SetProperty(ref _hasFinalRecommendation, value, "HasFinalRecommendation");
        }
    }

    public DelegateCommand StartSetupCommand => _startSetupCommand;

    public DelegateCommand BackCommand => _backCommand;

    public DelegateCommand ContinueFromSetupCommand => _continueFromSetupCommand;

    public bool IsContinueFromSetupEnabled => CanContinueFromSetup();

    public DelegateCommand BindSlotCommand => _bindSlotCommand;

    public DelegateCommand StartRoundCommand => _startRoundCommand;

    public DelegateCommand CopyRecommendedDpiCommand => _copyRecommendedDpiCommand;

    public DelegateCommand RemeasureCommand => _remeasureCommand;

    public DelegateCommand RebindCommand => _rebindCommand;

    public SensitivityMatchingPageViewModel(IRawInputBroker rawInputBroker)
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
        _localization.Initialize();
        _captureService = new SensitivityMatchCaptureService(rawInputBroker);
        _uiTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher);
        _uiTimer.Interval = TimeSpan.FromMilliseconds(16.666666666666668);
        _rounds = new ObservableCollection<SensitivityMatchRoundCardViewModel>();
        _sourceInfoBand = new SensitivityMatchInfoBandViewModel
        {
            BandHorizontalAlignment = HorizontalAlignment.Left,
            BandScaleX = 1.0
        };
        _targetInfoBand = new SensitivityMatchInfoBandViewModel
        {
            BandHorizontalAlignment = HorizontalAlignment.Right,
            BandScaleX = -1.0
        };
        const int roundCardCount = 3;
        for (int roundIndex = 1; roundIndex <= roundCardCount; roundIndex++)
        {
            _rounds.Add(new SensitivityMatchRoundCardViewModel());
        }
        _startSetupCommand = new DelegateCommand(RequestStartSetup, CanStartSetup);
        _backCommand = new DelegateCommand(RequestBack, CanGoBack);
        _continueFromSetupCommand = new DelegateCommand(RequestContinueFromSetup, CanContinueFromSetup);
        _bindSlotCommand = new DelegateCommand(RequestBindSlot, null);
        _startRoundCommand = new DelegateCommand(RequestStartRound, CanStartRound);
        _copyRecommendedDpiCommand = new DelegateCommand(RequestCopyRecommendedDpi, CanCopyRecommendedDpi);
        _remeasureCommand = new DelegateCommand(RequestRemeasure, CanRemeasure);
        _rebindCommand = new DelegateCommand(RequestRebind);
        _localization.LanguageChanged += OnLanguageChanged;
        _uiTimer.Tick += OnUiTimerTick;
        _captureService.DevicesChanged += OnDevicesChanged;
        _currentStep = SensitivityMatchWizardStep.Intro;
        _latestSnapshot = _captureService.CaptureSnapshot();
        RefreshFromSnapshot(allowAutoStepAdvance: false);
    }

    public void SetPageActive(bool isActive)
    {
        if (_isPageActive != isActive)
        {
            _isPageActive = isActive;
            if (isActive)
            {
                RefreshFromSnapshot(allowAutoStepAdvance: true);
                return;
            }
            _uiTimer.Stop();
            _captureService.CancelTransientActivity();
            RefreshFromSnapshot(allowAutoStepAdvance: false);
        }
    }

    public void ResetToDefaultState()
    {
        _uiTimer.Stop();
        _captureService.CancelTransientActivity();
        _captureService.ResetAll();
        _isPageActive = false;
        SetProperty(ref _sourceDpiText, string.Empty, "SourceDpiText");
        SetProperty(ref _targetCurrentDpiText, string.Empty, "TargetCurrentDpiText");
        CurrentStep = SensitivityMatchWizardStep.Intro;
        RefreshFromSnapshot(allowAutoStepAdvance: false);
    }

    void INavigationResettablePageViewModel.ResetToDefaultState()
    {
        this.ResetToDefaultState();
    }

    public void PrepareMeasureEntryPreview()
    {
        if (_latestSnapshot != null)
        {
            int nextRoundIndex = Math.Max(1, Math.Min(_rounds.Count, _latestSnapshot.CompletedRoundCount + 1));
            StatusPillText = string.Empty;
            StatusMessage = L("SensitivityMatch.Status.MeasureReady.Message", nextRoundIndex);
            HintText = L("SensitivityMatch.Status.MeasureReady.Hint");
            RefreshMeasurePresentation();
        }
    }

    private void RequestStartSetup()
    {
        if (CanStartSetup())
        {
            CurrentStep = SensitivityMatchWizardStep.Setup;
            RefreshStatus();
        }
    }

    private bool CanStartSetup()
    {
        return true;
    }

    private void RequestBack()
    {
        if (CanGoBack())
        {
            switch (CurrentStep)
            {
                case SensitivityMatchWizardStep.Setup:
                    _captureService.ResetAll();
                    CurrentStep = SensitivityMatchWizardStep.Intro;
                    break;
                case SensitivityMatchWizardStep.Measure:
                    _captureService.ResetMeasurements();
                    CurrentStep = SensitivityMatchWizardStep.Setup;
                    break;
            }
            RefreshFromSnapshot(allowAutoStepAdvance: false);
        }
    }

    private bool CanGoBack()
    {
        return CurrentStep != SensitivityMatchWizardStep.Intro;
    }

    private void RequestContinueFromSetup()
    {
        if (CanContinueFromSetup())
        {
            CurrentStep = SensitivityMatchWizardStep.Measure;
            RefreshStatus();
            RaiseCanExecuteChanges();
        }
    }

    private bool CanContinueFromSetup()
    {
        if (CurrentStep == SensitivityMatchWizardStep.Setup && HasMinimumDeviceCount && !IsSourceBindingPending && !IsTargetBindingPending && _latestSnapshot != null && _latestSnapshot.HasSourceDevice && _latestSnapshot.HasTargetDevice && !_latestSnapshot.IsSourceDisconnected && !_latestSnapshot.IsTargetDisconnected)
        {
            return HasValidDpiInputs();
        }
        return false;
    }

    private void RequestBindSlot(object parameter)
    {
        SensitivityMatchBindingSlot? sensitivityMatchBindingSlot = ResolveBindingSlot(parameter);
        if (sensitivityMatchBindingSlot.HasValue)
        {
            if (IsSlotBound(sensitivityMatchBindingSlot.Value) && !IsSlotPending(sensitivityMatchBindingSlot.Value))
            {
                _captureService.UnbindSlot(sensitivityMatchBindingSlot.Value);
                RefreshFromSnapshot(allowAutoStepAdvance: false);
            }
            else if (!_captureService.BeginBinding(sensitivityMatchBindingSlot.Value))
            {
                RefreshFromSnapshot(allowAutoStepAdvance: false);
            }
            else
            {
                RefreshFromSnapshot(allowAutoStepAdvance: false);
            }
        }
    }

    private bool IsSlotBound(SensitivityMatchBindingSlot slot)
    {
        if (_latestSnapshot == null)
        {
            return false;
        }
        if (slot == SensitivityMatchBindingSlot.SourceMouse)
        {
            return _latestSnapshot.HasSourceDevice;
        }
        return _latestSnapshot.HasTargetDevice;
    }

    private bool IsSlotPending(SensitivityMatchBindingSlot slot)
    {
        if (slot == SensitivityMatchBindingSlot.SourceMouse)
        {
            return IsSourceBindingPending;
        }
        return IsTargetBindingPending;
    }

    private void RequestStartRound()
    {
        if (CanStartRound())
        {
            int sourceDpi = 0;
            int targetCurrentDpi = 0;
            if (!TryParseDpi(SourceDpiText, ref sourceDpi) || !TryParseDpi(TargetCurrentDpiText, ref targetCurrentDpi))
            {
                RefreshStatus();
            }
            else if (!_captureService.StartRound(sourceDpi, targetCurrentDpi))
            {
                RefreshFromSnapshot(allowAutoStepAdvance: true);
            }
            else
            {
                RefreshFromSnapshot(allowAutoStepAdvance: true);
            }
        }
    }

    private bool CanStartRound()
    {
        if (CurrentStep == SensitivityMatchWizardStep.Measure && HasMinimumDeviceCount && !IsSourceBindingPending && !IsTargetBindingPending && _latestSnapshot != null && !_latestSnapshot.HasActiveRound && _latestSnapshot.CompletedRoundCount < 3 && _latestSnapshot.HasSourceDevice && _latestSnapshot.HasTargetDevice && !_latestSnapshot.IsSourceDisconnected && !_latestSnapshot.IsTargetDisconnected && !_latestSnapshot.ResultsExpired)
        {
            return HasValidDpiInputs();
        }
        return false;
    }

    bool ICaptureKeyboardShortcutHandler.TryHandleCaptureKeyboardShortcut(CaptureKeyboardShortcut shortcut)
    {
        if (shortcut != CaptureKeyboardShortcut.StartOrPause || !CanStartRound())
        {
            return false;
        }

        RequestStartRound();
        return true;
    }

    private void RequestCopyRecommendedDpi()
    {
        if (CanCopyRecommendedDpi() && _latestSnapshot != null && _latestSnapshot.FinalRecommendedTargetDpi.HasValue)
        {
            Clipboard.SetText(_latestSnapshot.FinalRecommendedTargetDpi.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private bool CanCopyRecommendedDpi()
    {
        if (HasFinalRecommendation && _latestSnapshot != null)
        {
            return !_latestSnapshot.ResultsExpired;
        }
        return false;
    }

    private void RequestRemeasure()
    {
        if (CanRemeasure())
        {
            _captureService.ResetMeasurements();
            CurrentStep = SensitivityMatchWizardStep.Measure;
            RefreshFromSnapshot(allowAutoStepAdvance: false);
        }
    }

    private bool CanRemeasure()
    {
        if (_latestSnapshot != null && _latestSnapshot.HasSourceDevice && _latestSnapshot.HasTargetDevice && !_latestSnapshot.IsSourceDisconnected && !_latestSnapshot.IsTargetDisconnected)
        {
            return HasValidDpiInputs();
        }
        return false;
    }

    private void RequestRebind()
    {
        _captureService.ResetAll();
        CurrentStep = SensitivityMatchWizardStep.Setup;
        RefreshFromSnapshot(allowAutoStepAdvance: false);
    }

    private void OnDpiInputsChanged()
    {
        _captureService.ResetMeasurements();
        RefreshFromSnapshot(allowAutoStepAdvance: false);
    }

    private void OnUiTimerTick(object sender, EventArgs e)
    {
        RefreshFromSnapshot(allowAutoStepAdvance: true);
    }

    private void OnDevicesChanged(object sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            RefreshFromSnapshot(allowAutoStepAdvance: true);
        });
    }

    private void OnLanguageChanged(object sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            RefreshFromSnapshot(allowAutoStepAdvance: false);
        });
    }

    private void RefreshFromSnapshot(bool allowAutoStepAdvance)
    {
        _latestSnapshot = _captureService.CaptureSnapshot();
        HasMinimumDeviceCount = _latestSnapshot != null && _latestSnapshot.HasMinimumDeviceCount;
        HasFinalRecommendation = _latestSnapshot != null && _latestSnapshot.HasFinalRecommendation;
        IsSourceBindingPending = _latestSnapshot != null && _latestSnapshot.PendingBindingSlot.HasValue && _latestSnapshot.PendingBindingSlot.Value == SensitivityMatchBindingSlot.SourceMouse;
        IsTargetBindingPending = _latestSnapshot != null && _latestSnapshot.PendingBindingSlot.HasValue && _latestSnapshot.PendingBindingSlot.Value == SensitivityMatchBindingSlot.TargetMouse;
        RefreshDeviceBindingText();
        RefreshProgressText();
        RefreshRoundCards();
        RefreshResultText();
        RefreshStatus();
        RefreshMeasurePresentation();
        SyncUiTimerState();
        RaiseCanExecuteChanges();
    }

    private void RefreshDeviceBindingText()
    {
        AvailableDeviceCountText = L("SensitivityMatch.Setup.DeviceCount", (_latestSnapshot != null) ? _latestSnapshot.AvailableDeviceCount : 0);
        UpdateBindingCard(_latestSnapshot?.SourceDevice, _latestSnapshot != null && _latestSnapshot.IsSourceDisconnected, IsSourceBindingPending, SensitivityMatchBindingSlot.SourceMouse);
        UpdateBindingCard(_latestSnapshot?.TargetDevice, _latestSnapshot != null && _latestSnapshot.IsTargetDisconnected, IsTargetBindingPending, SensitivityMatchBindingSlot.TargetMouse);
    }

    private void UpdateBindingCard(RawMouseDeviceInfo device, bool isDisconnected, bool isPending, SensitivityMatchBindingSlot slot)
    {
        string emptyTitleKey = slot == SensitivityMatchBindingSlot.SourceMouse ? "SensitivityMatch.Binding.Source.Empty" : "SensitivityMatch.Binding.Target.Empty";
        string pendingStateKey = slot == SensitivityMatchBindingSlot.SourceMouse ? "SensitivityMatch.Binding.Source.Waiting" : "SensitivityMatch.Binding.Target.Waiting";
        string boundStateKey = slot == SensitivityMatchBindingSlot.SourceMouse ? "SensitivityMatch.Binding.Source.Bound" : "SensitivityMatch.Binding.Target.Bound";
        string disconnectedStateKey = slot == SensitivityMatchBindingSlot.SourceMouse ? "SensitivityMatch.Binding.Source.Disconnected" : "SensitivityMatch.Binding.Target.Disconnected";
        string slotTitleKey = slot == SensitivityMatchBindingSlot.SourceMouse ? "SensitivityMatch.Binding.Source.Title" : "SensitivityMatch.Binding.Target.Title";
        bool isUnbound = device == null;
        string deviceTitle = isUnbound ? L(emptyTitleKey) : device.SelectionDisplayName;
        string deviceMetaText = isUnbound ? string.Empty : BuildDeviceMeta(device);
        string bindingStateText = isPending ? L(pendingStateKey) : (!isUnbound ? (isDisconnected ? L(disconnectedStateKey) : L(boundStateKey)) : string.Empty);
        string bindButtonText = isUnbound ? L("SensitivityMatch.Button.ClickBindMouse") : L("SensitivityMatch.Button.CancelBindMouse");
        if (slot == SensitivityMatchBindingSlot.SourceMouse)
        {
            SourceDeviceTitle = deviceTitle;
            SourceDeviceMetaText = deviceMetaText;
            SourceBindingStateText = bindingStateText;
            SourceBindButtonText = bindButtonText;
            SourceInfoBand.LabelText = L(slotTitleKey);
            SourceInfoBand.DeviceTitle = deviceTitle;
            SourceInfoBand.BindingStateText = bindingStateText;
            SourceInfoBand.MetaText = deviceMetaText;
        }
        else
        {
            TargetDeviceTitle = deviceTitle;
            TargetDeviceMetaText = deviceMetaText;
            TargetBindingStateText = bindingStateText;
            TargetBindButtonText = bindButtonText;
            TargetInfoBand.LabelText = L(slotTitleKey);
            TargetInfoBand.DeviceTitle = deviceTitle;
            TargetInfoBand.BindingStateText = bindingStateText;
            TargetInfoBand.MetaText = deviceMetaText;
        }
    }

    private void RefreshProgressText()
    {
        SensitivityMatchCurrentRoundState currentRoundState = _latestSnapshot?.CurrentRound;
        bool shouldShowPendingProgress = _latestSnapshot == null || _latestSnapshot.ResultsExpired || _latestSnapshot.IsSourceDisconnected || _latestSnapshot.IsTargetDisconnected || currentRoundState == null;
        if (shouldShowPendingProgress)
        {
            OverallProgressValue = 0.0;
            SourceProgressValue = 0.0;
            TargetProgressValue = 0.0;
            OverallProgressText = L("SensitivityMatch.Measure.Progress.Pending");
            SourceProgressText = L("SensitivityMatch.Measure.SourceProgress.Pending");
            TargetProgressText = L("SensitivityMatch.Measure.TargetProgress.Pending");
            StartRoundButtonText = ResolveStartRoundButtonText();
            return;
        }

        OverallProgressValue = currentRoundState.OverallProgress * 100.0;
        SourceProgressValue = currentRoundState.SourceProgress * 100.0;
        TargetProgressValue = currentRoundState.TargetProgress * 100.0;
        OverallProgressText = L("SensitivityMatch.Measure.Progress.Value", (int)Math.Round(OverallProgressValue));
        SourceProgressText = L("SensitivityMatch.Measure.SourceProgress.Value", (int)Math.Round(SourceProgressValue));
        TargetProgressText = L("SensitivityMatch.Measure.TargetProgress.Value", (int)Math.Round(TargetProgressValue));
        StartRoundButtonText = ResolveStartRoundButtonText();
    }

    private void RefreshRoundCards()
    {
        bool resultsUnavailable = _latestSnapshot != null && (_latestSnapshot.ResultsExpired || _latestSnapshot.IsSourceDisconnected || _latestSnapshot.IsTargetDisconnected);
        for (int currentRoundIndex = 1; currentRoundIndex <= _rounds.Count; currentRoundIndex++)
        {
            SensitivityMatchRoundCardViewModel roundCard = _rounds[currentRoundIndex - 1];
            roundCard.Title = L("SensitivityMatch.Measure.RoundTitle", currentRoundIndex);
            roundCard.IsCompleted = false;
            roundCard.IsCurrent = false;
            roundCard.IsFailed = false;
            roundCard.ShowProgressTrack = false;
            roundCard.SourceTrackProgressValue = 0.0;
            roundCard.TargetTrackProgressValue = 0.0;
            roundCard.TrackProgressValue = 0.0;
            roundCard.TrackCaptionText = string.Empty;
            roundCard.ValueText = "--";
            roundCard.DetailText = L("SensitivityMatch.Measure.Round.Pending");
            roundCard.StatusText = L("SensitivityMatch.Measure.Round.Status.Pending");
            SensitivityMatchRoundResult roundResult = _latestSnapshot?.CompletedRounds.FirstOrDefault(round => round.RoundIndex == currentRoundIndex);
            if (roundResult != null)
            {
                roundCard.IsCompleted = true;
                roundCard.StatusText = L("SensitivityMatch.Measure.Round.Status.Completed");
                roundCard.ValueText = FormatScale(roundResult.Scale);
                roundCard.DetailText = L("SensitivityMatch.Measure.Round.Result", roundResult.RecommendedTargetDpi);
            }
            else if (_latestSnapshot != null && _latestSnapshot.HasActiveRound && _latestSnapshot.CurrentRound.RoundIndex == currentRoundIndex)
            {
                roundCard.IsCurrent = true;
                roundCard.StatusText = _latestSnapshot.CurrentRound.Stage == SensitivityMatchRoundStage.Stabilizing ? L("SensitivityMatch.Measure.Round.Status.Finishing") : L("SensitivityMatch.Measure.Round.Status.Active");
                roundCard.ValueText = L("SensitivityMatch.Measure.Round.Progress", (int)Math.Round(_latestSnapshot.CurrentRound.OverallProgress * 100.0));
                roundCard.DetailText = L("SensitivityMatch.Measure.Round.ProgressDetail", (int)Math.Round(_latestSnapshot.CurrentRound.SourceProgress * 100.0), (int)Math.Round(_latestSnapshot.CurrentRound.TargetProgress * 100.0));
                roundCard.ShowProgressTrack = !resultsUnavailable;
                roundCard.SourceTrackProgressValue = _latestSnapshot.CurrentRound.SourceProgress * 100.0;
                roundCard.TargetTrackProgressValue = _latestSnapshot.CurrentRound.TargetProgress * 100.0;
                roundCard.TrackProgressValue = _latestSnapshot.CurrentRound.OverallProgress * 100.0;
                roundCard.TrackCaptionText = roundCard.DetailText;
            }
            else if (_latestSnapshot != null && _latestSnapshot.LastRoundFailureReason != SensitivityMatchRoundFailureReason.None && _latestSnapshot.LastRoundFailureIndex == currentRoundIndex)
            {
                roundCard.IsFailed = true;
                roundCard.StatusText = L("SensitivityMatch.Measure.Round.Status.Failed");
                roundCard.ValueText = L("SensitivityMatch.Measure.Round.Failed");
                roundCard.DetailText = ResolveRoundFailureText(_latestSnapshot.LastRoundFailureReason);
            }
            else if (_latestSnapshot != null && !resultsUnavailable && currentRoundIndex == _latestSnapshot.CompletedRoundCount + 1)
            {
                roundCard.StatusText = L("SensitivityMatch.Measure.Round.Status.Ready");
                roundCard.DetailText = L("SensitivityMatch.Measure.Round.Ready");
                roundCard.ShowProgressTrack = true;
                roundCard.TrackCaptionText = roundCard.DetailText;
            }
        }
    }

    private void RefreshResultText()
    {
        if (_latestSnapshot == null || !_latestSnapshot.HasFinalRecommendation)
        {
            RecommendedTargetDpiText = "--";
            ScaleText = "--";
            ConsistencyText = "--";
        }
        else
        {
            RecommendedTargetDpiText = _latestSnapshot.FinalRecommendedTargetDpi.Value.ToString(CultureInfo.InvariantCulture);
            ScaleText = FormatScale(_latestSnapshot.FinalScale.Value);
            ConsistencyText = ResolveConsistencyText(_latestSnapshot.ConsistencyPercent, _latestSnapshot.ConsistencyLevel);
        }
    }

    private void RefreshStatus()
    {
        if (IsIntroStep)
        {
            StatusPillText = L("SensitivityMatch.Status.Intro.Pill");
            StatusMessage = L("SensitivityMatch.Status.Intro.Message");
            HintText = L("SensitivityMatch.Status.Intro.Hint");
        }
        else if (IsSetupStep)
        {
            ApplySetupStatus();
        }
        else if (IsMeasureStep)
        {
            ApplyMeasureStatus();
        }
    }

    private void ApplySetupStatus()
    {
        if (_latestSnapshot == null || !_latestSnapshot.HasMinimumDeviceCount)
        {
            StatusPillText = L("SensitivityMatch.Status.NeedDevices.Pill");
            StatusMessage = L("SensitivityMatch.Status.NeedDevices.Message");
            HintText = L("SensitivityMatch.Status.NeedDevices.Hint");
        }
        else if (IsSourceBindingPending)
        {
            StatusPillText = L("SensitivityMatch.Status.Binding.Pill");
            StatusMessage = L("SensitivityMatch.Status.Binding.SourceMessage");
            HintText = L("SensitivityMatch.Status.Binding.Hint");
        }
        else if (IsTargetBindingPending)
        {
            StatusPillText = L("SensitivityMatch.Status.Binding.Pill");
            StatusMessage = L("SensitivityMatch.Status.Binding.TargetMessage");
            HintText = L("SensitivityMatch.Status.Binding.Hint");
        }
        else if (_latestSnapshot.LastBindingIssue == SensitivityMatchBindingIssue.TimedOut)
        {
            StatusPillText = L("SensitivityMatch.Status.BindingTimeout.Pill");
            StatusMessage = L("SensitivityMatch.Status.BindingTimeout.Message");
            HintText = L("SensitivityMatch.Status.BindingTimeout.Hint");
        }
        else if (_latestSnapshot.LastBindingIssue == SensitivityMatchBindingIssue.ConflictWithOtherSlot)
        {
            StatusPillText = L("SensitivityMatch.Status.BindingConflict.Pill");
            StatusMessage = L("SensitivityMatch.Status.BindingConflict.Message");
            HintText = L("SensitivityMatch.Status.BindingConflict.Hint");
        }
        else if (!HasValidDpiInputs())
        {
            StatusPillText = L("SensitivityMatch.Status.NeedDpi.Pill");
            StatusMessage = L("SensitivityMatch.Status.NeedDpi.Message");
            HintText = L("SensitivityMatch.Status.NeedDpi.Hint", 50, 50000);
        }
        else if (!_latestSnapshot.HasSourceDevice || !_latestSnapshot.HasTargetDevice)
        {
            StatusPillText = L("SensitivityMatch.Status.NeedBinding.Pill");
            StatusMessage = L("SensitivityMatch.Status.NeedBinding.Message");
            HintText = L("SensitivityMatch.Status.NeedBinding.Hint");
        }
        else if (_latestSnapshot.IsSourceDisconnected || _latestSnapshot.IsTargetDisconnected)
        {
            StatusPillText = L("SensitivityMatch.Status.BindingDisconnected.Pill");
            StatusMessage = L("SensitivityMatch.Status.BindingDisconnected.Message");
            HintText = L("SensitivityMatch.Status.BindingDisconnected.Hint");
        }
        else
        {
            StatusPillText = string.Empty;
            StatusMessage = L("SensitivityMatch.Status.SetupReady.Message");
            HintText = L("SensitivityMatch.Status.SetupReady.Hint");
        }
    }

    private void ApplyMeasureStatus()
    {
        if (_latestSnapshot == null)
        {
            return;
        }
        if (_latestSnapshot.HasFinalRecommendation && !_latestSnapshot.HasActiveRound)
        {
            ApplyResultStatus();
        }
        else if (_latestSnapshot.ResultsExpired || _latestSnapshot.IsSourceDisconnected || _latestSnapshot.IsTargetDisconnected)
        {
            StatusPillText = L("SensitivityMatch.Status.MeasureExpired.Pill");
            StatusMessage = L("SensitivityMatch.Status.MeasureExpired.Message");
            HintText = L("SensitivityMatch.Status.MeasureExpired.Hint");
        }
        else if (_latestSnapshot.HasActiveRound)
        {
            if (_latestSnapshot.CurrentRound.Stage == SensitivityMatchRoundStage.Stabilizing)
            {
                StatusPillText = L("SensitivityMatch.Status.Stabilizing.Pill");
                StatusMessage = L("SensitivityMatch.Status.Stabilizing.Message", _latestSnapshot.CurrentRound.RoundIndex);
                HintText = L("SensitivityMatch.Status.Stabilizing.Hint");
            }
            else
            {
                StatusPillText = L("SensitivityMatch.Status.Measuring.Pill");
                StatusMessage = L("SensitivityMatch.Status.Measuring.Message", _latestSnapshot.CurrentRound.RoundIndex);
                HintText = L("SensitivityMatch.Status.Measuring.Hint");
            }
        }
        else if (_latestSnapshot.LastRoundFailureReason != SensitivityMatchRoundFailureReason.None)
        {
            StatusPillText = L("SensitivityMatch.Status.RoundFailed.Pill");
            StatusMessage = ResolveRoundFailureText(_latestSnapshot.LastRoundFailureReason);
            HintText = L("SensitivityMatch.Status.RoundFailed.Hint");
        }
        else
        {
            StatusPillText = L("SensitivityMatch.Status.MeasureReady.Pill");
            StatusMessage = L("SensitivityMatch.Status.MeasureReady.Message", _latestSnapshot.CompletedRoundCount + 1);
            HintText = L("SensitivityMatch.Status.MeasureReady.Hint");
        }
    }

    private void ApplyResultStatus()
    {
        if (_latestSnapshot == null || !_latestSnapshot.HasFinalRecommendation)
        {
            StatusPillText = L("SensitivityMatch.Status.ResultPending.Pill");
            StatusMessage = L("SensitivityMatch.Status.ResultPending.Message");
            HintText = L("SensitivityMatch.Status.ResultPending.Hint");
        }
        else if (_latestSnapshot.ResultsExpired)
        {
            StatusPillText = L("SensitivityMatch.Status.ResultExpired.Pill");
            StatusMessage = L("SensitivityMatch.Status.ResultExpired.Message");
            HintText = L("SensitivityMatch.Status.ResultExpired.Hint");
        }
        else
        {
            StatusPillText = L("SensitivityMatch.Status.ResultReady.Pill");
            StatusMessage = L("SensitivityMatch.Status.ResultReady.Message");
            HintText = ResolveConsistencyHint(_latestSnapshot.ConsistencyLevel);
        }
    }

    private void RefreshMeasurePresentation()
    {
        SensitivityMatchCurrentRoundState currentRoundState = _latestSnapshot?.CurrentRound;
        bool isExpiredOrDisconnected = _latestSnapshot != null && (_latestSnapshot.ResultsExpired || _latestSnapshot.IsSourceDisconnected || _latestSnapshot.IsTargetDisconnected);
        bool hasActiveRound = currentRoundState != null && !isExpiredOrDisconnected;
        bool hasRoundFailure = !hasActiveRound && !isExpiredOrDisconnected && _latestSnapshot != null && _latestSnapshot.LastRoundFailureReason != SensitivityMatchRoundFailureReason.None;
        IsMeasureExpiredState = isExpiredOrDisconnected;
        IsMeasureActiveState = hasActiveRound;
        IsMeasureFailureState = hasRoundFailure;
        if (hasActiveRound)
        {
            MeasureHeroPrimaryText = currentRoundState.RoundIndex.ToString("00", CultureInfo.InvariantCulture);
            MeasureHeroLabelText = string.Empty;
            MeasureHeroSupportText = currentRoundState.Stage == SensitivityMatchRoundStage.Stabilizing ? StatusMessage : string.Empty;
            MeasureSourceValueText = FormatPercentageText(currentRoundState.SourceProgress * 100.0);
            MeasureTargetValueText = FormatPercentageText(currentRoundState.TargetProgress * 100.0);
            return;
        }

        MeasureSourceValueText = "--";
        MeasureTargetValueText = "--";
        if (isExpiredOrDisconnected)
        {
            MeasureHeroPrimaryText = "--";
            MeasureHeroLabelText = string.Empty;
            MeasureHeroSupportText = StatusMessage;
            return;
        }

        if (hasRoundFailure)
        {
            int failedRoundIndex = Math.Max(1, _latestSnapshot.LastRoundFailureIndex > 0 ? _latestSnapshot.LastRoundFailureIndex : _latestSnapshot.CompletedRoundCount + 1);
            MeasureHeroPrimaryText = failedRoundIndex.ToString("00", CultureInfo.InvariantCulture);
            MeasureHeroLabelText = string.Empty;
            MeasureHeroSupportText = StatusMessage;
            return;
        }

        int nextRoundIndex = 1;
        if (_latestSnapshot != null)
        {
            nextRoundIndex = Math.Max(1, Math.Min(_rounds.Count, _latestSnapshot.CompletedRoundCount + 1));
        }
        MeasureHeroPrimaryText = nextRoundIndex.ToString("00", CultureInfo.InvariantCulture);
        MeasureHeroLabelText = string.Empty;
        MeasureHeroSupportText = StatusMessage;
    }

    private string ResolveStartRoundButtonText()
    {
        if (_latestSnapshot != null && _latestSnapshot.HasFinalRecommendation)
        {
            return L("SensitivityMatch.Button.Remeasure");
        }
        if (_latestSnapshot != null && (_latestSnapshot.ResultsExpired || _latestSnapshot.IsSourceDisconnected || _latestSnapshot.IsTargetDisconnected))
        {
            int nextRoundIndex = Math.Max(1, Math.Min(_rounds.Count, _latestSnapshot.CompletedRoundCount + 1));
            return L("SensitivityMatch.Button.StartRound", nextRoundIndex);
        }
        if (_latestSnapshot != null && _latestSnapshot.HasActiveRound)
        {
            if (_latestSnapshot.CurrentRound.Stage == SensitivityMatchRoundStage.Stabilizing)
            {
                return L("SensitivityMatch.Button.Stabilizing");
            }
            return L("SensitivityMatch.Button.Measuring");
        }
        if (_latestSnapshot != null && _latestSnapshot.LastRoundFailureReason != SensitivityMatchRoundFailureReason.None)
        {
            return L("SensitivityMatch.Button.RetryRound");
        }

        int startRoundIndex = _latestSnapshot == null ? 1 : _latestSnapshot.CompletedRoundCount + 1;
        return L("SensitivityMatch.Button.StartRound", startRoundIndex);
    }

    private SensitivityMatchBindingSlot? ResolveBindingSlot(object parameter)
    {
        if (parameter == null)
        {
            return null;
        }

        if (parameter is SensitivityMatchBindingSlot bindingSlot)
        {
            return bindingSlot;
        }

        return Enum.TryParse(parameter.ToString(), ignoreCase: true, out SensitivityMatchBindingSlot parsedSlot)
            ? parsedSlot
            : null;
    }

    private bool HasValidDpiInputs()
    {
        int sourceDpi = 0;
        int targetCurrentDpi = 0;
        if (TryParseDpi(SourceDpiText, ref sourceDpi))
        {
            return TryParseDpi(TargetCurrentDpiText, ref targetCurrentDpi);
        }
        return false;
    }

    private static bool TryParseDpi(string inputText, ref int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(inputText))
        {
            return false;
        }
        if (!int.TryParse(inputText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }
        return value >= 50 && value <= 50000;
    }

    private string BuildDeviceMeta(RawMouseDeviceInfo device)
    {
        if (device == null)
        {
            return L("SensitivityMatch.Binding.EmptyMeta");
        }
        List<string> deviceMetaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(device.VendorProductLabel))
        {
            deviceMetaParts.Add(device.VendorProductLabel);
        }
        if (device.ButtonCount > 0)
        {
            deviceMetaParts.Add(L("Device.Detail.Buttons", device.ButtonCount));
        }
        if (device.IsVirtual)
        {
            deviceMetaParts.Add(L("Device.Detail.Virtual"));
        }
        else
        {
            deviceMetaParts.Add(RawMouseEndpointKindLocalization.Resolve(device.EndpointKind, key => L(key)));
        }
        if (!string.IsNullOrWhiteSpace(device.EndpointToken))
        {
            deviceMetaParts.Add(device.EndpointToken);
        }
        if (deviceMetaParts.Count == 0)
        {
            return L("SensitivityMatch.Binding.EmptyMeta");
        }
        return string.Join("  /  ", deviceMetaParts);
    }

    private string ResolveRoundFailureText(SensitivityMatchRoundFailureReason reason)
    {
        return reason switch
        {
            SensitivityMatchRoundFailureReason.Timeout => L("SensitivityMatch.Measure.Fail.Timeout"),
            SensitivityMatchRoundFailureReason.TooFast => L("SensitivityMatch.Measure.Fail.TooFast"),
            SensitivityMatchRoundFailureReason.InsufficientPackets => L("SensitivityMatch.Measure.Fail.InsufficientPackets"),
            SensitivityMatchRoundFailureReason.ExcessiveCurvature => L("SensitivityMatch.Measure.Fail.Curved"),
            SensitivityMatchRoundFailureReason.DirectionMismatch => L("SensitivityMatch.Measure.Fail.DirectionMismatch"),
            SensitivityMatchRoundFailureReason.PathShapeMismatch => L("SensitivityMatch.Measure.Fail.PathShapeMismatch"),
            SensitivityMatchRoundFailureReason.Unsynchronized => L("SensitivityMatch.Measure.Fail.Unsynchronized"),
            _ => L("SensitivityMatch.Measure.Fail.Unknown"),
        };
    }

    private string ResolveConsistencyBadge(SensitivityMatchConsistencyLevel level)
    {
        return level switch
        {
            SensitivityMatchConsistencyLevel.Excellent => L("SensitivityMatch.Result.Consistency.Excellent"),
            SensitivityMatchConsistencyLevel.Good => L("SensitivityMatch.Result.Consistency.Good"),
            SensitivityMatchConsistencyLevel.Fair => L("SensitivityMatch.Result.Consistency.Fair"),
            SensitivityMatchConsistencyLevel.Poor => L("SensitivityMatch.Result.Consistency.Poor"),
            _ => L("SensitivityMatch.Result.Pending"),
        };
    }

    private string ResolveConsistencyHint(SensitivityMatchConsistencyLevel level)
    {
        return level switch
        {
            SensitivityMatchConsistencyLevel.Excellent => L("SensitivityMatch.Result.Hint.Excellent"),
            SensitivityMatchConsistencyLevel.Good => L("SensitivityMatch.Result.Hint.Good"),
            SensitivityMatchConsistencyLevel.Fair => L("SensitivityMatch.Result.Hint.Fair"),
            SensitivityMatchConsistencyLevel.Poor => L("SensitivityMatch.Result.Hint.Poor"),
            _ => L("SensitivityMatch.Status.ResultPending.Hint"),
        };
    }

    private static string FormatScale(double value)
    {
        return value.ToString("0.000x", CultureInfo.InvariantCulture);
    }

    private static string FormatPercentageText(double value)
    {
        return ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture) + "%";
    }

    private string ResolveConsistencyText(double? consistencyPercent, SensitivityMatchConsistencyLevel consistencyLevel)
    {
        if (!consistencyPercent.HasValue)
        {
            return "--";
        }
        return string.Format(CultureInfo.InvariantCulture, "{0}  /  {1:0.00}%", ResolveConsistencyBadge(consistencyLevel), consistencyPercent.Value);
    }

    private string L(string key, params object[] args)
    {
        return _localization.GetString(key, args);
    }

    private void RaiseCanExecuteChanges()
    {
        _startSetupCommand.RaiseCanExecuteChanged();
        _backCommand.RaiseCanExecuteChanged();
        _continueFromSetupCommand.RaiseCanExecuteChanged();
        _startRoundCommand.RaiseCanExecuteChanged();
        _copyRecommendedDpiCommand.RaiseCanExecuteChanged();
        _remeasureCommand.RaiseCanExecuteChanged();
        RaisePropertyChanged("IsContinueFromSetupEnabled");
    }

    private void SyncUiTimerState()
    {
        if (!_isPageActive)
        {
            _uiTimer.Stop();
        }
        else if (_latestSnapshot != null && (_latestSnapshot.HasPendingBinding || _latestSnapshot.HasActiveRound))
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

    private static string NormalizeDpiText(string inputText)
    {
        if (string.IsNullOrEmpty(inputText))
        {
            return string.Empty;
        }
        StringBuilder normalizedText = new StringBuilder(inputText.Length);
        foreach (char character in inputText)
        {
            if (char.IsDigit(character))
            {
                normalizedText.Append(character);
            }
        }
        return normalizedText.ToString();
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

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _uiTimer.Stop();
            _localization.LanguageChanged -= OnLanguageChanged;
            _uiTimer.Tick -= OnUiTimerTick;
            _captureService.DevicesChanged -= OnDevicesChanged;
            _captureService.Dispose();
        }
    }

    void IDisposable.Dispose()
    {
        this.Dispose();
    }

}
