#define TRACE
using ClickSyncMouseTester.Models;
using ClickSyncMouseTester.Navigation;
using ClickSyncMouseTester.Services;
using ClickSyncMouseTester.ViewModels;
using ClickSyncMouseTester.Views.Shell;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;

namespace ClickSyncMouseTester;

[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private enum NavigationMenuTransitionState
    {
        Closed,
        Opening,
        OpenInteractive,
        Closing
    }

    private enum CaptureLockFailureReason
    {
        None,
        ActivateWindowFailed,
        MissingCaptureSurface,
        SetCursorPositionFailed,
        SetClipCursorFailed,
        UnexpectedError
    }

    private const int CursorClipHalfSize = 12;

    private const int CursorVisibilityLoopLimit = 24;

    private static readonly CornerRadius SquareWindowCornerRadius = new CornerRadius(0.0);

    private readonly ShellViewModel _shellViewModel;

    private readonly ThemeManager _themeManager;

    private readonly TimeZoneInfo _navigationLocalTimeZone;

    private readonly DispatcherTimer _navigationLocalClockTimer;

    private ICaptureSessionPageViewModel _activeCapturePage;

    private NativeMethods.RECT _savedClipRect;

    private bool _hasSavedClipRect;

    private bool _cursorHidden;

    private bool _isCaptureLocked;

    private string _lastRenderingDiagnostics;

    private nint _lastDisplayRefreshRateMonitorHandle;

    private double? _lastDisplayRefreshRateHz;

    private int _navigationMenuAnimationVersion;

    private HwndSource _windowSource;

    private bool _isPreparingNavigationCurtainOpen;

    private AppPageKey? _navigationMenuClosingHighlightPageKey;

    private NavigationMenuTransitionState _navigationMenuTransitionState;

    private List<NavigationMenuAnimationItem> _navigationMenuAnimationItems;

    private NavigationMenuPartsCache _navigationMenuPartsCache;

    private bool _isNavigationMenuLayoutInvalidated = true;

    private double _lastNavigationCurtainLayoutWidth = double.NaN;

    private double _lastNavigationCurtainLayoutHeight = double.NaN;

    private double _lastNavigationMenuScrollViewerWidth = double.NaN;

    private bool _hasPlayedWindowStartupAnimation;

    private readonly bool _usesStartupMenuHandoff;

    private bool _hasPreparedStartupMenuHandoffState;

    private bool _suppressNextNavigationMenuCloseAnimationForStartupHandoff;

    private sealed class NavigationMenuAnimationItem
    {
        public NavigationMenuAnimationItem(NavigationMenuParts parts)
            : this(parts?.Button, parts?.AnimatedContent, parts?.AnimatedTextElements, parts?.HoverSurface)
        {
        }

        public NavigationMenuAnimationItem(Button button, FrameworkElement animatedContent, List<FrameworkElement> animatedTextElements, Border hoverSurface)
        {
            Button = button;
            AnimatedContent = animatedContent;
            AnimatedTextElements = animatedTextElements ?? new List<FrameworkElement>();
            HoverSurface = hoverSurface;
        }

        public Button Button { get; }

        public FrameworkElement AnimatedContent { get; }

        public List<FrameworkElement> AnimatedTextElements { get; }

        public Border HoverSurface { get; }
    }

    public MainWindow()
        : this(null, usesStartupMenuHandoff: false)
    {
    }

    public MainWindow(ShellViewModel shellViewModel, bool usesStartupMenuHandoff)
    {
        _navigationLocalTimeZone = TimeZoneInfo.Local;
        InitializeComponent();
        _navigationMenuPartsCache = new NavigationMenuPartsCache(NavigationMenuItemsControl);
        _themeManager = ThemeManager.Instance;
        _shellViewModel = shellViewModel ?? new ShellViewModel();
        _usesStartupMenuHandoff = usesStartupMenuHandoff;
        base.DataContext = _shellViewModel;
        _shellViewModel.CurrentPageChanged += OnCurrentPageChanged;
        _shellViewModel.PropertyChanged += OnShellViewModelPropertyChanged;
        _shellViewModel.Pages.CollectionChanged += OnNavigationPagesChanged;
        _themeManager.ThemeChanged += OnThemeChanged;
        _navigationLocalClockTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1L)
        };
        _navigationLocalClockTimer.Tick += OnNavigationLocalClockTick;
        UpdateNavigationLocalClockText();
        _navigationLocalClockTimer.Start();
        AttachCapturePageHandlers(_shellViewModel.ActiveCapturePage);
    }

    private void OnCurrentPageChanged(object sender, EventArgs e)
    {
        AttachCapturePageHandlers(_shellViewModel.ActiveCapturePage);
        UpdateDisplayRefreshRateAwarePage(forceNotify: true);
        Dispatcher.BeginInvoke(new Action(AnimateCurrentPageHost));
    }

    private void AttachCapturePageHandlers(ICaptureSessionPageViewModel nextPage)
    {
        if (!ReferenceEquals(_activeCapturePage, nextPage))
        {
            if (_activeCapturePage != null)
            {
                _activeCapturePage.EnterLockRequested -= OnEnterLockRequested;
                _activeCapturePage.ExitLockRequested -= OnExitLockRequested;
            }
            _activeCapturePage = nextPage;
            if (_activeCapturePage != null)
            {
                _activeCapturePage.EnterLockRequested += OnEnterLockRequested;
                _activeCapturePage.ExitLockRequested += OnExitLockRequested;
            }
        }
    }

    private void OnEnterLockRequested(object sender, EventArgs e)
    {
        CaptureLockFailureReason failureReason = CaptureLockFailureReason.None;
        if (TryBeginCaptureLock(ref failureReason))
        {
            if (_activeCapturePage != null)
            {
                _activeCapturePage.OnLockEntered();
            }
            return;
        }
        EndCaptureLock();
        Trace.WriteLine($"Capture lock failed: {failureReason}.");
        LocalizationManager instance = LocalizationManager.Instance;
        AppAlertDialog appAlertDialog = new AppAlertDialog(instance.GetString("Dialog.LockFailed.Title"), ResolveCaptureLockFailureMessage(instance, failureReason), instance.GetString("Dialog.Common.Confirm"));
        appAlertDialog.Owner = this;
        appAlertDialog.ShowDialog();
    }

    private void OnExitLockRequested(object sender, CaptureLockRequestEventArgs e)
    {
        EndCaptureLock();
        if (_activeCapturePage != null)
        {
            _activeCapturePage.OnViewUnlockCompleted(e.Reason);
        }
    }

    private bool TryBeginCaptureLock(ref CaptureLockFailureReason failureReason)
    {
        failureReason = CaptureLockFailureReason.None;
        bool result;
        try
        {
            if (!Activate())
            {
                failureReason = CaptureLockFailureReason.ActivateWindowFailed;
                result = false;
            }
            else
            {
                Focus();
                FrameworkElement captureSurface = ResolveCaptureLockSurface();
                if (captureSurface == null)
                {
                    failureReason = CaptureLockFailureReason.MissingCaptureSurface;
                    result = false;
                }
                else
                {
                    captureSurface.Focus();
                    SaveCurrentClipRect();
                    NativeMethods.RECT cursorLockRect = BuildCursorLockRect(captureSurface);
                    int cursorCenterX = (cursorLockRect.Left + cursorLockRect.Right) / 2;
                    int cursorCenterY = (cursorLockRect.Top + cursorLockRect.Bottom) / 2;
                    if (!NativeMethods.SetCursorPos(cursorCenterX, cursorCenterY))
                    {
                        Trace.WriteLine($"SetCursorPos failed with Win32 error {Marshal.GetLastWin32Error()}.");
                        failureReason = CaptureLockFailureReason.SetCursorPositionFailed;
                        result = false;
                    }
                    else if (!NativeMethods.SetClipCursor(ref cursorLockRect))
                    {
                        Trace.WriteLine($"SetClipCursor failed with Win32 error {Marshal.GetLastWin32Error()}.");
                        failureReason = CaptureLockFailureReason.SetClipCursorFailed;
                        result = false;
                    }
                    else
                    {
                        HideCursor();
                        _isCaptureLocked = true;
                        result = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Unexpected capture lock failure: {ex}");
            failureReason = CaptureLockFailureReason.UnexpectedError;
            result = false;
        }
        return result;
    }

    private void SaveCurrentClipRect()
    {
        _hasSavedClipRect = NativeMethods.GetClipCursor(ref _savedClipRect);
    }

    private NativeMethods.RECT BuildCursorLockRect(FrameworkElement captureSurface)
    {
        Point screenCenter = captureSurface == null || captureSurface.ActualWidth <= 0.0 || captureSurface.ActualHeight <= 0.0
            ? PointToScreen(new Point(ActualWidth / 2.0, ActualHeight / 2.0))
            : captureSurface.PointToScreen(new Point(captureSurface.ActualWidth / 2.0, captureSurface.ActualHeight / 2.0));
        int centerX = (int)Math.Round(screenCenter.X);
        int centerY = (int)Math.Round(screenCenter.Y);
        return new NativeMethods.RECT
        {
            Left = centerX - CursorClipHalfSize,
            Top = centerY - CursorClipHalfSize,
            Right = centerX + CursorClipHalfSize,
            Bottom = centerY + CursorClipHalfSize
        };
    }

    private string ResolveCaptureLockFailureMessage(LocalizationManager localization, CaptureLockFailureReason failureReason)
    {
        string baseMessage = localization.GetString("Dialog.LockFailed.Message");
        string detailKey = failureReason switch
        {
            CaptureLockFailureReason.ActivateWindowFailed => "Dialog.LockFailed.Detail.ActivateWindow",
            CaptureLockFailureReason.MissingCaptureSurface => "Dialog.LockFailed.Detail.MissingCaptureSurface",
            CaptureLockFailureReason.SetCursorPositionFailed => "Dialog.LockFailed.Detail.SetCursorPos",
            CaptureLockFailureReason.SetClipCursorFailed => "Dialog.LockFailed.Detail.SetClipCursor",
            CaptureLockFailureReason.UnexpectedError => "Dialog.LockFailed.Detail.Unknown",
            _ => null
        };
        if (string.IsNullOrWhiteSpace(detailKey))
        {
            return baseMessage;
        }
        string detailMessage = localization.GetString(detailKey);
        if (string.IsNullOrWhiteSpace(detailMessage) || string.Equals(detailMessage, detailKey, StringComparison.Ordinal))
        {
            return baseMessage;
        }
        return baseMessage + Environment.NewLine + Environment.NewLine + detailMessage;
    }

    private void EndCaptureLock()
    {
        if (_hasSavedClipRect)
        {
            NativeMethods.SetClipCursor(ref _savedClipRect);
        }
        else
        {
            NativeMethods.ClearClipCursor(IntPtr.Zero);
        }
        _hasSavedClipRect = false;
        ShowCursorIfNeeded();
        _isCaptureLocked = false;
    }

    private void HideCursor()
    {
        if (_cursorHidden)
        {
            return;
        }
        for (int attempt = 0; attempt < CursorVisibilityLoopLimit; attempt++)
        {
            if (NativeMethods.ShowCursor(show: false) < 0)
            {
                break;
            }
        }
        _cursorHidden = true;
    }

    private void ShowCursorIfNeeded()
    {
        if (!_cursorHidden)
        {
            return;
        }
        for (int attempt = 0; attempt < CursorVisibilityLoopLimit; attempt++)
        {
            if (NativeMethods.ShowCursor(show: true) >= 0)
            {
                break;
            }
        }
        _cursorHidden = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        if (_windowSource != null)
        {
            _windowSource.AddHook(WndProc);
        }
        UpdateWindowControlGlyph();
        ApplyWindowCornerPreference();
        ApplyWindowChromeTheme();
        UpdateDisplayEnvironment();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_usesStartupMenuHandoff)
        {
            PrepareStartupMenuHandoffState();
        }
        else if (!_hasPlayedWindowStartupAnimation)
        {
            _hasPlayedWindowStartupAnimation = true;
            Dispatcher.BeginInvoke(new Action(AnimateWindowStartupPresentation), DispatcherPriority.Loaded);
        }
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        UpdateRenderingDiagnostics();
        UpdateDisplayRefreshRateAwarePageIfMonitorChanged();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateWindowControlGlyph();
        ApplyWindowCornerPreference();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_shellViewModel != null && _shellViewModel.IsNavigationMenuOpen && e.Key == Key.Return)
        {
            e.Handled = true;
            if (CanAcceptNavigationMenuInput)
            {
                ClearNavigationMenuClosingHighlight();
                _shellViewModel.CloseNavigationMenu();
            }
        }
        else if (_isCaptureLocked && CanHandleCaptureShortcut(e) && _activeCapturePage is ICaptureKeyboardShortcutHandler lockedShortcutHandler)
        {
            e.Handled = lockedShortcutHandler.TryHandleCaptureKeyboardShortcut(CaptureKeyboardShortcut.StartOrPause);
        }
        else if (!_isCaptureLocked && CanHandleCaptureShortcut(e) && Keyboard.FocusedElement is not TextBox && _activeCapturePage is ICaptureKeyboardShortcutHandler shortcutHandler)
        {
            e.Handled = shortcutHandler.TryHandleCaptureKeyboardShortcut(CaptureKeyboardShortcut.StartOrPause);
        }
        else
        {
            base.OnPreviewKeyDown(e);
        }
    }

    private bool CanHandleCaptureShortcut(KeyEventArgs e)
    {
        return e != null
            && !e.IsRepeat
            && e.Key == Key.S
            && Keyboard.Modifiers == ModifierKeys.None
            && (_shellViewModel == null || !_shellViewModel.IsNavigationMenuOpen);
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (_isCaptureLocked)
        {
            e.Handled = true;
            _activeCapturePage?.RequestPauseFromView();
        }
        else
        {
            base.OnPreviewMouseRightButtonDown(e);
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_isCaptureLocked)
        {
            _activeCapturePage?.RequestPauseFromView();
        }
    }

    private void NavigationBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e != null)
        {
            e.Handled = true;
        }
    }

    private void NavigationMenuToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_shellViewModel != null)
        {
            if (_shellViewModel.IsNavigationMenuOpen)
            {
                if (CanAcceptNavigationMenuInput)
                {
                    ClearNavigationMenuClosingHighlight();
                    _shellViewModel.CloseNavigationMenu();
                }
            }
            else
            {
                ClearNavigationMenuClosingHighlight();
                _shellViewModel.OpenNavigationMenu();
            }
        }
    }

    private void NavigationCurtainCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_shellViewModel != null && _shellViewModel.IsNavigationMenuOpen)
        {
            e.Handled = true;
            if (CanAcceptNavigationMenuInput)
            {
                ClearNavigationMenuClosingHighlight();
                _shellViewModel.CloseNavigationMenu();
            }
        }
    }

    private void GithubLinkButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/Nuitfanee/ClickSyncMouseTester",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to open GitHub link: {ex.Message}");
        }
    }

    private void NavigationMenuButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanAcceptNavigationMenuInput)
        {
            if (e != null)
            {
                e.Handled = true;
            }
            return;
        }
        Button button = sender as Button;
        RememberNavigationMenuClosingHighlight(button);
        SetNavigationMenuHoverSurfaceState(1.0, new[] { button });
    }

    private void NavigationMenuButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e != null && (e.Key == Key.Return || e.Key == Key.Space))
        {
            if (!CanAcceptNavigationMenuInput)
            {
                e.Handled = true;
                return;
            }
            Button button = sender as Button;
            RememberNavigationMenuClosingHighlight(button);
            SetNavigationMenuHoverSurfaceState(1.0, new[] { button });
        }
    }

    private void NavigationMenuButton_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateNavigationMenuHoverSurface(sender as Button, 1.0);
    }

    private void NavigationMenuButton_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateNavigationMenuHoverSurface(sender as Button, 0.0);
    }

    private async void OnShellViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e == null || string.IsNullOrEmpty(e.PropertyName))
        {
            InvalidateNavigationMenuLayoutCache();
        }
        else if (string.Equals(e.PropertyName, "CurrentPageTitle", StringComparison.Ordinal)
            || string.Equals(e.PropertyName, "CurrentPageSummary", StringComparison.Ordinal)
            || string.Equals(e.PropertyName, "IsChineseLanguageCurrent", StringComparison.Ordinal)
            || string.Equals(e.PropertyName, "IsEnglishLanguageCurrent", StringComparison.Ordinal))
        {
            InvalidateNavigationMenuLayoutCache();
        }

        if (e != null && !string.IsNullOrEmpty(e.PropertyName) && !string.Equals(e.PropertyName, "IsNavigationMenuOpen", StringComparison.Ordinal))
        {
            return;
        }
        _navigationMenuAnimationVersion++;
        int navigationMenuAnimationVersion = _navigationMenuAnimationVersion;
        bool isMenuOpening = _shellViewModel != null && _shellViewModel.IsNavigationMenuOpen;
        using IDisposable menuTransitionScope = UiPerformanceProbe.BeginStage(isMenuOpening ? "MainWindow.NavigationMenu.Open" : "MainWindow.NavigationMenu.Close", this);
        if (!isMenuOpening && ShouldSuppressNavigationMenuCloseAnimationForStartupHandoff())
        {
            CompleteStartupMenuCloseHandoffWithoutAnimation();
            return;
        }
        await Dispatcher.InvokeAsync((Action)(() =>
        {
            if (isMenuOpening)
            {
                PrepareNavigationOpenLayoutForAnimation();
            }
            else
            {
                NavigationCurtainSurface?.UpdateLayout();
                if (NavigationMenuItemsControl != null)
                {
                    NavigationMenuItemsControl.UpdateLayout();
                }
            }
        }));
        if (navigationMenuAnimationVersion != _navigationMenuAnimationVersion)
        {
            return;
        }
        if (isMenuOpening)
        {
            _navigationMenuTransitionState = NavigationMenuTransitionState.Opening;
            ClearNavigationMenuClosingHighlight();
            List<NavigationMenuAnimationItem> menuItems = ResolveNavigationMenuAnimationItems(updateLayout: false);
            _navigationMenuAnimationItems = menuItems;
            ResetNavigationMenuItemsForNextOpen(menuItems);
            Task curtainOpenAnimationTask = AnimateNavigationCurtainAsync(isOpening: true);
            Task menuItemsAnimationTask = AnimateNavigationMenuItemsAsync(isOpening: true, menuItems);
            SyncNavigationMenuHoverSurfaces(menuItems);
            await Task.WhenAll(new Task[2]
            {
                curtainOpenAnimationTask,
                menuItemsAnimationTask
            });
            if (navigationMenuAnimationVersion == _navigationMenuAnimationVersion && _shellViewModel != null && _shellViewModel.IsNavigationMenuOpen)
            {
                _navigationMenuTransitionState = NavigationMenuTransitionState.OpenInteractive;
                SyncNavigationMenuHoverSurfaces(menuItems);
            }
            return;
        }
        bool wasOpening = _navigationMenuTransitionState == NavigationMenuTransitionState.Opening;
        _navigationMenuTransitionState = NavigationMenuTransitionState.Closing;
        if (wasOpening)
        {
            FreezeNavigationMenuItemsCurrentState(_navigationMenuAnimationItems);
        }
        else
        {
            SetNavigationMenuItemsVisibleState(_navigationMenuAnimationItems);
        }
        if (NavigationCurtainSurface != null)
        {
            NavigationCurtainSurface.IsHitTestVisible = false;
        }
        Task curtainCloseAnimationTask = AnimateNavigationCurtainAsync(isOpening: false);
        await curtainCloseAnimationTask;
        if (navigationMenuAnimationVersion != _navigationMenuAnimationVersion || (_shellViewModel != null && _shellViewModel.IsNavigationMenuOpen))
        {
            return;
        }
        _navigationMenuTransitionState = NavigationMenuTransitionState.Closed;
        await Dispatcher.InvokeAsync((Action)(() =>
        {
            if (NavigationCurtainSurface != null)
            {
                NavigationCurtainSurface.Visibility = Visibility.Collapsed;
                NavigationCurtainSurface.IsHitTestVisible = false;
            }
            UpdateNavigationCurtainClip(0.0);
            ResetNavigationMenuItemsForNextOpen();
            _navigationMenuAnimationItems = null;
        }));
    }

    internal void SuppressNextNavigationMenuCloseAnimationForStartupHandoff()
    {
        if (_usesStartupMenuHandoff)
        {
            _suppressNextNavigationMenuCloseAnimationForStartupHandoff = true;
        }
    }

    internal void ClearNavigationMenuCloseAnimationSuppressionForStartupHandoff()
    {
        _suppressNextNavigationMenuCloseAnimationForStartupHandoff = false;
    }

    internal void PrepareHiddenStartupMenuHandoffWindow()
    {
        if (!_usesStartupMenuHandoff)
        {
            return;
        }
        ShowActivated = false;
        Opacity = 0.0;
        IsHitTestVisible = false;
        ShowInTaskbar = true;
        if (!IsVisible)
        {
            Show();
        }
        UpdateLayout();
        PrepareStartupMenuHandoffState();
    }

    internal void CompleteStartupMenuHandoffActivation()
    {
        if (_usesStartupMenuHandoff && !IsVisible)
        {
            PrepareHiddenStartupMenuHandoffWindow();
        }
        ShowInTaskbar = true;
        Opacity = 1.0;
        IsHitTestVisible = true;
        Activate();
        Focus();
        UiPerformanceProbe.Mark("MainWindow.CompleteStartupMenuHandoffActivation", this);
    }

    private bool ShouldSuppressNavigationMenuCloseAnimationForStartupHandoff()
    {
        return _usesStartupMenuHandoff && _suppressNextNavigationMenuCloseAnimationForStartupHandoff;
    }

    private void CompleteStartupMenuCloseHandoffWithoutAnimation()
    {
        _suppressNextNavigationMenuCloseAnimationForStartupHandoff = false;
        _navigationMenuTransitionState = NavigationMenuTransitionState.Closed;
        if (NavigationCurtainSurface != null)
        {
            NavigationCurtainSurface.Visibility = Visibility.Collapsed;
            NavigationCurtainSurface.IsHitTestVisible = false;
        }
        UpdateNavigationCurtainClip(0.0);
        ResetNavigationMenuItemsForNextOpen(_navigationMenuAnimationItems);
        _navigationMenuAnimationItems = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _shellViewModel.CurrentPageChanged -= OnCurrentPageChanged;
        _shellViewModel.PropertyChanged -= OnShellViewModelPropertyChanged;
        _shellViewModel.Pages.CollectionChanged -= OnNavigationPagesChanged;
        _themeManager.ThemeChanged -= OnThemeChanged;
        _navigationLocalClockTimer.Tick -= OnNavigationLocalClockTick;
        _navigationLocalClockTimer.Stop();
        AttachCapturePageHandlers(null);
        if (_windowSource != null)
        {
            _windowSource.RemoveHook(WndProc);
            _windowSource = null;
        }
        EndCaptureLock();
        _shellViewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnNavigationLocalClockTick(object sender, EventArgs e)
    {
        UpdateNavigationLocalClockText();
    }

    private void OnNavigationPagesChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateNavigationMenuPartsCache();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case 36:
                WindowSizingHelper.ApplyWindowBounds(this, hwnd, lParam);
                handled = true;
                return IntPtr.Zero;
            case 126:
            case 736:
                InvalidateNavigationMenuLayoutCache();
                Dispatcher.BeginInvoke(new Action(UpdateDisplayEnvironment));
                break;
        }
        return IntPtr.Zero;
    }

    private void OnThemeChanged(object sender, EventArgs e)
    {
        InvalidateNavigationMenuLayoutCache();
        Dispatcher.BeginInvoke(new Action(ApplyWindowChromeTheme));
    }

    private void AnimateCurrentPageHost()
    {
        if (CurrentPageHost != null && base.IsLoaded)
        {
            TranslateTransform translateTransform = CurrentPageHost.RenderTransform as TranslateTransform;
            if (translateTransform == null)
            {
                translateTransform = new TranslateTransform();
                CurrentPageHost.RenderTransform = translateTransform;
            }
            Duration duration = ResolveDurationResource("MotionPageSwapDuration", 180.0);
            IEasingFunction easingFunction = ResolveEasingFunctionResource("MotionOutEase");
            translateTransform.BeginAnimation(TranslateTransform.YProperty, null);
            CurrentPageHost.Opacity = 1.0;
            DoubleAnimation doubleAnimation = new DoubleAnimation
            {
                From = 14.0,
                To = 0.0,
                Duration = duration
            };
            if (easingFunction != null)
            {
                doubleAnimation.EasingFunction = easingFunction;
            }
            translateTransform.BeginAnimation(TranslateTransform.YProperty, doubleAnimation);
        }
    }

    private void AnimateWindowStartupPresentation()
    {
        if (WindowPresentationSurface == null || WindowContentSurface == null)
        {
            return;
        }
        Duration duration = ResolveDurationResource("MotionWindowStartupDuration", 340.0);
        IEasingFunction opacityEasing = ResolveEasingFunctionResource("MotionInOutEase");
        IEasingFunction contentOffsetEasing = ResolveEasingFunctionResource("MotionOutEase");
        TimeSpan value = TimeSpan.FromMilliseconds(55L);
        WindowPresentationSurface.BeginAnimation(UIElement.OpacityProperty, null);
        WindowPresentationSurface.Opacity = 0.0;
        DoubleAnimation doubleAnimation = new DoubleAnimation
        {
            From = WindowPresentationSurface.Opacity,
            To = 1.0,
            Duration = duration
        };
        if (opacityEasing != null)
        {
            doubleAnimation.EasingFunction = opacityEasing;
        }
        WindowPresentationSurface.BeginAnimation(UIElement.OpacityProperty, doubleAnimation);
        WindowContentSurface.BeginAnimation(UIElement.OpacityProperty, null);
        WindowContentSurface.Opacity = 0.0;
        DoubleAnimation contentOpacityAnimation = new DoubleAnimation
        {
            From = WindowContentSurface.Opacity,
            To = 1.0,
            BeginTime = value,
            Duration = duration
        };
        if (opacityEasing != null)
        {
            contentOpacityAnimation.EasingFunction = opacityEasing;
        }
        WindowContentSurface.BeginAnimation(UIElement.OpacityProperty, contentOpacityAnimation);
        if (WindowContentStartupTransform != null)
        {
            WindowContentStartupTransform.BeginAnimation(TranslateTransform.YProperty, null);
            WindowContentStartupTransform.Y = 12.0;
            DoubleAnimation contentOffsetAnimation = new DoubleAnimation
            {
                From = WindowContentStartupTransform.Y,
                To = 0.0,
                BeginTime = value,
                Duration = duration
            };
            if (contentOffsetEasing != null)
            {
                contentOffsetAnimation.EasingFunction = contentOffsetEasing;
            }
            WindowContentStartupTransform.BeginAnimation(TranslateTransform.YProperty, contentOffsetAnimation);
        }
    }

    private void PrepareStartupMenuHandoffState()
    {
        if (_hasPreparedStartupMenuHandoffState)
        {
            return;
        }
        _hasPreparedStartupMenuHandoffState = true;
        if (WindowPresentationSurface != null)
        {
            WindowPresentationSurface.BeginAnimation(UIElement.OpacityProperty, null);
            WindowPresentationSurface.Opacity = 1.0;
        }
        if (WindowContentSurface != null)
        {
            WindowContentSurface.BeginAnimation(UIElement.OpacityProperty, null);
            WindowContentSurface.Opacity = 1.0;
        }
        if (WindowContentStartupTransform != null)
        {
            WindowContentStartupTransform.BeginAnimation(TranslateTransform.YProperty, null);
            WindowContentStartupTransform.Y = 0.0;
        }
        if (_shellViewModel != null && _shellViewModel.IsNavigationMenuOpen)
        {
            NavigationCurtainSurface.Visibility = Visibility.Visible;
            NavigationCurtainSurface.IsHitTestVisible = true;
            NavigationCurtainSurface.UpdateLayout();
            UpdateNavigationCurtainGuideLayout();
            UpdateNavigationCurtainClip(Math.Max(NavigationCurtainSurface.ActualHeight, Math.Max(base.ActualHeight, 1.0)));
            _navigationMenuAnimationItems = ResolveNavigationMenuAnimationItems();
            SetNavigationMenuItemsVisibleState(_navigationMenuAnimationItems);
            _navigationMenuTransitionState = NavigationMenuTransitionState.OpenInteractive;
        }
    }

    private void NavigationCurtainSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (NavigationCurtainSurface == null)
        {
            return;
        }
        InvalidateNavigationMenuLayoutCache();
        UpdateNavigationCurtainGuideLayout();
        if (_isPreparingNavigationCurtainOpen)
        {
            return;
        }
        if (_shellViewModel == null || !_shellViewModel.IsNavigationMenuOpen)
        {
            UpdateNavigationCurtainClip(0.0);
            return;
        }
        double visibleHeight = 0.0;
        if (NavigationCurtainSurface.Visibility == Visibility.Visible)
        {
            visibleHeight = NavigationCurtainSurface.ActualHeight;
        }
        UpdateNavigationCurtainClip(visibleHeight);
    }

    private void NavigationMenuScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        InvalidateNavigationMenuLayoutCache();
        UpdateNavigationCurtainGuideLayout();
    }

    private void PrepareNavigationOpenLayoutForAnimation()
    {
        if (NavigationCurtainSurface == null)
        {
            return;
        }
        _isPreparingNavigationCurtainOpen = true;
        try
        {
            NavigationCurtainSurface.Visibility = Visibility.Visible;
            NavigationCurtainSurface.IsHitTestVisible = true;
            NavigationCurtainSurface.UpdateLayout();
            UpdateNavigationCurtainGuideLayout();
        }
        finally
        {
            _isPreparingNavigationCurtainOpen = false;
        }
    }

    private Task AnimateNavigationCurtainAsync(bool isOpening)
    {
        if (NavigationCurtainSurface == null)
        {
            return Task.CompletedTask;
        }
        if (isOpening)
        {
            _isPreparingNavigationCurtainOpen = true;
        }
        try
        {
            if (isOpening)
            {
                NavigationCurtainSurface.Visibility = Visibility.Visible;
                NavigationCurtainSurface.IsHitTestVisible = true;
            }
            Duration duration = ResolveDurationResource(isOpening ? "MotionNavigationCurtainEnterDuration" : "MotionNavigationCurtainExitDuration", 800.0);
            IEasingFunction easingFunction = ResolveEasingFunctionResource(isOpening ? "MotionNavigationCurtainEnterEase" : "MotionNavigationCurtainExitEase");
            int animationVersion = _navigationMenuAnimationVersion;
            return NavigationCurtainAnimator.AnimateAsync(
                NavigationCurtainSurface,
                ActualHeight,
                isOpening,
                duration,
                easingFunction,
                () => animationVersion == _navigationMenuAnimationVersion);
        }
        finally
        {
            if (isOpening)
            {
                _isPreparingNavigationCurtainOpen = false;
            }
        }
    }

    private void UpdateNavigationCurtainGuideLayout()
    {
        if (NavigationCurtainLayoutRoot == null || NavigationCurtainTopGuideRow == null || NavigationCurtainLeftGuideColumn == null)
        {
            return;
        }
        double layoutWidth = Math.Max(NavigationCurtainLayoutRoot.ActualWidth, 0.0);
        double layoutHeight = Math.Max(NavigationCurtainLayoutRoot.ActualHeight, 0.0);
        if (layoutWidth > 0.0 && layoutHeight > 0.0)
        {
            double scrollViewerWidth = NavigationMenuScrollViewer != null ? Math.Max(NavigationMenuScrollViewer.ActualWidth, 0.0) : 0.0;
            if (!_isNavigationMenuLayoutInvalidated
                && AreClose(layoutWidth, _lastNavigationCurtainLayoutWidth)
                && AreClose(layoutHeight, _lastNavigationCurtainLayoutHeight)
                && AreClose(scrollViewerWidth, _lastNavigationMenuScrollViewerWidth))
            {
                return;
            }

            _isNavigationMenuLayoutInvalidated = false;
            _lastNavigationCurtainLayoutWidth = layoutWidth;
            _lastNavigationCurtainLayoutHeight = layoutHeight;
            _lastNavigationMenuScrollViewerWidth = scrollViewerWidth;

            double frameWidth = ResolveDoubleResource("Layout.Navigation.FrameWidth", 860.0);
            double closeRailWidth = ResolveGridLengthResource("Layout.Navigation.CloseRailWidth", 96.0);
            double closeRailGap = ResolveGridLengthResource("Layout.Navigation.CloseRailGap", 44.0);
            double contentOffsetX = ResolveDoubleResource("Layout.Navigation.ContentOffsetX", 0.0);
            double verticalGuideOffsetX = ResolveDoubleResource("Layout.Navigation.VerticalGuideOffsetX", 0.0);
            Thickness thickness = ResolveThicknessResource("Layout.Navigation.OverlayPadding");
            double leftGuideWidth = (layoutWidth - frameWidth) / 2.0 + closeRailWidth + closeRailGap + contentOffsetX + verticalGuideOffsetX;
            leftGuideWidth = Math.Max(0.0, Math.Min(leftGuideWidth, layoutWidth));
            double measuredMenuHeight = ResolveNavigationMenuMeasuredHeight(layoutWidth, layoutHeight);
            double closeRailMinimumHeight = ResolveNavigationCloseRailMinimumHeight();
            double topGuideHeight = 0.0;
            if (measuredMenuHeight > 0.0)
            {
                topGuideHeight = (layoutHeight - Math.Min(measuredMenuHeight, layoutHeight)) / 2.0;
            }
            else if (_shellViewModel != null && _shellViewModel.IsNavigationMenuOpen)
            {
                topGuideHeight = Math.Max(NavigationCurtainTopGuideRow.Height.Value, 0.0);
            }
            topGuideHeight = Math.Max(topGuideHeight, closeRailMinimumHeight);
            topGuideHeight = Math.Max(0.0, Math.Min(topGuideHeight, layoutHeight));
            NavigationCurtainLeftGuideColumn.Width = new GridLength(leftGuideWidth, GridUnitType.Pixel);
            NavigationCurtainTopGuideRow.Height = new GridLength(topGuideHeight, GridUnitType.Pixel);
            if (NavigationCurtainVerticalGuide != null)
            {
                NavigationCurtainVerticalGuide.Margin = new Thickness(thickness.Left + leftGuideWidth, 0.0, 0.0, 0.0);
            }
            UpdateNavigationMenuContentAlignment();
        }
    }

    private void UpdateNavigationMenuContentAlignment(IReadOnlyList<NavigationMenuAnimationItem> menuItems = null, bool updateLayout = true)
    {
        if (NavigationCurtainLayoutRoot == null || NavigationMenuScrollViewer == null || NavigationMenuItemsControl == null || NavigationGithubLinkButton == null)
        {
            return;
        }
        if (updateLayout)
        {
            NavigationMenuItemsControl.UpdateLayout();
        }
        Thickness thickness = ResolveThicknessResource("Layout.Navigation.ContentStartMargin.Localized");
        Point scrollViewerOrigin = NavigationMenuScrollViewer.TranslatePoint(new Point(0.0, 0.0), NavigationCurtainLayoutRoot);
        Point githubButtonOrigin = NavigationGithubLinkButton.TranslatePoint(new Point(0.0, 0.0), NavigationCurtainLayoutRoot);
        double githubIconWidth = 26.0;
        double githubIconInset = Math.Max(0.0, (NavigationGithubLinkButton.ActualWidth - githubIconWidth) / 2.0);
        double contentLeftMargin = Math.Max(0.0, githubButtonOrigin.X + githubIconInset - scrollViewerOrigin.X);
        if (menuItems != null)
        {
            foreach (NavigationMenuAnimationItem menuItem in menuItems)
            {
                if (menuItem.AnimatedContent != null)
                {
                    menuItem.AnimatedContent.Margin = new Thickness(contentLeftMargin, thickness.Top, thickness.Right, thickness.Bottom);
                }
            }
            return;
        }
        IReadOnlyList<NavigationMenuParts> resolvedParts = _navigationMenuPartsCache.Resolve(updateLayout: false);
        foreach (NavigationMenuParts parts in resolvedParts)
        {
            FrameworkElement animatedContent = parts.AnimatedContent;
            if (animatedContent != null)
            {
                animatedContent.Margin = new Thickness(contentLeftMargin, thickness.Top, thickness.Right, thickness.Bottom);
            }
        }
    }

    private double ResolveNavigationMenuMeasuredHeight(double layoutWidth, double layoutHeight)
    {
        double availableWidth = layoutWidth;
        if (NavigationMenuScrollViewer != null && NavigationMenuScrollViewer.ActualWidth > 0.0)
        {
            availableWidth = NavigationMenuScrollViewer.ActualWidth;
        }
        double measuredItemsHeight = 0.0;
        Size desiredSize;
        if (NavigationMenuItemsControl != null)
        {
            NavigationMenuItemsControl.Measure(new Size(Math.Max(availableWidth, 1.0), double.PositiveInfinity));
            desiredSize = NavigationMenuItemsControl.DesiredSize;
            measuredItemsHeight = Math.Max(desiredSize.Height, NavigationMenuItemsControl.ActualHeight);
        }
        double maximumHeight = layoutHeight;
        if (NavigationMenuScrollViewer != null)
        {
            double maxHeight = NavigationMenuScrollViewer.MaxHeight;
            if (!double.IsNaN(maxHeight) && !double.IsInfinity(maxHeight) && maxHeight > 0.0)
            {
                maximumHeight = Math.Min(maximumHeight, maxHeight);
            }
        }
        if (measuredItemsHeight > 0.0)
        {
            return Math.Max(0.0, Math.Min(measuredItemsHeight, maximumHeight));
        }
        double measuredScrollViewerHeight = 0.0;
        if (NavigationMenuScrollViewer != null)
        {
            double actualHeight = NavigationMenuScrollViewer.ActualHeight;
            desiredSize = NavigationMenuScrollViewer.DesiredSize;
            measuredScrollViewerHeight = Math.Max(actualHeight, desiredSize.Height);
        }
        return Math.Max(0.0, Math.Min(measuredScrollViewerHeight, maximumHeight));
    }

    private double ResolveNavigationCloseRailMinimumHeight()
    {
        double result = 76.0;
        if (NavigationCurtainCloseButton == null)
        {
            return result;
        }
        if (NavigationCurtainCloseButton.ActualHeight > 0.0)
        {
            result = NavigationCurtainCloseButton.ActualHeight;
        }
        else if (!double.IsNaN(NavigationCurtainCloseButton.Height) && NavigationCurtainCloseButton.Height > 0.0)
        {
            result = NavigationCurtainCloseButton.Height;
        }
        return result;
    }

    private List<NavigationMenuAnimationItem> ResolveNavigationMenuAnimationItems(bool updateLayout = true)
    {
        List<NavigationMenuAnimationItem> menuItems = new List<NavigationMenuAnimationItem>();
        if (NavigationMenuItemsControl == null)
        {
            return menuItems;
        }
        if (updateLayout)
        {
            NavigationMenuItemsControl.UpdateLayout();
        }
        IReadOnlyList<NavigationMenuParts> menuParts = _navigationMenuPartsCache.Resolve(updateLayout: false);
        foreach (NavigationMenuParts parts in menuParts)
        {
            menuItems.Add(new NavigationMenuAnimationItem(parts));
        }
        UpdateNavigationMenuContentAlignment(menuItems, updateLayout: false);
        return menuItems;
    }

    private List<NavigationMenuAnimationItem> ResolveNavigationMenuAnimationItems(IEnumerable<Button> menuButtons)
    {
        if (menuButtons == null)
        {
            return ResolveNavigationMenuAnimationItems(updateLayout: false);
        }

        List<NavigationMenuAnimationItem> menuItems = new List<NavigationMenuAnimationItem>();
        foreach (Button menuButton in menuButtons)
        {
            NavigationMenuParts parts = _navigationMenuPartsCache.Resolve(menuButton);
            if (parts != null)
            {
                menuItems.Add(new NavigationMenuAnimationItem(parts));
            }
        }

        return menuItems;
    }

    private void InvalidateNavigationMenuPartsCache()
    {
        _navigationMenuPartsCache?.InvalidateParts();
        InvalidateNavigationMenuLayoutCache();
        _navigationMenuAnimationItems = null;
    }

    private void InvalidateNavigationMenuLayoutCache()
    {
        _isNavigationMenuLayoutInvalidated = true;
    }

    private static bool AreClose(double left, double right)
    {
        return Math.Abs(left - right) < 0.001;
    }

    private void UpdateNavigationLocalClockText()
    {
        if (NavigationLocalClockText != null)
        {
            DateTimeOffset dateTimeOffset = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _navigationLocalTimeZone);
            NavigationLocalClockText.Text = dateTimeOffset.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }

    private void UpdateNavigationCurtainClip(double visibleHeight)
    {
        NavigationCurtainAnimator.SetReveal(NavigationCurtainSurface, visibleHeight);
    }

    private async Task AnimateNavigationMenuItemsAsync(bool isOpening, IReadOnlyList<NavigationMenuAnimationItem> menuItems = null)
    {
        if (NavigationMenuItemsControl == null)
        {
            return;
        }
        menuItems ??= ResolveNavigationMenuAnimationItems();
        if (menuItems.Count == 0)
        {
            return;
        }
        if (!isOpening)
        {
            SetNavigationMenuItemsVisibleState(menuItems);
            return;
        }
        Duration duration = ResolveDurationResource("MotionNavigationItemEnterDuration", 800.0);
        IEasingFunction easingFunction = ResolveEasingFunctionResource("MotionNavigationContentEnterEase");
        double baseDelayMilliseconds = ResolveDoubleResource("MotionNavigationItemEnterBaseDelayMs", 400.0);
        double staggerRangeMilliseconds = ResolveDoubleResource("MotionNavigationItemEnterStaggerRangeMs", 200.0);
        double staggerPower = ResolveDoubleResource("MotionNavigationItemEnterStaggerPower", 1.35);
        int animationVersion = _navigationMenuAnimationVersion;
        SetNavigationMenuItemsState(1.0, 0.0, menuItems);
        List<IReadOnlyList<FrameworkElement>> textElementGroups = new List<IReadOnlyList<FrameworkElement>>(menuItems.Count);
        foreach (NavigationMenuAnimationItem menuItem in menuItems)
        {
            textElementGroups.Add(menuItem.AnimatedTextElements);
        }
        await NavigationMotion.AnimateMenuTextEnterAsync(
            textElementGroups,
            duration,
            easingFunction,
            baseDelayMilliseconds,
            staggerRangeMilliseconds,
            staggerPower,
            EnsureNavigationMenuTextTransform,
            ResolveNavigationMenuTextHiddenOffset,
            ResolveNavigationMenuTextVisibleOpacity,
            () => animationVersion == _navigationMenuAnimationVersion
                && _shellViewModel != null
                && _shellViewModel.IsNavigationMenuOpen);
    }

    private void ResetNavigationMenuItemsForNextOpen(IReadOnlyList<NavigationMenuAnimationItem> menuItems = null)
    {
        ClearNavigationMenuClosingHighlight();
        menuItems ??= ResolveNavigationMenuAnimationItems();
        SetNavigationMenuItemsState(1.0, 0.0, menuItems);
        SetNavigationMenuTextState(isVisible: false, menuItems);
        SetNavigationMenuHoverSurfaceState(0.0, menuItems);
    }

    private void SetNavigationMenuItemsVisibleState(IReadOnlyList<NavigationMenuAnimationItem> menuItems)
    {
        menuItems ??= ResolveNavigationMenuAnimationItems();
        SetNavigationMenuItemsState(1.0, 0.0, menuItems);
        SetNavigationMenuTextState(isVisible: true, menuItems);
        SyncNavigationMenuHoverSurfaces(menuItems);
    }

    private bool CanAcceptNavigationMenuInput =>
        _navigationMenuTransitionState == NavigationMenuTransitionState.Opening
        || _navigationMenuTransitionState == NavigationMenuTransitionState.OpenInteractive;

    private void FreezeNavigationMenuItemsCurrentState(IReadOnlyList<NavigationMenuAnimationItem> menuItems = null)
    {
        menuItems ??= _navigationMenuAnimationItems ?? ResolveNavigationMenuAnimationItems();
        foreach (NavigationMenuAnimationItem menuItem in menuItems)
        {
            if (menuItem.AnimatedContent != null)
            {
                FreezeNavigationMenuItemTransformState(menuItem.AnimatedContent);
            }
            if (menuItem.AnimatedTextElements != null)
            {
                foreach (FrameworkElement textElement in menuItem.AnimatedTextElements)
                {
                    FreezeNavigationMenuTextState(textElement);
                }
            }
            if (menuItem.HoverSurface != null)
            {
                menuItem.HoverSurface.BeginAnimation(UIElement.OpacityProperty, null);
            }
        }
    }

    private void FreezeNavigationMenuItemTransformState(FrameworkElement animatedContent)
    {
        ScaleTransform scaleTransform = null;
        TranslateTransform translateTransform = null;
        EnsureNavigationMenuItemTransforms(animatedContent, ref scaleTransform, ref translateTransform);
        double currentOpacity = animatedContent.Opacity;
        double currentScaleX = scaleTransform?.ScaleX ?? 1.0;
        double currentScaleY = scaleTransform?.ScaleY ?? 1.0;
        double currentTranslateY = translateTransform?.Y ?? 0.0;
        animatedContent.BeginAnimation(UIElement.OpacityProperty, null);
        scaleTransform?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scaleTransform?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        translateTransform?.BeginAnimation(TranslateTransform.YProperty, null);
        animatedContent.Opacity = currentOpacity;
        if (scaleTransform != null)
        {
            scaleTransform.ScaleX = currentScaleX;
            scaleTransform.ScaleY = currentScaleY;
        }
        if (translateTransform != null)
        {
            translateTransform.Y = currentTranslateY;
        }
    }

    private void FreezeNavigationMenuTextState(FrameworkElement textElement)
    {
        TranslateTransform translateTransform = EnsureNavigationMenuTextTransform(textElement);
        double currentOpacity = textElement.Opacity;
        double currentTranslateY = translateTransform.Y;
        textElement.BeginAnimation(UIElement.OpacityProperty, null);
        translateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        textElement.Opacity = currentOpacity;
        translateTransform.Y = currentTranslateY;
    }

    private void SetNavigationMenuItemsState(double scaleValue, double translateY, IReadOnlyList<NavigationMenuAnimationItem> menuItems)
    {
        if (menuItems == null)
        {
            return;
        }
        foreach (NavigationMenuAnimationItem menuItem in menuItems)
        {
            ApplyNavigationMenuItemState(menuItem.AnimatedContent, scaleValue, translateY);
        }
    }

    private void SetNavigationMenuItemsState(double scaleValue, double translateY, IEnumerable<Button> menuButtons = null)
    {
        if (NavigationMenuItemsControl == null)
        {
            return;
        }
        IReadOnlyList<NavigationMenuAnimationItem> resolvedMenuItems = ResolveNavigationMenuAnimationItems(menuButtons);
        foreach (NavigationMenuAnimationItem menuItem in resolvedMenuItems)
        {
            ApplyNavigationMenuItemState(menuItem.AnimatedContent, scaleValue, translateY);
        }
    }

    private void ApplyNavigationMenuItemState(FrameworkElement animatedContent, double scaleValue, double translateY)
    {
        if (animatedContent != null)
        {
            ScaleTransform scaleTransform = null;
            TranslateTransform translateTransform = null;
            EnsureNavigationMenuItemTransforms(animatedContent, ref scaleTransform, ref translateTransform);
            animatedContent.BeginAnimation(UIElement.OpacityProperty, null);
            scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            translateTransform.BeginAnimation(TranslateTransform.YProperty, null);
            animatedContent.Opacity = 1.0;
            scaleTransform.ScaleX = scaleValue;
            scaleTransform.ScaleY = scaleValue;
            translateTransform.Y = translateY;
        }
    }

    private Border ResolveNavigationMenuHoverSurface(Button menuButton)
    {
        return _navigationMenuPartsCache.Resolve(menuButton)?.HoverSurface;
    }

    private void AnimateNavigationMenuHoverSurface(NavigationMenuAnimationItem menuItem, double targetOpacity)
    {
        if (menuItem == null)
        {
            return;
        }
        AnimateNavigationMenuHoverSurface(menuItem.Button, menuItem.HoverSurface, targetOpacity);
    }

    private void AnimateNavigationMenuHoverSurface(Button menuButton, double targetOpacity)
    {
        AnimateNavigationMenuHoverSurface(menuButton, ResolveNavigationMenuHoverSurface(menuButton), targetOpacity);
    }

    private void AnimateNavigationMenuHoverSurface(Button menuButton, Border border, double targetOpacity)
    {
        if (border != null)
        {
            if (targetOpacity <= 0.0 && ShouldKeepNavigationMenuHoverSurfaceVisible(menuButton))
            {
                targetOpacity = 1.0;
            }
            Duration duration = ResolveDurationResource("MotionMediumDuration", 200.0);
            IEasingFunction easingFunction = ResolveEasingFunctionResource("MotionInOutEase");
            NavigationMenuAnimator.AnimateOpacity(border, targetOpacity, duration, easingFunction);
        }
    }

    private void SetNavigationMenuHoverSurfaceState(double targetOpacity, IReadOnlyList<NavigationMenuAnimationItem> menuItems)
    {
        if (menuItems == null)
        {
            return;
        }
        foreach (NavigationMenuAnimationItem menuItem in menuItems)
        {
            ApplyNavigationMenuHoverSurfaceState(
                ResolveNavigationMenuHoverSurfaceTargetOpacity(targetOpacity, menuItem.Button),
                menuItem.HoverSurface);
        }
    }

    private void SetNavigationMenuHoverSurfaceState(double targetOpacity, IEnumerable<Button> menuButtons = null)
    {
        if (NavigationMenuItemsControl == null)
        {
            return;
        }
        IReadOnlyList<NavigationMenuAnimationItem> resolvedMenuItems = ResolveNavigationMenuAnimationItems(menuButtons);
        foreach (NavigationMenuAnimationItem menuItem in resolvedMenuItems)
        {
            ApplyNavigationMenuHoverSurfaceState(
                ResolveNavigationMenuHoverSurfaceTargetOpacity(targetOpacity, menuItem.Button),
                menuItem.HoverSurface);
        }
    }

    private double ResolveNavigationMenuHoverSurfaceTargetOpacity(double targetOpacity, Button menuButton)
    {
        if (targetOpacity <= 0.0 && ShouldKeepNavigationMenuHoverSurfaceVisible(menuButton))
        {
            return 1.0;
        }
        return targetOpacity;
    }

    private static void ApplyNavigationMenuHoverSurfaceState(double targetOpacity, Border hoverSurface)
    {
        if (hoverSurface != null)
        {
            hoverSurface.BeginAnimation(UIElement.OpacityProperty, null);
            hoverSurface.Opacity = targetOpacity;
        }
    }

    private void SyncNavigationMenuHoverSurfaces(IReadOnlyList<NavigationMenuAnimationItem> menuItems)
    {
        if (menuItems == null)
        {
            return;
        }
        foreach (NavigationMenuAnimationItem menuItem in menuItems)
        {
            double targetOpacity = menuItem.Button != null && (menuItem.Button.IsMouseOver || ShouldKeepNavigationMenuHoverSurfaceVisible(menuItem.Button)) ? 1.0 : 0.0;
            ApplyNavigationMenuHoverSurfaceState(targetOpacity, menuItem.HoverSurface);
        }
    }

    private void SyncNavigationMenuHoverSurfaces(IEnumerable<Button> menuButtons = null)
    {
        if (NavigationMenuItemsControl == null)
        {
            return;
        }
        IReadOnlyList<NavigationMenuAnimationItem> resolvedMenuItems = ResolveNavigationMenuAnimationItems(menuButtons);
        foreach (NavigationMenuAnimationItem menuItem in resolvedMenuItems)
        {
            double targetOpacity = menuItem.Button != null && (menuItem.Button.IsMouseOver || ShouldKeepNavigationMenuHoverSurfaceVisible(menuItem.Button)) ? 1.0 : 0.0;
            ApplyNavigationMenuHoverSurfaceState(targetOpacity, menuItem.HoverSurface);
        }
    }

    private void SetNavigationMenuTextState(bool isVisible, IReadOnlyList<NavigationMenuAnimationItem> menuItems)
    {
        if (menuItems == null)
        {
            return;
        }
        foreach (NavigationMenuAnimationItem menuItem in menuItems)
        {
            ApplyNavigationMenuTextState(isVisible, menuItem.AnimatedTextElements);
        }
    }

    private void SetNavigationMenuTextState(bool isVisible, IEnumerable<Button> menuButtons = null)
    {
        if (NavigationMenuItemsControl == null)
        {
            return;
        }
        IReadOnlyList<NavigationMenuAnimationItem> resolvedMenuItems = ResolveNavigationMenuAnimationItems(menuButtons);
        foreach (NavigationMenuAnimationItem menuItem in resolvedMenuItems)
        {
            ApplyNavigationMenuTextState(isVisible, menuItem.AnimatedTextElements);
        }
    }

    private void ApplyNavigationMenuTextState(bool isVisible, IEnumerable<FrameworkElement> animatedTextElements)
    {
        if (animatedTextElements == null)
        {
            return;
        }
        foreach (FrameworkElement textElement in animatedTextElements)
        {
            TranslateTransform translateTransform = EnsureNavigationMenuTextTransform(textElement);
            textElement.BeginAnimation(UIElement.OpacityProperty, null);
            textElement.Opacity = isVisible ? ResolveNavigationMenuTextVisibleOpacity(textElement) : 0.0;
            translateTransform.BeginAnimation(TranslateTransform.YProperty, null);
            translateTransform.Y = isVisible ? 0.0 : ResolveNavigationMenuTextHiddenOffset(textElement);
        }
    }

    private static double ResolveNavigationMenuTextVisibleOpacity(FrameworkElement textElement)
    {
        if (textElement == null)
        {
            return 1.0;
        }
        string name = textElement.Name;
        if (string.Equals(name, "MenuIndexTextBlock", StringComparison.Ordinal) || string.Equals(name, "MenuMetaTextBlock", StringComparison.Ordinal))
        {
            return 0.62;
        }
        return 1.0;
    }

    private TranslateTransform EnsureNavigationMenuTextTransform(FrameworkElement textElement)
    {
        if (textElement == null)
        {
            return new TranslateTransform();
        }
        TranslateTransform translateTransform = textElement.RenderTransform as TranslateTransform;
        if (translateTransform != null && !translateTransform.IsFrozen)
        {
            return translateTransform;
        }
        double offsetX = 0.0;
        double offsetY = 0.0;
        if (translateTransform != null)
        {
            offsetX = translateTransform.X;
            offsetY = translateTransform.Y;
        }
        return (TranslateTransform)(textElement.RenderTransform = new TranslateTransform(offsetX, offsetY));
    }

    private double ResolveNavigationMenuTextHiddenOffset(FrameworkElement textElement)
    {
        if (textElement == null)
        {
            return -76.0;
        }
        double actualHeight = textElement.ActualHeight;
        Size desiredSize = textElement.DesiredSize;
        double elementHeight = Math.Max(actualHeight, desiredSize.Height);
        if (elementHeight <= 0.0 && textElement is TextBlock textBlock)
        {
            elementHeight = Math.Max(textBlock.FontSize * 0.64, textBlock.FontSize);
        }
        if (elementHeight <= 0.0)
        {
            elementHeight = 76.0;
        }
        return 0.0 - Math.Ceiling(elementHeight * 1.02);
    }

    private void RememberNavigationMenuClosingHighlight(Button menuButton)
    {
        AppPageKey? appPageKey = ResolveNavigationMenuPageKey(menuButton);
        if (appPageKey.HasValue)
        {
            _navigationMenuClosingHighlightPageKey = appPageKey.Value;
        }
    }

    private void ClearNavigationMenuClosingHighlight()
    {
        _navigationMenuClosingHighlightPageKey = null;
    }

    private AppPageKey? ResolveNavigationMenuPageKey(Button menuButton)
    {
        return menuButton?.CommandParameter is AppPageDescriptor appPageDescriptor ? appPageDescriptor.PageKey : null;
    }

    private bool ShouldKeepNavigationMenuHoverSurfaceVisible(Button menuButton)
    {
        if (menuButton == null || !_navigationMenuClosingHighlightPageKey.HasValue)
        {
            return false;
        }
        AppPageKey? appPageKey = ResolveNavigationMenuPageKey(menuButton);
        return appPageKey.HasValue && appPageKey.Value == _navigationMenuClosingHighlightPageKey.Value;
    }

    private void EnsureNavigationMenuItemTransforms(FrameworkElement element, ref ScaleTransform scaleTransform, ref TranslateTransform translateTransform)
    {
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        if (element.RenderTransform is TransformGroup transformGroup && transformGroup.Children.Count >= 2 && transformGroup.Children[0] is ScaleTransform && transformGroup.Children[1] is TranslateTransform)
        {
            scaleTransform = (ScaleTransform)transformGroup.Children[0];
            translateTransform = (TranslateTransform)transformGroup.Children[1];
            return;
        }
        double scaleX = 1.0;
        double scaleY = 1.0;
        double offsetX = 0.0;
        double offsetY = 0.0;
        ScaleTransform existingScaleTransform = element.RenderTransform as ScaleTransform;
        TranslateTransform existingTranslateTransform = element.RenderTransform as TranslateTransform;
        if (existingScaleTransform != null)
        {
            scaleX = existingScaleTransform.ScaleX;
            scaleY = existingScaleTransform.ScaleY;
        }
        if (existingTranslateTransform != null)
        {
            offsetX = existingTranslateTransform.X;
            offsetY = existingTranslateTransform.Y;
        }
        scaleTransform = new ScaleTransform(scaleX, scaleY);
        translateTransform = new TranslateTransform(offsetX, offsetY);
        TransformGroup navigationTransformGroup = new TransformGroup();
        navigationTransformGroup.Children.Add(scaleTransform);
        navigationTransformGroup.Children.Add(translateTransform);
        element.RenderTransform = navigationTransformGroup;
    }

    private IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        return NavigationMotion.FindVisualChildren<T>(root);
    }

    private T FindNamedDescendant<T>(DependencyObject root, string elementName) where T : FrameworkElement
    {
        return NavigationMotion.FindNamedDescendant<T>(root, elementName);
    }

    private FrameworkElement ResolveCaptureLockSurface()
    {
        if (CurrentPageHost == null)
        {
            return null;
        }
        CurrentPageHost.ApplyTemplate();
        return FindCaptureSurfaceHost(CurrentPageHost)?.CaptureLockSurface;
    }

    private ICaptureSurfaceHost FindCaptureSurfaceHost(DependencyObject root)
    {
        if (root == null)
        {
            return null;
        }
        if (root is ICaptureSurfaceHost result)
        {
            return result;
        }
        int childrenCount = VisualTreeHelper.GetChildrenCount(root);
        for (int childIndex = 0; childIndex < childrenCount; childIndex++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, childIndex);
            ICaptureSurfaceHost captureSurfaceHost = FindCaptureSurfaceHost(child);
            if (captureSurfaceHost != null)
            {
                return captureSurfaceHost;
            }
        }
        return null;
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (base.WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
        UpdateWindowControlGlyph();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void UpdateWindowControlGlyph()
    {
        if (MaximizeRestoreGlyph != null)
        {
            MaximizeRestoreGlyph.Text = ((base.WindowState == WindowState.Maximized) ? '\ue923' : '\ue922').ToString();
        }
    }

    private void ApplyWindowChromeTheme()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (!(handle == IntPtr.Zero))
        {
            bool isDarkTheme = _themeManager.CurrentTheme == AppTheme.Dark;
            Color titleBarBackgroundColor = ResolveColorResource("GlassTitleBarBackgroundColor", ResolveColorResource("WindowBackgroundColor", isDarkTheme ? Colors.Black : Colors.White));
            Color titleBarTextColor = ResolveColorResource("TextStrongColor", isDarkTheme ? Colors.White : Colors.Black);
            Color titleBarBorderColor = ResolveColorResource("WindowOuterBorderColor", isDarkTheme ? Colors.White : Colors.Black);
            NativeMethods.TrySetImmersiveDarkMode(handle, isDarkTheme);
            NativeMethods.TrySetSystemBackdropType(handle, 2);
            NativeMethods.TrySetWindowColorAttribute(handle, 35, ToColorRef(titleBarBackgroundColor));
            NativeMethods.TrySetWindowColorAttribute(handle, 36, ToColorRef(titleBarTextColor));
            NativeMethods.TrySetWindowColorAttribute(handle, 34, ToColorRef(titleBarBorderColor));
        }
    }

    private void ApplyWindowCornerPreference()
    {
        WindowChrome windowChrome = WindowChrome.GetWindowChrome(this);
        if (windowChrome != null)
        {
            windowChrome.CornerRadius = SquareWindowCornerRadius;
        }
        nint handle = new WindowInteropHelper(this).Handle;
        if (!(handle == IntPtr.Zero))
        {
            NativeMethods.TrySetWindowCornerPreference(handle, 1);
        }
    }

    private Color ResolveColorResource(string resourceKey, Color fallback)
    {
        object resource = TryFindResource(resourceKey);
        if (resource is Color)
        {
            return (Color)resource;
        }
        if (!(resource is SolidColorBrush { Color: var color }))
        {
            return fallback;
        }
        return color;
    }

    private double ResolveDoubleResource(string resourceKey, double fallback)
    {
        object resource = TryFindResource(resourceKey);
        if (resource is double)
        {
            return (double)resource;
        }
        return fallback;
    }

    private double ResolveGridLengthResource(string resourceKey, double fallback)
    {
        object resource = TryFindResource(resourceKey);
        if (resource is GridLength)
        {
            GridLength gridLength = (GridLength)resource;
            if (gridLength.IsAbsolute)
            {
                return gridLength.Value;
            }
        }
        return fallback;
    }

    private Thickness ResolveThicknessResource(string resourceKey)
    {
        object resource = TryFindResource(resourceKey);
        if (resource is Thickness)
        {
            return (Thickness)resource;
        }
        return new Thickness(0.0);
    }

    private Duration ResolveDurationResource(string resourceKey, double fallbackMilliseconds)
    {
        object resource = TryFindResource(resourceKey);
        if (resource is Duration)
        {
            return (Duration)resource;
        }
        return new Duration(TimeSpan.FromMilliseconds(fallbackMilliseconds));
    }

    private IEasingFunction ResolveEasingFunctionResource(string resourceKey)
    {
        return TryFindResource(resourceKey) as IEasingFunction;
    }

    private static int ToColorRef(Color color)
    {
        return color.R | (color.G << 8) | (color.B << 16);
    }

    private void UpdateRenderingDiagnostics()
    {
        string diagnosticsSummary = TextRenderingDiagnostics.BuildSummary(this);
        if (!StringComparer.Ordinal.Equals(diagnosticsSummary, _lastRenderingDiagnostics))
        {
            _lastRenderingDiagnostics = diagnosticsSummary;
            Trace.WriteLine(diagnosticsSummary);
        }
    }

    private void UpdateDisplayEnvironment()
    {
        UpdateRenderingDiagnostics();
        UpdateDisplayRefreshRateAwarePage(forceRefreshRateQuery: true);
    }

    private void UpdateDisplayRefreshRateAwarePageIfMonitorChanged()
    {
        nint monitorHandle = GetCurrentMonitorHandle();
        if (monitorHandle == IntPtr.Zero || monitorHandle == _lastDisplayRefreshRateMonitorHandle)
        {
            return;
        }

        UpdateDisplayRefreshRateAwarePage(forceRefreshRateQuery: true, knownMonitorHandle: monitorHandle);
    }

    private void UpdateDisplayRefreshRateAwarePage(bool forceNotify = false, bool forceRefreshRateQuery = false, nint knownMonitorHandle = default)
    {
        nint monitorHandle = knownMonitorHandle != IntPtr.Zero ? knownMonitorHandle : GetCurrentMonitorHandle();
        if (!forceRefreshRateQuery && !forceNotify && monitorHandle != IntPtr.Zero && monitorHandle == _lastDisplayRefreshRateMonitorHandle)
        {
            return;
        }

        double displayRefreshRateHz = 0.0;
        double? refreshRateHz = NativeMethods.TryGetWindowDisplayRefreshRate(new WindowInteropHelper(this).Handle, ref displayRefreshRateHz)
            ? displayRefreshRateHz
            : null;

        if (!forceNotify && AreEquivalentRefreshRates(_lastDisplayRefreshRateHz, refreshRateHz))
        {
            return;
        }

        _lastDisplayRefreshRateMonitorHandle = monitorHandle;
        _lastDisplayRefreshRateHz = refreshRateHz;
        if (_shellViewModel?.CurrentPage is IDisplayRefreshRateAwarePageViewModel displayRefreshRateAwarePage)
        {
            displayRefreshRateAwarePage.SetHostDisplayRefreshRate(refreshRateHz);
        }
    }

    private nint GetCurrentMonitorHandle()
    {
        nint windowHandle = new WindowInteropHelper(this).Handle;
        return windowHandle == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.MonitorFromWindow(windowHandle, NativeMethods.MONITOR_DEFAULTTONEAREST);
    }

    private static bool AreEquivalentRefreshRates(double? left, double? right)
    {
        if (!left.HasValue || !right.HasValue)
        {
            return left.HasValue == right.HasValue;
        }

        return Math.Abs(left.Value - right.Value) < 0.25;
    }
}


