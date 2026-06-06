using ClickSyncMouseTester.Infrastructure;
using ClickSyncMouseTester.Models;
using ClickSyncMouseTester.Navigation;
using ClickSyncMouseTester.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows.Threading;

namespace ClickSyncMouseTester.ViewModels.Pages;

[SupportedOSPlatform("windows")]
public class KeyDetectionPageViewModel : BindableBase, IDisposable, INavigationResettablePageViewModel
{
    private const double DefaultDoubleClickThresholdMs = 80.0;

    private const double MinDoubleClickThresholdMs = 1.0;

    private const double MaxDoubleClickThresholdMs = 1000.0;

    private const double ScrollPulseDurationMilliseconds = 150.0;

    private readonly Dispatcher _dispatcher;

    private readonly IRawInputBroker _rawInputBroker;

    private readonly LocalizationManager _localization;

    private RawInputKeyDetectionService _inputService;

    private readonly ObservableCollection<KeyDetectionButtonCardViewModel> _buttonCards;

    private readonly Dictionary<MouseButtonKind, KeyDetectionButtonCardViewModel> _cardByButton;

    private readonly InputTimingStatistics _customKeyStats;

    private readonly DelegateCommand _resetStatisticsCommand;

    private readonly DelegateCommand _toggleKeyPickModeCommand;

    private readonly DelegateCommand _resetCustomKeyCommand;

    private readonly DispatcherTimer _scrollUpPulseTimer;

    private readonly DispatcherTimer _scrollDownPulseTimer;

    private string _mouseDoubleClickThresholdText;

    private double _mouseDoubleClickThresholdMs;

    private string _keyDoubleClickThresholdText;

    private double _keyDoubleClickThresholdMs;

    private int _scrollUpCount;

    private int _scrollDownCount;

    private string _scrollUpCountText;

    private string _scrollDownCountText;

    private bool _isScrollUpPulseActive;

    private bool _isScrollDownPulseActive;

    private string _customKeyStatusText;

    private string _customKeySelectionText;

    private string _customKeyStatusValueText;

    private string _customKeySelectionValueText;

    private string _customKeyPickButtonText;

    private string _customKeyDownCountText;

    private string _customKeyUpCountText;

    private string _customKeyDoubleClickCountText;

    private string _customKeyCurrentDownDownText;

    private string _customKeyMinimumDownDownText;

    private string _customKeyAverageDownDownText;

    private string _customKeyCurrentDownUpText;

    private string _customKeyMinimumDownUpText;

    private string _customKeyAverageDownUpText;

    private bool _isCustomKeyDown;

    private bool _isPickingCustomKey;

    private bool _isPageActive;

    private bool _isWindowActive;

    private bool _isTextEntryActive;

    private KeyDetectionCustomKey _selectedCustomKey;

    private KeyDetectionCustomKey _pendingIgnoredCustomKeyRelease;

    private bool _disposed;

    public ObservableCollection<KeyDetectionButtonCardViewModel> ButtonCards => _buttonCards;

    public string MouseDoubleClickThresholdText
    {
        get
        {
            return _mouseDoubleClickThresholdText;
        }
        set
        {
            SetProperty(ref _mouseDoubleClickThresholdText, value, "MouseDoubleClickThresholdText");
        }
    }

    public string KeyDoubleClickThresholdText
    {
        get
        {
            return _keyDoubleClickThresholdText;
        }
        set
        {
            SetProperty(ref _keyDoubleClickThresholdText, value, "KeyDoubleClickThresholdText");
        }
    }

    public string ScrollUpCountText
    {
        get
        {
            return _scrollUpCountText;
        }
        private set
        {
            SetProperty(ref _scrollUpCountText, value, "ScrollUpCountText");
        }
    }

    public string ScrollDownCountText
    {
        get
        {
            return _scrollDownCountText;
        }
        private set
        {
            SetProperty(ref _scrollDownCountText, value, "ScrollDownCountText");
        }
    }

