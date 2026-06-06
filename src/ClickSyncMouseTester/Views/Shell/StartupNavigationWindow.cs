#define TRACE
using ClickSyncMouseTester.Navigation;
using ClickSyncMouseTester.Services;
using ClickSyncMouseTester.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClickSyncMouseTester.Views.Shell;

[SupportedOSPlatform("windows")]
public partial class StartupNavigationWindow : Window
{
    private const int StartupCarrierDeferredCloseDelayMilliseconds = 600;

    private readonly ShellViewModel _shellViewModel;

    private readonly MainWindow _mainWindow;

    private readonly TimeZoneInfo _navigationLocalTimeZone;

    private readonly DispatcherTimer _navigationLocalClockTimer;

    private bool _hasStartedPresentation;

    private int _navigationMenuAnimationVersion;

    private bool _isPreparingNavigationCurtainOpen;

    private bool _isStartupInteractionReady;

    private bool _hasStartupMenuTextEntered;

    private Task _startupCloseTransitionTask;

    private bool _hasStartedDeferredClose;

    private bool _hasHandedOffToMainWindow;

    private AppPageKey? _navigationMenuClosingHighlightPageKey;

    private List<NavigationMenuAnimationItem> _navigationMenuAnimationItems;

    private NavigationMenuPartsCache _navigationMenuPartsCache;

    private bool _isNavigationMenuLayoutInvalidated = true;

    private double _lastNavigationCurtainLayoutWidth = double.NaN;

    private double _lastNavigationCurtainLayoutHeight = double.NaN;

    private double _lastNavigationMenuScrollViewerWidth = double.NaN;

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

