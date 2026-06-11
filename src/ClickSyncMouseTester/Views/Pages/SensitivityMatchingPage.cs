using ClickSyncMouseTester.Services;
using ClickSyncMouseTester.ViewModels.Pages;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClickSyncMouseTester.Views.Pages;

[SupportedOSPlatform("windows")]
public partial class SensitivityMatchingPage : UserControl
{
    private sealed class IntroGlyphAnchor
    {
        public string Character { get; set; }

        public Point Position { get; set; }

        public FontFamily FontFamily { get; set; }

        public double FontSize { get; set; }

        public FontStyle FontStyle { get; set; }

        public FontStretch FontStretch { get; set; }

        public FontWeight FontWeight { get; set; }

        public Brush Foreground { get; set; }

        public TextFormattingMode TextFormattingMode { get; set; }

        public TextRenderingMode TextRenderingMode { get; set; }

        public TextHintingMode TextHintingMode { get; set; }

        public string GroupKey { get; set; }

        public int CharacterIndex { get; set; }

        public int SequenceIndex { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }
    }

    private sealed class MeasureTransitionTextRenderingState
    {
        public bool HasLocalTextRenderingMode { get; set; }

        public TextRenderingMode TextRenderingMode { get; set; }

        public bool HasLocalTextHintingMode { get; set; }

        public TextHintingMode TextHintingMode { get; set; }

        public bool HasLocalClearTypeHint { get; set; }

        public ClearTypeHint ClearTypeHint { get; set; }

        public bool HasLocalSnapsToDevicePixels { get; set; }

        public bool SnapsToDevicePixels { get; set; }

        public bool HasLocalUseLayoutRounding { get; set; }

        public bool UseLayoutRounding { get; set; }
    }

    private sealed class MeasureTransitionCacheState
    {
        public bool HasLocalCacheMode { get; set; }

        public CacheMode CacheMode { get; set; }
    }

    private sealed class IntroMorphToken
    {
        public TextBlock Element { get; set; }

        public ScaleTransform Scale { get; set; }

        public TranslateTransform Translate { get; set; }

        public Point StartPosition { get; set; }

        public Point EndPosition { get; set; }

        public double StartScale { get; set; }

        public double EndScale { get; set; }

        public double EnterDelay { get; set; }

        public double MotionDuration { get; set; }

        public double ExitStart { get; set; }

        public double ExitEnd { get; set; }

        public bool IsPersistent { get; set; }

        public double RevealThreshold { get; set; }

        public double RevealStartSeconds { get; set; }

        public double RevealEndSeconds { get; set; }

        public string GroupKey { get; set; }

        public int CharacterIndex { get; set; }

        public double ExitProgressStart { get; set; } = double.NaN;

        public double ExitProgressEnd { get; set; } = double.NaN;
    }

    private enum MorphAnimationMode
    {
        None,
        IntroOpening,
        IntroToSetupTransition,
        SetupToIntroTransition
    }

    private const double IntroFadeInEndSeconds = 0.22;

    private const double IntroMorphStartSeconds = 0.64;

    private const double IntroMorphEndSeconds = 1.48;

    private const double IntroCanvasFadeStartSeconds = 1.5;

    private const double IntroCanvasFadeEndSeconds = 1.78;

    private const double IntroActionRevealStartSeconds = 1.5;

    private const double IntroActionRevealEndSeconds = 1.9;

    private const double IntroTotalDurationSeconds = 1.9;

    private const double IntroOpeningSafePadding = 34.0;

    private const double SetupTransitionMorphStartSeconds = 0.1;

    private const double SetupTransitionMorphEndSeconds = 1.32;

    private const double SetupTransitionTotalDurationSeconds = 1.74;

    private const double SetupTopLeftEnterDelayAdvanceSeconds = 0.01;

    private const double SetupTopLeftMotionDurationTrimSeconds = 0.065;

    private const double SetupTopLeftExitAdvanceSeconds = 0.04;

    private const double SetupTopLeftExitDurationTrimSeconds = 0.02;

    private const double SetupUtilityRevealStartSeconds = 0.82;

    private const double SetupUtilityRevealEndSeconds = 1.28;

    private const double SetupSourcePanelRevealStartSeconds = 0.98;

    private const double SetupSourcePanelRevealEndSeconds = 1.46;

    private const double SetupTargetPanelRevealStartSeconds = 1.02;

    private const double SetupTargetPanelRevealEndSeconds = 1.5;

    private const double SetupSourceInfoRevealStartSeconds = 1.08;

    private const double SetupSourceInfoRevealEndSeconds = 1.54;

    private const double SetupTargetInfoRevealStartSeconds = 1.12;

    private const double SetupTargetInfoRevealEndSeconds = 1.58;

    private const double SetupFooterRevealStartSeconds = 1.26;

    private const double SetupFooterRevealEndSeconds = 1.68;

    private const double SetupToIntroActionRevealStartSeconds = 1.18;

    private const double SetupToIntroActionRevealEndSeconds = 1.66;

    private const double SetupUtilityRevealOffsetY = 26.0;

    private const double SetupPanelRevealOffsetY = 24.0;

    private const double SetupInfoBandRevealOffsetY = 12.0;

    private const double SetupFooterRevealOffsetY = 14.0;

    private const double MeasureWatermarkRevealDurationSeconds = 0.8;

    private const double MeasureContentRevealDurationSeconds = 0.44;

    private const double MeasureWatermarkLeadOpacity = 0.18;

    private const double MeasureWatermarkSettledOpacity = 0.04;

    private const double MeasureWatermarkLeadScale = 1.1;

    private const double MeasureMinimumTransitionTravel = 36.0;

    private const double MeasureSetupFadeDurationSeconds = 0.36;

    private const double MeasureSetupFooterFadeDurationSeconds = 0.34;

    private const double MeasureUtilityFadeDurationSeconds = 0.26;

    private const double MeasureUtilityFadeOffset = 12.0;

    private const double MeasureSetupInfoBandRevealDurationSeconds = 0.28;

    private const double MeasureSetupInfoBandRevealBeginSeconds = 0.28;

    private const double MeasureSetupInfoBandRevealOffset = 14.0;

    private const double MeasureExitContentDurationSeconds = 0.34;

    private const double MeasureExitWatermarkBeginSeconds = 0.02;

    private const double MeasureExitRoundsBeginSeconds = 0.02;

    private const double MeasureExitFooterBeginSeconds = 0.06;

    private const double MeasureOverlayCrossfadeStartSeconds = 0.6;

    private const double MeasureOverlayFadeDurationSeconds = 0.16;

    private const double MeasureCenterSwapDurationSeconds = 0.3;

    private const double MeasureCenterSwapToResultBeginSeconds = 0.3;

    private const double MeasureCenterSwapToRoundBeginSeconds = 0.2;

    private const double MeasureCenterSwapOutgoingOffset = 10.0;

    private const double MeasureCenterSwapIncomingOffset = 14.0;

    private const string Opening1 = "Opening1";

    private const string Opening2 = "Opening2";

    private const string Opening3 = "Opening3";

    private const string Opening4 = "Opening4";

    private const string TopLeft = "TopLeft";

    private const string LeftLabel = "LeftLabel";

    private const string Connector = "Connector";

    private const string RightLabel = "RightLabel";

    private const string Bottom1 = "Bottom1";

    private const string Bottom2 = "Bottom2";

    private const string Bottom3 = "Bottom3";

    private const string RightPrimary = "RightPrimary";

    private SensitivityMatchingPageViewModel _observedViewModel;

    private Stopwatch _introClock;

    private TimeSpan? _introRenderingStartTime;

    private readonly List<IntroMorphToken> _openingTokens;

    private readonly List<IntroMorphToken> _targetTokens;

    private bool _isIntroAnimationActive;

    private bool _isRenderingHooked;

    private MorphAnimationMode _activeAnimationMode;

    private bool _isMeasurePresentationActive;

    private bool _isMeasureForwardTransitionActive;

    private bool _isMeasureBackTransitionActive;

    private bool _isMeasureResultCenterVisible;

    private bool _isNormalizingDpiText;

    private DispatcherTimer _introResizeRestartTimer;

    private DispatcherTimer _measureTransitionTimer;

    private Action _measureTransitionCompletion;

    private readonly Dictionary<DependencyObject, MeasureTransitionTextRenderingState> _animatedTextRenderingStates;

    private readonly Dictionary<DependencyObject, MeasureTransitionTextRenderingState> _measureTransitionTextRenderingStates;

    private readonly Dictionary<UIElement, MeasureTransitionCacheState> _measureTransitionCacheStates;