    public bool IsScrollUpPulseActive
    {
        get
        {
            return _isScrollUpPulseActive;
        }
        private set
        {
            SetProperty(ref _isScrollUpPulseActive, value, "IsScrollUpPulseActive");
        }
    }

    public bool IsScrollDownPulseActive
    {
        get
        {
            return _isScrollDownPulseActive;
        }
        private set
        {
            SetProperty(ref _isScrollDownPulseActive, value, "IsScrollDownPulseActive");
        }
    }

    public string CustomKeyStatusText
    {
        get
        {
            return _customKeyStatusText;
        }
        private set
        {
            SetProperty(ref _customKeyStatusText, value, "CustomKeyStatusText");
        }
    }

    public string CustomKeySelectionText
    {
        get
        {
            return _customKeySelectionText;
        }
        private set
        {
            SetProperty(ref _customKeySelectionText, value, "CustomKeySelectionText");
        }
    }

    public string CustomKeyStatusValueText
    {
        get
        {
            return _customKeyStatusValueText;
        }
        private set
        {
            SetProperty(ref _customKeyStatusValueText, value, "CustomKeyStatusValueText");
        }
    }

    public string CustomKeySelectionValueText
    {
        get
        {
            return _customKeySelectionValueText;
        }
        private set
        {
            SetProperty(ref _customKeySelectionValueText, value, "CustomKeySelectionValueText");
        }
    }

    public string CustomKeyPickButtonText
    {
        get
        {
            return _customKeyPickButtonText;
        }
        private set
        {
            SetProperty(ref _customKeyPickButtonText, value, "CustomKeyPickButtonText");
        }
    }

    public string CustomKeyDownCountText
    {
        get
        {
            return _customKeyDownCountText;
        }
        private set
        {
            SetProperty(ref _customKeyDownCountText, value, "CustomKeyDownCountText");
        }
    }

    public string CustomKeyUpCountText
    {
        get
        {
            return _customKeyUpCountText;
        }
        private set
        {
            SetProperty(ref _customKeyUpCountText, value, "CustomKeyUpCountText");
        }
    }

    public string CustomKeyDoubleClickCountText
    {
        get
        {
            return _customKeyDoubleClickCountText;
        }
        private set
        {
            SetProperty(ref _customKeyDoubleClickCountText, value, "CustomKeyDoubleClickCountText");
        }
    }

    public string CustomKeyCurrentDownDownText
    {
        get
        {
            return _customKeyCurrentDownDownText;
        }
        private set
        {
            SetProperty(ref _customKeyCurrentDownDownText, value, "CustomKeyCurrentDownDownText");
        }
    }

    public string CustomKeyMinimumDownDownText
    {
        get
        {
            return _customKeyMinimumDownDownText;
        }
        private set
        {
            SetProperty(ref _customKeyMinimumDownDownText, value, "CustomKeyMinimumDownDownText");
        }
    }

    public string CustomKeyAverageDownDownText
    {
        get
        {
            return _customKeyAverageDownDownText;
        }
        private set
        {
            SetProperty(ref _customKeyAverageDownDownText, value, "CustomKeyAverageDownDownText");
        }
    }

    public string CustomKeyCurrentDownUpText
    {
        get
        {
            return _customKeyCurrentDownUpText;
        }
        private set
        {
            SetProperty(ref _customKeyCurrentDownUpText, value, "CustomKeyCurrentDownUpText");
        }
    }

    public string CustomKeyMinimumDownUpText
    {
        get
        {
            return _customKeyMinimumDownUpText;
        }
        private set
        {
            SetProperty(ref _customKeyMinimumDownUpText, value, "CustomKeyMinimumDownUpText");
        }
    }

    public string CustomKeyAverageDownUpText
    {
        get
        {
            return _customKeyAverageDownUpText;
        }
        private set
        {
            SetProperty(ref _customKeyAverageDownUpText, value, "CustomKeyAverageDownUpText");
        }
    }

    public bool IsCustomKeyDown
    {
        get
        {
            return _isCustomKeyDown;
        }
        private set
        {
            SetProperty(ref _isCustomKeyDown, value, "IsCustomKeyDown");
        }
    }