    public StartupNavigationWindow(ShellViewModel shellViewModel, MainWindow mainWindow)
    {
        _navigationLocalTimeZone = TimeZoneInfo.Local;
        if (shellViewModel == null)
        {
            throw new ArgumentNullException(nameof(shellViewModel));
        }
        if (mainWindow == null)
        {
            throw new ArgumentNullException(nameof(mainWindow));
        }
        InitializeComponent();
        _navigationMenuPartsCache = new NavigationMenuPartsCache(NavigationMenuItemsControl);
        _shellViewModel = shellViewModel;
        _mainWindow = mainWindow;
        base.DataContext = _shellViewModel;
        _shellViewModel.PropertyChanged += OnShellViewModelPropertyChanged;
        _shellViewModel.Pages.CollectionChanged += OnNavigationPagesChanged;
        _navigationLocalClockTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1L)
        };
        _navigationLocalClockTimer.Tick += OnNavigationLocalClockTick;
        UpdateNavigationLocalClockText();
        _navigationLocalClockTimer.Start();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        UiPerformanceProbe.CompleteStartupToFirstFrame(this);
        if (!_hasStartedPresentation)
        {
            _hasStartedPresentation = true;
            Dispatcher.BeginInvoke(new Action(BeginStartupPresentation), DispatcherPriority.Loaded);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _shellViewModel.PropertyChanged -= OnShellViewModelPropertyChanged;
        _shellViewModel.Pages.CollectionChanged -= OnNavigationPagesChanged;
        _navigationLocalClockTimer.Tick -= OnNavigationLocalClockTick;
        _navigationLocalClockTimer.Stop();
        if (!_hasHandedOffToMainWindow && _mainWindow != null)
        {
            _mainWindow.Close();
        }
        base.OnClosed(e);
    }

    private async void BeginStartupPresentation()
    {
        using IDisposable startupPresentationScope = UiPerformanceProbe.BeginStage("StartupNavigationWindow.BeginStartupPresentation", this);
        try
        {
            _navigationMenuAnimationVersion++;
            int navigationMenuAnimationVersion = _navigationMenuAnimationVersion;
            List<NavigationMenuAnimationItem> menuItems = null;
            await Dispatcher.InvokeAsync((Action)(() =>
            {
                if (StartupInteractionRoot != null)
                {
                    StartupInteractionRoot.IsHitTestVisible = false;
                }
                _isStartupInteractionReady = false;
                PrepareNavigationOpenLayoutForAnimation();
                UpdateNavigationCurtainClip(0.0);
                menuItems = ResolveNavigationMenuAnimationItems(updateLayout: false);
                _navigationMenuAnimationItems = menuItems;
                _hasStartupMenuTextEntered = false;
                ResetNavigationMenuItemsForNextOpen(menuItems);
            }), DispatcherPriority.Loaded);
            Task curtainAnimationTask = AnimateNavigationCurtainAsync(isOpening: true);
            Task menuItemsAnimationTask = AnimateNavigationMenuItemsOpenAfterFirstCurtainFrameAsync(menuItems);
            _ = CompleteStartupPresentationAnimationsAsync(menuItemsAnimationTask, navigationMenuAnimationVersion);
            await curtainAnimationTask;
            if (navigationMenuAnimationVersion != _navigationMenuAnimationVersion || !IsLoaded || _startupCloseTransitionTask != null)
            {
                return;
            }
            await Dispatcher.InvokeAsync((Action)(() =>
            {
                ReleaseStartupWindowInteractiveState();
                _isStartupInteractionReady = true;
                NavigationCurtainSurface.IsHitTestVisible = true;
                if (StartupInteractionRoot != null)
                {
                    StartupInteractionRoot.IsHitTestVisible = true;
                }
                Activate();
                Focus();
            }), DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Startup curtain presentation failed: {ex}");
            RecoverFromStartupPresentationFailure();
        }
    }

    private void ReleaseStartupWindowInteractiveState()
    {
        Topmost = false;
    }

    private async Task CompleteStartupPresentationAnimationsAsync(Task menuItemsAnimationTask, int animationVersion)
    {
        try
        {
            using (UiPerformanceProbe.BeginStage("StartupNavigationWindow.MenuTextEnter", this))
            {
                await menuItemsAnimationTask;
            }
            if (animationVersion == _navigationMenuAnimationVersion && IsLoaded && _startupCloseTransitionTask == null)
            {
                _hasStartupMenuTextEntered = true;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Startup menu text animation failed: {ex}");
        }
    }

    private void RecoverFromStartupPresentationFailure()
    {
        try
        {
            if (StartupInteractionRoot != null)
            {
                StartupInteractionRoot.IsHitTestVisible = false;
            }
            if (NavigationCurtainSurface != null)
            {
                NavigationCurtainSurface.IsHitTestVisible = false;
            }
            _isStartupInteractionReady = false;
            AlignMainWindowToStartupWindow();
            _hasHandedOffToMainWindow = true;
            CompleteMainWindowStartupHandoff();
            Close();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Startup presentation recovery failed: {ex}");
            System.Windows.Application.Current?.Shutdown();
        }
    }

    private void OnNavigationLocalClockTick(object sender, EventArgs e)
    {
        UpdateNavigationLocalClockText();
    }

    private void OnShellViewModelPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e == null || string.IsNullOrEmpty(e.PropertyName)
            || string.Equals(e.PropertyName, "CurrentPageTitle", StringComparison.Ordinal)
            || string.Equals(e.PropertyName, "CurrentPageSummary", StringComparison.Ordinal)
            || string.Equals(e.PropertyName, "IsChineseLanguageCurrent", StringComparison.Ordinal)
            || string.Equals(e.PropertyName, "IsEnglishLanguageCurrent", StringComparison.Ordinal))
        {
            InvalidateNavigationMenuLayoutCache();
        }
    }

    private void OnNavigationPagesChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateNavigationMenuPartsCache();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_shellViewModel != null && _shellViewModel.IsNavigationMenuOpen && e.Key == Key.Return)
        {
            e.Handled = true;
            if (_isStartupInteractionReady)
            {
                ClearNavigationMenuClosingHighlight();
                BeginStartupCloseTransition(() =>
                {
                    _shellViewModel.CloseNavigationMenu();
                });
            }
        }
        else
        {
            base.OnPreviewKeyDown(e);
        }
    }

    private void NavigationCurtainCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_shellViewModel != null && _shellViewModel.IsNavigationMenuOpen)
        {
            e.Handled = true;
            if (_isStartupInteractionReady)
            {
                ClearNavigationMenuClosingHighlight();
                BeginStartupCloseTransition(() =>
                {
                    _shellViewModel.CloseNavigationMenu();
                });
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

    private void StartupWindow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isStartupInteractionReady || _startupCloseTransitionTask != null || e == null || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }
        if (!CanBeginStartupWindowDrag(e))
        {
            return;
        }
        try
        {
            e.Handled = true;
            DragMove();
        }
        catch (InvalidOperationException ex)
        {
            Trace.WriteLine($"Startup window drag failed: {ex.Message}");
        }
    }

    private bool CanBeginStartupWindowDrag(MouseButtonEventArgs e)
    {
        if (NavigationCurtainSurface == null)
        {
            return false;
        }
        DependencyObject originalSource = e.OriginalSource as DependencyObject;
        if (IsWithinInteractiveStartupElement(originalSource))
        {
            return false;
        }
        Point position = e.GetPosition(this);
        double captionHeight = ResolveDoubleResource("Layout.Chrome.CaptionHeight", 44.0);
        double startupMenuHeaderBottom = ResolveStartupMenuHeaderBottom();
        double draggableHeight = Math.Max(captionHeight, startupMenuHeaderBottom);
        return position.Y >= 0.0 && position.Y <= draggableHeight;
    }

    private double ResolveStartupMenuHeaderBottom()
    {
        if (NavigationCurtainLayoutRoot == null || NavigationCurtainTopGuideRow == null)
        {
            return ResolveDoubleResource("Layout.Chrome.CaptionHeight", 44.0);
        }
        Point layoutOrigin = NavigationCurtainLayoutRoot.TranslatePoint(new Point(0.0, 0.0), this);
        double topGuideHeight = NavigationCurtainTopGuideRow.ActualHeight;
        if (topGuideHeight <= 0.0 && NavigationCurtainTopGuideRow.Height.IsAbsolute)
        {
            topGuideHeight = NavigationCurtainTopGuideRow.Height.Value;
        }
        return Math.Max(0.0, layoutOrigin.Y + Math.Max(0.0, topGuideHeight));
    }

    private static bool IsWithinInteractiveStartupElement(DependencyObject source)
    {
        DependencyObject current = source;
        while (current != null)
        {
            if (current is Button || current is ScrollViewer || current is ItemsControl || current is TextBox)
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void NavigationMenuButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isStartupInteractionReady)
        {
            if (e != null)
            {
                e.Handled = true;
            }
            return;
        }
        Button button = sender as Button;
        AppPageDescriptor appPageDescriptor = ResolveNavigationMenuButtonPageDescriptor(button);
        if (appPageDescriptor == null || _shellViewModel == null)
        {
            return;
        }
        e.Handled = true;
        RememberNavigationMenuClosingHighlight(button);
        SetNavigationMenuHoverSurfaceState(1.0, new[] { button });
        BeginStartupCloseTransition(() =>
        {
            if (_shellViewModel.NavigateToPageCommand != null && _shellViewModel.NavigateToPageCommand.CanExecute(appPageDescriptor))
            {
                _shellViewModel.NavigateToPageCommand.Execute(appPageDescriptor);
            }
        });
    }

    private void NavigationMenuButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e == null || (e.Key != Key.Return && e.Key != Key.Space))
        {
            return;
        }
        if (!_isStartupInteractionReady)
        {
            e.Handled = true;
            return;
        }
        Button button = sender as Button;
        AppPageDescriptor appPageDescriptor = ResolveNavigationMenuButtonPageDescriptor(button);
        if (appPageDescriptor == null || _shellViewModel == null)
        {
            return;
        }
        e.Handled = true;
        RememberNavigationMenuClosingHighlight(button);
        SetNavigationMenuHoverSurfaceState(1.0, new[] { button });
        BeginStartupCloseTransition(() =>
        {
            if (_shellViewModel.NavigateToPageCommand != null && _shellViewModel.NavigateToPageCommand.CanExecute(appPageDescriptor))
            {
                _shellViewModel.NavigateToPageCommand.Execute(appPageDescriptor);
            }
        });
    }

    private void NavigationMenuButton_MouseEnter(object sender, MouseEventArgs e)
    {
        SetNavigationMenuHoverSurfaceState(1.0, new[] { sender as Button });
    }

    private void NavigationMenuButton_MouseLeave(object sender, MouseEventArgs e)
    {
        SetNavigationMenuHoverSurfaceState(0.0, new[] { sender as Button });
    }

    private void BeginStartupCloseTransition(Action action)
    {
        if (_startupCloseTransitionTask == null && _mainWindow != null)
        {
            _startupCloseTransitionTask = RunStartupCloseTransitionAsync(action);
        }
    }

    private async Task RunStartupCloseTransitionAsync(Action action)
    {
        using IDisposable closeTransitionScope = UiPerformanceProbe.BeginStage("StartupNavigationWindow.RunStartupCloseTransition", this);
        try
        {
            _isStartupInteractionReady = false;
            Topmost = true;
            if (StartupInteractionRoot != null)
            {
                StartupInteractionRoot.IsHitTestVisible = false;
            }
            if (NavigationCurtainSurface != null)
            {
                NavigationCurtainSurface.IsHitTestVisible = false;
            }
            AlignMainWindowToStartupWindow();
            _hasHandedOffToMainWindow = true;
            UiPerformanceProbe.Mark("StartupHandoff.Begin", this);
            _mainWindow.SuppressNextNavigationMenuCloseAnimationForStartupHandoff();
            action?.Invoke();
            CompleteMainWindowStartupHandoff();
            _navigationMenuAnimationVersion++;
            int navigationMenuAnimationVersion = _navigationMenuAnimationVersion;
            if (_hasStartupMenuTextEntered)
            {
                SetNavigationMenuItemsVisibleState(_navigationMenuAnimationItems);
            }
            else
            {
                FreezeNavigationMenuItemsCurrentState(_navigationMenuAnimationItems);
            }
            Task curtainCloseTask = AnimateNavigationCurtainAsync(isOpening: false);
            await curtainCloseTask;
            HideStartupCarrierWindow();
            BeginDeferredStartupCarrierClose();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Startup close transition failed: {ex}");
            _mainWindow.ClearNavigationMenuCloseAnimationSuppressionForStartupHandoff();
            _hasHandedOffToMainWindow = true;
            CompleteMainWindowStartupHandoff();
            Close();
        }
    }

    private void HideStartupCarrierWindow()
    {
        Hide();
        base.Topmost = false;
        _navigationMenuAnimationItems = null;
        UiPerformanceProbe.Mark("StartupHandoff.StartupWindowHidden", this);
    }

    private void CompleteMainWindowStartupHandoff()
    {
        if (_mainWindow == null)
        {
            return;
        }
        AlignMainWindowToStartupWindow();
        _mainWindow.CompleteStartupMenuHandoffActivation();
        UiPerformanceProbe.Mark("StartupHandoff.MainWindowActivated", this);
    }

    private void BeginDeferredStartupCarrierClose()
    {
        if (!_hasStartedDeferredClose)
        {
            _hasStartedDeferredClose = true;
            CloseStartupCarrierDeferredAsync();
        }
    }

    private async void CloseStartupCarrierDeferredAsync()
    {
        try
        {
            await Task.Delay(StartupCarrierDeferredCloseDelayMilliseconds);
            Close();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Deferred startup carrier close failed: {ex}");
            Close();
        }
    }

    private void AlignMainWindowToStartupWindow()
    {
        if (_mainWindow != null)
        {
            double startupWindowWidth = Math.Max(ActualWidth, Width);
            double startupWindowHeight = Math.Max(ActualHeight, Height);
            if (startupWindowWidth > 0.0)
            {
                _mainWindow.Width = startupWindowWidth;
            }
            if (startupWindowHeight > 0.0)
            {
                _mainWindow.Height = startupWindowHeight;
            }
            _mainWindow.Left = Left;
            _mainWindow.Top = Top;
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
        if (!_isPreparingNavigationCurtainOpen)
        {
            if (NavigationCurtainSurface.Visibility != Visibility.Visible)
            {
                UpdateNavigationCurtainClip(0.0);
            }
            else
            {
                UpdateNavigationCurtainClip(NavigationCurtainSurface.ActualHeight);
            }
        }
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
                () => animationVersion == _navigationMenuAnimationVersion && IsLoaded);
        }
        finally
        {
            if (isOpening)
            {
                _isPreparingNavigationCurtainOpen = false;
            }
        }
    }

    private static AppPageDescriptor ResolveNavigationMenuButtonPageDescriptor(Button button)
    {
        return button?.CommandParameter as AppPageDescriptor ?? button?.DataContext as AppPageDescriptor;
    }

    private void RememberNavigationMenuClosingHighlight(Button menuButton)
    {
        AppPageDescriptor appPageDescriptor = ResolveNavigationMenuButtonPageDescriptor(menuButton);
        if (appPageDescriptor != null)
        {
            _navigationMenuClosingHighlightPageKey = appPageDescriptor.PageKey;
        }
    }

    private void ClearNavigationMenuClosingHighlight()
    {
        _navigationMenuClosingHighlightPageKey = null;
    }

    private bool ShouldKeepNavigationMenuHoverSurfaceVisible(Button menuButton)
    {
        if (menuButton == null || !_navigationMenuClosingHighlightPageKey.HasValue)
        {
            return false;
        }
        AppPageDescriptor appPageDescriptor = ResolveNavigationMenuButtonPageDescriptor(menuButton);
        return appPageDescriptor != null && appPageDescriptor.PageKey == _navigationMenuClosingHighlightPageKey.Value;
    }

    private async Task AnimateNavigationMenuItemsOpenAsync(IReadOnlyList<NavigationMenuAnimationItem> menuItems)
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
        Duration duration = ResolveDurationResource("MotionNavigationItemEnterDuration", 800.0);
        IEasingFunction easingFunction = ResolveEasingFunctionResource("MotionNavigationContentEnterEase");
        double baseDelayMilliseconds = ResolveDoubleResource("MotionNavigationItemEnterBaseDelayMs", 400.0);
        double staggerRangeMilliseconds = ResolveDoubleResource("MotionNavigationItemEnterStaggerRangeMs", 200.0);
        double staggerPower = ResolveDoubleResource("MotionNavigationItemEnterStaggerPower", 1.35);
        int animationVersion = _navigationMenuAnimationVersion;
        SetNavigationMenuItemsState(1.0, 0.0, menuItems);
        SetNavigationMenuTextState(isVisible: false, menuItems);
        SetNavigationMenuHoverSurfaceState(0.0, menuItems);
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
            () => animationVersion == _navigationMenuAnimationVersion && IsLoaded);
    }

    private async Task AnimateNavigationMenuItemsOpenAfterFirstCurtainFrameAsync(IReadOnlyList<NavigationMenuAnimationItem> menuItems)
    {
        await Dispatcher.InvokeAsync((Action)(() => { }), DispatcherPriority.Background);
        await AnimateNavigationMenuItemsOpenAsync(menuItems);
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
            else if (NavigationCurtainTopGuideRow.Height.IsAbsolute)
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
        SetNavigationMenuHoverSurfaceState(0.0, menuItems);
    }

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

    private Border ResolveNavigationMenuHoverSurface(Button menuButton)
    {
        return _navigationMenuPartsCache.Resolve(menuButton)?.HoverSurface;
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
}