    private bool _isThemeSubscribed;
    public SensitivityMatchingPage()
    {
        _openingTokens = new List<IntroMorphToken>();
        _targetTokens = new List<IntroMorphToken>();
        _animatedTextRenderingStates = new Dictionary<DependencyObject, MeasureTransitionTextRenderingState>();
        _measureTransitionTextRenderingStates = new Dictionary<DependencyObject, MeasureTransitionTextRenderingState>();
        _measureTransitionCacheStates = new Dictionary<UIElement, MeasureTransitionCacheState>();
        _introResizeRestartTimer = new DispatcherTimer(DispatcherPriority.Loaded, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100.0)
        };
        _introResizeRestartTimer.Tick += IntroResizeRestartTimer_Tick;
        InitializeComponent();
        ResetIntroHeroVisuals();
        ResetMeasureVisuals();
    }

    private void SensitivityMatchingPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_isThemeSubscribed)
        {
            ThemeManager.Instance.ThemeChanged += OnThemeChanged;
            _isThemeSubscribed = true;
        }
        RefreshThemeSensitiveVisuals();
        UpdateViewModelSubscription(base.DataContext as SensitivityMatchingPageViewModel);
        SyncViewModelState();
    }

    private void SensitivityMatchingPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_isThemeSubscribed)
        {
            ThemeManager.Instance.ThemeChanged -= OnThemeChanged;
            _isThemeSubscribed = false;
        }
        if (base.DataContext is SensitivityMatchingPageViewModel sensitivityMatchingPageViewModel)
        {
            sensitivityMatchingPageViewModel.SetPageActive(isActive: false);
        }
        StopIntroAnimation();
        StopIntroResizeRestartTimer();
        ResetIntroHeroVisuals();
        ResetMeasureVisuals();
        StopMeasureTransitionTimer();
        _isIntroAnimationActive = false;
        _activeAnimationMode = MorphAnimationMode.None;
        _isMeasurePresentationActive = false;
        _isMeasureForwardTransitionActive = false;
        _isMeasureBackTransitionActive = false;
    }

    private void SensitivityMatchingPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SyncViewModelState();
    }

    private void OnThemeChanged(object sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RefreshThemeSensitiveVisuals();
        }), DispatcherPriority.Background);
    }

    private void RefreshThemeSensitiveVisuals()
    {
        RefreshIntroMorphTokenForegrounds();
    }

    private void RefreshIntroMorphTokenForegrounds()
    {
        Brush fallbackBrush = ((IntroHeroTopLeftWord != null) ? IntroHeroTopLeftWord.Foreground : null);
        Brush brush = ResolveBrushResource("TextStrongBrush", fallbackBrush);
        if (brush == null)
        {
            return;
        }
        foreach (IntroMorphToken openingToken in _openingTokens)
        {
            if (openingToken?.Element != null)
            {
                openingToken.Element.Foreground = brush;
            }
        }
        foreach (IntroMorphToken targetToken in _targetTokens)
        {
            if (targetToken?.Element != null)
            {
                targetToken.Element.Foreground = brush;
            }
        }
    }

    private Brush ResolveBrushResource(string resourceKey, Brush fallbackBrush)
    {
        return (TryFindResource(resourceKey) as Brush) ?? fallbackBrush;
    }

    private void SensitivityMatchingPage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SensitivityMatchingPageViewModel sensitivityMatchingPageViewModel)
        {
            sensitivityMatchingPageViewModel.SetPageActive(isActive: false);
        }
        UpdateViewModelSubscription(e.NewValue as SensitivityMatchingPageViewModel);
        SyncViewModelState();
    }

    private void SensitivityMatchingPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!base.IsLoaded)
        {
            return;
        }

        bool widthChanged = Math.Abs(e.NewSize.Width - e.PreviousSize.Width) >= 1.0;
        bool heightChanged = Math.Abs(e.NewSize.Height - e.PreviousSize.Height) >= 1.0;
        if (!widthChanged && !heightChanged)
        {
            return;
        }

        ScheduleIntroAnimationResizeRestart();
    }

    private void ScheduleIntroAnimationResizeRestart()
    {
        if (_introResizeRestartTimer == null)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SyncIntroAnimationState(ShouldRestartCurrentAnimation());
            }), DispatcherPriority.Loaded);
            return;
        }

        _introResizeRestartTimer.Stop();
        _introResizeRestartTimer.Start();
    }

    private void StopIntroResizeRestartTimer()
    {
        _introResizeRestartTimer?.Stop();
    }

    private void IntroResizeRestartTimer_Tick(object sender, EventArgs e)
    {
        StopIntroResizeRestartTimer();
        SyncIntroAnimationState(ShouldRestartCurrentAnimation());
    }

    private void IntroStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeAnimationMode != MorphAnimationMode.IntroToSetupTransition && _activeAnimationMode != MorphAnimationMode.SetupToIntroTransition && base.DataContext is SensitivityMatchingPageViewModel { IsIntroStep: not false })
        {
            if (_activeAnimationMode == MorphAnimationMode.IntroOpening)
            {
                FinalizeIntroAnimation();
            }
            _activeAnimationMode = MorphAnimationMode.IntroToSetupTransition;
            SyncIntroAnimationState(forceRestart: true);
        }
    }

    private void SetupBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeAnimationMode != MorphAnimationMode.IntroToSetupTransition && _activeAnimationMode != MorphAnimationMode.SetupToIntroTransition && base.DataContext is SensitivityMatchingPageViewModel sensitivityMatchingPageViewModel)
        {
            if (!sensitivityMatchingPageViewModel.IsSetupStep)
            {
                ExecuteBackCommand();
                return;
            }
            _activeAnimationMode = MorphAnimationMode.SetupToIntroTransition;
            SyncIntroAnimationState(forceRestart: true);
        }
    }

    private void ContinueToMeasureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeAnimationMode == MorphAnimationMode.IntroToSetupTransition || _activeAnimationMode == MorphAnimationMode.SetupToIntroTransition || _isMeasureForwardTransitionActive || _isMeasureBackTransitionActive || !(base.DataContext is SensitivityMatchingPageViewModel { IsSetupStep: not false, ContinueFromSetupCommand: not null } sensitivityMatchingPageViewModel) || !sensitivityMatchingPageViewModel.ContinueFromSetupCommand.CanExecute(null))
        {
            return;
        }
        sensitivityMatchingPageViewModel.PrepareMeasureEntryPreview();
        _isMeasureForwardTransitionActive = true;
        PlaySetupToMeasureTransition(() =>
        {
            ExecuteContinueFromSetupCommand();
            if (!(base.DataContext is SensitivityMatchingPageViewModel { IsMeasureStep: not false }))
            {
                ResetSetupTransitionPresentation();
                ResetMeasureVisuals();
            }
        });
    }

    private void MeasureBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeAnimationMode == MorphAnimationMode.IntroToSetupTransition || _activeAnimationMode == MorphAnimationMode.SetupToIntroTransition || _isMeasureForwardTransitionActive || _isMeasureBackTransitionActive || !(base.DataContext is SensitivityMatchingPageViewModel sensitivityMatchingPageViewModel))
        {
            return;
        }
        if (!sensitivityMatchingPageViewModel.IsMeasureStep)
        {
            ExecuteBackCommand();
            return;
        }
        _isMeasureBackTransitionActive = true;
        PlayMeasureExitTransition(() =>
        {
            ExecuteBackCommand();
            ResetMeasureVisuals();
            if (!(base.DataContext is SensitivityMatchingPageViewModel { IsSetupStep: false }))
            {
                ResetSetupTransitionPresentation();
            }
        });
    }

    private void SetupDpiTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isNormalizingDpiText || sender is not TextBox textBox)
        {
            return;
        }

        string originalText = textBox.Text ?? string.Empty;
        string normalizedText = NormalizeDpiText(originalText);
        if (string.Equals(originalText, normalizedText, StringComparison.Ordinal))
        {
            return;
        }

        int selectionIndex = Math.Max(0, Math.Min(textBox.SelectionStart, originalText.Length));
        int digitCountBeforeSelection = CountDigitsBeforeIndex(originalText, selectionIndex);
        _isNormalizingDpiText = true;
        textBox.Text = normalizedText;
        textBox.SelectionStart = Math.Min(digitCountBeforeSelection, normalizedText.Length);
        _isNormalizingDpiText = false;
    }

    private void SyncViewModelState()
    {
        if (!(base.DataContext is SensitivityMatchingPageViewModel sensitivityMatchingPageViewModel))
        {
            SyncIntroAnimationState();
            SyncMeasurePresentationState();
        }
        else
        {
            sensitivityMatchingPageViewModel.SetPageActive(base.IsLoaded && base.IsVisible);
            SyncIntroAnimationState();
            SyncMeasurePresentationState();
        }
    }

    private void UpdateViewModelSubscription(SensitivityMatchingPageViewModel viewModel)
    {
        if (!ReferenceEquals(_observedViewModel, viewModel))
        {
            if (_observedViewModel != null)
            {
                _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }
            _observedViewModel = viewModel;
            if (_observedViewModel != null)
            {
                _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            }
        }
    }

    private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || string.Equals(e.PropertyName, "CurrentStep", StringComparison.Ordinal) || string.Equals(e.PropertyName, "IsIntroStep", StringComparison.Ordinal) || string.Equals(e.PropertyName, "HasFinalRecommendation", StringComparison.Ordinal))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SyncIntroAnimationState();
                SyncMeasurePresentationState();
            }), DispatcherPriority.Loaded);
        }
    }

    private void SyncIntroAnimationState(bool forceRestart = false)
    {
        if (ShouldPlaySetupTransition())
        {
            if (forceRestart || _activeAnimationMode != MorphAnimationMode.IntroToSetupTransition || !_isIntroAnimationActive)
            {
                RestartSetupTransition();
            }
        }
        else if (ShouldPlaySetupToIntroTransition())
        {
            if (forceRestart || _activeAnimationMode != MorphAnimationMode.SetupToIntroTransition || !_isIntroAnimationActive)
            {
                RestartSetupToIntroTransition();
            }
        }
        else if (ShouldPlayIntroAnimation())
        {
            if (forceRestart || _activeAnimationMode != MorphAnimationMode.IntroOpening || !_isIntroAnimationActive)
            {
                RestartIntroAnimation();
            }
        }
        else
        {
            StopIntroAnimation();
            ResetIntroHeroVisuals();
            _isIntroAnimationActive = false;
            _activeAnimationMode = MorphAnimationMode.None;
        }
    }

    private bool ShouldRestartCurrentAnimation()
    {
        if (_activeAnimationMode != MorphAnimationMode.IntroOpening && _activeAnimationMode != MorphAnimationMode.IntroToSetupTransition)
        {
            return _activeAnimationMode == MorphAnimationMode.SetupToIntroTransition;
        }
        return true;
    }

    private bool ShouldPlayIntroAnimation()
    {
        SensitivityMatchingPageViewModel sensitivityMatchingPageViewModel = base.DataContext as SensitivityMatchingPageViewModel;
        if (base.IsLoaded && base.IsVisible && sensitivityMatchingPageViewModel != null)
        {
            return sensitivityMatchingPageViewModel.IsIntroStep;
        }
        return false;
    }

    private bool ShouldPlaySetupTransition()
    {
        SensitivityMatchingPageViewModel sensitivityMatchingPageViewModel = base.DataContext as SensitivityMatchingPageViewModel;
        if (base.IsLoaded && base.IsVisible && _activeAnimationMode == MorphAnimationMode.IntroToSetupTransition && sensitivityMatchingPageViewModel != null)
        {
            return sensitivityMatchingPageViewModel.IsIntroStep;
        }
        return false;
    }

    private bool ShouldPlaySetupToIntroTransition()
    {
        SensitivityMatchingPageViewModel sensitivityMatchingPageViewModel = base.DataContext as SensitivityMatchingPageViewModel;
        if (base.IsLoaded && base.IsVisible && _activeAnimationMode == MorphAnimationMode.SetupToIntroTransition && sensitivityMatchingPageViewModel != null)
        {
            return sensitivityMatchingPageViewModel.IsSetupStep;
        }
        return false;
    }

    private void RestartIntroAnimation()
    {
        StopIntroAnimation();
        ResetIntroHeroVisuals();
        _activeAnimationMode = MorphAnimationMode.IntroOpening;
        if (IntroHeroRoot != null && !(IntroHeroRoot.ActualWidth <= 1.0) && !(IntroHeroRoot.ActualHeight <= 1.0))
        {
            UpdateLayout();
            if (!BuildIntroMorphScene())
            {
                FinalizeIntroAnimation();
                return;
            }
            SetAnimatedTextRenderingState(isAnimated: true);
            ShowIntroMorphCanvas();
            _introClock = Stopwatch.StartNew();
            _introRenderingStartTime = null;
            HookIntroRendering();
            _isIntroAnimationActive = true;
        }
    }

    private void RestartSetupTransition()
    {
        StopIntroAnimation();
        ResetIntroHeroVisuals();
        _activeAnimationMode = MorphAnimationMode.IntroToSetupTransition;
        if (IntroHeroRoot != null && SetupHeroRoot != null && !(IntroHeroRoot.ActualWidth <= 1.0) && !(IntroHeroRoot.ActualHeight <= 1.0))
        {
            UpdateLayout();
            if (!BuildIntroToSetupMorphScene())
            {
                CompleteSetupTransitionImmediately();
                return;
            }
            SetAnimatedTextRenderingState(isAnimated: true);
            ShowIntroMorphCanvas();
            IntroActionHost.Opacity = 0.0;
            IntroActionTranslate.Y = 12.0;
            IntroStartButton.IsHitTestVisible = false;
            IntroStartButton.IsTabStop = false;
            InitializeSetupTransitionPresentation();
            _introClock = Stopwatch.StartNew();
            _introRenderingStartTime = null;
            HookIntroRendering();
            _isIntroAnimationActive = true;
        }
    }

    private void RestartSetupToIntroTransition()
    {
        StopIntroAnimation();
        ResetIntroHeroVisuals();
        _activeAnimationMode = MorphAnimationMode.SetupToIntroTransition;
        ShowIntroTransitionHosts();
        if (IntroHeroRoot != null && SetupHeroRoot != null)
        {
            UpdateLayout();
            if (IntroHeroRoot.ActualWidth <= 1.0 || IntroHeroRoot.ActualHeight <= 1.0 || !BuildSetupToIntroMorphScene())
            {
                CompleteSetupToIntroTransitionImmediately();
                return;
            }
            SetAnimatedTextRenderingState(isAnimated: true);
            ShowIntroMorphCanvas();
            UpdateTransitionActionHost();
            InitializeSetupToIntroTransitionPresentation();
            _introClock = Stopwatch.StartNew();
            _introRenderingStartTime = null;
            HookIntroRendering();
            _isIntroAnimationActive = true;
        }
    }

    private bool BuildIntroMorphScene()
    {
        ClearMorphTokens();
        List<IntroGlyphAnchor> openingAnchors = new List<IntroGlyphAnchor>();
        openingAnchors.AddRange(CreateGlyphAnchors(IntroOpeningWord1Block, Opening1));
        openingAnchors.AddRange(CreateGlyphAnchors(IntroOpeningWord2Block, Opening2));
        openingAnchors.AddRange(CreateGlyphAnchors(IntroOpeningWord3Block, Opening3));
        openingAnchors.AddRange(CreateGlyphAnchors(IntroOpeningWord4Block, Opening4));
        AssignSequenceIndexes(openingAnchors);
        FitOpeningAnchorsToViewport(openingAnchors);
        if (openingAnchors.Count == 0)
        {
            return false;
        }

        List<IntroGlyphAnchor> settledAnchors = BuildIntroSettledAnchors();
        if (settledAnchors.Count == 0)
        {
            return false;
        }

        List<IntroGlyphAnchor> topLeftAnchors = settledAnchors
            .Where(anchor => string.Equals(anchor.GroupKey, TopLeft, StringComparison.Ordinal))
            .OrderBy(anchor => anchor.CharacterIndex)
            .ToList();
        List<IntroGlyphAnchor> remainingTargetAnchors = settledAnchors
            .Where(anchor => !string.Equals(anchor.GroupKey, TopLeft, StringComparison.Ordinal))
            .ToList();
        BuildOpeningMorphTokens(openingAnchors, topLeftAnchors);
        BuildTargetMorphTokens(openingAnchors, remainingTargetAnchors);
        return IntroMorphCanvas.Children.Count > 0;
    }

    private bool BuildIntroToSetupMorphScene()
    {
        ClearMorphTokens();
        List<IntroGlyphAnchor> introAnchors = BuildIntroSettledAnchors();
        List<IntroGlyphAnchor> setupAnchors = BuildSetupSettledAnchors();
        List<IntroGlyphAnchor> introWordAnchors = BuildIntroSettledWordAnchors();
        List<IntroGlyphAnchor> setupWordAnchors = BuildSetupSettledWordAnchors();
        if (introAnchors.Count == 0 || setupAnchors.Count == 0 || introWordAnchors.Count == 0 || setupWordAnchors.Count == 0)
        {
            return false;
        }

        List<IntroGlyphAnchor> introExitAnchors = introAnchors
            .Where(anchor => !IsPersistentTransitionWordGroup(anchor.GroupKey))
            .ToList();
        BuildTransitionSourceTokens(introExitAnchors);
        BuildTransitionPersistentWordTokens(introWordAnchors, setupWordAnchors, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { LeftLabel, TopLeft },
            { RightLabel, RightPrimary }
        });
        ApplyTransitionSweepExitProgress(introAnchors, setupAnchors);
        return IntroMorphCanvas.Children.Count > 0;
    }

    private bool BuildSetupToIntroMorphScene()
    {
        ClearMorphTokens();
        List<IntroGlyphAnchor> setupWordAnchors = BuildSetupSettledWordAnchors();
        List<IntroGlyphAnchor> introWordAnchors = BuildIntroSettledWordAnchors();
        List<IntroGlyphAnchor> introReturnTargetAnchors = BuildIntroSettledAnchors()
            .Where(anchor => !IsPersistentTransitionWordGroup(anchor.GroupKey))
            .ToList();
        if (setupWordAnchors.Count == 0 || introWordAnchors.Count == 0 || introReturnTargetAnchors.Count == 0)
        {
            return false;
        }

        BuildTransitionPersistentWordTokens(setupWordAnchors, introWordAnchors, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { TopLeft, LeftLabel },
            { RightPrimary, RightLabel }
        });
        BuildIntroReturnTargetTokens(introReturnTargetAnchors);
        return IntroMorphCanvas.Children.Count > 0;
    }

    private List<IntroGlyphAnchor> BuildIntroSettledAnchors()
    {
        Vector settledTranslation = new Vector(IntroSettledTranslate.X, IntroSettledTranslate.Y);
        List<IntroGlyphAnchor> anchors = new List<IntroGlyphAnchor>();
        anchors.AddRange(CreateGlyphAnchors(IntroHeroTopLeftWord, TopLeft, settledTranslation));
        anchors.AddRange(CreateGlyphAnchors(IntroHeroLeftLabel, LeftLabel, settledTranslation));
        anchors.AddRange(CreateGlyphAnchors(IntroHeroConnector, Connector, settledTranslation));
        anchors.AddRange(CreateGlyphAnchors(IntroHeroRightLabel, RightLabel, settledTranslation));
        anchors.AddRange(CreateGlyphAnchors(IntroHeroBottomRightWord1, Bottom1, settledTranslation));
        anchors.AddRange(CreateGlyphAnchors(IntroHeroBottomRightWord2, Bottom2, settledTranslation));
        anchors.AddRange(CreateGlyphAnchors(IntroHeroBottomRightWord3, Bottom3, settledTranslation));
        AssignSequenceIndexes(anchors);
        return anchors;
    }

    private List<IntroGlyphAnchor> BuildSetupSettledAnchors()
    {
        List<IntroGlyphAnchor> anchors = new List<IntroGlyphAnchor>();
        anchors.AddRange(CreateGlyphAnchors(SetupHeroTopLeftWord, TopLeft));
        anchors.AddRange(CreateGlyphAnchors(SetupHeroRightLabel, RightPrimary));
        AssignSequenceIndexes(anchors);
        return anchors;
    }

    private List<IntroGlyphAnchor> BuildIntroSettledWordAnchors()
    {
        Vector settledTranslation = new Vector(IntroSettledTranslate.X, IntroSettledTranslate.Y);
        List<IntroGlyphAnchor> anchors = new List<IntroGlyphAnchor>();
        anchors.AddRange(CreateWordAnchors(IntroHeroLeftLabel, LeftLabel, settledTranslation));
        anchors.AddRange(CreateWordAnchors(IntroHeroRightLabel, RightLabel, settledTranslation));
        AssignSequenceIndexes(anchors);
        return anchors;
    }

    private List<IntroGlyphAnchor> BuildSetupSettledWordAnchors()
    {
        List<IntroGlyphAnchor> anchors = new List<IntroGlyphAnchor>();
        anchors.AddRange(CreateWordAnchors(SetupHeroTopLeftWord, TopLeft));
        anchors.AddRange(CreateWordAnchors(SetupHeroRightLabel, RightPrimary));
        AssignSequenceIndexes(anchors);
        return anchors;
    }

    private void AssignSequenceIndexes(IList<IntroGlyphAnchor> anchors)
    {
        if (anchors == null)
        {
            return;
        }

        for (int index = 0; index < anchors.Count; index++)
        {
            anchors[index].SequenceIndex = index;
        }
    }

    private void BuildTransitionSourceTokens(IReadOnlyList<IntroGlyphAnchor> sourceAnchors, IReadOnlyList<IntroGlyphAnchor> targetAnchors = null, IReadOnlyDictionary<string, string> sourceToTargetGroups = null)
    {
        if (sourceAnchors == null)
        {
            return;
        }

        Dictionary<string, List<IntroGlyphAnchor>> targetAnchorsByGroup = null;
        if (targetAnchors != null && sourceToTargetGroups != null)
        {
            targetAnchorsByGroup = targetAnchors
                .GroupBy(anchor => anchor.GroupKey)
                .ToDictionary(group => group.Key, group => group.OrderBy(anchor => anchor.CharacterIndex).ToList(), StringComparer.Ordinal);
        }

        foreach (IntroGlyphAnchor sourceAnchor in sourceAnchors)
        {
            IntroMorphToken token = CreateMorphToken(sourceAnchor);
            double exitStartSeconds = ComputeSetupTransitionExitStart(sourceAnchor);
            double exitDurationSeconds = ComputeSetupTransitionExitDuration(sourceAnchor);

            if (string.Equals(sourceAnchor.GroupKey, TopLeft, StringComparison.Ordinal))
            {
                exitStartSeconds = Math.Max(SetupTransitionMorphStartSeconds, exitStartSeconds - SetupTopLeftExitAdvanceSeconds);
                exitDurationSeconds = Math.Max(0.001, exitDurationSeconds - SetupTopLeftExitDurationTrimSeconds);
            }

            token.StartPosition = sourceAnchor.Position;
            token.EndPosition = ComputeSetupTransitionExitPosition(sourceAnchor);
            token.StartScale = 1.0;
            token.EndScale = 0.82;
            token.EnterDelay = (sourceAnchor.SequenceIndex % 3) * 0.006;
            token.MotionDuration = SetupTransitionMorphEndSeconds - SetupTransitionMorphStartSeconds;
            token.GroupKey = sourceAnchor.GroupKey;
            token.CharacterIndex = sourceAnchor.CharacterIndex;

            if (string.Equals(sourceAnchor.GroupKey, TopLeft, StringComparison.Ordinal))
            {
                token.EnterDelay = Math.Max(0.0, token.EnterDelay - SetupTopLeftEnterDelayAdvanceSeconds);
                token.MotionDuration = Math.Max(0.001, token.MotionDuration - SetupTopLeftMotionDurationTrimSeconds);
            }

            token.ExitStart = exitStartSeconds;
            token.ExitEnd = Math.Min(token.ExitStart + exitDurationSeconds, SetupTransitionMorphEndSeconds);
            token.IsPersistent = false;
            token.RevealThreshold = 0.0;

            if (TryFindMatchingTransitionAnchor(sourceAnchor, sourceToTargetGroups, targetAnchorsByGroup, out IntroGlyphAnchor matchingTargetAnchor))
            {
                token.EndPosition = matchingTargetAnchor.Position;
                token.EndScale = matchingTargetAnchor.FontSize / Math.Max(sourceAnchor.FontSize, 1.0);
                token.ExitStart = SetupTransitionMorphEndSeconds;
                token.ExitEnd = SetupTransitionMorphEndSeconds;
                token.IsPersistent = true;

                if (string.Equals(sourceAnchor.GroupKey, RightLabel, StringComparison.Ordinal))
                {
                    token.EnterDelay = 0.09;
                    token.MotionDuration = Math.Max(0.001, SetupTransitionMorphEndSeconds - SetupTransitionMorphStartSeconds - token.EnterDelay);
                }
            }

            _openingTokens.Add(token);
            IntroMorphCanvas.Children.Add(token.Element);
            ApplyTokenState(token, token.StartPosition, token.StartScale, 1.0);
        }
    }

    private static bool TryFindMatchingTransitionAnchor(
        IntroGlyphAnchor sourceAnchor,
        IReadOnlyDictionary<string, string> sourceToTargetGroups,
        IReadOnlyDictionary<string, List<IntroGlyphAnchor>> targetAnchorsByGroup,
        out IntroGlyphAnchor matchingTargetAnchor)
    {
        matchingTargetAnchor = null;
        if (sourceAnchor == null || sourceToTargetGroups == null || targetAnchorsByGroup == null)
        {
            return false;
        }

        if (!sourceToTargetGroups.TryGetValue(sourceAnchor.GroupKey, out string targetGroupKey))
        {
            return false;
        }

        if (!targetAnchorsByGroup.TryGetValue(targetGroupKey, out List<IntroGlyphAnchor> targetGroupAnchors))
        {
            return false;
        }

        if (sourceAnchor.CharacterIndex < 0 || sourceAnchor.CharacterIndex >= targetGroupAnchors.Count)
        {
            return false;
        }

        matchingTargetAnchor = targetGroupAnchors[sourceAnchor.CharacterIndex];
        return true;
    }

    private void ApplyTransitionSweepExitProgress(IReadOnlyList<IntroGlyphAnchor> introAnchors, IReadOnlyList<IntroGlyphAnchor> setupAnchors)
    {
        ApplySourceSweepExitProgress(introAnchors, setupAnchors);
        ApplyTargetSweepExitProgress(introAnchors, setupAnchors);
    }

    private void ApplySourceSweepExitProgress(IReadOnlyList<IntroGlyphAnchor> introAnchors, IReadOnlyList<IntroGlyphAnchor> setupAnchors)
    {
        if (introAnchors == null || setupAnchors == null)
        {
            return;
        }

        if (!TryGetGroupCenter(introAnchors, LeftLabel, out Point sourceStart)
            || !TryGetGroupCenter(setupAnchors, TopLeft, out Point sourceEnd))
        {
            return;
        }

        Vector sourceTravel = sourceEnd - sourceStart;
        double sourceTravelLengthSquared = sourceTravel.X * sourceTravel.X + sourceTravel.Y * sourceTravel.Y;
        if (sourceTravelLengthSquared <= 0.001)
        {
            return;
        }

        ApplySweepExitProgressForGroup(introAnchors, sourceStart, sourceTravel, sourceTravelLengthSquared, TopLeft, 0.085, 0.9);
    }

    private void ApplyTargetSweepExitProgress(IReadOnlyList<IntroGlyphAnchor> introAnchors, IReadOnlyList<IntroGlyphAnchor> setupAnchors)
    {
        if (introAnchors == null || setupAnchors == null)
        {
            return;
        }

        if (!TryGetGroupCenter(introAnchors, RightLabel, out Point targetStart)
            || !TryGetGroupCenter(setupAnchors, RightPrimary, out Point targetEnd))
        {
            return;
        }

        Vector targetTravel = targetEnd - targetStart;
        double targetTravelLengthSquared = targetTravel.X * targetTravel.X + targetTravel.Y * targetTravel.Y;
        if (targetTravelLengthSquared <= 0.001)
        {
            return;
        }

        ApplySweepExitProgressForGroup(introAnchors, targetStart, targetTravel, targetTravelLengthSquared, Bottom1, 0.055);
        ApplySweepExitProgressForGroup(introAnchors, targetStart, targetTravel, targetTravelLengthSquared, Bottom2, 0.06);
        ApplySweepExitProgressForGroup(introAnchors, targetStart, targetTravel, targetTravelLengthSquared, Bottom3, 0.065);
    }

    private void ApplySweepExitProgressForGroup(IReadOnlyList<IntroGlyphAnchor> anchors, Point targetStart, Vector targetTravel, double targetTravelLengthSquared, string groupKey, double width, double minimumStartProgress = 0.0)
    {
        if (!TryGetGroupCenter(anchors, groupKey, out Point groupCenter))
        {
            return;
        }

        Vector targetToGroup = groupCenter - targetStart;
        double projectedProgress = Clamp((targetToGroup.X * targetTravel.X + targetToGroup.Y * targetTravel.Y) / targetTravelLengthSquared, 0.0, 1.0);
        double startProgress = Clamp(Math.Max(projectedProgress - width * 0.45, minimumStartProgress), 0.0, 1.0);
        double endProgress = Clamp(Math.Max(projectedProgress + width * 0.55, startProgress + width), startProgress + 0.001, 1.0);

        foreach (IntroMorphToken token in _openingTokens)
        {
            if (string.Equals(token.GroupKey, groupKey, StringComparison.Ordinal))
            {
                token.ExitProgressStart = startProgress;
                token.ExitProgressEnd = endProgress;
            }
        }
    }

    private static bool TryGetGroupCenter(IReadOnlyList<IntroGlyphAnchor> anchors, string groupKey, out Point center)
    {
        center = default;
        if (anchors == null)
        {
            return false;
        }

        List<IntroGlyphAnchor> groupAnchors = anchors
            .Where(anchor => string.Equals(anchor.GroupKey, groupKey, StringComparison.Ordinal))
            .ToList();
        if (groupAnchors.Count == 0)
        {
            return false;
        }

        double left = groupAnchors.Min(anchor => anchor.Position.X);
        double top = groupAnchors.Min(anchor => anchor.Position.Y);
        double right = groupAnchors.Max(anchor => anchor.Position.X + anchor.Width);
        double bottom = groupAnchors.Max(anchor => anchor.Position.Y + anchor.Height);
        center = new Point((left + right) / 2.0, (top + bottom) / 2.0);
        return true;
    }

    private void BuildTransitionPersistentWordTokens(IReadOnlyList<IntroGlyphAnchor> sourceWordAnchors, IReadOnlyList<IntroGlyphAnchor> targetWordAnchors, IReadOnlyDictionary<string, string> sourceToTargetGroups)
    {
        if (sourceWordAnchors == null || targetWordAnchors == null || sourceToTargetGroups == null)
        {
            return;
        }

        Dictionary<string, IntroGlyphAnchor> targetWordsByGroup = targetWordAnchors.ToDictionary(anchor => anchor.GroupKey, anchor => anchor, StringComparer.Ordinal);
        foreach (IntroGlyphAnchor sourceWordAnchor in sourceWordAnchors)
        {
            if (!sourceToTargetGroups.TryGetValue(sourceWordAnchor.GroupKey, out string targetGroupKey))
            {
                continue;
            }

            if (!targetWordsByGroup.TryGetValue(targetGroupKey, out IntroGlyphAnchor targetWordAnchor))
            {
                continue;
            }

            IntroMorphToken token = CreateMorphToken(sourceWordAnchor);
            token.StartPosition = sourceWordAnchor.Position;
            token.EndPosition = targetWordAnchor.Position;
            token.StartScale = 1.0;
            token.EndScale = targetWordAnchor.FontSize / Math.Max(sourceWordAnchor.FontSize, 1.0);
            token.EnterDelay = ResolvePersistentWordEnterDelay(sourceWordAnchor.GroupKey);
            token.MotionDuration = SetupTransitionMorphEndSeconds - SetupTransitionMorphStartSeconds;
            token.GroupKey = sourceWordAnchor.GroupKey;
            token.CharacterIndex = sourceWordAnchor.CharacterIndex;
            token.ExitStart = SetupTransitionMorphEndSeconds;
            token.ExitEnd = SetupTransitionMorphEndSeconds;
            token.IsPersistent = true;
            token.RevealThreshold = 0.0;

            _openingTokens.Add(token);
            IntroMorphCanvas.Children.Add(token.Element);
            ApplyTokenState(token, token.StartPosition, token.StartScale, 1.0);
        }
    }

    private static double ResolvePersistentWordEnterDelay(string groupKey)
    {
        return string.Equals(groupKey, RightLabel, StringComparison.Ordinal)
            || string.Equals(groupKey, RightPrimary, StringComparison.Ordinal)
            ? 0.09
            : 0.0;
    }

    private void BuildIntroReturnTargetTokens(IReadOnlyList<IntroGlyphAnchor> targetAnchors)
    {
        if (targetAnchors == null || targetAnchors.Count == 0)
        {
            return;
        }

        foreach (IntroGlyphAnchor targetAnchor in targetAnchors)
        {
            IntroMorphToken token = CreateMorphToken(targetAnchor);
            double sourceExitStartSeconds = ComputeSetupTransitionExitStart(targetAnchor);
            double sourceExitEndSeconds = Math.Min(sourceExitStartSeconds + ComputeSetupTransitionExitDuration(targetAnchor), SetupTransitionMorphEndSeconds);
            token.StartPosition = ComputeSetupTransitionExitPosition(targetAnchor);
            token.EndPosition = targetAnchor.Position;
            token.StartScale = 0.82;
            token.EndScale = 1.0;
            token.EnterDelay = (targetAnchor.SequenceIndex % 3) * 0.008;
            token.MotionDuration = SetupTransitionMorphEndSeconds - SetupTransitionMorphStartSeconds;
            token.GroupKey = targetAnchor.GroupKey;
            token.CharacterIndex = targetAnchor.CharacterIndex;
            token.ExitStart = 0.0;
            token.ExitEnd = 0.0;
            token.IsPersistent = true;
            token.RevealThreshold = 0.0;
            token.RevealStartSeconds = SetupTransitionTotalDurationSeconds - sourceExitEndSeconds;
            token.RevealEndSeconds = SetupTransitionTotalDurationSeconds - sourceExitStartSeconds;

            _targetTokens.Add(token);
            IntroMorphCanvas.Children.Add(token.Element);
            ApplyTokenState(token, token.StartPosition, token.StartScale, 0.0);
        }
    }

    private void BuildOpeningMorphTokens(IReadOnlyList<IntroGlyphAnchor> openingAnchors, IReadOnlyList<IntroGlyphAnchor> topLeftAnchors)
    {
        foreach (IntroGlyphAnchor openingAnchor in openingAnchors)
        {
            IntroMorphToken introMorphToken = CreateMorphToken(openingAnchor);
            introMorphToken.StartPosition = openingAnchor.Position;
            introMorphToken.StartScale = 1.0;
            introMorphToken.EndScale = 0.9;
            introMorphToken.EnterDelay = (double)(openingAnchor.SequenceIndex % 4) * 0.012;
            introMorphToken.MotionDuration = 0.66;
            introMorphToken.GroupKey = openingAnchor.GroupKey;
            introMorphToken.CharacterIndex = openingAnchor.CharacterIndex;
            introMorphToken.ExitStart = 0.84 + (double)openingAnchor.SequenceIndex * 0.004;
            introMorphToken.ExitEnd = introMorphToken.ExitStart + 0.3;
            introMorphToken.IsPersistent = false;
            introMorphToken.RevealThreshold = 0.0;
            if (string.Equals(openingAnchor.GroupKey, "Opening4", StringComparison.Ordinal) && openingAnchor.CharacterIndex < topLeftAnchors.Count)
            {
                IntroGlyphAnchor introGlyphAnchor = topLeftAnchors[openingAnchor.CharacterIndex];
                introMorphToken.EndPosition = introGlyphAnchor.Position;
                introMorphToken.EndScale = introGlyphAnchor.FontSize / Math.Max(openingAnchor.FontSize, 1.0);
                introMorphToken.EnterDelay = (double)openingAnchor.CharacterIndex * 0.01;
                introMorphToken.MotionDuration = 0.72;
                introMorphToken.ExitStart = 1.5;
                introMorphToken.ExitEnd = 1.78;
                introMorphToken.IsPersistent = true;
                introMorphToken.RevealThreshold = 0.0;
            }
            else
            {
                introMorphToken.EndPosition = ComputeDriftPosition(openingAnchor);
            }
            _openingTokens.Add(introMorphToken);
            IntroMorphCanvas.Children.Add(introMorphToken.Element);
            ApplyTokenState(introMorphToken, introMorphToken.StartPosition, introMorphToken.StartScale, 0.0);
        }
    }

    private void BuildTargetMorphTokens(IReadOnlyList<IntroGlyphAnchor> openingAnchors, IReadOnlyList<IntroGlyphAnchor> targetAnchors)
    {
        if (targetAnchors == null || targetAnchors.Count == 0)
        {
            return;
        }

        for (int targetIndex = 0; targetIndex < targetAnchors.Count; targetIndex++)
        {
            IntroGlyphAnchor targetAnchor = targetAnchors[targetIndex];
            IntroMorphToken token = CreateMorphToken(targetAnchor);
            token.StartPosition = ComputeTargetStartPosition(targetAnchor, targetIndex);
            token.EndPosition = targetAnchor.Position;
            token.StartScale = ComputeTargetStartScale(targetAnchor.GroupKey, targetIndex);
            token.EndScale = 1.0;
            token.EnterDelay = ComputeTargetDelay(targetAnchor.GroupKey, targetAnchor.CharacterIndex, targetIndex);
            token.MotionDuration = 0.58;
            token.GroupKey = targetAnchor.GroupKey;
            token.CharacterIndex = targetAnchor.CharacterIndex;
            token.ExitStart = IntroCanvasFadeStartSeconds;
            token.ExitEnd = IntroCanvasFadeEndSeconds;
            token.IsPersistent = true;
            token.RevealThreshold = ComputeTargetRevealThreshold(targetAnchor.GroupKey, targetAnchor.CharacterIndex);

            _targetTokens.Add(token);
            IntroMorphCanvas.Children.Add(token.Element);
            ApplyTokenState(token, token.StartPosition, token.StartScale, 0.0);
        }
    }

    private void FitOpeningAnchorsToViewport(IList<IntroGlyphAnchor> anchors)
    {
        if (anchors == null || anchors.Count == 0 || IntroHeroRoot == null)
        {
            return;
        }

        Rect anchorBounds = GetAnchorBounds(anchors);
        if (anchorBounds.Width <= 0.0 || anchorBounds.Height <= 0.0)
        {
            return;
        }

        double availableWidth = Math.Max(0.0, IntroHeroRoot.ActualWidth - IntroOpeningSafePadding * 2.0);
        double availableHeight = Math.Max(0.0, IntroHeroRoot.ActualHeight - IntroOpeningSafePadding * 2.0);
        if (availableWidth <= 0.0 || availableHeight <= 0.0)
        {
            return;
        }

        double viewportScale = Math.Min(1.0, Math.Min(availableWidth / anchorBounds.Width, availableHeight / anchorBounds.Height));
        Point anchorCenter = new Point(anchorBounds.X + anchorBounds.Width / 2.0, anchorBounds.Y + anchorBounds.Height / 2.0);
        Point viewportCenter = new Point(IntroHeroRoot.ActualWidth / 2.0, IntroHeroRoot.ActualHeight / 2.0);

        foreach (IntroGlyphAnchor anchor in anchors)
        {
            Vector offsetFromAnchorCenter = anchor.Position - anchorCenter;
            anchor.Position = viewportCenter + offsetFromAnchorCenter * viewportScale;
            anchor.FontSize *= viewportScale;
            anchor.Width *= viewportScale;
            anchor.Height *= viewportScale;
        }
    }

    private Rect GetAnchorBounds(IEnumerable<IntroGlyphAnchor> anchors)
    {
        if (anchors == null)
        {
            return Rect.Empty;
        }

        IntroGlyphAnchor[] anchorArray = anchors.ToArray();
        if (anchorArray.Length == 0)
        {
            return Rect.Empty;
        }

        double left = anchorArray.Min(anchor => anchor.Position.X);
        double top = anchorArray.Min(anchor => anchor.Position.Y);
        double right = anchorArray.Max(anchor => anchor.Position.X + anchor.Width);
        double bottom = anchorArray.Max(anchor => anchor.Position.Y + anchor.Height);
        return new Rect(left, top, Math.Max(0.0, right - left), Math.Max(0.0, bottom - top));
    }

    private List<IntroGlyphAnchor> CreateGlyphAnchors(TextBlock textBlock, string groupKey, Vector? translationCompensation = null)
    {
        List<IntroGlyphAnchor> anchors = new List<IntroGlyphAnchor>();
        if (textBlock == null || string.IsNullOrWhiteSpace(textBlock.Text))
        {
            return anchors;
        }

        Point origin = textBlock.TranslatePoint(new Point(0.0, 0.0), IntroMorphCanvas);
        if (translationCompensation.HasValue)
        {
            origin -= translationCompensation.Value;
        }

        List<Rect> formattedGlyphBounds = TryGetFormattedGlyphBounds(textBlock);
        if (formattedGlyphBounds != null && formattedGlyphBounds.Count == textBlock.Text.Length)
        {
            Rect textBounds = CreateBoundsFromGlyphRects(formattedGlyphBounds);
            double renderedWidth = Math.Max(textBlock.ActualWidth, textBounds.Width);
            double widthScale = textBounds.Width > 0.0 ? renderedWidth / textBounds.Width : 1.0;

            for (int characterIndex = 0; characterIndex < formattedGlyphBounds.Count; characterIndex++)
            {
                Rect glyphBounds = formattedGlyphBounds[characterIndex];
                anchors.Add(CreateGlyphAnchor(
                    textBlock,
                    groupKey,
                    characterIndex,
                    new Point(origin.X + (glyphBounds.X - textBounds.X) * widthScale, origin.Y + (glyphBounds.Y - textBounds.Y)),
                    Math.Max(glyphBounds.Width * widthScale, 0.5),
                    Math.Max(glyphBounds.Height, textBlock.ActualHeight)));
            }

            return anchors;
        }

        List<double> characterWidths = new List<double>();
        double maximumCharacterHeight = 0.0;
        for (int characterIndex = 0; characterIndex < textBlock.Text.Length; characterIndex++)
        {
            Size characterSize = MeasureCharacterSize(textBlock, textBlock.Text[characterIndex].ToString());
            characterWidths.Add(Math.Max(characterSize.Width, 0.5));
            maximumCharacterHeight = Math.Max(maximumCharacterHeight, characterSize.Height);
        }

        double measuredTextWidth = characterWidths.Sum();
        double desiredTextWidth = Math.Max(textBlock.ActualWidth, measuredTextWidth);
        double fallbackWidthScale = measuredTextWidth > 0.0 ? desiredTextWidth / measuredTextWidth : 1.0;
        double anchorHeight = Math.Max(textBlock.ActualHeight, maximumCharacterHeight);
        double nextCharacterX = origin.X;

        for (int characterIndex = 0; characterIndex < textBlock.Text.Length; characterIndex++)
        {
            double anchorWidth = characterWidths[characterIndex] * fallbackWidthScale;
            anchors.Add(CreateGlyphAnchor(
                textBlock,
                groupKey,
                characterIndex,
                new Point(nextCharacterX, origin.Y),
                anchorWidth,
                anchorHeight));
            nextCharacterX += anchorWidth;
        }

        return anchors;
    }

    private static Rect CreateBoundsFromGlyphRects(IReadOnlyList<Rect> glyphBounds)
    {
        double left = glyphBounds.Min(bounds => bounds.Left);
        double top = glyphBounds.Min(bounds => bounds.Top);
        double right = glyphBounds.Max(bounds => bounds.Right);
        double bottom = glyphBounds.Max(bounds => bounds.Bottom);
        return new Rect(left, top, Math.Max(right - left, 0.5), Math.Max(bottom - top, 0.5));
    }

    private static IntroGlyphAnchor CreateGlyphAnchor(TextBlock sourceTextBlock, string groupKey, int characterIndex, Point position, double width, double height)
    {
        return new IntroGlyphAnchor
        {
            Character = sourceTextBlock.Text[characterIndex].ToString(),
            Position = position,
            FontFamily = sourceTextBlock.FontFamily,
            FontSize = sourceTextBlock.FontSize,
            FontStyle = sourceTextBlock.FontStyle,
            FontStretch = sourceTextBlock.FontStretch,
            FontWeight = sourceTextBlock.FontWeight,
            Foreground = sourceTextBlock.Foreground,
            TextFormattingMode = TextOptions.GetTextFormattingMode(sourceTextBlock),
            TextRenderingMode = TextOptions.GetTextRenderingMode(sourceTextBlock),
            TextHintingMode = TextOptions.GetTextHintingMode(sourceTextBlock),
            GroupKey = groupKey,
            CharacterIndex = characterIndex,
            SequenceIndex = characterIndex,
            Width = width,
            Height = height
        };
    }

    private List<IntroGlyphAnchor> CreateWordAnchors(TextBlock textBlock, string groupKey, Vector? translationCompensation = null)
    {
        List<IntroGlyphAnchor> anchors = new List<IntroGlyphAnchor>();
        if (textBlock == null || string.IsNullOrWhiteSpace(textBlock.Text))
        {
            return anchors;
        }

        Point origin = textBlock.TranslatePoint(new Point(0.0, 0.0), IntroMorphCanvas);
        if (translationCompensation.HasValue)
        {
            origin -= translationCompensation.Value;
        }

        anchors.Add(new IntroGlyphAnchor
        {
            Character = textBlock.Text,
            Position = origin,
            FontFamily = textBlock.FontFamily,
            FontSize = textBlock.FontSize,
            FontStyle = textBlock.FontStyle,
            FontStretch = textBlock.FontStretch,
            FontWeight = textBlock.FontWeight,
            Foreground = textBlock.Foreground,
            TextFormattingMode = TextOptions.GetTextFormattingMode(textBlock),
            TextRenderingMode = TextOptions.GetTextRenderingMode(textBlock),
            TextHintingMode = TextOptions.GetTextHintingMode(textBlock),
            GroupKey = groupKey,
            CharacterIndex = 0,
            SequenceIndex = 0,
            Width = Math.Max(textBlock.ActualWidth, 0.0),
            Height = Math.Max(textBlock.ActualHeight, 0.0)
        });
        return anchors;
    }

    private List<Rect> TryGetFormattedGlyphBounds(TextBlock textBlock)
    {
        if (textBlock == null || string.IsNullOrWhiteSpace(textBlock.Text))
        {
            return null;
        }

        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(textBlock);
            FormattedText formattedText = new FormattedText(
                textBlock.Text,
                CultureInfo.CurrentUICulture,
                textBlock.FlowDirection,
                new Typeface(textBlock.FontFamily, textBlock.FontStyle, textBlock.FontWeight, textBlock.FontStretch),
                textBlock.FontSize,
                textBlock.Foreground ?? Brushes.Transparent,
                dpi.PixelsPerDip)
            {
                MaxLineCount = 1,
                Trimming = TextTrimming.None,
                TextAlignment = textBlock.TextAlignment
            };

            if (textBlock.LineHeight > 0.0)
            {
                formattedText.LineHeight = textBlock.LineHeight;
            }

            List<Rect> glyphBounds = new List<Rect>(textBlock.Text.Length);
            for (int characterIndex = 0; characterIndex < textBlock.Text.Length; characterIndex++)
            {
                Geometry glyphGeometry = formattedText.BuildHighlightGeometry(new Point(0.0, 0.0), characterIndex, 1);
                if (glyphGeometry == null || glyphGeometry.Bounds.IsEmpty)
                {
                    return null;
                }

                glyphBounds.Add(glyphGeometry.Bounds);
            }

            return glyphBounds;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsPersistentTransitionWordGroup(string groupKey)
    {
        return string.Equals(groupKey, LeftLabel, StringComparison.Ordinal)
            || string.Equals(groupKey, RightLabel, StringComparison.Ordinal);
    }

    private static bool IsBottomWordGroup(string groupKey)
    {
        return string.Equals(groupKey, Bottom1, StringComparison.Ordinal)
            || string.Equals(groupKey, Bottom2, StringComparison.Ordinal)
            || string.Equals(groupKey, Bottom3, StringComparison.Ordinal);
    }

    private Size MeasureCharacterSize(TextBlock reference, string character)
    {
        TextBlock textBlock = new TextBlock();
        textBlock.Text = character;
        textBlock.FontFamily = reference.FontFamily;
        textBlock.FontSize = reference.FontSize;
        textBlock.FontStyle = reference.FontStyle;
        textBlock.FontStretch = reference.FontStretch;
        textBlock.FontWeight = reference.FontWeight;
        textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return textBlock.DesiredSize;
    }

    private Point ComputeDriftPosition(IntroGlyphAnchor anchor)
    {
        double heroCenterX = IntroHeroRoot.ActualWidth / 2.0;
        double heroCenterY = IntroHeroRoot.ActualHeight / 2.0;
        Vector outwardDirection = new Vector(anchor.Position.X - heroCenterX, anchor.Position.Y - heroCenterY);
        if (outwardDirection.Length < 0.001)
        {
            outwardDirection = new Vector((anchor.SequenceIndex - 6) * 9.0, -22.0);
        }

        outwardDirection.Normalize();
        double travelDistance = 42.0 + (anchor.SequenceIndex % 5) * 8.0;
        double jitterX = Math.Sin((anchor.SequenceIndex + 1) * 0.94) * 12.0;
        double jitterY = Math.Cos((anchor.SequenceIndex + 1) * 1.21) * 10.0;
        return new Point(
            anchor.Position.X + outwardDirection.X * travelDistance + jitterX,
            anchor.Position.Y + outwardDirection.Y * travelDistance + jitterY);
    }

    private Point ComputeSetupTransitionExitPosition(IntroGlyphAnchor anchor)
    {
        if (anchor == null)
        {
            return default;
        }

        Vector exitDirection;
        double travelDistance;
        double perpendicularOffset;
        switch (anchor.GroupKey)
        {
            case TopLeft:
                exitDirection = new Vector(-1.0, -0.46);
                travelDistance = 72.0 + (anchor.CharacterIndex % 4) * 5.0;
                perpendicularOffset = ((anchor.CharacterIndex % 5) - 2.0) * 2.5;
                break;
            case Bottom1:
            case Bottom2:
            case Bottom3:
                int bottomLineIndex = GetBottomWordLineIndex(anchor.GroupKey);
                exitDirection = new Vector(1.0, 0.18 + bottomLineIndex * 0.08);
                travelDistance = 58.0 + bottomLineIndex * 10.0 + (anchor.CharacterIndex % 4) * 4.0;
                perpendicularOffset = ((anchor.CharacterIndex % 5) - 2.0) * 2.0;
                break;
            case Connector:
                exitDirection = new Vector(0.0, -1.0);
                travelDistance = 38.0 + (anchor.CharacterIndex % 3) * 4.0;
                perpendicularOffset = ((anchor.CharacterIndex % 3) - 1.0) * 3.0;
                break;
            default:
                return ComputeDriftPosition(anchor);
        }

        if (exitDirection.Length < 0.001)
        {
            return anchor.Position;
        }

        exitDirection.Normalize();
        Vector perpendicularDirection = new Vector(-exitDirection.Y, exitDirection.X);
        return new Point(
            anchor.Position.X + exitDirection.X * travelDistance + perpendicularDirection.X * perpendicularOffset,
            anchor.Position.Y + exitDirection.Y * travelDistance + perpendicularDirection.Y * perpendicularOffset);
    }

    private double ComputeSetupTransitionExitStart(IntroGlyphAnchor anchor)
    {
        if (anchor == null)
        {
            return 0.98;
        }

        return anchor.GroupKey switch
        {
            TopLeft => 0.99 + anchor.CharacterIndex * 0.004,
            Bottom1 or Bottom2 or Bottom3 => 0.56 + GetBottomWordLineIndex(anchor.GroupKey) * 0.024 + anchor.CharacterIndex * 0.0015,
            Connector => 0.84 + anchor.CharacterIndex * 0.008,
            _ => 0.98 + anchor.SequenceIndex * 0.005
        };
    }

    private double ComputeSetupTransitionExitDuration(IntroGlyphAnchor anchor)
    {
        if (anchor == null)
        {
            return 0.24;
        }

        return anchor.GroupKey switch
        {
            TopLeft => 0.2,
            Bottom1 or Bottom2 or Bottom3 => 0.14,
            Connector => 0.14,
            _ => 0.22
        };
    }

    private static int GetBottomWordLineIndex(string groupKey)
    {
        return groupKey switch
        {
            Bottom2 => 1,
            Bottom3 => 2,
            _ => 0
        };
    }

    private Point ComputeTargetStartPosition(IntroGlyphAnchor anchor, int targetIndex)
    {
        Point heroCenter = new Point(IntroHeroRoot.ActualWidth / 2.0, IntroHeroRoot.ActualHeight / 2.0);
        Vector directionFromTargetToCenter = new Vector(heroCenter.X - anchor.Position.X, heroCenter.Y - anchor.Position.Y);
        if (directionFromTargetToCenter.Length < 0.001)
        {
            directionFromTargetToCenter = new Vector(1.0, -1.0);
        }

        directionFromTargetToCenter.Normalize();
        Vector perpendicularDirection = new Vector(-directionFromTargetToCenter.Y, directionFromTargetToCenter.X);
        double travelDistance = 36.0 + (targetIndex % 4) * 6.0;
        double driftOffset = Math.Sin((targetIndex + 1) * 1.41) * 8.0 + (targetIndex % 3 - 1) * 4.0;
        return new Point(
            anchor.Position.X + directionFromTargetToCenter.X * travelDistance + perpendicularDirection.X * driftOffset,
            anchor.Position.Y + directionFromTargetToCenter.Y * travelDistance + perpendicularDirection.Y * driftOffset);
    }

    private double ComputeTargetDelay(string groupKey, int characterIndex, int overallIndex)
    {
        double groupDelaySeconds = groupKey switch
        {
            LeftLabel => 0.08,
            Connector => 0.14,
            RightLabel => 0.18,
            Bottom1 => 0.14,
            Bottom2 => 0.19,
            Bottom3 => 0.24,
            _ => 0.12
        };

        return groupDelaySeconds + characterIndex * 0.009 + (overallIndex % 2) * 0.003;
    }

    private double ComputeTargetStartScale(string groupKey, int overallIndex)
    {
        double baseScale = groupKey switch
        {
            LeftLabel or RightLabel or Connector => 0.82,
            _ => 0.88
        };

        return baseScale + (overallIndex % 3) * 0.018;
    }

    private double ComputeTargetRevealThreshold(string groupKey, int characterIndex)
    {
        double baseThreshold = groupKey switch
        {
            LeftLabel or RightLabel => 0.64,
            Connector => 0.6,
            _ => 0.56
        };

        return Clamp(baseThreshold + characterIndex * 0.01, 0.52, 0.76);
    }

    private IntroMorphToken CreateMorphToken(IntroGlyphAnchor anchor)
    {
        TranslateTransform translateTransform = new TranslateTransform();
        ScaleTransform scaleTransform = new ScaleTransform(1.0, 1.0);
        TransformGroup renderTransform = new TransformGroup();
        renderTransform.Children.Add(scaleTransform);
        renderTransform.Children.Add(translateTransform);
        TextBlock textBlock = new TextBlock
        {
            Text = anchor.Character,
            FontFamily = anchor.FontFamily,
            FontSize = anchor.FontSize,
            FontStyle = anchor.FontStyle,
            FontStretch = anchor.FontStretch,
            FontWeight = anchor.FontWeight,
            Foreground = anchor.Foreground,
            Opacity = 0.0,
            RenderTransformOrigin = new Point(0.0, 0.0),
            RenderTransform = renderTransform,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };
        TextOptions.SetTextFormattingMode(textBlock, anchor.TextFormattingMode);
        TextOptions.SetTextRenderingMode(textBlock, anchor.TextRenderingMode);
        TextOptions.SetTextHintingMode(textBlock, anchor.TextHintingMode);
        return new IntroMorphToken
        {
            Element = textBlock,
            Scale = scaleTransform,
            Translate = translateTransform
        };
    }

    private void HookIntroRendering()
    {
        if (!_isRenderingHooked)
        {
            CompositionTarget.Rendering += OnIntroRendering;
            _isRenderingHooked = true;
        }
    }

    private void UnhookIntroRendering()
    {
        if (_isRenderingHooked)
        {
            CompositionTarget.Rendering -= OnIntroRendering;
            _isRenderingHooked = false;
        }
    }

    private void OnIntroRendering(object sender, EventArgs e)
    {
        if (_introClock == null)
        {
            return;
        }
        double totalSeconds = ResolveIntroElapsedSeconds(e);
        switch (_activeAnimationMode)
        {
            case MorphAnimationMode.IntroOpening:
                UpdateOpeningTokens(totalSeconds);
                UpdateTargetTokens(totalSeconds);
                UpdateSettledLayer(totalSeconds);
                UpdateActionHost(totalSeconds);
                if (totalSeconds >= IntroTotalDurationSeconds)
                {
                    FinalizeIntroAnimation();
                }
                break;
            case MorphAnimationMode.IntroToSetupTransition:
                UpdateTransitionSourceTokens(totalSeconds);
                UpdateTransitionTargetTokens(totalSeconds);
                UpdateTransitionActionHost();
                UpdateSetupTransitionPresentation(totalSeconds);
                if (totalSeconds >= SetupTransitionTotalDurationSeconds)
                {
                    FinalizeSetupTransition();
                }
                break;
            case MorphAnimationMode.SetupToIntroTransition:
                UpdateTransitionSourceTokens(totalSeconds);
                UpdateTransitionTargetTokens(totalSeconds);
                UpdateSetupToIntroActionHost(totalSeconds);
                UpdateSetupToIntroTransitionPresentation(totalSeconds);
                if (totalSeconds >= SetupTransitionTotalDurationSeconds)
                {
                    FinalizeSetupToIntroTransition();
                }
                break;
        }
    }

    private double ResolveIntroElapsedSeconds(EventArgs renderingEventArgs)
    {
        if (renderingEventArgs is RenderingEventArgs renderingArgs)
        {
            if (!_introRenderingStartTime.HasValue)
            {
                _introRenderingStartTime = renderingArgs.RenderingTime;
            }

            return Math.Max(0.0, (renderingArgs.RenderingTime - _introRenderingStartTime.Value).TotalSeconds);
        }

        return _introClock?.Elapsed.TotalSeconds ?? 0.0;
    }

    private void UpdateOpeningTokens(double elapsedSeconds)
    {
        double entranceOpacity = elapsedSeconds <= IntroFadeInEndSeconds
            ? EaseOutCubic(Clamp01(elapsedSeconds / IntroFadeInEndSeconds))
            : 1.0;

        foreach (IntroMorphToken openingToken in _openingTokens)
        {
            double motionProgress = FinalizeMotionProgress(
                EaseSmootherStep(Clamp01((elapsedSeconds - IntroMorphStartSeconds - openingToken.EnterDelay) / Math.Max(openingToken.MotionDuration, 0.001))),
                0.9997);
            Point position = Lerp(openingToken.StartPosition, openingToken.EndPosition, motionProgress);
            double scale = Lerp(openingToken.StartScale, openingToken.EndScale, motionProgress);
            double opacity = entranceOpacity;

            if (!openingToken.IsPersistent)
            {
                double exitProgress = EaseInOutCubic(Clamp01((elapsedSeconds - openingToken.ExitStart) / Math.Max(openingToken.ExitEnd - openingToken.ExitStart, 0.001)));
                opacity *= 1.0 - exitProgress;
            }

            ApplyTokenState(openingToken, position, scale, opacity);
        }
    }

    private void UpdateTargetTokens(double elapsedSeconds)
    {
        foreach (IntroMorphToken targetToken in _targetTokens)
        {
            double motionProgress = FinalizeMotionProgress(
                EaseSmootherStep(Clamp01((elapsedSeconds - IntroMorphStartSeconds - targetToken.EnterDelay) / Math.Max(targetToken.MotionDuration, 0.001))),
                0.9997);
            double opacity = EaseOutCubic(Clamp01((motionProgress - targetToken.RevealThreshold) / Math.Max(1.0 - targetToken.RevealThreshold, 0.001)));
            Point position = Lerp(targetToken.StartPosition, targetToken.EndPosition, motionProgress);
            double scale = Lerp(targetToken.StartScale, targetToken.EndScale, motionProgress);
            ApplyTokenState(targetToken, position, scale, opacity);
        }

        ShowIntroMorphCanvas();
    }

    private void UpdateTransitionSourceTokens(double elapsedSeconds)
    {
        foreach (IntroMorphToken openingToken in _openingTokens)
        {
            double motionProgress = ComputeSetupTransitionMotionProgress(elapsedSeconds, openingToken);
            Point position = Lerp(openingToken.StartPosition, openingToken.EndPosition, motionProgress);
            double scale = Lerp(openingToken.StartScale, openingToken.EndScale, motionProgress);
            double sweepProgress = ResolveTransitionSweepProgress(openingToken, elapsedSeconds);
            double opacity = 1.0 - ComputeSweepExitProgress(openingToken, sweepProgress);

            if (!openingToken.IsPersistent && double.IsNaN(openingToken.ExitProgressStart))
            {
                double exitProgress = ComputeTimedTransitionExitProgress(openingToken, elapsedSeconds);
                opacity *= 1.0 - exitProgress;
            }

            ApplyTokenState(openingToken, position, scale, opacity);
        }

        ShowIntroMorphCanvas();
    }

    private double ResolveTransitionSweepProgress(IntroMorphToken token, double elapsedSeconds)
    {
        return token != null && string.Equals(token.GroupKey, TopLeft, StringComparison.Ordinal)
            ? ResolveSourceSweepProgress(elapsedSeconds)
            : ResolveTargetSweepProgress(elapsedSeconds);
    }

    private double ResolveSourceSweepProgress(double elapsedSeconds)
    {
        double linearProgress = Clamp01((elapsedSeconds - SetupTransitionMorphStartSeconds) / Math.Max(SetupTransitionMorphEndSeconds - SetupTransitionMorphStartSeconds - SetupTopLeftMotionDurationTrimSeconds, 0.001));
        return _activeAnimationMode == MorphAnimationMode.SetupToIntroTransition
            ? EaseSmootherStep(Clamp01(linearProgress * 1.08 - 0.02))
            : EaseSmootherStep(linearProgress);
    }

    private double ResolveTargetSweepProgress(double elapsedSeconds)
    {
        double linearProgress = Clamp01((elapsedSeconds - SetupTransitionMorphStartSeconds - 0.09) / Math.Max(SetupTransitionMorphEndSeconds - SetupTransitionMorphStartSeconds - 0.09, 0.001));
        return _activeAnimationMode == MorphAnimationMode.SetupToIntroTransition
            ? EaseSmootherStep(Clamp01(linearProgress * 1.08 - 0.02))
            : EaseSmootherStep(linearProgress);
    }

    private static double ComputeSweepExitProgress(IntroMorphToken token, double sweepProgress)
    {
        if (token == null || double.IsNaN(token.ExitProgressStart) || double.IsNaN(token.ExitProgressEnd))
        {
            return 0.0;
        }

        return EaseInOutSine(Clamp01((sweepProgress - token.ExitProgressStart) / Math.Max(token.ExitProgressEnd - token.ExitProgressStart, 0.001)));
    }

    private static double ComputeTimedTransitionExitProgress(IntroMorphToken token, double elapsedSeconds)
    {
        double linearProgress = Clamp01((elapsedSeconds - token.ExitStart) / Math.Max(token.ExitEnd - token.ExitStart, 0.001));
        return IsBottomWordGroup(token.GroupKey)
            ? EaseOutCubic(linearProgress)
            : EaseInCubic(linearProgress);
    }

    private void UpdateTransitionTargetTokens(double elapsedSeconds)
    {
        foreach (IntroMorphToken targetToken in _targetTokens)
        {
            double motionProgress = ComputeSetupTransitionMotionProgress(elapsedSeconds, targetToken);
            double opacity = targetToken.RevealEndSeconds > targetToken.RevealStartSeconds
                ? EaseInOutCubic(ComputeRevealProgress(elapsedSeconds, targetToken.RevealStartSeconds, targetToken.RevealEndSeconds))
                : EaseOutCubic(Clamp01((motionProgress - targetToken.RevealThreshold) / Math.Max(1.0 - targetToken.RevealThreshold, 0.001)));
            Point position = Lerp(targetToken.StartPosition, targetToken.EndPosition, motionProgress);
            double scale = Lerp(targetToken.StartScale, targetToken.EndScale, motionProgress);
            ApplyTokenState(targetToken, position, scale, opacity);
        }

        ShowIntroMorphCanvas();
    }

    private void UpdateSettledLayer(double elapsedSeconds)
    {
        IntroSettledLayer.Opacity = 0.0;
        IntroSettledTranslate.Y = 22.0;
    }

    private void UpdateActionHost(double elapsedSeconds)
    {
        double actionProgress = StabilizeLandingProgress(
            EaseOutCubic(ComputeRevealProgress(elapsedSeconds, IntroActionRevealStartSeconds, IntroActionRevealEndSeconds)),
            0.997);
        IntroActionHost.Opacity = actionProgress;
        IntroActionTranslate.Y = Lerp(12.0, 0.0, actionProgress);
        IntroStartButton.IsHitTestVisible = actionProgress >= 0.999;
        IntroStartButton.IsTabStop = actionProgress >= 0.999;
    }

    private void UpdateSetupToIntroActionHost(double elapsedSeconds)
    {
        double actionProgress = StabilizeLandingProgress(
            EaseOutCubic(ComputeRevealProgress(elapsedSeconds, SetupToIntroActionRevealStartSeconds, SetupToIntroActionRevealEndSeconds)),
            0.997);
        IntroActionHost.Opacity = actionProgress;
        IntroActionTranslate.Y = Lerp(12.0, 0.0, actionProgress);
        IntroStartButton.IsHitTestVisible = actionProgress >= 0.999;
        IntroStartButton.IsTabStop = actionProgress >= 0.999;
    }

    private void UpdateTransitionActionHost()
    {
        IntroActionHost.Opacity = 0.0;
        IntroActionTranslate.Y = 12.0;
        IntroStartButton.IsHitTestVisible = false;
        IntroStartButton.IsTabStop = false;
    }

    private void InitializeSetupTransitionPresentation()
    {
        if (SetupSectionHost != null)
        {
            SetupSectionHost.Opacity = 1.0;
        }
        if (SetupFooterHost != null)
        {
            SetupFooterHost.SetValue(UIElement.VisibilityProperty, Visibility.Visible);
        }
        SetTransitionHostVisualState(SetupSectionHost, isInteractive: false);
        SetTransitionHostVisualState(SetupFooterHost, isInteractive: false);
        HideSetupSettledLayer();
        if (SetupUtilityOverlay != null)
        {
            SetupUtilityOverlay.Opacity = 0.0;
        }
        if (SetupUtilityTranslate != null)
        {
            SetupUtilityTranslate.Y = SetupUtilityRevealOffsetY;
        }
        SetRevealState(SetupSourcePanel, SetupSourcePanelTranslate, 0.0, 0.0, SetupPanelRevealOffsetY);
        SetRevealState(SetupTargetPanel, SetupTargetPanelTranslate, 0.0, 0.0, SetupPanelRevealOffsetY);
        SetRevealState(SetupSourceInfoBandHost, SetupSourceInfoBandTranslate, 0.0, 0.0, SetupInfoBandRevealOffsetY);
        SetRevealState(SetupTargetInfoBandHost, SetupTargetInfoBandTranslate, 0.0, 0.0, SetupInfoBandRevealOffsetY);
        SetRevealState(SetupFooterHost, SetupFooterTranslate, 0.0, 0.0, SetupFooterRevealOffsetY);
    }

    private void InitializeSetupToIntroTransitionPresentation()
    {
        if (SetupSectionHost != null)
        {
            SetupSectionHost.Opacity = 1.0;
        }
        SetTransitionHostVisualState(SetupSectionHost, isInteractive: false);
        SetTransitionHostVisualState(SetupFooterHost, isInteractive: false);
        HideSetupSettledLayer();
        if (SetupUtilityOverlay != null)
        {
            SetupUtilityOverlay.Opacity = 1.0;
        }
        if (SetupUtilityTranslate != null)
        {
            SetupUtilityTranslate.Y = 0.0;
        }
        SetRevealState(SetupSourcePanel, SetupSourcePanelTranslate, 1.0, 1.0, SetupPanelRevealOffsetY);
        SetRevealState(SetupTargetPanel, SetupTargetPanelTranslate, 1.0, 1.0, SetupPanelRevealOffsetY);
        SetRevealState(SetupSourceInfoBandHost, SetupSourceInfoBandTranslate, 1.0, 1.0, SetupInfoBandRevealOffsetY);
        SetRevealState(SetupTargetInfoBandHost, SetupTargetInfoBandTranslate, 1.0, 1.0, SetupInfoBandRevealOffsetY);
        SetRevealState(SetupFooterHost, SetupFooterTranslate, 1.0, 1.0, SetupFooterRevealOffsetY);
    }

    private void UpdateSetupTransitionPresentation(double elapsedSeconds)
    {
        HideSetupSettledLayer();
        if (IntroMorphCanvas != null)
        {
            IntroMorphCanvas.Opacity = 1.0;
        }

        double utilityProgress = StabilizeLandingProgress(ComputeRevealProgress(elapsedSeconds, SetupUtilityRevealStartSeconds, SetupUtilityRevealEndSeconds), 0.996);
        if (SetupUtilityOverlay != null)
        {
            SetupUtilityOverlay.Opacity = EaseInOutSine(utilityProgress);
        }
        if (SetupUtilityTranslate != null)
        {
            SetupUtilityTranslate.Y = Lerp(SetupUtilityRevealOffsetY, 0.0, EaseOutCubic(utilityProgress));
        }

        ApplyReveal(SetupSourcePanel, SetupSourcePanelTranslate, elapsedSeconds, SetupSourcePanelRevealStartSeconds, SetupSourcePanelRevealEndSeconds, SetupPanelRevealOffsetY);
        ApplyReveal(SetupTargetPanel, SetupTargetPanelTranslate, elapsedSeconds, SetupTargetPanelRevealStartSeconds, SetupTargetPanelRevealEndSeconds, SetupPanelRevealOffsetY);
        ApplyReveal(SetupSourceInfoBandHost, SetupSourceInfoBandTranslate, elapsedSeconds, SetupSourceInfoRevealStartSeconds, SetupSourceInfoRevealEndSeconds, SetupInfoBandRevealOffsetY);
        ApplyReveal(SetupTargetInfoBandHost, SetupTargetInfoBandTranslate, elapsedSeconds, SetupTargetInfoRevealStartSeconds, SetupTargetInfoRevealEndSeconds, SetupInfoBandRevealOffsetY);
        ApplyReveal(SetupFooterHost, SetupFooterTranslate, elapsedSeconds, SetupFooterRevealStartSeconds, SetupFooterRevealEndSeconds, SetupFooterRevealOffsetY);
    }

    private void UpdateSetupToIntroTransitionPresentation(double elapsedSeconds)
    {
        HideSetupSettledLayer();
        double utilityProgress = StabilizeLandingProgress(ComputeMirroredRevealProgress(elapsedSeconds, SetupUtilityRevealStartSeconds, SetupUtilityRevealEndSeconds), 0.996);
        if (SetupUtilityOverlay != null)
        {
            SetupUtilityOverlay.Opacity = 1.0 - EaseOutCubic(utilityProgress);
        }
        if (SetupUtilityTranslate != null)
        {
            SetupUtilityTranslate.Y = Lerp(0.0, SetupUtilityRevealOffsetY, EaseInCubic(utilityProgress));
        }

        ApplyRetreat(SetupSourcePanel, SetupSourcePanelTranslate, elapsedSeconds, SetupSourcePanelRevealStartSeconds, SetupSourcePanelRevealEndSeconds, SetupPanelRevealOffsetY);
        ApplyRetreat(SetupTargetPanel, SetupTargetPanelTranslate, elapsedSeconds, SetupTargetPanelRevealStartSeconds, SetupTargetPanelRevealEndSeconds, SetupPanelRevealOffsetY);
        ApplyRetreat(SetupSourceInfoBandHost, SetupSourceInfoBandTranslate, elapsedSeconds, SetupSourceInfoRevealStartSeconds, SetupSourceInfoRevealEndSeconds, SetupInfoBandRevealOffsetY);
        ApplyRetreat(SetupTargetInfoBandHost, SetupTargetInfoBandTranslate, elapsedSeconds, SetupTargetInfoRevealStartSeconds, SetupTargetInfoRevealEndSeconds, SetupInfoBandRevealOffsetY);
        ApplyRetreat(SetupFooterHost, SetupFooterTranslate, elapsedSeconds, SetupFooterRevealStartSeconds, SetupFooterRevealEndSeconds, SetupFooterRevealOffsetY);
    }

    private void ApplyReveal(UIElement host, TranslateTransform translate, double elapsedSeconds, double startSeconds, double endSeconds, double startOffsetY)
    {
        double value = StabilizeLandingProgress(ComputeRevealProgress(elapsedSeconds, startSeconds, endSeconds), 0.996);
        SetRevealState(host, translate, EaseInOutSine(value), EaseOutCubic(value), startOffsetY);
    }

    private void ApplyRetreat(UIElement host, TranslateTransform translate, double elapsedSeconds, double startSeconds, double endSeconds, double endOffsetY)
    {
        double value = StabilizeLandingProgress(ComputeMirroredRevealProgress(elapsedSeconds, startSeconds, endSeconds), 0.996);
        SetRetreatState(host, translate, EaseOutCubic(value), EaseInCubic(value), endOffsetY);
    }

    private void SetRevealState(UIElement host, TranslateTransform translate, double opacityProgress, double translateProgress, double startOffsetY)
    {
        if (host != null)
        {
            host.Opacity = Clamp01(opacityProgress);
        }
        if (translate != null)
        {
            translate.Y = Lerp(startOffsetY, 0.0, Clamp01(translateProgress));
        }
    }

    private void SetRetreatState(UIElement host, TranslateTransform translate, double opacityProgress, double translateProgress, double endOffsetY)
    {
        double hiddenProgress = Clamp01(opacityProgress);
        double offsetProgress = Clamp01(translateProgress);
        if (host != null)
        {
            host.Opacity = 1.0 - hiddenProgress;
        }
        if (translate != null)
        {
            translate.Y = Lerp(0.0, endOffsetY, offsetProgress);
        }
    }

    private static double ComputeRevealProgress(double elapsedSeconds, double startSeconds, double endSeconds)
    {
        return Clamp01((elapsedSeconds - startSeconds) / Math.Max(endSeconds - startSeconds, 0.001));
    }

    private static double ComputeMirroredRevealProgress(double elapsedSeconds, double startSeconds, double endSeconds)
    {
        double mirroredStartSeconds = SetupTransitionTotalDurationSeconds - endSeconds;
        double mirroredEndSeconds = SetupTransitionTotalDurationSeconds - startSeconds;
        return ComputeRevealProgress(elapsedSeconds, mirroredStartSeconds, mirroredEndSeconds);
    }

    private double ComputeSetupTransitionMotionProgress(double elapsedSeconds, IntroMorphToken token)
    {
        double linearProgress = Clamp01((elapsedSeconds - SetupTransitionMorphStartSeconds - token.EnterDelay) / Math.Max(token.MotionDuration, 0.001));
        if (_activeAnimationMode == MorphAnimationMode.SetupToIntroTransition)
        {
            return FinalizeMotionProgress(EaseSmootherStep(Clamp01(linearProgress * 1.08 - 0.02)), 0.9997);
        }

        return FinalizeMotionProgress(EaseSmootherStep(linearProgress), 0.9997);
    }

    private void ApplyTokenState(IntroMorphToken token, Point position, double scale, double opacity)
    {
        if (token != null && token.Element != null)
        {
            if (token.Translate != null)
            {
                token.Translate.X = position.X;
                token.Translate.Y = position.Y;
            }
            if (token.Scale != null)
            {
                token.Scale.ScaleX = scale;
                token.Scale.ScaleY = scale;
            }
            token.Element.Opacity = Clamp(opacity, 0.0, 1.0);
        }
    }

    private void StopIntroAnimation()
    {
        UnhookIntroRendering();
        if (_introClock != null)
        {
            _introClock.Stop();
            _introClock = null;
        }
        _introRenderingStartTime = null;
        ClearMorphTokens();
        SetAnimatedTextRenderingState(isAnimated: false);
        _isIntroAnimationActive = false;
    }

    private void FinalizeIntroAnimation()
    {
        UnhookIntroRendering();
        if (_introClock != null)
        {
            _introClock.Stop();
            _introClock = null;
        }
        _introRenderingStartTime = null;
        FreezeIntroTokensToFinalState();
        ShowIntroMorphCanvas();
        IntroSettledLayer.Opacity = 0.0;
        IntroSettledTranslate.Y = 22.0;
        IntroActionHost.Opacity = 1.0;
        IntroActionTranslate.Y = 0.0;
        IntroStartButton.IsHitTestVisible = true;
        IntroStartButton.IsTabStop = true;
        SetAnimatedTextRenderingState(isAnimated: false);
        _activeAnimationMode = MorphAnimationMode.IntroOpening;
        _isIntroAnimationActive = true;
    }

    private void FinalizeSetupTransition()
    {
        UnhookIntroRendering();
        if (_introClock != null)
        {
            _introClock.Stop();
            _introClock = null;
        }
        _introRenderingStartTime = null;
        FreezeIntroTokensToFinalState();
        UpdateTransitionActionHost();
        CompleteSetupTransitionPresentation();
        SetAnimatedTextRenderingState(isAnimated: false);
        _activeAnimationMode = MorphAnimationMode.None;
        _isIntroAnimationActive = false;
        ExecuteStartSetupCommand();
    }

    private void FinalizeSetupToIntroTransition()
    {
        UnhookIntroRendering();
        if (_introClock != null)
        {
            _introClock.Stop();
            _introClock = null;
        }
        _introRenderingStartTime = null;
        FreezeIntroTokensToFinalState();
        ShowIntroMorphCanvas();
        IntroSettledLayer.Opacity = 0.0;
        IntroSettledTranslate.Y = 22.0;
        IntroActionHost.Opacity = 1.0;
        IntroActionTranslate.Y = 0.0;
        IntroStartButton.IsHitTestVisible = true;
        IntroStartButton.IsTabStop = true;
        CompleteSetupToIntroTransitionPresentation();
        SetAnimatedTextRenderingState(isAnimated: false);
        _activeAnimationMode = MorphAnimationMode.IntroOpening;
        _isIntroAnimationActive = true;
        ExecuteBackCommand();
        ClearIntroTransitionHosts();
    }

    private void CompleteSetupTransitionImmediately()
    {
        CompleteSetupTransitionPresentation();
        SetAnimatedTextRenderingState(isAnimated: false);
        _activeAnimationMode = MorphAnimationMode.None;
        _isIntroAnimationActive = false;
        ExecuteStartSetupCommand();
    }

    private void CompleteSetupToIntroTransitionImmediately()
    {
        FinalizeIntroSettledPresentation();
        CompleteSetupToIntroTransitionPresentation();
        SetAnimatedTextRenderingState(isAnimated: false);
        _activeAnimationMode = MorphAnimationMode.IntroOpening;
        _isIntroAnimationActive = true;
        ExecuteBackCommand();
        ClearIntroTransitionHosts();
    }

    private void ExecuteStartSetupCommand()
    {
        if (base.DataContext is SensitivityMatchingPageViewModel { StartSetupCommand: not null } sensitivityMatchingPageViewModel && sensitivityMatchingPageViewModel.StartSetupCommand.CanExecute(null))
        {
            sensitivityMatchingPageViewModel.StartSetupCommand.Execute(null);
        }
    }

    private void ExecuteContinueFromSetupCommand()
    {
        if (base.DataContext is SensitivityMatchingPageViewModel { ContinueFromSetupCommand: not null } sensitivityMatchingPageViewModel && sensitivityMatchingPageViewModel.ContinueFromSetupCommand.CanExecute(null))
        {
            sensitivityMatchingPageViewModel.ContinueFromSetupCommand.Execute(null);
        }
    }

    private void ExecuteBackCommand()
    {
        if (base.DataContext is SensitivityMatchingPageViewModel { BackCommand: not null } sensitivityMatchingPageViewModel && sensitivityMatchingPageViewModel.BackCommand.CanExecute(null))
        {
            sensitivityMatchingPageViewModel.BackCommand.Execute(null);
        }
    }

    private void FinalizeIntroSettledPresentation()
    {
        ShowIntroMorphCanvas();
        IntroSettledLayer.Opacity = 0.0;
        IntroSettledTranslate.Y = 22.0;
        IntroActionHost.Opacity = 1.0;
        IntroActionTranslate.Y = 0.0;
        IntroStartButton.IsHitTestVisible = true;
        IntroStartButton.IsTabStop = true;
    }

    private void FreezeIntroTokensToFinalState()
    {
        foreach (IntroMorphToken openingToken in _openingTokens)
        {
            if (openingToken.IsPersistent)
            {
                ApplyTokenState(openingToken, openingToken.EndPosition, openingToken.EndScale, 1.0);
            }
            else
            {
                ApplyTokenState(openingToken, openingToken.EndPosition, openingToken.EndScale, 0.0);
            }
        }
        foreach (IntroMorphToken targetToken in _targetTokens)
        {
            ApplyTokenState(targetToken, targetToken.EndPosition, targetToken.EndScale, 1.0);
        }
    }

    private void ClearMorphTokens()
    {
        _openingTokens.Clear();
        _targetTokens.Clear();
        if (IntroMorphCanvas != null)
        {
            IntroMorphCanvas.Children.Clear();
        }
    }

    private void ResetIntroHeroVisuals()
    {
        if (IntroOpeningGuideLayer != null)
        {
            ClearIntroTransitionHosts();
            ClearMorphTokens();
            IntroOpeningGuideLayer.Opacity = 0.0;
            ResetIntroMorphCanvas();
            IntroSettledLayer.Opacity = 0.0;
            IntroSettledTranslate.Y = 22.0;
            IntroActionHost.Opacity = 0.0;
            IntroActionTranslate.Y = 12.0;
            IntroStartButton.IsHitTestVisible = false;
            IntroStartButton.IsTabStop = false;
            SetAnimatedTextRenderingState(isAnimated: false);
            ResetSetupTransitionPresentation();
        }
    }

    private void CompleteSetupTransitionPresentation()
    {
        HideIntroMorphCanvas(clearTokens: true);
        if (SetupSectionHost != null)
        {
            SetupSectionHost.Opacity = 1.0;
        }
        SetTransitionHostVisualState(SetupSectionHost, isInteractive: true);
        SetTransitionHostVisualState(SetupFooterHost, isInteractive: true);
        ShowSetupSettledLayer();
        if (SetupUtilityOverlay != null)
        {
            SetupUtilityOverlay.Opacity = 1.0;
        }
        if (SetupUtilityTranslate != null)
        {
            SetupUtilityTranslate.Y = 0.0;
        }
        SetRevealState(SetupSourcePanel, SetupSourcePanelTranslate, 1.0, 1.0, SetupPanelRevealOffsetY);
        SetRevealState(SetupTargetPanel, SetupTargetPanelTranslate, 1.0, 1.0, SetupPanelRevealOffsetY);
        SetRevealState(SetupSourceInfoBandHost, SetupSourceInfoBandTranslate, 1.0, 1.0, SetupInfoBandRevealOffsetY);
        SetRevealState(SetupTargetInfoBandHost, SetupTargetInfoBandTranslate, 1.0, 1.0, SetupInfoBandRevealOffsetY);
        SetRevealState(SetupFooterHost, SetupFooterTranslate, 1.0, 1.0, SetupFooterRevealOffsetY);
    }

    private void CompleteSetupToIntroTransitionPresentation()
    {
        HideSetupSettledLayer();
        SetTransitionHostVisualState(SetupSectionHost, isInteractive: false);
        SetTransitionHostVisualState(SetupFooterHost, isInteractive: false);
        if (SetupUtilityOverlay != null)
        {
            SetupUtilityOverlay.Opacity = 0.0;
        }
        if (SetupUtilityTranslate != null)
        {
            SetupUtilityTranslate.Y = SetupUtilityRevealOffsetY;
        }
        SetRetreatState(SetupSourcePanel, SetupSourcePanelTranslate, 1.0, 1.0, SetupPanelRevealOffsetY);
        SetRetreatState(SetupTargetPanel, SetupTargetPanelTranslate, 1.0, 1.0, SetupPanelRevealOffsetY);
        SetRetreatState(SetupSourceInfoBandHost, SetupSourceInfoBandTranslate, 1.0, 1.0, SetupInfoBandRevealOffsetY);
        SetRetreatState(SetupTargetInfoBandHost, SetupTargetInfoBandTranslate, 1.0, 1.0, SetupInfoBandRevealOffsetY);
        SetRetreatState(SetupFooterHost, SetupFooterTranslate, 1.0, 1.0, SetupFooterRevealOffsetY);
    }

    private void ResetSetupTransitionPresentation()
    {
        if (SetupSectionHost != null)
        {
            SetupSectionHost.ClearValue(UIElement.OpacityProperty);
            SetupSectionHost.ClearValue(UIElement.IsHitTestVisibleProperty);
            SetupSectionHost.ClearValue(UIElement.IsEnabledProperty);
        }
        if (SetupFooterHost != null)
        {
            SetupFooterHost.ClearValue(UIElement.VisibilityProperty);
            SetupFooterHost.ClearValue(UIElement.OpacityProperty);
            SetupFooterHost.ClearValue(UIElement.IsHitTestVisibleProperty);
            SetupFooterHost.ClearValue(UIElement.IsEnabledProperty);
        }
        ShowSetupSettledLayer();
        if (SetupUtilityOverlay != null)
        {
            SetupUtilityOverlay.Opacity = 1.0;
        }
        if (SetupUtilityTranslate != null)
        {
            SetupUtilityTranslate.Y = 0.0;
        }
        SetRevealState(SetupSourcePanel, SetupSourcePanelTranslate, 1.0, 1.0, SetupPanelRevealOffsetY);
        SetRevealState(SetupTargetPanel, SetupTargetPanelTranslate, 1.0, 1.0, SetupPanelRevealOffsetY);
        SetRevealState(SetupSourceInfoBandHost, SetupSourceInfoBandTranslate, 1.0, 1.0, SetupInfoBandRevealOffsetY);
        SetRevealState(SetupTargetInfoBandHost, SetupTargetInfoBandTranslate, 1.0, 1.0, SetupInfoBandRevealOffsetY);
        SetRevealState(SetupFooterHost, SetupFooterTranslate, 1.0, 1.0, SetupFooterRevealOffsetY);
    }

    private void SyncMeasurePresentationState()
    {
        SensitivityMatchingPageViewModel sensitivityMatchingPageViewModel = base.DataContext as SensitivityMatchingPageViewModel;
        if (base.IsLoaded && base.IsVisible && sensitivityMatchingPageViewModel != null && sensitivityMatchingPageViewModel.IsMeasureStep)
        {
            if (_isMeasureForwardTransitionActive)
            {
                FinalizeMeasureForwardTransition();
                _isMeasurePresentationActive = true;
                return;
            }
            if (!_isMeasurePresentationActive)
            {
                PlayMeasureEntryTransition();
            }
            SyncMeasureCenterPresentation(_isMeasurePresentationActive);
            _isMeasurePresentationActive = true;
        }
        else
        {
            ResetMeasureVisuals();
            _isMeasurePresentationActive = false;
        }
    }

    private void SyncMeasureCenterPresentation(bool animate)
    {
        bool shouldShowResult = base.DataContext is SensitivityMatchingPageViewModel { IsMeasureStep: not false } viewModel
            && viewModel.HasFinalRecommendation;

        if (MeasureCenterRoundHost == null || MeasureCenterResultHost == null || MeasureCenterRoundTranslate == null || MeasureCenterResultTranslate == null)
        {
            _isMeasureResultCenterVisible = shouldShowResult;
        }
        else if (animate && shouldShowResult != _isMeasureResultCenterVisible)
        {
            PlayMeasureCenterSwap(shouldShowResult);
        }
        else
        {
            SetMeasureCenterPresentation(shouldShowResult);
        }
    }

    private void PlayMeasureCenterSwap(bool showResult)
    {
        StopAnimation(MeasureCenterRoundHost, UIElement.OpacityProperty);
        StopAnimation(MeasureCenterResultHost, UIElement.OpacityProperty);
        StopAnimation(MeasureCenterRoundTranslate, TranslateTransform.YProperty);
        StopAnimation(MeasureCenterResultTranslate, TranslateTransform.YProperty);

        MeasureCenterRoundHost.Visibility = Visibility.Visible;
        MeasureCenterResultHost.Visibility = Visibility.Visible;

        Grid outgoingHost = showResult ? MeasureCenterRoundHost : MeasureCenterResultHost;
        TranslateTransform outgoingTranslate = showResult ? MeasureCenterRoundTranslate : MeasureCenterResultTranslate;
        Grid incomingHost = showResult ? MeasureCenterResultHost : MeasureCenterRoundHost;
        TranslateTransform incomingTranslate = showResult ? MeasureCenterResultTranslate : MeasureCenterRoundTranslate;
        double outgoingOffsetY = showResult ? -MeasureCenterSwapOutgoingOffset : MeasureCenterSwapOutgoingOffset;
        double incomingOffsetY = showResult ? MeasureCenterSwapIncomingOffset : -MeasureCenterSwapIncomingOffset;
        double incomingBeginSeconds = showResult ? MeasureCenterSwapToResultBeginSeconds : MeasureCenterSwapToRoundBeginSeconds;

        outgoingHost.Opacity = 1.0;
        outgoingTranslate.Y = 0.0;
        incomingHost.Opacity = 0.0;
        incomingTranslate.Y = incomingOffsetY;

        CubicEase outgoingEase = new CubicEase
        {
            EasingMode = EasingMode.EaseIn
        };
        SineEase incomingEase = new SineEase
        {
            EasingMode = EasingMode.EaseOut
        };

        StartDoubleAnimation(outgoingHost, UIElement.OpacityProperty, 1.0, 0.0, MeasureCenterSwapDurationSeconds, 0.0, outgoingEase);
        StartDoubleAnimation(outgoingTranslate, TranslateTransform.YProperty, 0.0, outgoingOffsetY, MeasureCenterSwapDurationSeconds, 0.0, outgoingEase);
        StartDoubleAnimation(incomingHost, UIElement.OpacityProperty, 0.0, 1.0, MeasureCenterSwapDurationSeconds, incomingBeginSeconds, incomingEase);
        StartDoubleAnimation(incomingTranslate, TranslateTransform.YProperty, incomingOffsetY, 0.0, MeasureCenterSwapDurationSeconds, incomingBeginSeconds, incomingEase);
        _isMeasureResultCenterVisible = showResult;
    }

    private void SetMeasureCenterPresentation(bool showResult)
    {
        StopAnimation(MeasureCenterRoundHost, UIElement.OpacityProperty);
        StopAnimation(MeasureCenterResultHost, UIElement.OpacityProperty);
        StopAnimation(MeasureCenterRoundTranslate, TranslateTransform.YProperty);
        StopAnimation(MeasureCenterResultTranslate, TranslateTransform.YProperty);
        MeasureCenterRoundHost.Visibility = Visibility.Visible;
        MeasureCenterResultHost.Visibility = Visibility.Visible;
        MeasureCenterRoundHost.Opacity = (showResult ? 0.0 : 1.0);
        MeasureCenterResultHost.Opacity = (showResult ? 1.0 : 0.0);
        MeasureCenterRoundTranslate.Y = 0.0;
        MeasureCenterResultTranslate.Y = 0.0;
        _isMeasureResultCenterVisible = showResult;
    }

    private void PlaySetupToMeasureTransition(Action onCompleted)
    {
        if (SetupSectionHost == null || SetupFooterHost == null || MeasureSectionHost == null || MeasureFooterHost == null)
        {
            onCompleted?.Invoke();
            return;
        }
        StopMeasureTransitionTimer();
        StopMeasureAnimations();
        UpdateLayout();
        SetTransitionHostVisualState(SetupSectionHost, isInteractive: false);
        SetTransitionHostVisualState(SetupFooterHost, isInteractive: false);
        SetTransitionHostVisualState(MeasureSectionHost, isInteractive: false);
        SetTransitionHostVisualState(MeasureFooterHost, isInteractive: false);
        MeasureSectionHost.SetValue(UIElement.VisibilityProperty, Visibility.Visible);
        MeasureFooterHost.SetValue(UIElement.VisibilityProperty, Visibility.Visible);
        UpdateLayout();
        EnableMeasureTransitionVisualStability();
        SetupFooterHost.Opacity = 1.0;
        if (SetupSettledLayer != null)
        {
            SetupSettledLayer.Opacity = 0.0;
        }
        if (SetupUtilityOverlay != null)
        {
            SetupUtilityOverlay.Opacity = 1.0;
        }
        if (SetupUtilityTranslate != null)
        {
            SetupUtilityTranslate.Y = 0.0;
        }
        if (MeasureHeroLeftWord != null)
        {
            MeasureHeroLeftWord.Opacity = 0.0;
        }
        if (MeasureHeroRightWord != null)
        {
            MeasureHeroRightWord.Opacity = 0.0;
        }
        PrepareMeasureWordTransitionOverlay(isForward: true, 1.0);
        if (MeasureDataLayer != null)
        {
            MeasureDataLayer.Opacity = 0.0;
        }
        if (MeasureDataTranslate != null)
        {
            MeasureDataTranslate.Y = 24.0;
        }
        if (MeasureRoundsList != null)
        {
            MeasureRoundsList.Opacity = 0.0;
        }
        if (MeasureRoundsTranslate != null)
        {
            MeasureRoundsTranslate.Y = 16.0;
        }
        if (MeasureFooterHost != null)
        {
            MeasureFooterHost.Opacity = 0.0;
        }
        if (MeasureFooterTranslate != null)
        {
            MeasureFooterTranslate.Y = 12.0;
        }
        CubicEase setupExitEase = new CubicEase
        {
            EasingMode = EasingMode.EaseIn
        };
        SineEase wordTransitionEase = new SineEase
        {
            EasingMode = EasingMode.EaseInOut
        };
        CubicEase measureEnterEase = new CubicEase
        {
            EasingMode = EasingMode.EaseOut
        };
        StartDoubleAnimation(SetupUtilityOverlay, UIElement.OpacityProperty, 1.0, 0.0, 0.26, 0.04, setupExitEase);
        StartDoubleAnimation(SetupUtilityTranslate, TranslateTransform.YProperty, 0.0, -12.0, 0.26, 0.04, setupExitEase);
        StartDoubleAnimation(SetupFooterHost, UIElement.OpacityProperty, 1.0, 0.0, 0.34, 0.04, setupExitEase);
        StartDoubleAnimation(SetupFooterTranslate, TranslateTransform.YProperty, 0.0, 12.0, 0.34, 0.04, setupExitEase);
        AnimateMeasureWordTransitionOverlay(isForward: true, wordTransitionEase, 0.02, 1.0, 0.04);
        StartDoubleAnimation(MeasureDataLayer, UIElement.OpacityProperty, 0.0, 1.0, 0.44, 0.22, measureEnterEase);
        StartDoubleAnimation(MeasureDataTranslate, TranslateTransform.YProperty, 24.0, 0.0, 0.44, 0.22, measureEnterEase);
        StartDoubleAnimation(MeasureRoundsList, UIElement.OpacityProperty, 0.0, 1.0, 0.44, 0.3, measureEnterEase);
        StartDoubleAnimation(MeasureRoundsTranslate, TranslateTransform.YProperty, 16.0, 0.0, 0.44, 0.3, measureEnterEase);
        StartDoubleAnimation(MeasureFooterHost, UIElement.OpacityProperty, 0.0, 1.0, 0.44, 0.38, measureEnterEase);
        StartDoubleAnimation(MeasureFooterTranslate, TranslateTransform.YProperty, 12.0, 0.0, 0.44, 0.38, measureEnterEase);
        double durationSeconds = Math.Max(0.3, 0.8200000000000001) + 0.02;
        StartMeasureTransitionTimer(durationSeconds, onCompleted);
    }

    private void FinalizeMeasureForwardTransition()
    {
        ResetSetupTransitionPresentation();
        ResetMeasureVisuals();
        _isMeasureForwardTransitionActive = false;
    }

    private void PlayMeasureEntryTransition()
    {
        if (MeasureSectionHost == null)
        {
            return;
        }

        ResetMeasureVisuals();
        UpdateLayout();

        Vector sourceWordOffset = EnsureMinimumTransitionOffset(
            ComputeRelativeOffset(SetupHeroTopLeftWord, MeasureHeroLeftWord, MeasureSectionHost),
            new Vector(-56.0, -24.0),
            MeasureMinimumTransitionTravel);
        Vector targetWordOffset = EnsureMinimumTransitionOffset(
            ComputeRelativeOffset(SetupHeroRightLabel, MeasureHeroRightWord, MeasureSectionHost),
            new Vector(56.0, 24.0),
            MeasureMinimumTransitionTravel);

        if (MeasureHeroLeftWord != null)
        {
            MeasureHeroLeftWord.Opacity = MeasureWatermarkLeadOpacity;
        }
        if (MeasureHeroRightWord != null)
        {
            MeasureHeroRightWord.Opacity = MeasureWatermarkLeadOpacity;
        }
        if (MeasureSourceScale != null)
        {
            MeasureSourceScale.ScaleX = MeasureWatermarkLeadScale;
            MeasureSourceScale.ScaleY = MeasureWatermarkLeadScale;
        }
        if (MeasureTargetScale != null)
        {
            MeasureTargetScale.ScaleX = MeasureWatermarkLeadScale;
            MeasureTargetScale.ScaleY = MeasureWatermarkLeadScale;
        }
        if (MeasureSourceTranslate != null)
        {
            MeasureSourceTranslate.X = sourceWordOffset.X;
            MeasureSourceTranslate.Y = sourceWordOffset.Y;
        }
        if (MeasureTargetTranslate != null)
        {
            MeasureTargetTranslate.X = targetWordOffset.X;
            MeasureTargetTranslate.Y = targetWordOffset.Y;
        }
        if (MeasureDataLayer != null)
        {
            MeasureDataLayer.Opacity = 0.0;
        }
        if (MeasureDataTranslate != null)
        {
            MeasureDataTranslate.Y = 24.0;
        }
        if (MeasureRoundsList != null)
        {
            MeasureRoundsList.Opacity = 0.0;
        }
        if (MeasureRoundsTranslate != null)
        {
            MeasureRoundsTranslate.Y = 18.0;
        }
        if (MeasureFooterHost != null)
        {
            MeasureFooterHost.Opacity = 0.0;
        }
        if (MeasureFooterTranslate != null)
        {
            MeasureFooterTranslate.Y = 14.0;
        }

        CubicEase watermarkEase = new CubicEase
        {
            EasingMode = EasingMode.EaseOut
        };
        CubicEase contentEase = new CubicEase
        {
            EasingMode = EasingMode.EaseOut
        };

        StartDoubleAnimation(MeasureHeroLeftWord, UIElement.OpacityProperty, MeasureWatermarkLeadOpacity, MeasureWatermarkSettledOpacity, MeasureWatermarkRevealDurationSeconds, 0.0, watermarkEase);
        StartDoubleAnimation(MeasureHeroRightWord, UIElement.OpacityProperty, MeasureWatermarkLeadOpacity, MeasureWatermarkSettledOpacity, MeasureWatermarkRevealDurationSeconds, 0.03, watermarkEase);
        StartDoubleAnimation(MeasureSourceScale, ScaleTransform.ScaleXProperty, MeasureWatermarkLeadScale, 1.0, MeasureWatermarkRevealDurationSeconds, 0.0, watermarkEase);
        StartDoubleAnimation(MeasureSourceScale, ScaleTransform.ScaleYProperty, MeasureWatermarkLeadScale, 1.0, MeasureWatermarkRevealDurationSeconds, 0.0, watermarkEase);
        StartDoubleAnimation(MeasureTargetScale, ScaleTransform.ScaleXProperty, MeasureWatermarkLeadScale, 1.0, MeasureWatermarkRevealDurationSeconds, 0.03, watermarkEase);
        StartDoubleAnimation(MeasureTargetScale, ScaleTransform.ScaleYProperty, MeasureWatermarkLeadScale, 1.0, MeasureWatermarkRevealDurationSeconds, 0.03, watermarkEase);
        StartDoubleAnimation(MeasureSourceTranslate, TranslateTransform.XProperty, sourceWordOffset.X, 0.0, MeasureWatermarkRevealDurationSeconds, 0.0, watermarkEase);
        StartDoubleAnimation(MeasureSourceTranslate, TranslateTransform.YProperty, sourceWordOffset.Y, 0.0, MeasureWatermarkRevealDurationSeconds, 0.0, watermarkEase);
        StartDoubleAnimation(MeasureTargetTranslate, TranslateTransform.XProperty, targetWordOffset.X, 0.0, MeasureWatermarkRevealDurationSeconds, 0.03, watermarkEase);
        StartDoubleAnimation(MeasureTargetTranslate, TranslateTransform.YProperty, targetWordOffset.Y, 0.0, MeasureWatermarkRevealDurationSeconds, 0.03, watermarkEase);
        StartDoubleAnimation(MeasureDataLayer, UIElement.OpacityProperty, 0.0, 1.0, MeasureContentRevealDurationSeconds, 0.12, contentEase);
        StartDoubleAnimation(MeasureDataTranslate, TranslateTransform.YProperty, 24.0, 0.0, MeasureContentRevealDurationSeconds, 0.12, contentEase);
        StartDoubleAnimation(MeasureRoundsList, UIElement.OpacityProperty, 0.0, 1.0, MeasureContentRevealDurationSeconds, 0.2, contentEase);
        StartDoubleAnimation(MeasureRoundsTranslate, TranslateTransform.YProperty, 18.0, 0.0, MeasureContentRevealDurationSeconds, 0.2, contentEase);
        StartDoubleAnimation(MeasureFooterHost, UIElement.OpacityProperty, 0.0, 1.0, MeasureContentRevealDurationSeconds, 0.28, contentEase);
        StartDoubleAnimation(MeasureFooterTranslate, TranslateTransform.YProperty, 14.0, 0.0, MeasureContentRevealDurationSeconds, 0.28, contentEase);
    }

    private void PlayMeasureExitTransition(Action onCompleted)
    {
        if (MeasureSectionHost == null || MeasureFooterHost == null || SetupSectionHost == null || SetupFooterHost == null)
        {
            onCompleted?.Invoke();
            return;
        }
        StopMeasureTransitionTimer();
        StopMeasureAnimations();
        SetupFooterHost.SetValue(UIElement.VisibilityProperty, Visibility.Visible);
        SetupSectionHost.Opacity = 1.0;
        UpdateLayout();
        EnableMeasureTransitionVisualStability();
        SetTransitionHostVisualState(MeasureSectionHost, isInteractive: false);
        SetTransitionHostVisualState(MeasureFooterHost, isInteractive: false);
        SetTransitionHostVisualState(SetupSectionHost, isInteractive: false);
        SetTransitionHostVisualState(SetupFooterHost, isInteractive: false);
        if (SetupSettledLayer != null)
        {
            SetupSettledLayer.Opacity = 0.0;
        }
        if (SetupUtilityOverlay != null)
        {
            SetupUtilityOverlay.Opacity = 0.0;
        }
        if (SetupUtilityTranslate != null)
        {
            SetupUtilityTranslate.Y = 12.0;
        }
        SetRevealState(SetupSourceInfoBandHost, SetupSourceInfoBandTranslate, 0.0, 0.0, MeasureSetupInfoBandRevealOffset);
        SetRevealState(SetupTargetInfoBandHost, SetupTargetInfoBandTranslate, 0.0, 0.0, MeasureSetupInfoBandRevealOffset);
        SetupFooterHost.Opacity = 0.0;
        if (SetupFooterTranslate != null)
        {
            SetupFooterTranslate.Y = 12.0;
        }
        if (MeasureHeroLeftWord != null)
        {
            MeasureHeroLeftWord.Opacity = 0.0;
        }
        if (MeasureHeroRightWord != null)
        {
            MeasureHeroRightWord.Opacity = 0.0;
        }
        PrepareMeasureWordTransitionOverlay(isForward: false, 0.04);
        if (MeasureDataLayer != null)
        {
            MeasureDataLayer.Opacity = 1.0;
        }
        if (MeasureDataTranslate != null)
        {
            MeasureDataTranslate.Y = 0.0;
        }
        if (MeasureRoundsList != null)
        {
            MeasureRoundsList.Opacity = 1.0;
        }
        if (MeasureRoundsTranslate != null)
        {
            MeasureRoundsTranslate.Y = 0.0;
        }
        if (MeasureFooterHost != null)
        {
            MeasureFooterHost.Opacity = 1.0;
        }
        if (MeasureFooterTranslate != null)
        {
            MeasureFooterTranslate.Y = 0.0;
        }
        CubicEase setupEnterEase = new CubicEase
        {
            EasingMode = EasingMode.EaseOut
        };
        SineEase wordOverlayEase = new SineEase
        {
            EasingMode = EasingMode.EaseInOut
        };
        CubicEase measureExitEase = new CubicEase
        {
            EasingMode = EasingMode.EaseIn
        };
        CubicEase infoBandEnterEase = new CubicEase
        {
            EasingMode = EasingMode.EaseOut
        };
        StartDoubleAnimation(SetupUtilityOverlay, UIElement.OpacityProperty, 0.0, 1.0, MeasureUtilityFadeDurationSeconds, MeasureUtilityFadeDurationSeconds, setupEnterEase);
        StartDoubleAnimation(SetupUtilityTranslate, TranslateTransform.YProperty, 12.0, 0.0, MeasureUtilityFadeDurationSeconds, MeasureUtilityFadeDurationSeconds, setupEnterEase);
        StartDoubleAnimation(SetupFooterHost, UIElement.OpacityProperty, 0.0, 1.0, MeasureSetupFooterFadeDurationSeconds, 0.32, setupEnterEase);
        StartDoubleAnimation(SetupFooterTranslate, TranslateTransform.YProperty, 12.0, 0.0, MeasureSetupFooterFadeDurationSeconds, 0.32, setupEnterEase);
        StartDoubleAnimation(SetupSourceInfoBandHost, UIElement.OpacityProperty, 0.0, 1.0, MeasureSetupInfoBandRevealDurationSeconds, MeasureSetupInfoBandRevealDurationSeconds, infoBandEnterEase);
        StartDoubleAnimation(SetupSourceInfoBandTranslate, TranslateTransform.YProperty, MeasureSetupInfoBandRevealOffset, 0.0, MeasureSetupInfoBandRevealDurationSeconds, MeasureSetupInfoBandRevealDurationSeconds, infoBandEnterEase);
        StartDoubleAnimation(SetupTargetInfoBandHost, UIElement.OpacityProperty, 0.0, 1.0, MeasureSetupInfoBandRevealDurationSeconds, MeasureSetupInfoBandRevealDurationSeconds, infoBandEnterEase);
        StartDoubleAnimation(SetupTargetInfoBandTranslate, TranslateTransform.YProperty, MeasureSetupInfoBandRevealOffset, 0.0, MeasureSetupInfoBandRevealDurationSeconds, MeasureSetupInfoBandRevealDurationSeconds, infoBandEnterEase);
        AnimateMeasureWordTransitionOverlay(isForward: false, wordOverlayEase, MeasureExitWatermarkBeginSeconds, MeasureWatermarkSettledOpacity, 1.0);
        StartDoubleAnimation(MeasureDataLayer, UIElement.OpacityProperty, 1.0, 0.0, MeasureExitContentDurationSeconds, 0.0, measureExitEase);
        StartDoubleAnimation(MeasureDataTranslate, TranslateTransform.YProperty, 0.0, 20.0, MeasureExitContentDurationSeconds, 0.0, measureExitEase);
        StartDoubleAnimation(MeasureRoundsList, UIElement.OpacityProperty, 1.0, 0.0, MeasureExitContentDurationSeconds, MeasureExitRoundsBeginSeconds, measureExitEase);
        StartDoubleAnimation(MeasureRoundsTranslate, TranslateTransform.YProperty, 0.0, MeasureSetupInfoBandRevealOffset, MeasureExitContentDurationSeconds, MeasureExitRoundsBeginSeconds, measureExitEase);
        StartDoubleAnimation(MeasureFooterHost, UIElement.OpacityProperty, 1.0, 0.0, MeasureExitContentDurationSeconds, MeasureExitFooterBeginSeconds, measureExitEase);
        StartDoubleAnimation(MeasureFooterTranslate, TranslateTransform.YProperty, 0.0, 12.0, MeasureExitContentDurationSeconds, MeasureExitFooterBeginSeconds, measureExitEase);
        double durationSeconds = Math.Max(MeasureWatermarkRevealDurationSeconds, SetupFooterRevealEndSeconds - SetupUtilityRevealStartSeconds) + 0.02;
        StartMeasureTransitionTimer(durationSeconds, onCompleted);
    }


    private void PrepareMeasureWordTransitionOverlay(bool isForward, double initialOpacity)
    {
        if (MeasureTransitionOverlay != null)
        {
            MeasureTransitionOverlay.Visibility = Visibility.Visible;
            if (isForward)
            {
                PrepareMeasureWordTransitionToken(MeasureTransitionLeftWord, MeasureTransitionLeftScale, MeasureTransitionLeftTranslate, SetupHeroTopLeftWord, MeasureHeroLeftWord, initialOpacity);
                PrepareMeasureWordTransitionToken(MeasureTransitionRightWord, MeasureTransitionRightScale, MeasureTransitionRightTranslate, SetupHeroRightLabel, MeasureHeroRightWord, initialOpacity);
            }
            else
            {
                PrepareMeasureWordTransitionToken(MeasureTransitionLeftWord, MeasureTransitionLeftScale, MeasureTransitionLeftTranslate, MeasureHeroLeftWord, SetupHeroTopLeftWord, initialOpacity);
                PrepareMeasureWordTransitionToken(MeasureTransitionRightWord, MeasureTransitionRightScale, MeasureTransitionRightTranslate, MeasureHeroRightWord, SetupHeroRightLabel, initialOpacity);
            }
        }
    }

    private void PrepareMeasureWordTransitionToken(TextBlock overlayWord, ScaleTransform overlayScale, TranslateTransform overlayTranslate, TextBlock fromElement, TextBlock toElement, double initialOpacity)
    {
        if (overlayWord == null || overlayScale == null || overlayTranslate == null || fromElement == null || toElement == null || MeasureTransitionOverlay == null)
        {
            return;
        }

        Point startPoint = ComputeRelativePoint(fromElement, MeasureTransitionOverlay);
        overlayWord.Text = fromElement.Text;
        Canvas.SetLeft(overlayWord, startPoint.X);
        Canvas.SetTop(overlayWord, startPoint.Y);
        overlayScale.ScaleX = ComputeOverlayScale(fromElement, overlayWord);
        overlayScale.ScaleY = overlayScale.ScaleX;
        overlayTranslate.X = 0.0;
        overlayTranslate.Y = 0.0;
        overlayWord.Opacity = Clamp(initialOpacity, 0.0, 1.0);
        overlayWord.Visibility = Visibility.Visible;
    }

    private void AnimateMeasureWordTransitionOverlay(bool isForward, IEasingFunction easing, double beginTimeSeconds, double fromOpacity, double toOpacity)
    {
        if (MeasureTransitionOverlay != null)
        {
            if (isForward)
            {
                AnimateMeasureWordTransitionToken(MeasureTransitionLeftWord, MeasureTransitionLeftScale, MeasureTransitionLeftTranslate, SetupHeroTopLeftWord, MeasureHeroLeftWord, beginTimeSeconds, fromOpacity, toOpacity, easing);
                AnimateMeasureWordTransitionToken(MeasureTransitionRightWord, MeasureTransitionRightScale, MeasureTransitionRightTranslate, SetupHeroRightLabel, MeasureHeroRightWord, beginTimeSeconds, fromOpacity, toOpacity, easing);
            }
            else
            {
                AnimateMeasureWordTransitionToken(MeasureTransitionLeftWord, MeasureTransitionLeftScale, MeasureTransitionLeftTranslate, MeasureHeroLeftWord, SetupHeroTopLeftWord, beginTimeSeconds, fromOpacity, toOpacity, easing);
                AnimateMeasureWordTransitionToken(MeasureTransitionRightWord, MeasureTransitionRightScale, MeasureTransitionRightTranslate, MeasureHeroRightWord, SetupHeroRightLabel, beginTimeSeconds, fromOpacity, toOpacity, easing);
            }
        }
    }

    private void AnimateMeasureWordTransitionToken(TextBlock overlayWord, ScaleTransform overlayScale, TranslateTransform overlayTranslate, TextBlock fromElement, TextBlock toElement, double beginTimeSeconds, double fromOpacity, double targetOpacity, IEasingFunction easing)
    {
        if (overlayWord == null || overlayScale == null || overlayTranslate == null || fromElement == null || toElement == null || MeasureTransitionOverlay == null)
        {
            return;
        }

        Point startPoint = ComputeRelativePoint(fromElement, MeasureTransitionOverlay);
        Point endPoint = ComputeRelativePoint(toElement, MeasureTransitionOverlay);
        double startScale = ComputeOverlayScale(fromElement, overlayWord);
        double endScale = ComputeOverlayScale(toElement, overlayWord);
        Canvas.SetLeft(overlayWord, startPoint.X);
        Canvas.SetTop(overlayWord, startPoint.Y);
        StartDoubleAnimation(overlayScale, ScaleTransform.ScaleXProperty, startScale, endScale, MeasureWatermarkRevealDurationSeconds, beginTimeSeconds, easing);
        StartDoubleAnimation(overlayScale, ScaleTransform.ScaleYProperty, startScale, endScale, MeasureWatermarkRevealDurationSeconds, beginTimeSeconds, easing);
        StartDoubleAnimation(overlayTranslate, TranslateTransform.XProperty, 0.0, endPoint.X - startPoint.X, MeasureWatermarkRevealDurationSeconds, beginTimeSeconds, easing);
        StartDoubleAnimation(overlayTranslate, TranslateTransform.YProperty, 0.0, endPoint.Y - startPoint.Y, MeasureWatermarkRevealDurationSeconds, beginTimeSeconds, easing);
        StartDoubleAnimation(overlayWord, UIElement.OpacityProperty, Clamp(fromOpacity, 0.0, 1.0), targetOpacity, MeasureWatermarkRevealDurationSeconds, beginTimeSeconds, easing);
    }

    private void HideMeasureTransitionOverlay()
    {
        if (MeasureTransitionOverlay != null)
        {
            MeasureTransitionOverlay.Visibility = Visibility.Collapsed;
            ResetMeasureTransitionToken(MeasureTransitionLeftWord, MeasureTransitionLeftScale, MeasureTransitionLeftTranslate);
            ResetMeasureTransitionToken(MeasureTransitionRightWord, MeasureTransitionRightScale, MeasureTransitionRightTranslate);
        }
    }

    private static void ResetMeasureTransitionToken(TextBlock overlayWord, ScaleTransform overlayScale, TranslateTransform overlayTranslate)
    {
        if (overlayWord != null)
        {
            overlayWord.Opacity = 0.0;
            overlayWord.Visibility = Visibility.Collapsed;
            Canvas.SetLeft(overlayWord, 0.0);
            Canvas.SetTop(overlayWord, 0.0);
        }
        if (overlayScale != null)
        {
            overlayScale.ScaleX = 1.0;
            overlayScale.ScaleY = 1.0;
        }
        if (overlayTranslate != null)
        {
            overlayTranslate.X = 0.0;
            overlayTranslate.Y = 0.0;
        }
    }

    private void ResetMeasureVisuals()
    {
        HideMeasureTransitionOverlay();
        StopMeasureTransitionTimer();
        StopMeasureAnimations();
        DisableMeasureTransitionVisualStability();
        _isMeasureForwardTransitionActive = false;
        _isMeasureBackTransitionActive = false;
        if (MeasureSectionHost != null)
        {
            MeasureSectionHost.ClearValue(UIElement.VisibilityProperty);
            MeasureSectionHost.ClearValue(UIElement.IsHitTestVisibleProperty);
            MeasureSectionHost.ClearValue(UIElement.IsEnabledProperty);
        }
        if (MeasureFooterHost != null)
        {
            MeasureFooterHost.ClearValue(UIElement.VisibilityProperty);
            MeasureFooterHost.ClearValue(UIElement.IsHitTestVisibleProperty);
            MeasureFooterHost.ClearValue(UIElement.IsEnabledProperty);
        }
        if (MeasureHeroLeftWord != null)
        {
            MeasureHeroLeftWord.Opacity = 0.04;
        }
        if (MeasureHeroRightWord != null)
        {
            MeasureHeroRightWord.Opacity = 0.04;
        }
        if (MeasureSourceScale != null)
        {
            MeasureSourceScale.ScaleX = 1.0;
            MeasureSourceScale.ScaleY = 1.0;
        }
        if (MeasureTargetScale != null)
        {
            MeasureTargetScale.ScaleX = 1.0;
            MeasureTargetScale.ScaleY = 1.0;
        }
        if (MeasureSourceTranslate != null)
        {
            MeasureSourceTranslate.X = 0.0;
            MeasureSourceTranslate.Y = 0.0;
        }
        if (MeasureTargetTranslate != null)
        {
            MeasureTargetTranslate.X = 0.0;
            MeasureTargetTranslate.Y = 0.0;
        }
        if (MeasureDataLayer != null)
        {
            MeasureDataLayer.Opacity = 1.0;
        }
        if (MeasureDataTranslate != null)
        {
            MeasureDataTranslate.Y = 0.0;
        }
        if (MeasureRoundsList != null)
        {
            MeasureRoundsList.Opacity = 1.0;
        }
        if (MeasureRoundsTranslate != null)
        {
            MeasureRoundsTranslate.Y = 0.0;
        }
        if (MeasureFooterHost != null)
        {
            MeasureFooterHost.Opacity = 1.0;
        }
        if (MeasureFooterTranslate != null)
        {
            MeasureFooterTranslate.Y = 0.0;
        }
        SyncMeasureCenterPresentation(animate: false);
    }

    private void StartMeasureTransitionTimer(double durationSeconds, Action onCompleted)
    {
        StopMeasureTransitionTimer();
        _measureTransitionCompletion = onCompleted;
        _measureTransitionTimer = new DispatcherTimer(DispatcherPriority.Loaded, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(Math.Max(0.01, durationSeconds))
        };
        _measureTransitionTimer.Tick += MeasureTransitionTimer_Tick;
        _measureTransitionTimer.Start();
    }

    private void MeasureTransitionTimer_Tick(object sender, EventArgs e)
    {
        Action measureTransitionCompletion = _measureTransitionCompletion;
        StopMeasureTransitionTimer();
        measureTransitionCompletion?.Invoke();
    }

    private void StopMeasureTransitionTimer()
    {
        if (_measureTransitionTimer != null)
        {
            _measureTransitionTimer.Tick -= MeasureTransitionTimer_Tick;
            _measureTransitionTimer.Stop();
            _measureTransitionTimer = null;
        }
        _measureTransitionCompletion = null;
    }

    private void StopMeasureAnimations()
    {
        StopAnimation(SetupSettledLayer, UIElement.OpacityProperty);
        StopAnimation(SetupUtilityOverlay, UIElement.OpacityProperty);
        StopAnimation(SetupUtilityTranslate, TranslateTransform.YProperty);
        StopAnimation(SetupSourceInfoBandHost, UIElement.OpacityProperty);
        StopAnimation(SetupSourceInfoBandTranslate, TranslateTransform.YProperty);
        StopAnimation(SetupTargetInfoBandHost, UIElement.OpacityProperty);
        StopAnimation(SetupTargetInfoBandTranslate, TranslateTransform.YProperty);
        StopAnimation(SetupFooterHost, UIElement.OpacityProperty);
        StopAnimation(SetupFooterTranslate, TranslateTransform.YProperty);
        StopAnimation(MeasureTransitionLeftWord, UIElement.OpacityProperty);
        StopAnimation(MeasureTransitionLeftScale, ScaleTransform.ScaleXProperty);
        StopAnimation(MeasureTransitionLeftScale, ScaleTransform.ScaleYProperty);
        StopAnimation(MeasureTransitionLeftTranslate, TranslateTransform.XProperty);
        StopAnimation(MeasureTransitionLeftTranslate, TranslateTransform.YProperty);
        StopAnimation(MeasureTransitionRightWord, UIElement.OpacityProperty);
        StopAnimation(MeasureTransitionRightScale, ScaleTransform.ScaleXProperty);
        StopAnimation(MeasureTransitionRightScale, ScaleTransform.ScaleYProperty);
        StopAnimation(MeasureTransitionRightTranslate, TranslateTransform.XProperty);
        StopAnimation(MeasureTransitionRightTranslate, TranslateTransform.YProperty);
        StopAnimation(MeasureHeroLeftWord, UIElement.OpacityProperty);
        StopAnimation(MeasureHeroRightWord, UIElement.OpacityProperty);
        StopAnimation(MeasureSourceScale, ScaleTransform.ScaleXProperty);
        StopAnimation(MeasureSourceScale, ScaleTransform.ScaleYProperty);
        StopAnimation(MeasureTargetScale, ScaleTransform.ScaleXProperty);
        StopAnimation(MeasureTargetScale, ScaleTransform.ScaleYProperty);
        StopAnimation(MeasureSourceTranslate, TranslateTransform.XProperty);
        StopAnimation(MeasureSourceTranslate, TranslateTransform.YProperty);
        StopAnimation(MeasureTargetTranslate, TranslateTransform.XProperty);
        StopAnimation(MeasureTargetTranslate, TranslateTransform.YProperty);
        StopAnimation(MeasureDataLayer, UIElement.OpacityProperty);
        StopAnimation(MeasureDataTranslate, TranslateTransform.YProperty);
        StopAnimation(MeasureRoundsList, UIElement.OpacityProperty);
        StopAnimation(MeasureRoundsTranslate, TranslateTransform.YProperty);
        StopAnimation(MeasureFooterHost, UIElement.OpacityProperty);
        StopAnimation(MeasureFooterTranslate, TranslateTransform.YProperty);
        StopAnimation(MeasureCenterRoundHost, UIElement.OpacityProperty);
        StopAnimation(MeasureCenterRoundTranslate, TranslateTransform.YProperty);
        StopAnimation(MeasureCenterResultHost, UIElement.OpacityProperty);
        StopAnimation(MeasureCenterResultTranslate, TranslateTransform.YProperty);
    }

    private static Vector ComputeRelativeOffset(FrameworkElement fromElement, FrameworkElement toElement, UIElement relativeTo)
    {
        if (fromElement == null || toElement == null || relativeTo == null)
        {
            return default;
        }

        try
        {
            Point fromPoint = fromElement.TranslatePoint(new Point(0.0, 0.0), relativeTo);
            Point toPoint = toElement.TranslatePoint(new Point(0.0, 0.0), relativeTo);
            return fromPoint - toPoint;
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static Point ComputeRelativePoint(FrameworkElement fromElement, UIElement relativeTo)
    {
        if (fromElement == null || relativeTo == null)
        {
            return default;
        }

        try
        {
            return fromElement.TranslatePoint(new Point(0.0, 0.0), relativeTo);
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static Vector EnsureMinimumTransitionOffset(Vector offset, Vector fallbackVector, double minimumDistance)
    {
        if (offset.Length < minimumDistance)
        {
            return fallbackVector;
        }
        return offset;
    }

    private static double ComputeOverlayScale(TextBlock fromText, TextBlock overlayWord)
    {
        if (fromText == null || overlayWord == null || overlayWord.FontSize <= 0.0)
        {
            return 1.0;
        }
        return Clamp(fromText.FontSize / overlayWord.FontSize, 0.45, 1.6);
    }

    private static double ComputeAnchorScale(TextBlock fromText, TextBlock toText)
    {
        if (fromText == null || toText == null || toText.FontSize <= 0.0)
        {
            return 1.0;
        }
        return Clamp(fromText.FontSize / toText.FontSize, 0.72, 1.4);
    }

    private static void StartDoubleAnimation(UIElement target, DependencyProperty property, double fromValue, double toValue, double durationSeconds, double beginTimeSeconds = 0.0, IEasingFunction easing = null)
    {
        target?.BeginAnimation(property, CreateDoubleAnimation(fromValue, toValue, durationSeconds, beginTimeSeconds, easing));
    }

    private static void StartDoubleAnimation(Animatable target, DependencyProperty property, double fromValue, double toValue, double durationSeconds, double beginTimeSeconds = 0.0, IEasingFunction easing = null)
    {
        target?.BeginAnimation(property, CreateDoubleAnimation(fromValue, toValue, durationSeconds, beginTimeSeconds, easing));
    }

    private static DoubleAnimation CreateDoubleAnimation(double fromValue, double toValue, double durationSeconds, double beginTimeSeconds, IEasingFunction easing)
    {
        return new DoubleAnimation
        {
            From = fromValue,
            To = toValue,
            Duration = TimeSpan.FromSeconds(Math.Max(0.001, durationSeconds)),
            BeginTime = TimeSpan.FromSeconds(Math.Max(0.0, beginTimeSeconds)),
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
    }

    private static void StopAnimation(UIElement target, DependencyProperty property)
    {
        target?.BeginAnimation(property, null);
    }

    private static void StopAnimation(Animatable target, DependencyProperty property)
    {
        target?.BeginAnimation(property, null);
    }

    private void EnableMeasureTransitionVisualStability()
    {
    }

    private void DisableMeasureTransitionVisualStability()
    {
    }

    private IEnumerable<DependencyObject> GetMeasureTransitionTextRoots()
    {
        return new DependencyObject[5] { SetupSectionHost, SetupFooterHost, MeasureSectionHost, MeasureFooterHost, MeasureTransitionOverlay };
    }

    private IEnumerable<UIElement> GetMeasureTransitionCacheHosts()
    {
        return new UIElement[11]
        {
            SetupUtilityOverlay, SetupSourceInfoBandHost, SetupTargetInfoBandHost, SetupFooterHost, MeasureDataLayer, MeasureRoundsList, MeasureFooterHost, MeasureTransitionLeftWord, MeasureTransitionRightWord, MeasureHeroLeftWord,
            MeasureHeroRightWord
        };
    }

    private void ApplyMeasureTransitionTextRenderingOverride(DependencyObject root)
    {
        ApplyTextRenderingOverride(root, _measureTransitionTextRenderingStates);
    }

    private void RestoreMeasureTransitionTextRenderingOverrides()
    {
        foreach (KeyValuePair<DependencyObject, MeasureTransitionTextRenderingState> measureTransitionTextRenderingState in _measureTransitionTextRenderingStates)
        {
            RestoreDependencyProperty(measureTransitionTextRenderingState.Key, TextOptions.TextRenderingModeProperty, measureTransitionTextRenderingState.Value.HasLocalTextRenderingMode, measureTransitionTextRenderingState.Value.TextRenderingMode);
            RestoreDependencyProperty(measureTransitionTextRenderingState.Key, TextOptions.TextHintingModeProperty, measureTransitionTextRenderingState.Value.HasLocalTextHintingMode, measureTransitionTextRenderingState.Value.TextHintingMode);
            RestoreDependencyProperty(measureTransitionTextRenderingState.Key, RenderOptions.ClearTypeHintProperty, measureTransitionTextRenderingState.Value.HasLocalClearTypeHint, measureTransitionTextRenderingState.Value.ClearTypeHint);
            if (measureTransitionTextRenderingState.Key is UIElement target)
            {
                RestoreDependencyProperty(target, UIElement.SnapsToDevicePixelsProperty, measureTransitionTextRenderingState.Value.HasLocalSnapsToDevicePixels, measureTransitionTextRenderingState.Value.SnapsToDevicePixels);
            }
            if (measureTransitionTextRenderingState.Key is FrameworkElement target2)
            {
                RestoreDependencyProperty(target2, FrameworkElement.UseLayoutRoundingProperty, measureTransitionTextRenderingState.Value.HasLocalUseLayoutRounding, measureTransitionTextRenderingState.Value.UseLayoutRounding);
            }
        }
        _measureTransitionTextRenderingStates.Clear();
    }

    private static MeasureTransitionTextRenderingState CaptureMeasureTransitionTextRenderingState(DependencyObject target)
    {
        UIElement uIElement = target as UIElement;
        FrameworkElement frameworkElement = target as FrameworkElement;
        return new MeasureTransitionTextRenderingState
        {
            HasLocalTextRenderingMode = HasLocalValue(target, TextOptions.TextRenderingModeProperty),
            TextRenderingMode = TextOptions.GetTextRenderingMode(target),
            HasLocalTextHintingMode = HasLocalValue(target, TextOptions.TextHintingModeProperty),
            TextHintingMode = TextOptions.GetTextHintingMode(target),
            HasLocalClearTypeHint = HasLocalValue(target, RenderOptions.ClearTypeHintProperty),
            ClearTypeHint = RenderOptions.GetClearTypeHint(target),
            HasLocalSnapsToDevicePixels = (uIElement != null && HasLocalValue(uIElement, UIElement.SnapsToDevicePixelsProperty)),
            SnapsToDevicePixels = (uIElement?.SnapsToDevicePixels ?? false),
            HasLocalUseLayoutRounding = (frameworkElement != null && HasLocalValue(frameworkElement, FrameworkElement.UseLayoutRoundingProperty)),
            UseLayoutRounding = (frameworkElement?.UseLayoutRounding ?? false)
        };
    }

    private void ApplyMeasureTransitionBitmapCache(UIElement host)
    {
        if (host != null)
        {
            if (!_measureTransitionCacheStates.ContainsKey(host))
            {
                _measureTransitionCacheStates[host] = CaptureMeasureTransitionCacheState(host);
            }
            DpiScale dpi = VisualTreeHelper.GetDpi(host);
            host.CacheMode = new BitmapCache
            {
                EnableClearType = false,
                RenderAtScale = Math.Max(1.0, Math.Max(dpi.DpiScaleX, dpi.DpiScaleY))
            };
        }
    }

    private void RestoreMeasureTransitionBitmapCaches()
    {
        foreach (KeyValuePair<UIElement, MeasureTransitionCacheState> measureTransitionCacheState in _measureTransitionCacheStates)
        {
            RestoreDependencyProperty(measureTransitionCacheState.Key, UIElement.CacheModeProperty, measureTransitionCacheState.Value.HasLocalCacheMode, measureTransitionCacheState.Value.CacheMode);
        }
        _measureTransitionCacheStates.Clear();
    }

    private static MeasureTransitionCacheState CaptureMeasureTransitionCacheState(UIElement host)
    {
        return new MeasureTransitionCacheState
        {
            HasLocalCacheMode = HasLocalValue(host, UIElement.CacheModeProperty),
            CacheMode = host.CacheMode
        };
    }

    private static bool HasLocalValue(DependencyObject target, DependencyProperty property)
    {
        if (target == null || property == null)
        {
            return false;
        }
        return DependencyPropertyHelper.GetValueSource(target, property).BaseValueSource == BaseValueSource.Local;
    }

    private static void RestoreDependencyProperty(DependencyObject target, DependencyProperty property, bool hadLocalValue, object value)
    {
        if (target != null && property != null)
        {
            if (hadLocalValue)
            {
                target.SetValue(property, value);
            }
            else
            {
                target.ClearValue(property);
            }
        }
    }

    private void SetAnimatedTextRenderingState(bool isAnimated)
    {
        if (isAnimated)
        {
            foreach (DependencyObject root in GetAnimatedTextRoots())
            {
                ApplyAnimatedTextRenderingOverride(root);
            }
            return;
        }

        RestoreAnimatedTextRenderingOverrides();
    }

    private IEnumerable<DependencyObject> GetAnimatedTextRoots()
    {
        return new DependencyObject[3] { IntroOpeningGuideLayer, IntroMorphCanvas, IntroActionHost };
    }

    private void ApplyAnimatedTextRenderingOverride(DependencyObject root)
    {
        ApplyTextRenderingOverride(root, _animatedTextRenderingStates);
    }

    private static void ApplyTextRenderingOverride(DependencyObject root, IDictionary<DependencyObject, MeasureTransitionTextRenderingState> savedStates)
    {
        if (root == null || savedStates == null)
        {
            return;
        }

        Stack<DependencyObject> pendingNodes = new Stack<DependencyObject>();
        pendingNodes.Push(root);
        while (pendingNodes.Count > 0)
        {
            DependencyObject currentNode = pendingNodes.Pop();
            if (currentNode == null)
            {
                continue;
            }

            if (!savedStates.ContainsKey(currentNode))
            {
                savedStates[currentNode] = CaptureMeasureTransitionTextRenderingState(currentNode);
            }

            if (currentNode is UIElement uiElement)
            {
                uiElement.SnapsToDevicePixels = false;
            }
            if (currentNode is FrameworkElement frameworkElement)
            {
                frameworkElement.UseLayoutRounding = false;
            }

            for (int childIndex = VisualTreeHelper.GetChildrenCount(currentNode) - 1; childIndex >= 0; childIndex--)
            {
                pendingNodes.Push(VisualTreeHelper.GetChild(currentNode, childIndex));
            }
        }
    }

    private void RestoreAnimatedTextRenderingOverrides()
    {
        foreach (KeyValuePair<DependencyObject, MeasureTransitionTextRenderingState> animatedTextRenderingState in _animatedTextRenderingStates)
        {
            RestoreDependencyProperty(animatedTextRenderingState.Key, TextOptions.TextRenderingModeProperty, animatedTextRenderingState.Value.HasLocalTextRenderingMode, animatedTextRenderingState.Value.TextRenderingMode);
            RestoreDependencyProperty(animatedTextRenderingState.Key, TextOptions.TextHintingModeProperty, animatedTextRenderingState.Value.HasLocalTextHintingMode, animatedTextRenderingState.Value.TextHintingMode);
            RestoreDependencyProperty(animatedTextRenderingState.Key, RenderOptions.ClearTypeHintProperty, animatedTextRenderingState.Value.HasLocalClearTypeHint, animatedTextRenderingState.Value.ClearTypeHint);
            if (animatedTextRenderingState.Key is UIElement target)
            {
                RestoreDependencyProperty(target, UIElement.SnapsToDevicePixelsProperty, animatedTextRenderingState.Value.HasLocalSnapsToDevicePixels, animatedTextRenderingState.Value.SnapsToDevicePixels);
            }
            if (animatedTextRenderingState.Key is FrameworkElement target2)
            {
                RestoreDependencyProperty(target2, FrameworkElement.UseLayoutRoundingProperty, animatedTextRenderingState.Value.HasLocalUseLayoutRounding, animatedTextRenderingState.Value.UseLayoutRounding);
            }
        }
        _animatedTextRenderingStates.Clear();
    }

    private static double Clamp01(double value)
    {
        return Clamp(value, 0.0, 1.0);
    }

    private void ShowIntroMorphCanvas()
    {
        if (IntroMorphCanvas != null)
        {
            IntroMorphCanvas.Visibility = Visibility.Visible;
            IntroMorphCanvas.Opacity = 1.0;
        }
    }

    private void HideIntroMorphCanvas(bool clearTokens = false)
    {
        if (IntroMorphCanvas != null)
        {
            IntroMorphCanvas.Opacity = 0.0;
            IntroMorphCanvas.Visibility = Visibility.Hidden;
        }
        if (clearTokens)
        {
            ClearMorphTokens();
        }
    }

    private void ResetIntroMorphCanvas()
    {
        if (IntroMorphCanvas != null)
        {
            IntroMorphCanvas.Visibility = Visibility.Visible;
            IntroMorphCanvas.Opacity = 0.0;
        }
    }

    private void ShowIntroTransitionHosts()
    {
        if (IntroSectionHost != null)
        {
            IntroSectionHost.Visibility = Visibility.Visible;
        }
        if (IntroActionSectionHost != null)
        {
            IntroActionSectionHost.Visibility = Visibility.Visible;
        }
    }

    private void ClearIntroTransitionHosts()
    {
        if (IntroSectionHost != null)
        {
            IntroSectionHost.ClearValue(UIElement.VisibilityProperty);
        }
        if (IntroActionSectionHost != null)
        {
            IntroActionSectionHost.ClearValue(UIElement.VisibilityProperty);
        }
    }

    private void HideSetupSettledLayer()
    {
        if (SetupSettledLayer != null)
        {
            SetupSettledLayer.Opacity = 0.0;
            SetupSettledLayer.Visibility = Visibility.Hidden;
        }
    }

    private void ShowSetupSettledLayer()
    {
        if (SetupSettledLayer != null)
        {
            SetupSettledLayer.Visibility = Visibility.Visible;
            SetupSettledLayer.Opacity = 1.0;
        }
    }

    private void SetTransitionHostVisualState(UIElement host, bool isInteractive)
    {
        if (host != null)
        {
            host.SetValue(UIElement.IsEnabledProperty, true);
            host.SetValue(UIElement.IsHitTestVisibleProperty, isInteractive);
        }
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Max(minimum, Math.Min(maximum, value));
    }

    private static double EaseOutCubic(double value)
    {
        double progress = Clamp01(value);
        double remainingProgress = 1.0 - progress;
        return 1.0 - remainingProgress * remainingProgress * remainingProgress;
    }

    private static double EaseInOutCubic(double value)
    {
        double progress = Clamp01(value);
        if (progress < 0.5)
        {
            return 4.0 * progress * progress * progress;
        }

        double mirroredProgress = -2.0 * progress + 2.0;
        return 1.0 - mirroredProgress * mirroredProgress * mirroredProgress / 2.0;
    }

    private static double EaseInCubic(double value)
    {
        double progress = Clamp01(value);
        return progress * progress * progress;
    }

    private static double EaseInOutSine(double value)
    {
        double progress = Clamp01(value);
        return -(Math.Cos(Math.PI * progress) - 1.0) / 2.0;
    }

    private static double EaseSmootherStep(double value)
    {
        double progress = Clamp01(value);
        return progress * progress * progress * (progress * (progress * 6.0 - 15.0) + 10.0);
    }

    private static double StabilizeLandingProgress(double progress, double threshold)
    {
        double clampedProgress = Clamp01(progress);
        if (clampedProgress <= threshold)
        {
            return clampedProgress;
        }

        double tailProgress = Clamp01((clampedProgress - threshold) / Math.Max(1.0 - threshold, 0.0001));
        double stabilizedProgress = threshold + (1.0 - threshold) * EaseInOutSine(tailProgress);
        if (stabilizedProgress >= 0.99985)
        {
            return 1.0;
        }

        return stabilizedProgress;
    }

    private static double FinalizeMotionProgress(double progress, double snapThreshold)
    {
        double clampedProgress = Clamp01(progress);
        if (clampedProgress >= snapThreshold)
        {
            return 1.0;
        }

        return clampedProgress;
    }

    private static double Lerp(double startValue, double endValue, double progress)
    {
        return startValue + (endValue - startValue) * progress;
    }

    private static Point Lerp(Point startPoint, Point endPoint, double progress)
    {
        return new Point(Lerp(startPoint.X, endPoint.X, progress), Lerp(startPoint.Y, endPoint.Y, progress));
    }

    private static string NormalizeDpiText(string inputText)
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

    private static int CountDigitsBeforeIndex(string inputText, int index)
    {
        if (string.IsNullOrEmpty(inputText) || index <= 0)
        {
            return 0;
        }

        int safeCharacterCount = Math.Max(0, Math.Min(index, inputText.Length));
        int digitCount = 0;
        for (int characterIndex = 0; characterIndex < safeCharacterCount; characterIndex++)
        {
            if (char.IsDigit(inputText[characterIndex]))
            {
                digitCount++;
            }
        }

        return digitCount;
    }

}