    public DelegateCommand ResetStatisticsCommand => _resetStatisticsCommand;

    public DelegateCommand ToggleKeyPickModeCommand => _toggleKeyPickModeCommand;

    public DelegateCommand ResetCustomKeyCommand => _resetCustomKeyCommand;

    public KeyDetectionPageViewModel(IRawInputBroker rawInputBroker)
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
        _buttonCards = new ObservableCollection<KeyDetectionButtonCardViewModel>();
        _cardByButton = new Dictionary<MouseButtonKind, KeyDetectionButtonCardViewModel>();
        _customKeyStats = new InputTimingStatistics();
        _resetStatisticsCommand = new DelegateCommand(RequestResetStatistics);
        _toggleKeyPickModeCommand = new DelegateCommand(RequestToggleKeyPickMode);
        _resetCustomKeyCommand = new DelegateCommand(RequestResetCustomKey);
        _scrollUpPulseTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher);
        _scrollUpPulseTimer.Interval = TimeSpan.FromMilliseconds(150.0);
        _scrollDownPulseTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher);
        _scrollDownPulseTimer.Interval = TimeSpan.FromMilliseconds(150.0);
        _localization.LanguageChanged += OnLanguageChanged;
        _scrollUpPulseTimer.Tick += OnScrollUpPulseTimerTick;
        _scrollDownPulseTimer.Tick += OnScrollDownPulseTimerTick;
        _mouseDoubleClickThresholdMs = 80.0;
        _mouseDoubleClickThresholdText = FormatThreshold(_mouseDoubleClickThresholdMs);
        _keyDoubleClickThresholdMs = 80.0;
        _keyDoubleClickThresholdText = FormatThreshold(_keyDoubleClickThresholdMs);
        _isWindowActive = true;
        InitializeButtonCards();
        SyncScrollDisplay();
        SyncCustomKeyStats();
        RefreshLocalization();
    }

    public void SetPageActive(bool isActive)
    {
        if (_isPageActive != isActive)
        {
            _isPageActive = isActive;
            if (isActive)
            {
                EnsureInputService();
            }
            else
            {
                ResetTransientInputState();
            }
        }
    }

    public void ResetToDefaultState()
    {
        ReleaseInputService();
        _mouseDoubleClickThresholdMs = 80.0;
        MouseDoubleClickThresholdText = FormatThreshold(_mouseDoubleClickThresholdMs);
        _keyDoubleClickThresholdMs = 80.0;
        KeyDoubleClickThresholdText = FormatThreshold(_keyDoubleClickThresholdMs);
        foreach (KeyDetectionButtonCardViewModel buttonCard in _buttonCards)
        {
            buttonCard.ResetStatistics();
        }
        _scrollUpCount = 0;
        _scrollDownCount = 0;
        _scrollUpPulseTimer.Stop();
        _scrollDownPulseTimer.Stop();
        IsScrollUpPulseActive = false;
        IsScrollDownPulseActive = false;
        SyncScrollDisplay();
        _selectedCustomKey = null;
        _pendingIgnoredCustomKeyRelease = null;
        _isPickingCustomKey = false;
        _isPageActive = false;
        _isWindowActive = true;
        _isTextEntryActive = false;
        _customKeyStats.Reset();
        SyncCustomKeyStats();
        UpdatePickButtonText();
        UpdateCustomKeySelectionText();
        UpdateCustomKeyStatusIdle();
    }

    void INavigationResettablePageViewModel.ResetToDefaultState()
    {
        this.ResetToDefaultState();
    }

    public void SetWindowActive(bool isActive)
    {
        if (_isWindowActive != isActive)
        {
            _isWindowActive = isActive;
            if (!isActive)
            {
                ResetTransientInputState();
            }
        }
    }

    public void SetTextEntryActive(bool isActive)
    {
        _isTextEntryActive = isActive;
    }

    public void CommitMouseDoubleClickThresholdInput()
    {
        _mouseDoubleClickThresholdMs = NormalizeThreshold(MouseDoubleClickThresholdText);
        MouseDoubleClickThresholdText = FormatThreshold(_mouseDoubleClickThresholdMs);
    }

    public void CommitKeyDoubleClickThresholdInput()
    {
        _keyDoubleClickThresholdMs = NormalizeThreshold(KeyDoubleClickThresholdText);
        KeyDoubleClickThresholdText = FormatThreshold(_keyDoubleClickThresholdMs);
    }

    private void InitializeButtonCards()
    {
        AddCard(MouseButtonKind.LeftButton);
        AddCard(MouseButtonKind.MiddleButton);
        AddCard(MouseButtonKind.RightButton);
        AddCard(MouseButtonKind.ForwardButton);
        AddCard(MouseButtonKind.BackButton);
    }

    private void AddCard(MouseButtonKind buttonKind)
    {
        KeyDetectionButtonCardViewModel keyDetectionButtonCardViewModel = new KeyDetectionButtonCardViewModel(buttonKind);
        _buttonCards.Add(keyDetectionButtonCardViewModel);
        _cardByButton[buttonKind] = keyDetectionButtonCardViewModel;
    }

    private void OnMouseButtonInput(object sender, RawMouseButtonInputEventArgs e)
    {
        if (e != null && e.Input != null)
        {
            RunOnUiThread(() =>
            {
                HandleMouseButtonInput(e.Input);
            });
        }
    }

    private void HandleMouseButtonInput(RawMouseButtonInput input)
    {
        if (input != null && CanConsumePointerInput() && _cardByButton.ContainsKey(input.ButtonKind))
        {
            KeyDetectionButtonCardViewModel keyDetectionButtonCardViewModel = _cardByButton[input.ButtonKind];
            if (input.IsButtonDown)
            {
                keyDetectionButtonCardViewModel.RegisterDown(input.TimestampMs, _mouseDoubleClickThresholdMs);
            }
            else
            {
                keyDetectionButtonCardViewModel.RegisterUp(input.TimestampMs);
            }
        }
    }

    private void OnMouseWheelInput(object sender, RawMouseWheelInputEventArgs e)
    {
        if (e != null && e.Input != null)
        {
            RunOnUiThread(() =>
            {
                HandleMouseWheelInput(e.Input);
            });
        }
    }

    private void HandleMouseWheelInput(RawMouseWheelInput input)
    {
        if (input == null || !CanConsumePointerInput())
        {
            return;
        }
        if (input.Delta > 0)
        {
            _scrollUpCount++;
            SyncScrollDisplay();
            PulseScrollIndicator(isScrollUp: true);
        }
        else if (input.Delta < 0)
        {
            _scrollDownCount++;
            SyncScrollDisplay();
            PulseScrollIndicator(isScrollUp: false);
        }
    }

    private void OnKeyboardInput(object sender, RawKeyboardInputEventArgs e)
    {
        if (e != null && e.Input != null)
        {
            RunOnUiThread(() =>
            {
                HandleKeyboardInput(e.Input);
            });
        }
    }

    private void HandleKeyboardInput(RawKeyboardInput input)
    {
        if (input == null)
        {
            return;
        }
        if (_isPickingCustomKey)
        {
            HandlePickingKeyboardInput(input);
        }
        else
        {
            if (!CanConsumeKeyboardInput())
            {
                return;
            }
            if (_pendingIgnoredCustomKeyRelease != null && _pendingIgnoredCustomKeyRelease.Matches(input))
            {
                if (!input.IsKeyDown)
                {
                    _pendingIgnoredCustomKeyRelease = null;
                }
            }
            else if (_selectedCustomKey != null && _selectedCustomKey.Matches(input))
            {
                if (input.IsKeyDown)
                {
                    _customKeyStats.RegisterDown(input.TimestampMs, _keyDoubleClickThresholdMs);
                    SyncCustomKeyStats();
                    UpdateCustomKeyStatusDown();
                }
                else
                {
                    _customKeyStats.RegisterUp(input.TimestampMs);
                    SyncCustomKeyStats();
                    UpdateCustomKeyStatusUp();
                }
            }
        }
    }

    private void HandlePickingKeyboardInput(RawKeyboardInput input)
    {
        if (input == null)
        {
            return;
        }
        if (!input.IsKeyDown)
        {
            if (_pendingIgnoredCustomKeyRelease != null && _pendingIgnoredCustomKeyRelease.Matches(input))
            {
                _pendingIgnoredCustomKeyRelease = null;
            }
            return;
        }
        KeyDetectionCustomKey selectedCustomKey = (_pendingIgnoredCustomKeyRelease = KeyDetectionCustomKey.FromInput(input));
        if (input.VirtualKey == 27)
        {
            _isPickingCustomKey = false;
            UpdatePickButtonText();
            UpdateCustomKeyStatusPickCanceled();
            return;
        }
        bool customKeyChanged = _selectedCustomKey == null || !_selectedCustomKey.Matches(input);
        _selectedCustomKey = selectedCustomKey;
        _isPickingCustomKey = false;
        UpdateCustomKeySelectionText();
        if (customKeyChanged)
        {
            ResetCustomKeyStatisticsOnly();
        }
        else
        {
            UpdateCustomKeyStatusSelected();
        }
        UpdatePickButtonText();
        UpdateCustomKeyStatusSelected();
    }

    private bool CanConsumePointerInput()
    {
        if (_isPageActive)
        {
            return _isWindowActive;
        }
        return false;
    }

    private bool CanConsumeKeyboardInput()
    {
        if (_isPageActive && _isWindowActive)
        {
            return !_isTextEntryActive;
        }
        return false;
    }

    private void RequestResetStatistics()
    {
        foreach (KeyDetectionButtonCardViewModel buttonCard in _buttonCards)
        {
            buttonCard.ResetStatistics();
        }
        _scrollUpCount = 0;
        _scrollDownCount = 0;
        IsScrollUpPulseActive = false;
        IsScrollDownPulseActive = false;
        _scrollUpPulseTimer.Stop();
        _scrollDownPulseTimer.Stop();
        SyncScrollDisplay();
        ResetCustomKeyStatisticsOnly();
        UpdateCustomKeyStatusByCurrentState();
    }

    private void RequestToggleKeyPickMode()
    {
        _isPickingCustomKey = !_isPickingCustomKey;
        UpdatePickButtonText();
        if (_isPickingCustomKey)
        {
            UpdateCustomKeyStatusPicking();
        }
        else
        {
            UpdateCustomKeyStatusPickExited();
        }
    }

    private void RequestResetCustomKey()
    {
        _selectedCustomKey = null;
        _pendingIgnoredCustomKeyRelease = null;
        _isPickingCustomKey = false;
        UpdatePickButtonText();
        UpdateCustomKeySelectionText();
        ResetCustomKeyStatisticsOnly();
        UpdateCustomKeyStatusIdle();
    }

    private void ResetCustomKeyStatisticsOnly()
    {
        _customKeyStats.Reset();
        SyncCustomKeyStats();
    }

    private void SyncScrollDisplay()
    {
        ScrollUpCountText = KeyDetectionFormatting.FormatCount(_scrollUpCount);
        ScrollDownCountText = KeyDetectionFormatting.FormatCount(_scrollDownCount);
    }

    private void SyncCustomKeyStats()
    {
        CustomKeyDownCountText = KeyDetectionFormatting.FormatCount(_customKeyStats.DownCount);
        CustomKeyUpCountText = KeyDetectionFormatting.FormatCount(_customKeyStats.UpCount);
        CustomKeyDoubleClickCountText = KeyDetectionFormatting.FormatCount(_customKeyStats.DoubleClickCount);
        CustomKeyCurrentDownDownText = KeyDetectionFormatting.FormatMilliseconds(_customKeyStats.CurrentDownDownMs);
        CustomKeyMinimumDownDownText = KeyDetectionFormatting.FormatMilliseconds(_customKeyStats.MinimumDownDownMs);
        CustomKeyAverageDownDownText = KeyDetectionFormatting.FormatMilliseconds(_customKeyStats.AverageDownDownMs);
        CustomKeyCurrentDownUpText = KeyDetectionFormatting.FormatMilliseconds(_customKeyStats.CurrentDownUpMs);
        CustomKeyMinimumDownUpText = KeyDetectionFormatting.FormatMilliseconds(_customKeyStats.MinimumDownUpMs);
        CustomKeyAverageDownUpText = KeyDetectionFormatting.FormatMilliseconds(_customKeyStats.AverageDownUpMs);
        IsCustomKeyDown = _customKeyStats.IsPressed;
    }

    private void PulseScrollIndicator(bool isScrollUp)
    {
        if (isScrollUp)
        {
            _scrollUpPulseTimer.Stop();
            IsScrollUpPulseActive = true;
            _scrollUpPulseTimer.Start();
        }
        else
        {
            _scrollDownPulseTimer.Stop();
            IsScrollDownPulseActive = true;
            _scrollDownPulseTimer.Start();
        }
    }

    private void OnScrollUpPulseTimerTick(object sender, EventArgs e)
    {
        _scrollUpPulseTimer.Stop();
        IsScrollUpPulseActive = false;
    }

    private void OnScrollDownPulseTimerTick(object sender, EventArgs e)
    {
        _scrollDownPulseTimer.Stop();
        IsScrollDownPulseActive = false;
    }

    private void OnLanguageChanged(object sender, EventArgs e)
    {
        RunOnUiThread(RefreshLocalization);
    }

    private void RefreshLocalization()
    {
        foreach (KeyDetectionButtonCardViewModel buttonCard in _buttonCards)
        {
            buttonCard.RefreshLocalization(_localization);
        }
        UpdateCustomKeySelectionText();
        UpdatePickButtonText();
        UpdateCustomKeyStatusByCurrentState();
    }

    private void UpdateCustomKeySelectionText()
    {
        if (_selectedCustomKey == null)
        {
            CustomKeySelectionText = L("KeyDetection.CustomKey.Selection.Empty");
            CustomKeySelectionValueText = L("KeyDetection.CustomKey.Selection.Placeholder");
        }
        else
        {
            CustomKeySelectionText = L("KeyDetection.CustomKey.Selection.Value", _selectedCustomKey.DisplayName);
            CustomKeySelectionValueText = _selectedCustomKey.DisplayName;
        }
    }

    private void UpdatePickButtonText()
    {
        CustomKeyPickButtonText = (_isPickingCustomKey ? L("KeyDetection.CustomKey.Button.Exit") : L("KeyDetection.CustomKey.Button.Pick"));
    }

    private void UpdateCustomKeyStatusByCurrentState()
    {
        if (_isPickingCustomKey)
        {
            UpdateCustomKeyStatusPicking();
        }
        else if (_customKeyStats.IsPressed && _selectedCustomKey != null)
        {
            UpdateCustomKeyStatusDown();
        }
        else if (_selectedCustomKey != null)
        {
            UpdateCustomKeyStatusSelected();
        }
        else
        {
            UpdateCustomKeyStatusIdle();
        }
    }

    private void UpdateCustomKeyStatusIdle()
    {
        CustomKeyStatusText = L("KeyDetection.CustomKey.Status.Idle");
        CustomKeyStatusValueText = L("KeyDetection.CustomKey.StatusValue.Idle");
    }

    private void UpdateCustomKeyStatusPicking()
    {
        CustomKeyStatusText = L("KeyDetection.CustomKey.Status.Picking");
        CustomKeyStatusValueText = L("KeyDetection.CustomKey.StatusValue.Picking");
    }

    private void UpdateCustomKeyStatusPickCanceled()
    {
        CustomKeyStatusText = L("KeyDetection.CustomKey.Status.PickCanceled");
        CustomKeyStatusValueText = L("KeyDetection.CustomKey.StatusValue.PickCanceled");
    }

    private void UpdateCustomKeyStatusPickExited()
    {
        if (_selectedCustomKey != null)
        {
            CustomKeyStatusText = L("KeyDetection.CustomKey.Status.Selected", _selectedCustomKey.DisplayName);
            CustomKeyStatusValueText = L("KeyDetection.CustomKey.StatusValue.Selected", _selectedCustomKey.DisplayName);
        }
        else
        {
            CustomKeyStatusText = L("KeyDetection.CustomKey.Status.PickExited");
            CustomKeyStatusValueText = L("KeyDetection.CustomKey.StatusValue.PickExited");
        }
    }

    private void UpdateCustomKeyStatusSelected()
    {
        if (_selectedCustomKey == null)
        {
            UpdateCustomKeyStatusIdle();
            return;
        }
        CustomKeyStatusText = L("KeyDetection.CustomKey.Status.Selected", _selectedCustomKey.DisplayName);
        CustomKeyStatusValueText = L("KeyDetection.CustomKey.StatusValue.Selected", _selectedCustomKey.DisplayName);
    }

    private void UpdateCustomKeyStatusDown()
    {
        if (_selectedCustomKey == null)
        {
            UpdateCustomKeyStatusIdle();
            return;
        }
        CustomKeyStatusText = L("KeyDetection.CustomKey.Status.Down", _selectedCustomKey.DisplayName);
        CustomKeyStatusValueText = L("KeyDetection.CustomKey.StatusValue.Down", _selectedCustomKey.DisplayName);
    }

    private void UpdateCustomKeyStatusUp()
    {
        if (_selectedCustomKey == null)
        {
            UpdateCustomKeyStatusIdle();
            return;
        }
        CustomKeyStatusText = L("KeyDetection.CustomKey.Status.Up", _selectedCustomKey.DisplayName);
        CustomKeyStatusValueText = L("KeyDetection.CustomKey.StatusValue.Up", _selectedCustomKey.DisplayName);
    }

    private void ResetTransientInputState()
    {
        foreach (KeyDetectionButtonCardViewModel buttonCard in _buttonCards)
        {
            buttonCard.ResetPressedState();
        }
        _customKeyStats.ResetPressedState();
        SyncCustomKeyStats();
        UpdateCustomKeyStatusByCurrentState();
    }

    private double NormalizeThreshold(string value)
    {
        double parsedValue = 0.0;
        if (TryParseThreshold(value, ref parsedValue) && parsedValue > 1.0 && parsedValue < 1000.0)
        {
            return parsedValue;
        }
        return 80.0;
    }

    private void EnsureInputService()
    {
        if (_inputService == null)
        {
            RawInputKeyDetectionService rawInputKeyDetectionService = new RawInputKeyDetectionService(_rawInputBroker);
            rawInputKeyDetectionService.MouseButtonInput += OnMouseButtonInput;
            rawInputKeyDetectionService.MouseWheelInput += OnMouseWheelInput;
            rawInputKeyDetectionService.KeyboardInput += OnKeyboardInput;
            _inputService = rawInputKeyDetectionService;
        }
    }

    private void ReleaseInputService()
    {
        if (_inputService != null)
        {
            _inputService.MouseButtonInput -= OnMouseButtonInput;
            _inputService.MouseWheelInput -= OnMouseWheelInput;
            _inputService.KeyboardInput -= OnKeyboardInput;
            _inputService.Dispose();
            _inputService = null;
        }
    }

    private static bool TryParseThreshold(string value, ref double parsedValue)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out parsedValue))
        {
            return true;
        }
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedValue);
    }

    private static string FormatThreshold(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
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
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _scrollUpPulseTimer.Stop();
        _scrollDownPulseTimer.Stop();
        _localization.LanguageChanged -= OnLanguageChanged;
        _scrollUpPulseTimer.Tick -= OnScrollUpPulseTimerTick;
        _scrollDownPulseTimer.Tick -= OnScrollDownPulseTimerTick;
        foreach (KeyDetectionButtonCardViewModel buttonCard in _buttonCards)
        {
            buttonCard.Dispose();
        }
        ReleaseInputService();
    }

    void IDisposable.Dispose()
    {
        this.Dispose();
    }
}





