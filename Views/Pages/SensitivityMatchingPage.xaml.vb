Imports System.ComponentModel
Imports System.Runtime.Versioning
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media.Animation
Imports System.Windows.Threading
Imports WpfApp1.Services
Imports WpfApp1.ViewModels.Pages

Namespace Views.Pages
    <SupportedOSPlatform("windows")>
    Partial Public Class SensitivityMatchingPage
        Private Const IntroFadeInEndSeconds As Double = 0.18
        Private Const IntroMorphStartSeconds As Double = 0.78
        Private Const IntroMorphEndSeconds As Double = 1.55
        Private Const IntroCanvasFadeStartSeconds As Double = 1.54
        Private Const IntroCanvasFadeEndSeconds As Double = 1.78
        Private Const IntroActionRevealStartSeconds As Double = 1.55
        Private Const IntroActionRevealEndSeconds As Double = 1.9
        Private Const IntroTotalDurationSeconds As Double = 1.9
        Private Const IntroOpeningSafePadding As Double = 34.0
        Private Const SetupTransitionMorphStartSeconds As Double = 0.16
        Private Const SetupTransitionMorphEndSeconds As Double = 1.52
        Private Const SetupTransitionTotalDurationSeconds As Double = 1.86
        Private Const SetupTopLeftEnterDelayAdvanceSeconds As Double = 0.01
        Private Const SetupTopLeftMotionDurationTrimSeconds As Double = 0.065
        Private Const SetupTopLeftExitAdvanceSeconds As Double = 0.04
        Private Const SetupTopLeftExitDurationTrimSeconds As Double = 0.02
        Private Const SetupUtilityRevealStartSeconds As Double = 0.8
        Private Const SetupUtilityRevealEndSeconds As Double = 1.42
        Private Const SetupSourcePanelRevealStartSeconds As Double = 0.86
        Private Const SetupSourcePanelRevealEndSeconds As Double = 1.28
        Private Const SetupTargetPanelRevealStartSeconds As Double = 0.92
        Private Const SetupTargetPanelRevealEndSeconds As Double = 1.34
        Private Const SetupSourceInfoRevealStartSeconds As Double = 0.98
        Private Const SetupSourceInfoRevealEndSeconds As Double = 1.44
        Private Const SetupTargetInfoRevealStartSeconds As Double = 1.04
        Private Const SetupTargetInfoRevealEndSeconds As Double = 1.5
        Private Const SetupFooterRevealStartSeconds As Double = 1.14
        Private Const SetupFooterRevealEndSeconds As Double = 1.62
        Private Const SetupToIntroActionRevealStartSeconds As Double = 1.18
        Private Const SetupToIntroActionRevealEndSeconds As Double = 1.66
        Private Const MeasureWatermarkRevealDurationSeconds As Double = 0.8
        Private Const MeasureContentRevealDurationSeconds As Double = 0.44
        Private Const MeasureWatermarkLeadOpacity As Double = 0.18
        Private Const MeasureWatermarkSettledOpacity As Double = 0.04
        Private Const MeasureWatermarkLeadScale As Double = 1.1
        Private Const MeasureMinimumTransitionTravel As Double = 36.0
        Private Const MeasureSetupFadeDurationSeconds As Double = 0.36
        Private Const MeasureSetupFooterFadeDurationSeconds As Double = 0.34
        Private Const MeasureUtilityFadeDurationSeconds As Double = 0.26
        Private Const MeasureUtilityFadeOffset As Double = 12.0
        Private Const MeasureSetupInfoBandRevealDurationSeconds As Double = 0.28
        Private Const MeasureSetupInfoBandRevealBeginSeconds As Double = 0.28
        Private Const MeasureSetupInfoBandRevealOffset As Double = 14.0
        Private Const MeasureExitContentDurationSeconds As Double = 0.34
        Private Const MeasureExitWatermarkBeginSeconds As Double = 0.02
        Private Const MeasureExitRoundsBeginSeconds As Double = 0.02
        Private Const MeasureExitFooterBeginSeconds As Double = 0.06
        Private Const MeasureOverlayCrossfadeStartSeconds As Double = 0.6
        Private Const MeasureOverlayFadeDurationSeconds As Double = 0.16
        Private Const MeasureCenterSwapDurationSeconds As Double = 0.3
        Private Const MeasureCenterSwapToResultBeginSeconds As Double = 0.3
        Private Const MeasureCenterSwapToRoundBeginSeconds As Double = 0.2
        Private Const MeasureCenterSwapOutgoingOffset As Double = 10.0
        Private Const MeasureCenterSwapIncomingOffset As Double = 14.0

        Private _observedViewModel As SensitivityMatchingPageViewModel
        Private _introClock As Stopwatch
        Private ReadOnly _openingTokens As New List(Of IntroMorphToken)()
        Private ReadOnly _targetTokens As New List(Of IntroMorphToken)()
        Private _isIntroAnimationActive As Boolean
        Private _isRenderingHooked As Boolean
        Private _activeAnimationMode As MorphAnimationMode
        Private _isMeasurePresentationActive As Boolean
        Private _isMeasureForwardTransitionActive As Boolean
        Private _isMeasureBackTransitionActive As Boolean
        Private _isMeasureResultCenterVisible As Boolean
        Private _isNormalizingDpiText As Boolean
        Private _measureTransitionTimer As DispatcherTimer
        Private _measureTransitionCompletion As Action
        Private ReadOnly _animatedTextRenderingStates As New Dictionary(Of DependencyObject, MeasureTransitionTextRenderingState)()
        Private ReadOnly _animatedTextCacheStates As New Dictionary(Of UIElement, MeasureTransitionCacheState)()
        Private ReadOnly _measureTransitionTextRenderingStates As New Dictionary(Of DependencyObject, MeasureTransitionTextRenderingState)()
        Private ReadOnly _measureTransitionCacheStates As New Dictionary(Of UIElement, MeasureTransitionCacheState)()
        Private _isThemeSubscribed As Boolean

        Public Sub New()
            InitializeComponent()
            ResetIntroHeroVisuals()
            ResetMeasureVisuals()
        End Sub

        Private Sub SensitivityMatchingPage_Loaded(sender As Object, e As RoutedEventArgs)
            If Not _isThemeSubscribed Then
                AddHandler ThemeManager.Instance.ThemeChanged, AddressOf OnThemeChanged
                _isThemeSubscribed = True
            End If

            RefreshThemeSensitiveVisuals()
            UpdateViewModelSubscription(TryCast(DataContext, SensitivityMatchingPageViewModel))
            SyncViewModelState()
        End Sub

        Private Sub SensitivityMatchingPage_Unloaded(sender As Object, e As RoutedEventArgs)
            If _isThemeSubscribed Then
                RemoveHandler ThemeManager.Instance.ThemeChanged, AddressOf OnThemeChanged
                _isThemeSubscribed = False
            End If

            Dim viewModel = TryCast(DataContext, SensitivityMatchingPageViewModel)
            If viewModel IsNot Nothing Then
                viewModel.SetPageActive(False)
            End If

            StopIntroAnimation()
            ResetIntroHeroVisuals()
            ResetMeasureVisuals()
            StopMeasureTransitionTimer()
            _isIntroAnimationActive = False
            _activeAnimationMode = MorphAnimationMode.None
            _isMeasurePresentationActive = False
            _isMeasureForwardTransitionActive = False
            _isMeasureBackTransitionActive = False
        End Sub

        Private Sub SensitivityMatchingPage_IsVisibleChanged(sender As Object, e As DependencyPropertyChangedEventArgs)
            SyncViewModelState()
        End Sub

        Private Sub OnThemeChanged(sender As Object, e As EventArgs)
            Dispatcher.BeginInvoke(Sub() RefreshThemeSensitiveVisuals(), DispatcherPriority.Render)
        End Sub

        Private Sub RefreshThemeSensitiveVisuals()
            RefreshIntroMorphTokenForegrounds()
        End Sub

        Private Sub RefreshIntroMorphTokenForegrounds()
            Dim fallbackBrush = If(IntroHeroTopLeftWord IsNot Nothing, IntroHeroTopLeftWord.Foreground, Nothing)
            Dim foregroundBrush = ResolveBrushResource("TextStrongBrush", fallbackBrush)
            If foregroundBrush Is Nothing Then
                Return
            End If

            For Each token In _openingTokens
                If token?.Element IsNot Nothing Then
                    token.Element.Foreground = foregroundBrush
                End If
            Next

            For Each token In _targetTokens
                If token?.Element IsNot Nothing Then
                    token.Element.Foreground = foregroundBrush
                End If
            Next
        End Sub

        Private Function ResolveBrushResource(resourceKey As String, fallbackBrush As Brush) As Brush
            Dim resolvedBrush = TryCast(TryFindResource(resourceKey), Brush)
            Return If(resolvedBrush, fallbackBrush)
        End Function

        Private Sub SensitivityMatchingPage_DataContextChanged(sender As Object, e As DependencyPropertyChangedEventArgs)
            Dim previousViewModel = TryCast(e.OldValue, SensitivityMatchingPageViewModel)
            If previousViewModel IsNot Nothing Then
                previousViewModel.SetPageActive(False)
            End If

            UpdateViewModelSubscription(TryCast(e.NewValue, SensitivityMatchingPageViewModel))
            SyncViewModelState()
        End Sub

        Private Sub SensitivityMatchingPage_SizeChanged(sender As Object, e As SizeChangedEventArgs)
            If Not IsLoaded Then
                Return
            End If

            If Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 1.0 AndAlso
               Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 1.0 Then
                Return
            End If

            Dispatcher.BeginInvoke(Sub() SyncIntroAnimationState(forceRestart:=ShouldRestartCurrentAnimation()),
                                   DispatcherPriority.Loaded)
        End Sub

        Private Sub IntroStartButton_Click(sender As Object, e As RoutedEventArgs)
            If _activeAnimationMode = MorphAnimationMode.IntroToSetupTransition OrElse
               _activeAnimationMode = MorphAnimationMode.SetupToIntroTransition Then
                Return
            End If

            Dim viewModel = TryCast(DataContext, SensitivityMatchingPageViewModel)
            If viewModel Is Nothing OrElse Not viewModel.IsIntroStep Then
                Return
            End If

            If _activeAnimationMode = MorphAnimationMode.IntroOpening Then
                FinalizeIntroAnimation()
            End If

            _activeAnimationMode = MorphAnimationMode.IntroToSetupTransition
            SyncIntroAnimationState(forceRestart:=True)
        End Sub

        Private Sub SetupBackButton_Click(sender As Object, e As RoutedEventArgs)
            If _activeAnimationMode = MorphAnimationMode.IntroToSetupTransition OrElse
               _activeAnimationMode = MorphAnimationMode.SetupToIntroTransition Then
                Return
            End If

            Dim viewModel = TryCast(DataContext, SensitivityMatchingPageViewModel)
            If viewModel Is Nothing Then
                Return
            End If

            If Not viewModel.IsSetupStep Then
                ExecuteBackCommand()
                Return
            End If

            _activeAnimationMode = MorphAnimationMode.SetupToIntroTransition
            SyncIntroAnimationState(forceRestart:=True)
        End Sub

        Private Sub ContinueToMeasureButton_Click(sender As Object, e As RoutedEventArgs)
            If _activeAnimationMode = MorphAnimationMode.IntroToSetupTransition OrElse
               _activeAnimationMode = MorphAnimationMode.SetupToIntroTransition OrElse
               _isMeasureForwardTransitionActive OrElse
               _isMeasureBackTransitionActive Then
                Return
            End If

            Dim viewModel = TryCast(DataContext, SensitivityMatchingPageViewModel)
            If viewModel Is Nothing OrElse
               Not viewModel.IsSetupStep OrElse
               viewModel.ContinueFromSetupCommand Is Nothing OrElse
               Not viewModel.ContinueFromSetupCommand.CanExecute(Nothing) Then
                Return
            End If

            viewModel.PrepareMeasureEntryPreview()
            _isMeasureForwardTransitionActive = True
            PlaySetupToMeasureTransition(Sub()
                                             ExecuteContinueFromSetupCommand()

                                             Dim updatedViewModel = TryCast(DataContext, SensitivityMatchingPageViewModel)
                                             If updatedViewModel Is Nothing OrElse Not updatedViewModel.IsMeasureStep Then
                                                 ResetSetupTransitionPresentation()
                                                 ResetMeasureVisuals()
                                             End If
                                         End Sub)
        End Sub

        Private Sub MeasureBackButton_Click(sender As Object, e As RoutedEventArgs)
            If _activeAnimationMode = MorphAnimationMode.IntroToSetupTransition OrElse
               _activeAnimationMode = MorphAnimationMode.SetupToIntroTransition OrElse
               _isMeasureForwardTransitionActive OrElse
               _isMeasureBackTransitionActive Then
                Return
            End If

            Dim viewModel = TryCast(DataContext, SensitivityMatchingPageViewModel)
            If viewModel Is Nothing Then
                Return
            End If

            If Not viewModel.IsMeasureStep Then
                ExecuteBackCommand()
                Return
            End If

            _isMeasureBackTransitionActive = True
            PlayMeasureExitTransition(Sub()
                                          ExecuteBackCommand()
                                          ResetMeasureVisuals()

                                          Dim updatedViewModel = TryCast(DataContext, SensitivityMatchingPageViewModel)
                                          If updatedViewModel Is Nothing OrElse updatedViewModel.IsSetupStep Then
                                              ResetSetupTransitionPresentation()
                                           End If
                                       End Sub)
        End Sub

        Private Sub SetupDpiTextBox_TextChanged(sender As Object, e As TextChangedEventArgs)
            If _isNormalizingDpiText Then
                Return
            End If

            Dim textBox = TryCast(sender, TextBox)
            If textBox Is Nothing Then
                Return
            End If

            Dim originalText = If(textBox.Text, String.Empty)
            Dim normalizedText = NormalizeDpiText(originalText)
            If String.Equals(originalText, normalizedText, StringComparison.Ordinal) Then
                Return
            End If

            Dim selectionStart = Math.Max(0, Math.Min(textBox.SelectionStart, originalText.Length))
            Dim digitsBeforeCaret = CountDigitsBeforeIndex(originalText, selectionStart)

            _isNormalizingDpiText = True
            textBox.Text = normalizedText
            textBox.SelectionStart = Math.Min(digitsBeforeCaret, normalizedText.Length)
            _isNormalizingDpiText = False
        End Sub

        Private Sub SyncViewModelState()
            Dim viewModel = TryCast(DataContext, SensitivityMatchingPageViewModel)
            If viewModel Is Nothing Then
                SyncIntroAnimationState()
                SyncMeasurePresentationState()
                Return
            End If

            viewModel.SetPageActive(IsLoaded AndAlso IsVisible)
            SyncIntroAnimationState()
            SyncMeasurePresentationState()
        End Sub

        Private Sub UpdateViewModelSubscription(viewModel As SensitivityMatchingPageViewModel)
            If ReferenceEquals(_observedViewModel, viewModel) Then
                Return
            End If

            If _observedViewModel IsNot Nothing Then
                RemoveHandler _observedViewModel.PropertyChanged, AddressOf OnViewModelPropertyChanged
            End If

            _observedViewModel = viewModel
            If _observedViewModel IsNot Nothing Then
                AddHandler _observedViewModel.PropertyChanged, AddressOf OnViewModelPropertyChanged
            End If
        End Sub

        Private Sub OnViewModelPropertyChanged(sender As Object, e As PropertyChangedEventArgs)
            If Not String.IsNullOrEmpty(e.PropertyName) AndAlso
               e.PropertyName <> NameOf(SensitivityMatchingPageViewModel.CurrentStep) AndAlso
               e.PropertyName <> NameOf(SensitivityMatchingPageViewModel.IsIntroStep) AndAlso
               e.PropertyName <> NameOf(SensitivityMatchingPageViewModel.HasFinalRecommendation) Then
                Return
            End If

            Dispatcher.BeginInvoke(Sub()
                                       SyncIntroAnimationState()
                                       SyncMeasurePresentationState()
                                   End Sub,
                                   DispatcherPriority.Loaded)
        End Sub

        Private Sub SyncIntroAnimationState(Optional forceRestart As Boolean = False)
            If ShouldPlaySetupTransition() Then
                If forceRestart OrElse _activeAnimationMode <> MorphAnimationMode.IntroToSetupTransition OrElse Not _isIntroAnimationActive Then
                    RestartSetupTransition()
                End If

                Return
            End If

            If ShouldPlaySetupToIntroTransition() Then
                If forceRestart OrElse _activeAnimationMode <> MorphAnimationMode.SetupToIntroTransition OrElse Not _isIntroAnimationActive Then
                    RestartSetupToIntroTransition()
                End If

                Return
            End If

            Dim shouldPlay = ShouldPlayIntroAnimation()

            If shouldPlay Then
                If forceRestart OrElse _activeAnimationMode <> MorphAnimationMode.IntroOpening OrElse Not _isIntroAnimationActive Then
                    RestartIntroAnimation()
                End If
            Else
                StopIntroAnimation()
                ResetIntroHeroVisuals()
                _isIntroAnimationActive = False
                _activeAnimationMode = MorphAnimationMode.None
            End If
        End Sub

        Private Function ShouldRestartCurrentAnimation() As Boolean
            Return _activeAnimationMode = MorphAnimationMode.IntroOpening OrElse
                   _activeAnimationMode = MorphAnimationMode.IntroToSetupTransition OrElse
                   _activeAnimationMode = MorphAnimationMode.SetupToIntroTransition
        End Function

        Private Function ShouldPlayIntroAnimation() As Boolean
            Dim viewModel = TryCast(DataContext, SensitivityMatchingPageViewModel)
            Return IsLoaded AndAlso IsVisible AndAlso viewModel IsNot Nothing AndAlso viewModel.IsIntroStep
        End Function

        Private Function ShouldPlaySetupTransition() As Boolean
            Dim viewModel = TryCast(DataContext, SensitivityMatchingPageViewModel)
            Return IsLoaded AndAlso IsVisible AndAlso
                   _activeAnimationMode = MorphAnimationMode.IntroToSetupTransition AndAlso
                   viewModel IsNot Nothing AndAlso
                   viewModel.IsIntroStep
        End Function

        Private Function ShouldPlaySetupToIntroTransition() As Boolean
            Dim viewModel = TryCast(DataContext, SensitivityMatchingPageViewModel)
            Return IsLoaded AndAlso IsVisible AndAlso
                   _activeAnimationMode = MorphAnimationMode.SetupToIntroTransition AndAlso
                   viewModel IsNot Nothing AndAlso
                   viewModel.IsSetupStep
        End Function

        Private Sub RestartIntroAnimation()
            StopIntroAnimation()
            ResetIntroHeroVisuals()
            _activeAnimationMode = MorphAnimationMode.IntroOpening

            If IntroHeroRoot Is Nothing OrElse
               IntroHeroRoot.ActualWidth <= 1.0 OrElse
               IntroHeroRoot.ActualHeight <= 1.0 Then
                Return
            End If

            UpdateLayout()
            If Not BuildIntroMorphScene() Then
                FinalizeIntroAnimation()
                Return
            End If

            SetAnimatedTextRenderingState(True)
            ShowIntroMorphCanvas()
            _introClock = Stopwatch.StartNew()
            HookIntroRendering()
            _isIntroAnimationActive = True
        End Sub

        Private Sub RestartSetupTransition()
            StopIntroAnimation()
            ResetIntroHeroVisuals()
            _activeAnimationMode = MorphAnimationMode.IntroToSetupTransition

            If IntroHeroRoot Is Nothing OrElse
               SetupHeroRoot Is Nothing OrElse
               IntroHeroRoot.ActualWidth <= 1.0 OrElse
               IntroHeroRoot.ActualHeight <= 1.0 Then
                Return
            End If

            UpdateLayout()
            If Not BuildIntroToSetupMorphScene() Then
                CompleteSetupTransitionImmediately()
                Return
            End If

            SetAnimatedTextRenderingState(True)
            ShowIntroMorphCanvas()
            IntroActionHost.Opacity = 0
            IntroActionTranslate.Y = 12
            IntroStartButton.IsHitTestVisible = False
            IntroStartButton.IsTabStop = False
            InitializeSetupTransitionPresentation()
            _introClock = Stopwatch.StartNew()
            HookIntroRendering()
            _isIntroAnimationActive = True
        End Sub

        Private Sub RestartSetupToIntroTransition()
            StopIntroAnimation()
            ResetIntroHeroVisuals()
            _activeAnimationMode = MorphAnimationMode.SetupToIntroTransition
            ShowIntroTransitionHosts()

            If IntroHeroRoot Is Nothing OrElse
               SetupHeroRoot Is Nothing Then
                Return
            End If

            UpdateLayout()
            If IntroHeroRoot.ActualWidth <= 1.0 OrElse
               IntroHeroRoot.ActualHeight <= 1.0 OrElse
               Not BuildSetupToIntroMorphScene() Then
                CompleteSetupToIntroTransitionImmediately()
                Return
            End If

            SetAnimatedTextRenderingState(True)
            ShowIntroMorphCanvas()
            UpdateTransitionActionHost()
            InitializeSetupToIntroTransitionPresentation()
            _introClock = Stopwatch.StartNew()
            HookIntroRendering()
            _isIntroAnimationActive = True
        End Sub

        Private Function BuildIntroMorphScene() As Boolean
            ClearMorphTokens()

            Dim openingAnchors As New List(Of IntroGlyphAnchor)()
            openingAnchors.AddRange(CreateGlyphAnchors(IntroOpeningWord1Block, "Opening1"))
            openingAnchors.AddRange(CreateGlyphAnchors(IntroOpeningWord2Block, "Opening2"))
            openingAnchors.AddRange(CreateGlyphAnchors(IntroOpeningWord3Block, "Opening3"))
            openingAnchors.AddRange(CreateGlyphAnchors(IntroOpeningWord4Block, "Opening4"))
            For index = 0 To openingAnchors.Count - 1
                openingAnchors(index).SequenceIndex = index
            Next
            FitOpeningAnchorsToViewport(openingAnchors)

            If openingAnchors.Count = 0 Then
                Return False
            End If

            Dim targetAnchors = BuildIntroSettledAnchors()

            If targetAnchors.Count = 0 Then
                Return False
            End If

            Dim topLeftAnchors = targetAnchors.Where(Function(anchor) String.Equals(anchor.GroupKey, "TopLeft", StringComparison.Ordinal)).
                                              OrderBy(Function(anchor) anchor.CharacterIndex).
                                              ToList()
            BuildOpeningMorphTokens(openingAnchors, topLeftAnchors)
            BuildTargetMorphTokens(openingAnchors,
                                   targetAnchors.Where(Function(anchor) Not String.Equals(anchor.GroupKey, "TopLeft", StringComparison.Ordinal)).
                                                 ToList())

            Return IntroMorphCanvas.Children.Count > 0
        End Function

        Private Function BuildIntroToSetupMorphScene() As Boolean
            ClearMorphTokens()

            Dim sourceAnchors = BuildIntroSettledAnchors()
            Dim targetAnchors = BuildSetupSettledAnchors()
            If sourceAnchors.Count = 0 OrElse
               targetAnchors.Count = 0 Then
                Return False
            End If

            BuildTransitionSourceTokens(sourceAnchors,
                                        targetAnchors,
                                        New Dictionary(Of String, String)(StringComparer.Ordinal) From {
                                            {"LeftLabel", "TopLeft"},
                                            {"RightLabel", "RightPrimary"}
                                        })
            Return IntroMorphCanvas.Children.Count > 0
        End Function

        Private Function BuildSetupToIntroMorphScene() As Boolean
            ClearMorphTokens()

            Dim sourceWordAnchors = BuildSetupSettledWordAnchors()
            Dim targetWordAnchors = BuildIntroSettledWordAnchors()
            Dim introReturnAnchors = BuildIntroSettledAnchors().
                Where(Function(anchor) Not IsPersistentTransitionWordGroup(anchor.GroupKey)).
                ToList()
            If sourceWordAnchors.Count = 0 OrElse
               targetWordAnchors.Count = 0 OrElse
               introReturnAnchors.Count = 0 Then
                Return False
            End If

            BuildTransitionPersistentWordTokens(sourceWordAnchors,
                                               targetWordAnchors,
                                               New Dictionary(Of String, String)(StringComparer.Ordinal) From {
                                                   {"TopLeft", "LeftLabel"},
                                                   {"RightPrimary", "RightLabel"}
                                               })
            BuildIntroReturnTargetTokens(introReturnAnchors)
            Return IntroMorphCanvas.Children.Count > 0
        End Function

        Private Function BuildIntroSettledAnchors() As List(Of IntroGlyphAnchor)
            Dim settledOffset = New Vector(IntroSettledTranslate.X, IntroSettledTranslate.Y)
            Dim anchors As New List(Of IntroGlyphAnchor)()
            anchors.AddRange(CreateGlyphAnchors(IntroHeroTopLeftWord, "TopLeft", settledOffset))
            anchors.AddRange(CreateGlyphAnchors(IntroHeroLeftLabel, "LeftLabel", settledOffset))
            anchors.AddRange(CreateGlyphAnchors(IntroHeroConnector, "Connector", settledOffset))
            anchors.AddRange(CreateGlyphAnchors(IntroHeroRightLabel, "RightLabel", settledOffset))
            anchors.AddRange(CreateGlyphAnchors(IntroHeroBottomRightWord1, "Bottom1", settledOffset))
            anchors.AddRange(CreateGlyphAnchors(IntroHeroBottomRightWord2, "Bottom2", settledOffset))
            anchors.AddRange(CreateGlyphAnchors(IntroHeroBottomRightWord3, "Bottom3", settledOffset))
            AssignSequenceIndexes(anchors)
            Return anchors
        End Function

        Private Function BuildSetupSettledAnchors() As List(Of IntroGlyphAnchor)
            Dim anchors As New List(Of IntroGlyphAnchor)()
            anchors.AddRange(CreateGlyphAnchors(SetupHeroTopLeftWord, "TopLeft"))
            anchors.AddRange(CreateGlyphAnchors(SetupHeroRightLabel, "RightPrimary"))
            AssignSequenceIndexes(anchors)
            Return anchors
        End Function

        Private Function BuildIntroSettledWordAnchors() As List(Of IntroGlyphAnchor)
            Dim settledOffset = New Vector(IntroSettledTranslate.X, IntroSettledTranslate.Y)
            Dim anchors As New List(Of IntroGlyphAnchor)()
            anchors.AddRange(CreateWordAnchors(IntroHeroLeftLabel, "LeftLabel", settledOffset))
            anchors.AddRange(CreateWordAnchors(IntroHeroRightLabel, "RightLabel", settledOffset))
            AssignSequenceIndexes(anchors)
            Return anchors
        End Function

        Private Function BuildSetupSettledWordAnchors() As List(Of IntroGlyphAnchor)
            Dim anchors As New List(Of IntroGlyphAnchor)()
            anchors.AddRange(CreateWordAnchors(SetupHeroTopLeftWord, "TopLeft"))
            anchors.AddRange(CreateWordAnchors(SetupHeroRightLabel, "RightPrimary"))
            AssignSequenceIndexes(anchors)
            Return anchors
        End Function

        Private Sub AssignSequenceIndexes(anchors As IList(Of IntroGlyphAnchor))
            If anchors Is Nothing Then
                Return
            End If

            For index = 0 To anchors.Count - 1
                anchors(index).SequenceIndex = index
            Next
        End Sub

        Private Sub BuildTransitionSourceTokens(sourceAnchors As IReadOnlyList(Of IntroGlyphAnchor),
                                                Optional targetAnchors As IReadOnlyList(Of IntroGlyphAnchor) = Nothing,
                                                Optional sourceToTargetGroups As IReadOnlyDictionary(Of String, String) = Nothing)
            If sourceAnchors Is Nothing Then
                Return
            End If

            Dim targetGroups As Dictionary(Of String, List(Of IntroGlyphAnchor)) = Nothing
            If targetAnchors IsNot Nothing AndAlso sourceToTargetGroups IsNot Nothing Then
                targetGroups = targetAnchors.GroupBy(Function(anchor) anchor.GroupKey).
                    ToDictionary(Function(group) group.Key,
                                 Function(group) group.OrderBy(Function(anchor) anchor.CharacterIndex).ToList(),
                                 StringComparer.Ordinal)
            End If

            For Each anchor In sourceAnchors
                Dim token = CreateMorphToken(anchor)
                Dim exitStart = ComputeSetupTransitionExitStart(anchor)
                Dim exitDuration = ComputeSetupTransitionExitDuration(anchor)

                If String.Equals(anchor.GroupKey, "TopLeft", StringComparison.Ordinal) Then
                    exitStart = Math.Max(SetupTransitionMorphStartSeconds, exitStart - SetupTopLeftExitAdvanceSeconds)
                    exitDuration = Math.Max(0.001, exitDuration - SetupTopLeftExitDurationTrimSeconds)
                End If

                token.StartPosition = anchor.Position
                token.EndPosition = ComputeSetupTransitionExitPosition(anchor)
                token.StartScale = 1.0
                token.EndScale = 0.82
                token.EnterDelay = (anchor.SequenceIndex Mod 3) * 0.008
                token.MotionDuration = SetupTransitionMorphEndSeconds - SetupTransitionMorphStartSeconds

                If String.Equals(anchor.GroupKey, "TopLeft", StringComparison.Ordinal) Then
                    token.EnterDelay = Math.Max(0, token.EnterDelay - SetupTopLeftEnterDelayAdvanceSeconds)
                    token.MotionDuration = Math.Max(0.001, token.MotionDuration - SetupTopLeftMotionDurationTrimSeconds)
                End If

                token.ExitStart = exitStart
                token.ExitEnd = Math.Min(token.ExitStart + exitDuration, SetupTransitionMorphEndSeconds)
                token.IsPersistent = False
                token.RevealThreshold = 0

                If targetGroups IsNot Nothing Then
                    Dim mappedGroup As String = Nothing
                    If sourceToTargetGroups.TryGetValue(anchor.GroupKey, mappedGroup) AndAlso
                       targetGroups.ContainsKey(mappedGroup) AndAlso
                       anchor.CharacterIndex < targetGroups(mappedGroup).Count Then
                        Dim targetAnchor = targetGroups(mappedGroup)(anchor.CharacterIndex)
                        token.EndPosition = targetAnchor.Position
                        token.EndScale = targetAnchor.FontSize / Math.Max(anchor.FontSize, 1.0)
                        token.ExitStart = SetupTransitionMorphEndSeconds
                        token.ExitEnd = SetupTransitionMorphEndSeconds
                        token.IsPersistent = True

                        If String.Equals(anchor.GroupKey, "RightLabel", StringComparison.Ordinal) Then
                            token.EnterDelay = 0.11
                            token.MotionDuration = Math.Max(0.001, (SetupTransitionMorphEndSeconds - SetupTransitionMorphStartSeconds) - token.EnterDelay)
                        End If
                    End If
                End If

                _openingTokens.Add(token)
                IntroMorphCanvas.Children.Add(token.Element)
                ApplyTokenState(token, token.StartPosition, token.StartScale, 1)
            Next
        End Sub

        Private Sub BuildTransitionPersistentWordTokens(sourceWordAnchors As IReadOnlyList(Of IntroGlyphAnchor),
                                                        targetWordAnchors As IReadOnlyList(Of IntroGlyphAnchor),
                                                        sourceToTargetGroups As IReadOnlyDictionary(Of String, String))
            If sourceWordAnchors Is Nothing OrElse targetWordAnchors Is Nothing Then
                Return
            End If
            Dim targetWords = targetWordAnchors.ToDictionary(Function(anchor) anchor.GroupKey,
                                                             Function(anchor) anchor,
                                                             StringComparer.Ordinal)

            For Each sourceWord In sourceWordAnchors
                Dim mappedGroup As String = Nothing
                If Not sourceToTargetGroups.TryGetValue(sourceWord.GroupKey, mappedGroup) OrElse
                   Not targetWords.ContainsKey(mappedGroup) Then
                    Continue For
                End If

                Dim targetWord = targetWords(mappedGroup)
                Dim token = CreateMorphToken(sourceWord)
                token.StartPosition = sourceWord.Position
                token.EndPosition = targetWord.Position
                token.StartScale = 1.0
                token.EndScale = targetWord.FontSize / Math.Max(sourceWord.FontSize, 1.0)
                token.EnterDelay = sourceWord.SequenceIndex * 0.016
                token.MotionDuration = SetupTransitionMorphEndSeconds - SetupTransitionMorphStartSeconds
                token.ExitStart = SetupTransitionMorphEndSeconds
                token.ExitEnd = SetupTransitionMorphEndSeconds
                token.IsPersistent = True
                token.RevealThreshold = 0

                _openingTokens.Add(token)
                IntroMorphCanvas.Children.Add(token.Element)
                ApplyTokenState(token, token.StartPosition, token.StartScale, 1)
            Next
        End Sub

        Private Sub BuildIntroReturnTargetTokens(targetAnchors As IReadOnlyList(Of IntroGlyphAnchor))
            If targetAnchors Is Nothing OrElse targetAnchors.Count = 0 Then
                Return
            End If

            For Each targetAnchor In targetAnchors
                Dim token = CreateMorphToken(targetAnchor)
                Dim forwardExitStart = ComputeSetupTransitionExitStart(targetAnchor)
                Dim forwardExitEnd = Math.Min(forwardExitStart + ComputeSetupTransitionExitDuration(targetAnchor), SetupTransitionMorphEndSeconds)

                token.StartPosition = ComputeSetupTransitionExitPosition(targetAnchor)
                token.EndPosition = targetAnchor.Position
                token.StartScale = 0.82
                token.EndScale = 1.0
                token.EnterDelay = (targetAnchor.SequenceIndex Mod 3) * 0.008
                token.MotionDuration = SetupTransitionMorphEndSeconds - SetupTransitionMorphStartSeconds
                token.ExitStart = 0
                token.ExitEnd = 0
                token.IsPersistent = True
                token.RevealThreshold = 0
                token.RevealStartSeconds = SetupTransitionTotalDurationSeconds - forwardExitEnd
                token.RevealEndSeconds = SetupTransitionTotalDurationSeconds - forwardExitStart

                _targetTokens.Add(token)
                IntroMorphCanvas.Children.Add(token.Element)
                ApplyTokenState(token, token.StartPosition, token.StartScale, 0)
            Next
        End Sub

        Private Sub BuildOpeningMorphTokens(openingAnchors As IReadOnlyList(Of IntroGlyphAnchor),
                                            topLeftAnchors As IReadOnlyList(Of IntroGlyphAnchor))
            For Each anchor In openingAnchors
                Dim token = CreateMorphToken(anchor)
                token.StartPosition = anchor.Position
                token.StartScale = 1.0
                token.EndScale = 0.82
                token.EnterDelay = (anchor.SequenceIndex Mod 4) * 0.018
                token.MotionDuration = 0.56
                token.ExitStart = 0.9 + (anchor.SequenceIndex * 0.006)
                token.ExitEnd = token.ExitStart + 0.22
                token.IsPersistent = False
                token.RevealThreshold = 0

                If String.Equals(anchor.GroupKey, "Opening4", StringComparison.Ordinal) AndAlso
                   anchor.CharacterIndex < topLeftAnchors.Count Then
                    Dim targetAnchor = topLeftAnchors(anchor.CharacterIndex)
                    token.EndPosition = targetAnchor.Position
                    token.EndScale = targetAnchor.FontSize / Math.Max(anchor.FontSize, 1.0)
                    token.EnterDelay = anchor.CharacterIndex * 0.014
                    token.MotionDuration = 0.62
                    token.ExitStart = IntroCanvasFadeStartSeconds
                    token.ExitEnd = IntroCanvasFadeEndSeconds
                    token.IsPersistent = True
                    token.RevealThreshold = 0
                Else
                    token.EndPosition = ComputeDriftPosition(anchor)
                End If

                _openingTokens.Add(token)
                IntroMorphCanvas.Children.Add(token.Element)
                ApplyTokenState(token, token.StartPosition, token.StartScale, 0)
            Next
        End Sub

        Private Sub BuildTargetMorphTokens(openingAnchors As IReadOnlyList(Of IntroGlyphAnchor),
                                           targetAnchors As IReadOnlyList(Of IntroGlyphAnchor))
            If targetAnchors Is Nothing OrElse targetAnchors.Count = 0 Then
                Return
            End If

            For targetIndex = 0 To targetAnchors.Count - 1
                Dim targetAnchor = targetAnchors(targetIndex)
                Dim token = CreateMorphToken(targetAnchor)
                token.StartPosition = ComputeTargetStartPosition(targetAnchor, targetIndex)
                token.EndPosition = targetAnchor.Position
                token.StartScale = ComputeTargetStartScale(targetAnchor.GroupKey, targetIndex)
                token.EndScale = 1.0
                token.EnterDelay = ComputeTargetDelay(targetAnchor.GroupKey, targetAnchor.CharacterIndex, targetIndex)
                token.MotionDuration = 0.46
                token.ExitStart = IntroCanvasFadeStartSeconds
                token.ExitEnd = IntroCanvasFadeEndSeconds
                token.IsPersistent = True
                token.RevealThreshold = ComputeTargetRevealThreshold(targetAnchor.GroupKey, targetAnchor.CharacterIndex)

                _targetTokens.Add(token)
                IntroMorphCanvas.Children.Add(token.Element)
                ApplyTokenState(token, token.StartPosition, token.StartScale, 0)
            Next
        End Sub

        Private Sub FitOpeningAnchorsToViewport(anchors As IList(Of IntroGlyphAnchor))
            If anchors Is Nothing OrElse anchors.Count = 0 OrElse IntroHeroRoot Is Nothing Then
                Return
            End If

            Dim bounds = GetAnchorBounds(anchors)
            If bounds.Width <= 0 OrElse bounds.Height <= 0 Then
                Return
            End If

            Dim availableWidth = Math.Max(0.0, IntroHeroRoot.ActualWidth - (IntroOpeningSafePadding * 2.0))
            Dim availableHeight = Math.Max(0.0, IntroHeroRoot.ActualHeight - (IntroOpeningSafePadding * 2.0))
            If availableWidth <= 0 OrElse availableHeight <= 0 Then
                Return
            End If

            Dim scale = Math.Min(1.0, Math.Min(availableWidth / bounds.Width, availableHeight / bounds.Height))
            Dim boundsCenter = New Point(bounds.X + (bounds.Width / 2.0), bounds.Y + (bounds.Height / 2.0))
            Dim targetCenter = New Point(IntroHeroRoot.ActualWidth / 2.0, IntroHeroRoot.ActualHeight / 2.0)

            For Each anchor In anchors
                Dim relativeX = anchor.Position.X - boundsCenter.X
                Dim relativeY = anchor.Position.Y - boundsCenter.Y
                anchor.Position = New Point(targetCenter.X + (relativeX * scale),
                                            targetCenter.Y + (relativeY * scale))
                anchor.FontSize *= scale
                anchor.Width *= scale
                anchor.Height *= scale
            Next
        End Sub

        Private Function GetAnchorBounds(anchors As IEnumerable(Of IntroGlyphAnchor)) As Rect
            If anchors Is Nothing Then
                Return Rect.Empty
            End If

            Dim anchorArray = anchors.ToArray()
            If anchorArray.Length = 0 Then
                Return Rect.Empty
            End If

            Dim minX = anchorArray.Min(Function(anchor) anchor.Position.X)
            Dim minY = anchorArray.Min(Function(anchor) anchor.Position.Y)
            Dim maxX = anchorArray.Max(Function(anchor) anchor.Position.X + anchor.Width)
            Dim maxY = anchorArray.Max(Function(anchor) anchor.Position.Y + anchor.Height)
            Return New Rect(minX, minY, Math.Max(0.0, maxX - minX), Math.Max(0.0, maxY - minY))
        End Function

        Private Function CreateGlyphAnchors(textBlock As TextBlock,
                                            groupKey As String,
                                            Optional translationCompensation As Vector? = Nothing) As List(Of IntroGlyphAnchor)
            Dim result As New List(Of IntroGlyphAnchor)()
            If textBlock Is Nothing OrElse String.IsNullOrWhiteSpace(textBlock.Text) Then
                Return result
            End If

            Dim wordTopLeft = textBlock.TranslatePoint(New Point(0, 0), IntroMorphCanvas)
            If translationCompensation.HasValue Then
                wordTopLeft = Point.Subtract(wordTopLeft, translationCompensation.Value)
            End If

            Dim glyphBounds = TryGetFormattedGlyphBounds(textBlock)
            If glyphBounds IsNot Nothing AndAlso glyphBounds.Count = textBlock.Text.Length Then
                Dim formattedBounds = New Rect(glyphBounds.Min(Function(bounds) bounds.Left),
                                               glyphBounds.Min(Function(bounds) bounds.Top),
                                               Math.Max(glyphBounds.Max(Function(bounds) bounds.Right) - glyphBounds.Min(Function(bounds) bounds.Left), 0.5),
                                               Math.Max(glyphBounds.Max(Function(bounds) bounds.Bottom) - glyphBounds.Min(Function(bounds) bounds.Top), 0.5))
                Dim actualWordWidth = Math.Max(textBlock.ActualWidth, formattedBounds.Width)
                Dim widthScale = If(formattedBounds.Width > 0, actualWordWidth / formattedBounds.Width, 1.0)

                For characterIndex = 0 To glyphBounds.Count - 1
                    Dim bounds = glyphBounds(characterIndex)
                    result.Add(New IntroGlyphAnchor With {
                        .Character = textBlock.Text(characterIndex).ToString(),
                        .Position = New Point(wordTopLeft.X + ((bounds.X - formattedBounds.X) * widthScale),
                                              wordTopLeft.Y + (bounds.Y - formattedBounds.Y)),
                        .FontFamily = textBlock.FontFamily,
                        .FontSize = textBlock.FontSize,
                        .FontStyle = textBlock.FontStyle,
                        .FontStretch = textBlock.FontStretch,
                        .FontWeight = textBlock.FontWeight,
                        .Foreground = textBlock.Foreground,
                        .TextFormattingMode = TextOptions.GetTextFormattingMode(textBlock),
                        .TextRenderingMode = TextOptions.GetTextRenderingMode(textBlock),
                        .TextHintingMode = TextOptions.GetTextHintingMode(textBlock),
                        .GroupKey = groupKey,
                        .CharacterIndex = characterIndex,
                        .SequenceIndex = characterIndex,
                        .Width = Math.Max(bounds.Width * widthScale, 0.5),
                        .Height = Math.Max(bounds.Height, textBlock.ActualHeight)
                    })
                Next

                Return result
            End If

            Dim widths As New List(Of Double)()
            Dim tallestGlyphHeight = 0.0
            For Each character In textBlock.Text
                Dim measuredSize = MeasureCharacterSize(textBlock, character.ToString())
                widths.Add(Math.Max(measuredSize.Width, 0.5))
                tallestGlyphHeight = Math.Max(tallestGlyphHeight, measuredSize.Height)
            Next

            Dim totalMeasuredWidth = widths.Sum()
            Dim fallbackWordWidth = Math.Max(textBlock.ActualWidth, totalMeasuredWidth)
            Dim fallbackWidthScale = If(totalMeasuredWidth > 0, fallbackWordWidth / totalMeasuredWidth, 1.0)
            Dim fallbackGlyphHeight = Math.Max(textBlock.ActualHeight, tallestGlyphHeight)
            Dim currentX = wordTopLeft.X

            For characterIndex = 0 To textBlock.Text.Length - 1
                Dim scaledWidth = widths(characterIndex) * fallbackWidthScale
                result.Add(New IntroGlyphAnchor With {
                    .Character = textBlock.Text(characterIndex).ToString(),
                    .Position = New Point(currentX, wordTopLeft.Y),
                    .FontFamily = textBlock.FontFamily,
                    .FontSize = textBlock.FontSize,
                    .FontStyle = textBlock.FontStyle,
                    .FontStretch = textBlock.FontStretch,
                    .FontWeight = textBlock.FontWeight,
                    .Foreground = textBlock.Foreground,
                    .TextFormattingMode = TextOptions.GetTextFormattingMode(textBlock),
                    .TextRenderingMode = TextOptions.GetTextRenderingMode(textBlock),
                    .TextHintingMode = TextOptions.GetTextHintingMode(textBlock),
                    .GroupKey = groupKey,
                    .CharacterIndex = characterIndex,
                    .SequenceIndex = characterIndex,
                    .Width = scaledWidth,
                    .Height = fallbackGlyphHeight
                })
                currentX += scaledWidth
            Next

            Return result
        End Function

        Private Function CreateWordAnchors(textBlock As TextBlock,
                                           groupKey As String,
                                           Optional translationCompensation As Vector? = Nothing) As List(Of IntroGlyphAnchor)
            Dim result As New List(Of IntroGlyphAnchor)()
            If textBlock Is Nothing OrElse String.IsNullOrWhiteSpace(textBlock.Text) Then
                Return result
            End If

            Dim wordTopLeft = textBlock.TranslatePoint(New Point(0, 0), IntroMorphCanvas)
            If translationCompensation.HasValue Then
                wordTopLeft = Point.Subtract(wordTopLeft, translationCompensation.Value)
            End If

            result.Add(New IntroGlyphAnchor With {
                .Character = textBlock.Text,
                .Position = wordTopLeft,
                .FontFamily = textBlock.FontFamily,
                .FontSize = textBlock.FontSize,
                .FontStyle = textBlock.FontStyle,
                .FontStretch = textBlock.FontStretch,
                .FontWeight = textBlock.FontWeight,
                .Foreground = textBlock.Foreground,
                .TextFormattingMode = TextOptions.GetTextFormattingMode(textBlock),
                .TextRenderingMode = TextOptions.GetTextRenderingMode(textBlock),
                .TextHintingMode = TextOptions.GetTextHintingMode(textBlock),
                .GroupKey = groupKey,
                .CharacterIndex = 0,
                .SequenceIndex = 0,
                .Width = Math.Max(textBlock.ActualWidth, 0),
                .Height = Math.Max(textBlock.ActualHeight, 0)
            })

            Return result
        End Function

        Private Function TryGetFormattedGlyphBounds(textBlock As TextBlock) As List(Of Rect)
            If textBlock Is Nothing OrElse String.IsNullOrWhiteSpace(textBlock.Text) Then
                Return Nothing
            End If

            Try
                Dim dpi = VisualTreeHelper.GetDpi(textBlock)
                Dim formattedText As New FormattedText(textBlock.Text,
                                                       Globalization.CultureInfo.CurrentUICulture,
                                                       textBlock.FlowDirection,
                                                       New Typeface(textBlock.FontFamily,
                                                                    textBlock.FontStyle,
                                                                    textBlock.FontWeight,
                                                                    textBlock.FontStretch),
                                                       textBlock.FontSize,
                                                       If(textBlock.Foreground, Brushes.Transparent),
                                                       dpi.PixelsPerDip) With {
                    .MaxLineCount = 1,
                    .Trimming = TextTrimming.None,
                    .TextAlignment = textBlock.TextAlignment
                }

                If textBlock.LineHeight > 0 Then
                    formattedText.LineHeight = textBlock.LineHeight
                End If

                Dim bounds As New List(Of Rect)(textBlock.Text.Length)
                For index = 0 To textBlock.Text.Length - 1
                    Dim glyphGeometry = formattedText.BuildHighlightGeometry(New Point(0, 0), index, 1)
                    If glyphGeometry Is Nothing OrElse glyphGeometry.Bounds.IsEmpty Then
                        Return Nothing
                    End If

                    bounds.Add(glyphGeometry.Bounds)
                Next

                Return bounds
            Catch
                Return Nothing
            End Try
        End Function

        Private Shared Function IsPersistentTransitionWordGroup(groupKey As String) As Boolean
            Return String.Equals(groupKey, "LeftLabel", StringComparison.Ordinal) OrElse
                   String.Equals(groupKey, "RightLabel", StringComparison.Ordinal)
        End Function

        Private Function MeasureCharacterSize(reference As TextBlock,
                                              character As String) As Size
            Dim probe As New TextBlock With {
                .Text = character,
                .FontFamily = reference.FontFamily,
                .FontSize = reference.FontSize,
                .FontStyle = reference.FontStyle,
                .FontStretch = reference.FontStretch,
                .FontWeight = reference.FontWeight
            }

            probe.Measure(New Size(Double.PositiveInfinity, Double.PositiveInfinity))
            Return probe.DesiredSize
        End Function

        Private Function ComputeDriftPosition(anchor As IntroGlyphAnchor) As Point
            Dim centerX = IntroHeroRoot.ActualWidth / 2.0
            Dim centerY = IntroHeroRoot.ActualHeight / 2.0
            Dim offsetX = anchor.Position.X - centerX
            Dim offsetY = anchor.Position.Y - centerY
            Dim length = Math.Sqrt((offsetX * offsetX) + (offsetY * offsetY))
            If length < 0.001 Then
                offsetX = (anchor.SequenceIndex - 6) * 9.0
                offsetY = -22.0
                length = Math.Sqrt((offsetX * offsetX) + (offsetY * offsetY))
            End If

            Dim normalizedX = offsetX / Math.Max(length, 0.001)
            Dim normalizedY = offsetY / Math.Max(length, 0.001)
            Dim spread = 58.0 + ((anchor.SequenceIndex Mod 5) * 12.0)
            Dim jitterX = Math.Sin((anchor.SequenceIndex + 1) * 0.94) * 20.0
            Dim jitterY = Math.Cos((anchor.SequenceIndex + 1) * 1.21) * 16.0

            Return New Point(anchor.Position.X + (normalizedX * spread) + jitterX,
                             anchor.Position.Y + (normalizedY * spread) + jitterY)
        End Function

        Private Function ComputeSetupTransitionExitPosition(anchor As IntroGlyphAnchor) As Point
            If anchor Is Nothing Then
                Return New Point()
            End If

            Dim direction As Vector
            Dim spread As Double
            Dim crossOffset As Double

            Select Case anchor.GroupKey
                Case "TopLeft"
                    direction = New Vector(-1.0, -0.46)
                    spread = 72.0 + ((anchor.CharacterIndex Mod 4) * 5.0)
                    crossOffset = ((anchor.CharacterIndex Mod 5) - 2.0) * 2.5
                Case "Bottom1", "Bottom2", "Bottom3"
                    Dim lineIndex = GetBottomWordLineIndex(anchor.GroupKey)
                    direction = New Vector(1.0, 0.18 + (lineIndex * 0.08))
                    spread = 58.0 + (lineIndex * 10.0) + ((anchor.CharacterIndex Mod 4) * 4.0)
                    crossOffset = ((anchor.CharacterIndex Mod 5) - 2.0) * 2.0
                Case "Connector"
                    direction = New Vector(0.0, -1.0)
                    spread = 38.0 + ((anchor.CharacterIndex Mod 3) * 4.0)
                    crossOffset = ((anchor.CharacterIndex Mod 3) - 1.0) * 3.0
                Case Else
                    Return ComputeDriftPosition(anchor)
            End Select

            If direction.Length < 0.001 Then
                Return anchor.Position
            End If

            direction.Normalize()
            Dim perpendicular As New Vector(-direction.Y, direction.X)
            Return New Point(anchor.Position.X + (direction.X * spread) + (perpendicular.X * crossOffset),
                             anchor.Position.Y + (direction.Y * spread) + (perpendicular.Y * crossOffset))
        End Function

        Private Function ComputeSetupTransitionExitStart(anchor As IntroGlyphAnchor) As Double
            If anchor Is Nothing Then
                Return 0.98
            End If

            Select Case anchor.GroupKey
                Case "TopLeft"
                    Return 0.99 + (anchor.CharacterIndex * 0.004)
                Case "Bottom1", "Bottom2", "Bottom3"
                    Return 0.76 + (GetBottomWordLineIndex(anchor.GroupKey) * 0.045) + (anchor.CharacterIndex * 0.0025)
                Case "Connector"
                    Return 0.84 + (anchor.CharacterIndex * 0.008)
                Case Else
                    Return 0.98 + (anchor.SequenceIndex * 0.005)
            End Select
        End Function

        Private Function ComputeSetupTransitionExitDuration(anchor As IntroGlyphAnchor) As Double
            If anchor Is Nothing Then
                Return 0.24
            End If

            Select Case anchor.GroupKey
                Case "TopLeft", "Bottom1", "Bottom2", "Bottom3"
                    Return If(String.Equals(anchor.GroupKey, "TopLeft", StringComparison.Ordinal), 0.2, 0.18)
                Case "Connector"
                    Return 0.14
                Case Else
                    Return 0.22
            End Select
        End Function

        Private Shared Function GetBottomWordLineIndex(groupKey As String) As Integer
            Select Case groupKey
                Case "Bottom2"
                    Return 1
                Case "Bottom3"
                    Return 2
                Case Else
                    Return 0
            End Select
        End Function

        Private Function ComputeTargetStartPosition(anchor As IntroGlyphAnchor,
                                                    targetIndex As Integer) As Point
            Dim center = New Point(IntroHeroRoot.ActualWidth / 2.0, IntroHeroRoot.ActualHeight / 2.0)
            Dim towardCenter As New Vector(center.X - anchor.Position.X, center.Y - anchor.Position.Y)
            If towardCenter.Length < 0.001 Then
                towardCenter = New Vector(1, -1)
            End If

            towardCenter.Normalize()
            Dim perpendicular As New Vector(-towardCenter.Y, towardCenter.X)
            Dim radialDistance = 48.0 + ((targetIndex Mod 4) * 8.0)
            Dim sideOffset = (Math.Sin((targetIndex + 1) * 1.41) * 12.0) + (((targetIndex Mod 3) - 1) * 6.0)
            Return New Point(anchor.Position.X + (towardCenter.X * radialDistance) + (perpendicular.X * sideOffset),
                             anchor.Position.Y + (towardCenter.Y * radialDistance) + (perpendicular.Y * sideOffset))
        End Function

        Private Function ComputeTargetDelay(groupKey As String,
                                            characterIndex As Integer,
                                            overallIndex As Integer) As Double
            Dim groupOffset As Double
            Select Case groupKey
                Case "LeftLabel"
                    groupOffset = 0.08
                Case "Connector"
                    groupOffset = 0.16
                Case "RightLabel"
                    groupOffset = 0.22
                Case "Bottom1"
                    groupOffset = 0.18
                Case "Bottom2"
                    groupOffset = 0.24
                Case "Bottom3"
                    groupOffset = 0.31
                Case Else
                    groupOffset = 0.12
            End Select

            Return groupOffset + (characterIndex * 0.012) + ((overallIndex Mod 2) * 0.004)
        End Function

        Private Function ComputeTargetStartScale(groupKey As String,
                                                 overallIndex As Integer) As Double
            Dim baseScale As Double
            Select Case groupKey
                Case "LeftLabel", "RightLabel", "Connector"
                    baseScale = 0.72
                Case Else
                    baseScale = 0.82
            End Select

            Return baseScale + ((overallIndex Mod 3) * 0.03)
        End Function

        Private Function ComputeTargetRevealThreshold(groupKey As String,
                                                      characterIndex As Integer) As Double
            Dim baseThreshold As Double
            Select Case groupKey
                Case "LeftLabel", "RightLabel"
                    baseThreshold = 0.76
                Case "Connector"
                    baseThreshold = 0.72
                Case Else
                    baseThreshold = 0.68
            End Select

            Return Clamp(baseThreshold + (characterIndex * 0.015), 0.62, 0.86)
        End Function

        Private Function CreateMorphToken(anchor As IntroGlyphAnchor) As IntroMorphToken
            Dim scale As New ScaleTransform(1, 1)
            Dim element As New TextBlock With {
                .Text = anchor.Character,
                .FontFamily = anchor.FontFamily,
                .FontSize = anchor.FontSize,
                .FontStyle = anchor.FontStyle,
                .FontStretch = anchor.FontStretch,
                .FontWeight = anchor.FontWeight,
                .Foreground = anchor.Foreground,
                .Opacity = 0,
                .RenderTransformOrigin = New Point(0, 0),
                .RenderTransform = scale,
                .SnapsToDevicePixels = False,
                .UseLayoutRounding = False
            }
            TextOptions.SetTextFormattingMode(element, anchor.TextFormattingMode)
            TextOptions.SetTextRenderingMode(element, TextRenderingMode.Grayscale)
            TextOptions.SetTextHintingMode(element, TextHintingMode.Animated)
            RenderOptions.SetClearTypeHint(element, ClearTypeHint.Auto)

            Return New IntroMorphToken With {
                .Element = element,
                .Scale = scale
            }
        End Function

        Private Sub HookIntroRendering()
            If _isRenderingHooked Then
                Return
            End If

            AddHandler CompositionTarget.Rendering, AddressOf OnIntroRendering
            _isRenderingHooked = True
        End Sub

        Private Sub UnhookIntroRendering()
            If Not _isRenderingHooked Then
                Return
            End If

            RemoveHandler CompositionTarget.Rendering, AddressOf OnIntroRendering
            _isRenderingHooked = False
        End Sub

        Private Sub OnIntroRendering(sender As Object, e As EventArgs)
            If _introClock Is Nothing Then
                Return
            End If

            Dim elapsedSeconds = _introClock.Elapsed.TotalSeconds
            Select Case _activeAnimationMode
                Case MorphAnimationMode.IntroOpening
                    UpdateOpeningTokens(elapsedSeconds)
                    UpdateTargetTokens(elapsedSeconds)
                    UpdateSettledLayer(elapsedSeconds)
                    UpdateActionHost(elapsedSeconds)

                    If elapsedSeconds >= IntroTotalDurationSeconds Then
                        FinalizeIntroAnimation()
                    End If
                Case MorphAnimationMode.IntroToSetupTransition
                    UpdateTransitionSourceTokens(elapsedSeconds)
                    UpdateTransitionTargetTokens(elapsedSeconds)
                    UpdateTransitionActionHost()
                    UpdateSetupTransitionPresentation(elapsedSeconds)

                    If elapsedSeconds >= SetupTransitionTotalDurationSeconds Then
                        FinalizeSetupTransition()
                    End If
                Case MorphAnimationMode.SetupToIntroTransition
                    UpdateTransitionSourceTokens(elapsedSeconds)
                    UpdateTransitionTargetTokens(elapsedSeconds)
                    UpdateSetupToIntroActionHost(elapsedSeconds)
                    UpdateSetupToIntroTransitionPresentation(elapsedSeconds)

                    If elapsedSeconds >= SetupTransitionTotalDurationSeconds Then
                        FinalizeSetupToIntroTransition()
                    End If
            End Select
        End Sub

        Private Sub UpdateOpeningTokens(elapsedSeconds As Double)
            Dim openingVisibility = If(elapsedSeconds <= IntroFadeInEndSeconds,
                                       EaseOutCubic(Clamp01(elapsedSeconds / IntroFadeInEndSeconds)),
                                       1.0)

            For Each token In _openingTokens
                Dim moveProgress = FinalizeMotionProgress(EaseInOutCubic(Clamp01((elapsedSeconds - IntroMorphStartSeconds - token.EnterDelay) /
                                                                                  Math.Max(token.MotionDuration, 0.001))),
                                                          0.9997)
                Dim position = Lerp(token.StartPosition, token.EndPosition, moveProgress)
                Dim scale = Lerp(token.StartScale, token.EndScale, moveProgress)
                Dim opacity = openingVisibility

                If Not token.IsPersistent Then
                    Dim fadeOut = EaseInOutCubic(Clamp01((elapsedSeconds - token.ExitStart) /
                                                         Math.Max(token.ExitEnd - token.ExitStart, 0.001)))
                    opacity *= (1.0 - fadeOut)
                End If

                ApplyTokenState(token, position, scale, opacity)
            Next
        End Sub

        Private Sub UpdateTargetTokens(elapsedSeconds As Double)
            For Each token In _targetTokens
                Dim motionProgress = FinalizeMotionProgress(EaseInOutCubic(Clamp01((elapsedSeconds - IntroMorphStartSeconds - token.EnterDelay) /
                                                                                    Math.Max(token.MotionDuration, 0.001))),
                                                            0.9997)
                Dim opacityProgress = EaseOutCubic(Clamp01((motionProgress - token.RevealThreshold) /
                                                           Math.Max(1.0 - token.RevealThreshold, 0.001)))
                Dim position = Lerp(token.StartPosition, token.EndPosition, motionProgress)
                Dim scale = Lerp(token.StartScale, token.EndScale, motionProgress)
                ApplyTokenState(token, position, scale, opacityProgress)
            Next
            ShowIntroMorphCanvas()
        End Sub

        Private Sub UpdateTransitionSourceTokens(elapsedSeconds As Double)
            For Each token In _openingTokens
                Dim motionProgress = ComputeSetupTransitionMotionProgress(elapsedSeconds, token)
                Dim position = Lerp(token.StartPosition, token.EndPosition, motionProgress)
                Dim scale = Lerp(token.StartScale, token.EndScale, motionProgress)
                Dim opacity = 1.0

                If Not token.IsPersistent Then
                    Dim fadeOut = EaseInCubic(Clamp01((elapsedSeconds - token.ExitStart) /
                                                      Math.Max(token.ExitEnd - token.ExitStart, 0.001)))
                    opacity *= (1.0 - fadeOut)
                End If

                ApplyTokenState(token, position, scale, opacity)
            Next

            ShowIntroMorphCanvas()
        End Sub

        Private Sub UpdateTransitionTargetTokens(elapsedSeconds As Double)
            For Each token In _targetTokens
                Dim motionProgress = ComputeSetupTransitionMotionProgress(elapsedSeconds, token)
                Dim opacityProgress As Double
                If token.RevealEndSeconds > token.RevealStartSeconds Then
                    opacityProgress = EaseInOutCubic(ComputeRevealProgress(elapsedSeconds,
                                                                           token.RevealStartSeconds,
                                                                           token.RevealEndSeconds))
                Else
                    opacityProgress = EaseOutCubic(Clamp01((motionProgress - token.RevealThreshold) /
                                                           Math.Max(1.0 - token.RevealThreshold, 0.001)))
                End If
                Dim position = Lerp(token.StartPosition, token.EndPosition, motionProgress)
                Dim scale = Lerp(token.StartScale, token.EndScale, motionProgress)
                ApplyTokenState(token, position, scale, opacityProgress)
            Next

            ShowIntroMorphCanvas()
        End Sub

        Private Sub UpdateSettledLayer(elapsedSeconds As Double)
            IntroSettledLayer.Opacity = 0
            IntroSettledTranslate.Y = 22
        End Sub

        Private Sub UpdateActionHost(elapsedSeconds As Double)
            Dim actionProgress = StabilizeLandingProgress(EaseOutCubic(Clamp01((elapsedSeconds - IntroActionRevealStartSeconds) /
                                                                               Math.Max(IntroActionRevealEndSeconds - IntroActionRevealStartSeconds, 0.001))),
                                                          0.997)
            IntroActionHost.Opacity = actionProgress
            IntroActionTranslate.Y = Lerp(12.0, 0.0, actionProgress)
            IntroStartButton.IsHitTestVisible = actionProgress >= 0.999
            IntroStartButton.IsTabStop = actionProgress >= 0.999
        End Sub

        Private Sub UpdateSetupToIntroActionHost(elapsedSeconds As Double)
            Dim actionProgress = StabilizeLandingProgress(EaseOutCubic(ComputeRevealProgress(elapsedSeconds,
                                                                                             SetupToIntroActionRevealStartSeconds,
                                                                                             SetupToIntroActionRevealEndSeconds)),
                                                          0.997)
            IntroActionHost.Opacity = actionProgress
            IntroActionTranslate.Y = Lerp(12.0, 0.0, actionProgress)
            IntroStartButton.IsHitTestVisible = actionProgress >= 0.999
            IntroStartButton.IsTabStop = actionProgress >= 0.999
        End Sub

        Private Sub UpdateTransitionActionHost()
            IntroActionHost.Opacity = 0
            IntroActionTranslate.Y = 12
            IntroStartButton.IsHitTestVisible = False
            IntroStartButton.IsTabStop = False
        End Sub

        Private Sub InitializeSetupTransitionPresentation()
            If SetupSectionHost IsNot Nothing Then
                SetupSectionHost.Opacity = 1
            End If

            If SetupFooterHost IsNot Nothing Then
                SetupFooterHost.SetValue(UIElement.VisibilityProperty, Visibility.Visible)
            End If

            SetTransitionHostVisualState(SetupSectionHost, isInteractive:=False)
            SetTransitionHostVisualState(SetupFooterHost, isInteractive:=False)

            HideSetupSettledLayer()

            If SetupUtilityOverlay IsNot Nothing Then
                SetupUtilityOverlay.Opacity = 0
            End If

            If SetupUtilityTranslate IsNot Nothing Then
                SetupUtilityTranslate.Y = 26
            End If

            SetRevealState(SetupSourcePanel, SetupSourcePanelTranslate, 0, 0, 18)
            SetRevealState(SetupTargetPanel, SetupTargetPanelTranslate, 0, 0, 18)
            SetRevealState(SetupSourceInfoBandHost, SetupSourceInfoBandTranslate, 0, 0, 10)
            SetRevealState(SetupTargetInfoBandHost, SetupTargetInfoBandTranslate, 0, 0, 10)
            SetRevealState(SetupFooterHost, SetupFooterTranslate, 0, 0, 14)
        End Sub

        Private Sub InitializeSetupToIntroTransitionPresentation()
            If SetupSectionHost IsNot Nothing Then
                SetupSectionHost.Opacity = 1
            End If

            SetTransitionHostVisualState(SetupSectionHost, isInteractive:=False)
            SetTransitionHostVisualState(SetupFooterHost, isInteractive:=False)
            HideSetupSettledLayer()

            If SetupUtilityOverlay IsNot Nothing Then
                SetupUtilityOverlay.Opacity = 1
            End If

            If SetupUtilityTranslate IsNot Nothing Then
                SetupUtilityTranslate.Y = 0
            End If

            SetRevealState(SetupSourcePanel, SetupSourcePanelTranslate, 1, 1, 18)
            SetRevealState(SetupTargetPanel, SetupTargetPanelTranslate, 1, 1, 18)
            SetRevealState(SetupSourceInfoBandHost, SetupSourceInfoBandTranslate, 1, 1, 10)
            SetRevealState(SetupTargetInfoBandHost, SetupTargetInfoBandTranslate, 1, 1, 10)
            SetRevealState(SetupFooterHost, SetupFooterTranslate, 1, 1, 14)
        End Sub

        Private Sub UpdateSetupTransitionPresentation(elapsedSeconds As Double)
            HideSetupSettledLayer()

            If IntroMorphCanvas IsNot Nothing Then
                IntroMorphCanvas.Opacity = 1.0
            End If

            Dim utilityProgress = StabilizeLandingProgress(ComputeRevealProgress(elapsedSeconds,
                                                                                 SetupUtilityRevealStartSeconds,
                                                                                 SetupUtilityRevealEndSeconds),
                                                           0.996)
            If SetupUtilityOverlay IsNot Nothing Then
                SetupUtilityOverlay.Opacity = EaseInOutSine(utilityProgress)
            End If

            If SetupUtilityTranslate IsNot Nothing Then
                SetupUtilityTranslate.Y = Lerp(26.0, 0.0, EaseOutCubic(utilityProgress))
            End If

            ApplyReveal(SetupSourcePanel,
                        SetupSourcePanelTranslate,
                        elapsedSeconds,
                        SetupSourcePanelRevealStartSeconds,
                        SetupSourcePanelRevealEndSeconds,
                        18.0)
            ApplyReveal(SetupTargetPanel,
                        SetupTargetPanelTranslate,
                        elapsedSeconds,
                        SetupTargetPanelRevealStartSeconds,
                        SetupTargetPanelRevealEndSeconds,
                        18.0)
            ApplyReveal(SetupSourceInfoBandHost,
                        SetupSourceInfoBandTranslate,
                        elapsedSeconds,
                        SetupSourceInfoRevealStartSeconds,
                        SetupSourceInfoRevealEndSeconds,
                        10.0)
            ApplyReveal(SetupTargetInfoBandHost,
                        SetupTargetInfoBandTranslate,
                        elapsedSeconds,
                        SetupTargetInfoRevealStartSeconds,
                        SetupTargetInfoRevealEndSeconds,
                        10.0)
            ApplyReveal(SetupFooterHost,
                        SetupFooterTranslate,
                        elapsedSeconds,
                        SetupFooterRevealStartSeconds,
                        SetupFooterRevealEndSeconds,
                        14.0)
        End Sub

        Private Sub UpdateSetupToIntroTransitionPresentation(elapsedSeconds As Double)
            HideSetupSettledLayer()

            Dim utilityProgress = StabilizeLandingProgress(ComputeMirroredRevealProgress(elapsedSeconds,
                                                                                         SetupUtilityRevealStartSeconds,
                                                                                         SetupUtilityRevealEndSeconds),
                                                           0.996)
            If SetupUtilityOverlay IsNot Nothing Then
                SetupUtilityOverlay.Opacity = 1.0 - EaseOutCubic(utilityProgress)
            End If

            If SetupUtilityTranslate IsNot Nothing Then
                SetupUtilityTranslate.Y = Lerp(0.0, 26.0, EaseInCubic(utilityProgress))
            End If

            ApplyRetreat(SetupSourcePanel,
                         SetupSourcePanelTranslate,
                         elapsedSeconds,
                         SetupSourcePanelRevealStartSeconds,
                         SetupSourcePanelRevealEndSeconds,
                         18.0)
            ApplyRetreat(SetupTargetPanel,
                         SetupTargetPanelTranslate,
                         elapsedSeconds,
                         SetupTargetPanelRevealStartSeconds,
                         SetupTargetPanelRevealEndSeconds,
                         18.0)
            ApplyRetreat(SetupSourceInfoBandHost,
                         SetupSourceInfoBandTranslate,
                         elapsedSeconds,
                         SetupSourceInfoRevealStartSeconds,
                         SetupSourceInfoRevealEndSeconds,
                         10.0)
            ApplyRetreat(SetupTargetInfoBandHost,
                         SetupTargetInfoBandTranslate,
                         elapsedSeconds,
                         SetupTargetInfoRevealStartSeconds,
                         SetupTargetInfoRevealEndSeconds,
                         10.0)
            ApplyRetreat(SetupFooterHost,
                         SetupFooterTranslate,
                         elapsedSeconds,
                         SetupFooterRevealStartSeconds,
                         SetupFooterRevealEndSeconds,
                         14.0)
        End Sub

        Private Sub ApplyReveal(host As UIElement,
                                translate As TranslateTransform,
                                elapsedSeconds As Double,
                                startSeconds As Double,
                                endSeconds As Double,
                                startOffsetY As Double)
            Dim progress = StabilizeLandingProgress(ComputeRevealProgress(elapsedSeconds, startSeconds, endSeconds), 0.996)
            SetRevealState(host,
                           translate,
                           EaseInOutSine(progress),
                           EaseOutCubic(progress),
                           startOffsetY)
        End Sub

        Private Sub ApplyRetreat(host As UIElement,
                                 translate As TranslateTransform,
                                 elapsedSeconds As Double,
                                 startSeconds As Double,
                                 endSeconds As Double,
                                 endOffsetY As Double)
            Dim progress = StabilizeLandingProgress(ComputeMirroredRevealProgress(elapsedSeconds, startSeconds, endSeconds), 0.996)
            SetRetreatState(host,
                            translate,
                            EaseOutCubic(progress),
                            EaseInCubic(progress),
                            endOffsetY)
        End Sub

        Private Sub SetRevealState(host As UIElement,
                                   translate As TranslateTransform,
                                   opacityProgress As Double,
                                   translateProgress As Double,
                                   startOffsetY As Double)
            If host IsNot Nothing Then
                host.Opacity = Clamp01(opacityProgress)
            End If

            If translate IsNot Nothing Then
                translate.Y = Math.Round(Lerp(startOffsetY, 0.0, Clamp01(translateProgress)))
            End If
        End Sub

        Private Sub SetRetreatState(host As UIElement,
                                    translate As TranslateTransform,
                                    opacityProgress As Double,
                                    translateProgress As Double,
                                    endOffsetY As Double)
            Dim opacityClamped = Clamp01(opacityProgress)
            Dim translateClamped = Clamp01(translateProgress)
            If host IsNot Nothing Then
                host.Opacity = 1.0 - opacityClamped
            End If

            If translate IsNot Nothing Then
                translate.Y = Math.Round(Lerp(0.0, endOffsetY, translateClamped))
            End If
        End Sub

        Private Shared Function ComputeRevealProgress(elapsedSeconds As Double,
                                                      startSeconds As Double,
                                                      endSeconds As Double) As Double
            Return Clamp01((elapsedSeconds - startSeconds) / Math.Max(endSeconds - startSeconds, 0.001))
        End Function

        Private Shared Function ComputeMirroredRevealProgress(elapsedSeconds As Double,
                                                              startSeconds As Double,
                                                              endSeconds As Double) As Double
            Dim mirroredStart = SetupTransitionTotalDurationSeconds - endSeconds
            Dim mirroredEnd = SetupTransitionTotalDurationSeconds - startSeconds
            Return ComputeRevealProgress(elapsedSeconds, mirroredStart, mirroredEnd)
        End Function

        Private Function ComputeSetupTransitionMotionProgress(elapsedSeconds As Double,
                                                              token As IntroMorphToken) As Double
            Dim linearProgress = Clamp01((elapsedSeconds - SetupTransitionMorphStartSeconds - token.EnterDelay) /
                                         Math.Max(token.MotionDuration, 0.001))

            If _activeAnimationMode = MorphAnimationMode.SetupToIntroTransition Then
                Return FinalizeMotionProgress(EaseInOutCubic(Clamp01((linearProgress * 1.08) - 0.02)), 0.9997)
            End If

            Return FinalizeMotionProgress(EaseInOutCubic(linearProgress), 0.9997)
        End Function

        Private Sub ApplyTokenState(token As IntroMorphToken,
                                    position As Point,
                                    scale As Double,
                                    opacity As Double)
            If token Is Nothing OrElse token.Element Is Nothing Then
                Return
            End If

            Canvas.SetLeft(token.Element, position.X)
            Canvas.SetTop(token.Element, position.Y)
            token.Scale.ScaleX = scale
            token.Scale.ScaleY = scale
            token.Element.Opacity = Clamp(opacity, 0, 1)
        End Sub

        Private Sub StopIntroAnimation()
            UnhookIntroRendering()

            If _introClock IsNot Nothing Then
                _introClock.Stop()
                _introClock = Nothing
            End If

            ClearMorphTokens()
            SetAnimatedTextRenderingState(False)
            _isIntroAnimationActive = False
        End Sub

        Private Sub FinalizeIntroAnimation()
            UnhookIntroRendering()

            If _introClock IsNot Nothing Then
                _introClock.Stop()
                _introClock = Nothing
            End If

            FreezeIntroTokensToFinalState()
            ShowIntroMorphCanvas()
            IntroSettledLayer.Opacity = 0
            IntroSettledTranslate.Y = 22
            IntroActionHost.Opacity = 1
            IntroActionTranslate.Y = 0
            IntroStartButton.IsHitTestVisible = True
            IntroStartButton.IsTabStop = True
            SetAnimatedTextRenderingState(False)
            _activeAnimationMode = MorphAnimationMode.IntroOpening
            _isIntroAnimationActive = True
        End Sub

        Private Sub FinalizeSetupTransition()
            UnhookIntroRendering()

            If _introClock IsNot Nothing Then
                _introClock.Stop()
                _introClock = Nothing
            End If

            FreezeIntroTokensToFinalState()
            UpdateTransitionActionHost()
            CompleteSetupTransitionPresentation()
            SetAnimatedTextRenderingState(False)
            _activeAnimationMode = MorphAnimationMode.None
            _isIntroAnimationActive = False
            ExecuteStartSetupCommand()
        End Sub

        Private Sub FinalizeSetupToIntroTransition()
            UnhookIntroRendering()

            If _introClock IsNot Nothing Then
                _introClock.Stop()
                _introClock = Nothing
            End If

            FreezeIntroTokensToFinalState()
            ShowIntroMorphCanvas()
            IntroSettledLayer.Opacity = 0
            IntroSettledTranslate.Y = 22
            IntroActionHost.Opacity = 1
            IntroActionTranslate.Y = 0
            IntroStartButton.IsHitTestVisible = True
            IntroStartButton.IsTabStop = True
            CompleteSetupToIntroTransitionPresentation()
            SetAnimatedTextRenderingState(False)
            _activeAnimationMode = MorphAnimationMode.IntroOpening
            _isIntroAnimationActive = True
            ExecuteBackCommand()
            ClearIntroTransitionHosts()
        End Sub

        Private Sub CompleteSetupTransitionImmediately()
            CompleteSetupTransitionPresentation()
            SetAnimatedTextRenderingState(False)
            _activeAnimationMode = MorphAnimationMode.None
            _isIntroAnimationActive = False
            ExecuteStartSetupCommand()
        End Sub

        Private Sub CompleteSetupToIntroTransitionImmediately()
            FinalizeIntroSettledPresentation()
            CompleteSetupToIntroTransitionPresentation()
            SetAnimatedTextRenderingState(False)
            _activeAnimationMode = MorphAnimationMode.IntroOpening
            _isIntroAnimationActive = True
            ExecuteBackCommand()
            ClearIntroTransitionHosts()
        End Sub

        Private Sub ExecuteStartSetupCommand()
            Dim viewModel = TryCast(DataContext, SensitivityMatchingPageViewModel)
            If viewModel Is Nothing OrElse viewModel.StartSetupCommand Is Nothing Then
                Return
            End If

            If viewModel.StartSetupCommand.CanExecute(Nothing) Then
                viewModel.StartSetupCommand.Execute(Nothing)
            End If
        End Sub

        Private Sub ExecuteContinueFromSetupCommand()
            Dim viewModel = TryCast(DataContext, SensitivityMatchingPageViewModel)
            If viewModel Is Nothing OrElse viewModel.ContinueFromSetupCommand Is Nothing Then
                Return
            End If

            If viewModel.ContinueFromSetupCommand.CanExecute(Nothing) Then
                viewModel.ContinueFromSetupCommand.Execute(Nothing)
            End If
        End Sub

        Private Sub ExecuteBackCommand()
            Dim viewModel = TryCast(DataContext, SensitivityMatchingPageViewModel)
            If viewModel Is Nothing OrElse viewModel.BackCommand Is Nothing Then
                Return
            End If

            If viewModel.BackCommand.CanExecute(Nothing) Then
                viewModel.BackCommand.Execute(Nothing)
            End If
        End Sub

        Private Sub FinalizeIntroSettledPresentation()
            ShowIntroMorphCanvas()
            IntroSettledLayer.Opacity = 0
            IntroSettledTranslate.Y = 22
            IntroActionHost.Opacity = 1
            IntroActionTranslate.Y = 0
            IntroStartButton.IsHitTestVisible = True
            IntroStartButton.IsTabStop = True
        End Sub

        Private Sub FreezeIntroTokensToFinalState()
            For Each token In _openingTokens
                If token.IsPersistent Then
                    ApplyTokenState(token, token.EndPosition, token.EndScale, 1)
                Else
                    ApplyTokenState(token, token.EndPosition, token.EndScale, 0)
                End If
            Next

            For Each token In _targetTokens
                ApplyTokenState(token, token.EndPosition, token.EndScale, 1)
            Next
        End Sub

        Private Sub ClearMorphTokens()
            _openingTokens.Clear()
            _targetTokens.Clear()

            If IntroMorphCanvas IsNot Nothing Then
                IntroMorphCanvas.Children.Clear()
            End If
        End Sub

        Private Sub ResetIntroHeroVisuals()
            If IntroOpeningGuideLayer Is Nothing Then
                Return
            End If

            ClearIntroTransitionHosts()
            ClearMorphTokens()
            IntroOpeningGuideLayer.Opacity = 0
            ResetIntroMorphCanvas()

            IntroSettledLayer.Opacity = 0
            IntroSettledTranslate.Y = 22

            IntroActionHost.Opacity = 0
            IntroActionTranslate.Y = 12
            IntroStartButton.IsHitTestVisible = False
            IntroStartButton.IsTabStop = False
            SetAnimatedTextRenderingState(False)
            ResetSetupTransitionPresentation()
        End Sub

        Private Sub CompleteSetupTransitionPresentation()
            HideIntroMorphCanvas(clearTokens:=True)

            If SetupSectionHost IsNot Nothing Then
                SetupSectionHost.Opacity = 1
            End If

            SetTransitionHostVisualState(SetupSectionHost, isInteractive:=True)
            SetTransitionHostVisualState(SetupFooterHost, isInteractive:=True)

            ShowSetupSettledLayer()

            If SetupUtilityOverlay IsNot Nothing Then
                SetupUtilityOverlay.Opacity = 1
            End If

            If SetupUtilityTranslate IsNot Nothing Then
                SetupUtilityTranslate.Y = 0
            End If

            SetRevealState(SetupSourcePanel, SetupSourcePanelTranslate, 1, 1, 18)
            SetRevealState(SetupTargetPanel, SetupTargetPanelTranslate, 1, 1, 18)
            SetRevealState(SetupSourceInfoBandHost, SetupSourceInfoBandTranslate, 1, 1, 10)
            SetRevealState(SetupTargetInfoBandHost, SetupTargetInfoBandTranslate, 1, 1, 10)
            SetRevealState(SetupFooterHost, SetupFooterTranslate, 1, 1, 14)
        End Sub

        Private Sub CompleteSetupToIntroTransitionPresentation()
            HideSetupSettledLayer()
            SetTransitionHostVisualState(SetupSectionHost, isInteractive:=False)
            SetTransitionHostVisualState(SetupFooterHost, isInteractive:=False)

            If SetupUtilityOverlay IsNot Nothing Then
                SetupUtilityOverlay.Opacity = 0
            End If

            If SetupUtilityTranslate IsNot Nothing Then
                SetupUtilityTranslate.Y = 26
            End If

            SetRetreatState(SetupSourcePanel, SetupSourcePanelTranslate, 1, 1, 18)
            SetRetreatState(SetupTargetPanel, SetupTargetPanelTranslate, 1, 1, 18)
            SetRetreatState(SetupSourceInfoBandHost, SetupSourceInfoBandTranslate, 1, 1, 10)
            SetRetreatState(SetupTargetInfoBandHost, SetupTargetInfoBandTranslate, 1, 1, 10)
            SetRetreatState(SetupFooterHost, SetupFooterTranslate, 1, 1, 14)
        End Sub

        Private Sub ResetSetupTransitionPresentation()
            If SetupSectionHost IsNot Nothing Then
                SetupSectionHost.ClearValue(UIElement.OpacityProperty)
                SetupSectionHost.ClearValue(UIElement.IsHitTestVisibleProperty)
                SetupSectionHost.ClearValue(UIElement.IsEnabledProperty)
            End If

            If SetupFooterHost IsNot Nothing Then
                SetupFooterHost.ClearValue(UIElement.VisibilityProperty)
                SetupFooterHost.ClearValue(UIElement.OpacityProperty)
                SetupFooterHost.ClearValue(UIElement.IsHitTestVisibleProperty)
                SetupFooterHost.ClearValue(UIElement.IsEnabledProperty)
            End If

            ShowSetupSettledLayer()

            If SetupUtilityOverlay IsNot Nothing Then
                SetupUtilityOverlay.Opacity = 1
            End If

            If SetupUtilityTranslate IsNot Nothing Then
                SetupUtilityTranslate.Y = 0
            End If

            SetRevealState(SetupSourcePanel, SetupSourcePanelTranslate, 1, 1, 18)
            SetRevealState(SetupTargetPanel, SetupTargetPanelTranslate, 1, 1, 18)
            SetRevealState(SetupSourceInfoBandHost, SetupSourceInfoBandTranslate, 1, 1, 10)
            SetRevealState(SetupTargetInfoBandHost, SetupTargetInfoBandTranslate, 1, 1, 10)
            SetRevealState(SetupFooterHost, SetupFooterTranslate, 1, 1, 14)
        End Sub

        Private Sub SyncMeasurePresentationState()
            Dim viewModel = TryCast(DataContext, SensitivityMatchingPageViewModel)
            Dim shouldPresent = IsLoaded AndAlso
                                IsVisible AndAlso
                                viewModel IsNot Nothing AndAlso
                                viewModel.IsMeasureStep

            If shouldPresent Then
                If _isMeasureForwardTransitionActive Then
                    FinalizeMeasureForwardTransition()
                    _isMeasurePresentationActive = True
                    Return
                End If

                If Not _isMeasurePresentationActive Then
                    PlayMeasureEntryTransition()
                End If

                SyncMeasureCenterPresentation(animate:=_isMeasurePresentationActive)
                _isMeasurePresentationActive = True
                Return
            End If

            ResetMeasureVisuals()
            _isMeasurePresentationActive = False
        End Sub

        Private Sub SyncMeasureCenterPresentation(animate As Boolean)
            Dim viewModel = TryCast(DataContext, SensitivityMatchingPageViewModel)
            Dim shouldShowResult = viewModel IsNot Nothing AndAlso
                                   viewModel.IsMeasureStep AndAlso
                                   viewModel.HasFinalRecommendation

            If MeasureCenterRoundHost Is Nothing OrElse
               MeasureCenterResultHost Is Nothing OrElse
               MeasureCenterRoundTranslate Is Nothing OrElse
               MeasureCenterResultTranslate Is Nothing Then
                _isMeasureResultCenterVisible = shouldShowResult
                Return
            End If

            If animate AndAlso shouldShowResult <> _isMeasureResultCenterVisible Then
                PlayMeasureCenterSwap(showResult:=shouldShowResult)
                Return
            End If

            SetMeasureCenterPresentation(showResult:=shouldShowResult)
        End Sub

        Private Sub PlayMeasureCenterSwap(showResult As Boolean)
            StopAnimation(MeasureCenterRoundHost, UIElement.OpacityProperty)
            StopAnimation(MeasureCenterResultHost, UIElement.OpacityProperty)
            StopAnimation(MeasureCenterRoundTranslate, TranslateTransform.YProperty)
            StopAnimation(MeasureCenterResultTranslate, TranslateTransform.YProperty)

            MeasureCenterRoundHost.Visibility = Visibility.Visible
            MeasureCenterResultHost.Visibility = Visibility.Visible

            Dim outgoingHost = If(showResult, MeasureCenterRoundHost, MeasureCenterResultHost)
            Dim outgoingTranslate = If(showResult, MeasureCenterRoundTranslate, MeasureCenterResultTranslate)
            Dim incomingHost = If(showResult, MeasureCenterResultHost, MeasureCenterRoundHost)
            Dim incomingTranslate = If(showResult, MeasureCenterResultTranslate, MeasureCenterRoundTranslate)
            Dim outgoingOffset = If(showResult, -MeasureCenterSwapOutgoingOffset, MeasureCenterSwapOutgoingOffset)
            Dim incomingStartOffset = If(showResult, MeasureCenterSwapIncomingOffset, -MeasureCenterSwapIncomingOffset)
            Dim incomingBeginSeconds = If(showResult,
                                          MeasureCenterSwapToResultBeginSeconds,
                                          MeasureCenterSwapToRoundBeginSeconds)

            outgoingHost.Opacity = 1
            outgoingTranslate.Y = 0
            incomingHost.Opacity = 0
            incomingTranslate.Y = incomingStartOffset

            Dim outgoingEase As New CubicEase With {
                .EasingMode = EasingMode.EaseIn
            }
            Dim incomingEase As New SineEase With {
                .EasingMode = EasingMode.EaseOut
            }

            StartDoubleAnimation(outgoingHost,
                                 UIElement.OpacityProperty,
                                 1.0,
                                 0.0,
                                 MeasureCenterSwapDurationSeconds,
                                 easing:=outgoingEase)
            StartDoubleAnimation(outgoingTranslate,
                                 TranslateTransform.YProperty,
                                 0.0,
                                 outgoingOffset,
                                 MeasureCenterSwapDurationSeconds,
                                 easing:=outgoingEase)

            StartDoubleAnimation(incomingHost,
                                 UIElement.OpacityProperty,
                                 0.0,
                                 1.0,
                                 MeasureCenterSwapDurationSeconds,
                                 beginTimeSeconds:=incomingBeginSeconds,
                                 easing:=incomingEase)
            StartDoubleAnimation(incomingTranslate,
                                 TranslateTransform.YProperty,
                                 incomingStartOffset,
                                 0.0,
                                 MeasureCenterSwapDurationSeconds,
                                 beginTimeSeconds:=incomingBeginSeconds,
                                 easing:=incomingEase)

            _isMeasureResultCenterVisible = showResult
        End Sub

        Private Sub SetMeasureCenterPresentation(showResult As Boolean)
            StopAnimation(MeasureCenterRoundHost, UIElement.OpacityProperty)
            StopAnimation(MeasureCenterResultHost, UIElement.OpacityProperty)
            StopAnimation(MeasureCenterRoundTranslate, TranslateTransform.YProperty)
            StopAnimation(MeasureCenterResultTranslate, TranslateTransform.YProperty)

            MeasureCenterRoundHost.Visibility = Visibility.Visible
            MeasureCenterResultHost.Visibility = Visibility.Visible

            MeasureCenterRoundHost.Opacity = If(showResult, 0.0, 1.0)
            MeasureCenterResultHost.Opacity = If(showResult, 1.0, 0.0)
            MeasureCenterRoundTranslate.Y = 0
            MeasureCenterResultTranslate.Y = 0

            _isMeasureResultCenterVisible = showResult
        End Sub

        Private Sub PlaySetupToMeasureTransition(onCompleted As Action)
            If SetupSectionHost Is Nothing OrElse
               SetupFooterHost Is Nothing OrElse
               MeasureSectionHost Is Nothing OrElse
               MeasureFooterHost Is Nothing Then
                If onCompleted IsNot Nothing Then
                    onCompleted.Invoke()
                End If
                Return
            End If

            StopMeasureTransitionTimer()
            StopMeasureAnimations()
            UpdateLayout()

            SetTransitionHostVisualState(SetupSectionHost, isInteractive:=False)
            SetTransitionHostVisualState(SetupFooterHost, isInteractive:=False)
            SetTransitionHostVisualState(MeasureSectionHost, isInteractive:=False)
            SetTransitionHostVisualState(MeasureFooterHost, isInteractive:=False)

            MeasureSectionHost.SetValue(UIElement.VisibilityProperty, Visibility.Visible)
            MeasureFooterHost.SetValue(UIElement.VisibilityProperty, Visibility.Visible)
            UpdateLayout()
            EnableMeasureTransitionVisualStability()

            SetupFooterHost.Opacity = 1

            If SetupSettledLayer IsNot Nothing Then
                SetupSettledLayer.Opacity = 0
            End If

            If SetupUtilityOverlay IsNot Nothing Then
                SetupUtilityOverlay.Opacity = 1
            End If

            If SetupUtilityTranslate IsNot Nothing Then
                SetupUtilityTranslate.Y = 0
            End If

            If MeasureHeroLeftWord IsNot Nothing Then
                MeasureHeroLeftWord.Opacity = 0
            End If

            If MeasureHeroRightWord IsNot Nothing Then
                MeasureHeroRightWord.Opacity = 0
            End If

            PrepareMeasureWordTransitionOverlay(isForward:=True,
                                                initialOpacity:=1.0)

            If MeasureDataLayer IsNot Nothing Then
                MeasureDataLayer.Opacity = 0
            End If

            If MeasureDataTranslate IsNot Nothing Then
                MeasureDataTranslate.Y = 24
            End If

            If MeasureRoundsList IsNot Nothing Then
                MeasureRoundsList.Opacity = 0
            End If

            If MeasureRoundsTranslate IsNot Nothing Then
                MeasureRoundsTranslate.Y = 16
            End If

            If MeasureFooterHost IsNot Nothing Then
                MeasureFooterHost.Opacity = 0
            End If

            If MeasureFooterTranslate IsNot Nothing Then
                MeasureFooterTranslate.Y = 12
            End If

            Dim utilityEase As New CubicEase With {
                .EasingMode = EasingMode.EaseIn
            }
            Dim watermarkEase As New SineEase With {
                .EasingMode = EasingMode.EaseInOut
            }
            Dim contentEase As New CubicEase With {
                .EasingMode = EasingMode.EaseOut
            }
            StartDoubleAnimation(SetupUtilityOverlay,
                                 UIElement.OpacityProperty,
                                 1.0,
                                 0.0,
                                 MeasureUtilityFadeDurationSeconds,
                                 beginTimeSeconds:=0.04,
                                 easing:=utilityEase)
            StartDoubleAnimation(SetupUtilityTranslate,
                                 TranslateTransform.YProperty,
                                 0.0,
                                 -MeasureUtilityFadeOffset,
                                 MeasureUtilityFadeDurationSeconds,
                                 beginTimeSeconds:=0.04,
                                 easing:=utilityEase)
            StartDoubleAnimation(SetupFooterHost,
                                 UIElement.OpacityProperty,
                                 1.0,
                                 0.0,
                                 MeasureSetupFooterFadeDurationSeconds,
                                 beginTimeSeconds:=0.04,
                                 easing:=utilityEase)
            StartDoubleAnimation(SetupFooterTranslate,
                                 TranslateTransform.YProperty,
                                 0.0,
                                 MeasureUtilityFadeOffset,
                                 MeasureSetupFooterFadeDurationSeconds,
                                 beginTimeSeconds:=0.04,
                                 easing:=utilityEase)
            AnimateMeasureWordTransitionOverlay(isForward:=True,
                                                easing:=watermarkEase,
                                                beginTimeSeconds:=MeasureExitWatermarkBeginSeconds,
                                                fromOpacity:=1.0,
                                                toOpacity:=MeasureWatermarkSettledOpacity)

            StartDoubleAnimation(MeasureDataLayer,
                                 UIElement.OpacityProperty,
                                 0.0,
                                 1.0,
                                 MeasureContentRevealDurationSeconds,
                                 beginTimeSeconds:=0.22,
                                 easing:=contentEase)
            StartDoubleAnimation(MeasureDataTranslate,
                                 TranslateTransform.YProperty,
                                 24.0,
                                 0.0,
                                 MeasureContentRevealDurationSeconds,
                                 beginTimeSeconds:=0.22,
                                 easing:=contentEase)

            StartDoubleAnimation(MeasureRoundsList,
                                 UIElement.OpacityProperty,
                                 0.0,
                                 1.0,
                                 MeasureContentRevealDurationSeconds,
                                 beginTimeSeconds:=0.3,
                                 easing:=contentEase)
            StartDoubleAnimation(MeasureRoundsTranslate,
                                 TranslateTransform.YProperty,
                                 16.0,
                                 0.0,
                                 MeasureContentRevealDurationSeconds,
                                 beginTimeSeconds:=0.3,
                                 easing:=contentEase)

            StartDoubleAnimation(MeasureFooterHost,
                                 UIElement.OpacityProperty,
                                 0.0,
                                 1.0,
                                 MeasureContentRevealDurationSeconds,
                                 beginTimeSeconds:=0.38,
                                 easing:=contentEase)
            StartDoubleAnimation(MeasureFooterTranslate,
                                 TranslateTransform.YProperty,
                                 12.0,
                                 0.0,
                                 MeasureContentRevealDurationSeconds,
                                 beginTimeSeconds:=0.38,
                                 easing:=contentEase)

            Dim completionDelaySeconds = Math.Max(MeasureUtilityFadeDurationSeconds + 0.04,
                                                  0.38 + MeasureContentRevealDurationSeconds) + 0.02
            StartMeasureTransitionTimer(completionDelaySeconds, onCompleted)
        End Sub

        Private Sub FinalizeMeasureForwardTransition()
            ResetSetupTransitionPresentation()
            ResetMeasureVisuals()
            _isMeasureForwardTransitionActive = False
        End Sub

        Private Sub PlayMeasureEntryTransition()
            If MeasureSectionHost Is Nothing Then
                Return
            End If

            ResetMeasureVisuals()
            UpdateLayout()

            Dim leftOffset = EnsureMinimumTransitionOffset(
                ComputeRelativeOffset(SetupHeroTopLeftWord, MeasureHeroLeftWord, MeasureSectionHost),
                New Vector(-56.0, -24.0),
                MeasureMinimumTransitionTravel)
            Dim rightOffset = EnsureMinimumTransitionOffset(
                ComputeRelativeOffset(SetupHeroRightLabel, MeasureHeroRightWord, MeasureSectionHost),
                New Vector(56.0, 24.0),
                MeasureMinimumTransitionTravel)

            If MeasureHeroLeftWord IsNot Nothing Then
                MeasureHeroLeftWord.Opacity = MeasureWatermarkLeadOpacity
            End If

            If MeasureHeroRightWord IsNot Nothing Then
                MeasureHeroRightWord.Opacity = MeasureWatermarkLeadOpacity
            End If

            If MeasureSourceScale IsNot Nothing Then
                MeasureSourceScale.ScaleX = MeasureWatermarkLeadScale
                MeasureSourceScale.ScaleY = MeasureWatermarkLeadScale
            End If

            If MeasureTargetScale IsNot Nothing Then
                MeasureTargetScale.ScaleX = MeasureWatermarkLeadScale
                MeasureTargetScale.ScaleY = MeasureWatermarkLeadScale
            End If

            If MeasureSourceTranslate IsNot Nothing Then
                MeasureSourceTranslate.X = leftOffset.X
                MeasureSourceTranslate.Y = leftOffset.Y
            End If

            If MeasureTargetTranslate IsNot Nothing Then
                MeasureTargetTranslate.X = rightOffset.X
                MeasureTargetTranslate.Y = rightOffset.Y
            End If

            If MeasureDataLayer IsNot Nothing Then
                MeasureDataLayer.Opacity = 0
            End If

            If MeasureDataTranslate IsNot Nothing Then
                MeasureDataTranslate.Y = 24
            End If

            If MeasureRoundsList IsNot Nothing Then
                MeasureRoundsList.Opacity = 0
            End If

            If MeasureRoundsTranslate IsNot Nothing Then
                MeasureRoundsTranslate.Y = 18
            End If

            If MeasureFooterHost IsNot Nothing Then
                MeasureFooterHost.Opacity = 0
            End If

            If MeasureFooterTranslate IsNot Nothing Then
                MeasureFooterTranslate.Y = 14
            End If

            Dim watermarkEase As New CubicEase With {
                .EasingMode = EasingMode.EaseOut
            }
            Dim contentEase As New CubicEase With {
                .EasingMode = EasingMode.EaseOut
            }

            StartDoubleAnimation(MeasureHeroLeftWord,
                                 UIElement.OpacityProperty,
                                 MeasureWatermarkLeadOpacity,
                                 MeasureWatermarkSettledOpacity,
                                 MeasureWatermarkRevealDurationSeconds,
                                 easing:=watermarkEase)
            StartDoubleAnimation(MeasureHeroRightWord,
                                 UIElement.OpacityProperty,
                                 MeasureWatermarkLeadOpacity,
                                 MeasureWatermarkSettledOpacity,
                                 MeasureWatermarkRevealDurationSeconds,
                                 beginTimeSeconds:=0.03,
                                 easing:=watermarkEase)

            StartDoubleAnimation(MeasureSourceScale,
                                 ScaleTransform.ScaleXProperty,
                                 MeasureWatermarkLeadScale,
                                 1.0,
                                 MeasureWatermarkRevealDurationSeconds,
                                 easing:=watermarkEase)
            StartDoubleAnimation(MeasureSourceScale,
                                 ScaleTransform.ScaleYProperty,
                                 MeasureWatermarkLeadScale,
                                 1.0,
                                 MeasureWatermarkRevealDurationSeconds,
                                 easing:=watermarkEase)
            StartDoubleAnimation(MeasureTargetScale,
                                 ScaleTransform.ScaleXProperty,
                                 MeasureWatermarkLeadScale,
                                 1.0,
                                 MeasureWatermarkRevealDurationSeconds,
                                 beginTimeSeconds:=0.03,
                                 easing:=watermarkEase)
            StartDoubleAnimation(MeasureTargetScale,
                                 ScaleTransform.ScaleYProperty,
                                 MeasureWatermarkLeadScale,
                                 1.0,
                                 MeasureWatermarkRevealDurationSeconds,
                                 beginTimeSeconds:=0.03,
                                 easing:=watermarkEase)

            StartDoubleAnimation(MeasureSourceTranslate,
                                 TranslateTransform.XProperty,
                                 leftOffset.X,
                                 0.0,
                                 MeasureWatermarkRevealDurationSeconds,
                                 easing:=watermarkEase)
            StartDoubleAnimation(MeasureSourceTranslate,
                                 TranslateTransform.YProperty,
                                 leftOffset.Y,
                                 0.0,
                                 MeasureWatermarkRevealDurationSeconds,
                                 easing:=watermarkEase)
            StartDoubleAnimation(MeasureTargetTranslate,
                                 TranslateTransform.XProperty,
                                 rightOffset.X,
                                 0.0,
                                 MeasureWatermarkRevealDurationSeconds,
                                 beginTimeSeconds:=0.03,
                                 easing:=watermarkEase)
            StartDoubleAnimation(MeasureTargetTranslate,
                                 TranslateTransform.YProperty,
                                 rightOffset.Y,
                                 0.0,
                                 MeasureWatermarkRevealDurationSeconds,
                                 beginTimeSeconds:=0.03,
                                 easing:=watermarkEase)

            StartDoubleAnimation(MeasureDataLayer,
                                 UIElement.OpacityProperty,
                                 0.0,
                                 1.0,
                                 MeasureContentRevealDurationSeconds,
                                 beginTimeSeconds:=0.12,
                                 easing:=contentEase)
            StartDoubleAnimation(MeasureDataTranslate,
                                 TranslateTransform.YProperty,
                                 24.0,
                                 0.0,
                                 MeasureContentRevealDurationSeconds,
                                 beginTimeSeconds:=0.12,
                                 easing:=contentEase)

            StartDoubleAnimation(MeasureRoundsList,
                                 UIElement.OpacityProperty,
                                 0.0,
                                 1.0,
                                 MeasureContentRevealDurationSeconds,
                                 beginTimeSeconds:=0.2,
                                 easing:=contentEase)
            StartDoubleAnimation(MeasureRoundsTranslate,
                                 TranslateTransform.YProperty,
                                 18.0,
                                 0.0,
                                 MeasureContentRevealDurationSeconds,
                                 beginTimeSeconds:=0.2,
                                 easing:=contentEase)

            StartDoubleAnimation(MeasureFooterHost,
                                 UIElement.OpacityProperty,
                                 0.0,
                                 1.0,
                                 MeasureContentRevealDurationSeconds,
                                 beginTimeSeconds:=0.28,
                                 easing:=contentEase)
            StartDoubleAnimation(MeasureFooterTranslate,
                                 TranslateTransform.YProperty,
                                 14.0,
                                 0.0,
                                 MeasureContentRevealDurationSeconds,
                                 beginTimeSeconds:=0.28,
                                 easing:=contentEase)
        End Sub

        Private Sub PlayMeasureExitTransition(onCompleted As Action)
            If MeasureSectionHost Is Nothing OrElse
               MeasureFooterHost Is Nothing OrElse
               SetupSectionHost Is Nothing OrElse
               SetupFooterHost Is Nothing Then
                If onCompleted IsNot Nothing Then
                    onCompleted.Invoke()
                End If
                Return
            End If

            StopMeasureTransitionTimer()
            StopMeasureAnimations()
            SetupFooterHost.SetValue(UIElement.VisibilityProperty, Visibility.Visible)
            SetupSectionHost.Opacity = 1
            UpdateLayout()
            EnableMeasureTransitionVisualStability()

            SetTransitionHostVisualState(MeasureSectionHost, isInteractive:=False)
            SetTransitionHostVisualState(MeasureFooterHost, isInteractive:=False)
            SetTransitionHostVisualState(SetupSectionHost, isInteractive:=False)
            SetTransitionHostVisualState(SetupFooterHost, isInteractive:=False)

            If SetupSettledLayer IsNot Nothing Then
                SetupSettledLayer.Opacity = 0
            End If

            If SetupUtilityOverlay IsNot Nothing Then
                SetupUtilityOverlay.Opacity = 0
            End If

            If SetupUtilityTranslate IsNot Nothing Then
                SetupUtilityTranslate.Y = MeasureUtilityFadeOffset
            End If

            SetRevealState(SetupSourceInfoBandHost, SetupSourceInfoBandTranslate, 0, 0, MeasureSetupInfoBandRevealOffset)
            SetRevealState(SetupTargetInfoBandHost, SetupTargetInfoBandTranslate, 0, 0, MeasureSetupInfoBandRevealOffset)

            SetupFooterHost.Opacity = 0

            If SetupFooterTranslate IsNot Nothing Then
                SetupFooterTranslate.Y = MeasureUtilityFadeOffset
            End If

            If MeasureHeroLeftWord IsNot Nothing Then
                MeasureHeroLeftWord.Opacity = 0
            End If

            If MeasureHeroRightWord IsNot Nothing Then
                MeasureHeroRightWord.Opacity = 0
            End If

            PrepareMeasureWordTransitionOverlay(isForward:=False,
                                                initialOpacity:=MeasureWatermarkSettledOpacity)

            If MeasureDataLayer IsNot Nothing Then
                MeasureDataLayer.Opacity = 1
            End If

            If MeasureDataTranslate IsNot Nothing Then
                MeasureDataTranslate.Y = 0
            End If

            If MeasureRoundsList IsNot Nothing Then
                MeasureRoundsList.Opacity = 1
            End If

            If MeasureRoundsTranslate IsNot Nothing Then
                MeasureRoundsTranslate.Y = 0
            End If

            If MeasureFooterHost IsNot Nothing Then
                MeasureFooterHost.Opacity = 1
            End If

            If MeasureFooterTranslate IsNot Nothing Then
                MeasureFooterTranslate.Y = 0
            End If

            Dim utilityEase As New CubicEase With {
                .EasingMode = EasingMode.EaseOut
            }
            Dim watermarkEase As New SineEase With {
                .EasingMode = EasingMode.EaseInOut
            }
            Dim contentEase As New CubicEase With {
                .EasingMode = EasingMode.EaseIn
            }
            Dim setupInfoBandEase As New CubicEase With {
                .EasingMode = EasingMode.EaseOut
            }

            StartDoubleAnimation(SetupUtilityOverlay,
                                 UIElement.OpacityProperty,
                                 0.0,
                                 1.0,
                                 MeasureUtilityFadeDurationSeconds,
                                 beginTimeSeconds:=0.26,
                                 easing:=utilityEase)
            StartDoubleAnimation(SetupUtilityTranslate,
                                 TranslateTransform.YProperty,
                                 MeasureUtilityFadeOffset,
                                 0.0,
                                 MeasureUtilityFadeDurationSeconds,
                                 beginTimeSeconds:=0.26,
                                 easing:=utilityEase)
            StartDoubleAnimation(SetupFooterHost,
                                 UIElement.OpacityProperty,
                                 0.0,
                                 1.0,
                                 MeasureSetupFooterFadeDurationSeconds,
                                 beginTimeSeconds:=0.32,
                                 easing:=utilityEase)
            StartDoubleAnimation(SetupFooterTranslate,
                                 TranslateTransform.YProperty,
                                 MeasureUtilityFadeOffset,
                                 0.0,
                                 MeasureSetupFooterFadeDurationSeconds,
                                 beginTimeSeconds:=0.32,
                                 easing:=utilityEase)
            StartDoubleAnimation(SetupSourceInfoBandHost,
                                 UIElement.OpacityProperty,
                                 0.0,
                                 1.0,
                                 MeasureSetupInfoBandRevealDurationSeconds,
                                 beginTimeSeconds:=MeasureSetupInfoBandRevealBeginSeconds,
                                 easing:=setupInfoBandEase)
            StartDoubleAnimation(SetupSourceInfoBandTranslate,
                                 TranslateTransform.YProperty,
                                 MeasureSetupInfoBandRevealOffset,
                                 0.0,
                                 MeasureSetupInfoBandRevealDurationSeconds,
                                 beginTimeSeconds:=MeasureSetupInfoBandRevealBeginSeconds,
                                 easing:=setupInfoBandEase)
            StartDoubleAnimation(SetupTargetInfoBandHost,
                                 UIElement.OpacityProperty,
                                 0.0,
                                 1.0,
                                 MeasureSetupInfoBandRevealDurationSeconds,
                                 beginTimeSeconds:=MeasureSetupInfoBandRevealBeginSeconds,
                                 easing:=setupInfoBandEase)
            StartDoubleAnimation(SetupTargetInfoBandTranslate,
                                 TranslateTransform.YProperty,
                                 MeasureSetupInfoBandRevealOffset,
                                 0.0,
                                 MeasureSetupInfoBandRevealDurationSeconds,
                                 beginTimeSeconds:=MeasureSetupInfoBandRevealBeginSeconds,
                                 easing:=setupInfoBandEase)
            AnimateMeasureWordTransitionOverlay(isForward:=False,
                                                easing:=watermarkEase,
                                                beginTimeSeconds:=MeasureExitWatermarkBeginSeconds,
                                                fromOpacity:=MeasureWatermarkSettledOpacity,
                                                toOpacity:=1.0)

            StartDoubleAnimation(MeasureDataLayer,
                                 UIElement.OpacityProperty,
                                 1.0,
                                 0.0,
                                 MeasureExitContentDurationSeconds,
                                 easing:=contentEase)
            StartDoubleAnimation(MeasureDataTranslate,
                                 TranslateTransform.YProperty,
                                 0.0,
                                 20.0,
                                 MeasureExitContentDurationSeconds,
                                 easing:=contentEase)

            StartDoubleAnimation(MeasureRoundsList,
                                 UIElement.OpacityProperty,
                                 1.0,
                                 0.0,
                                 MeasureExitContentDurationSeconds,
                                 beginTimeSeconds:=MeasureExitRoundsBeginSeconds,
                                 easing:=contentEase)
            StartDoubleAnimation(MeasureRoundsTranslate,
                                 TranslateTransform.YProperty,
                                 0.0,
                                 14.0,
                                 MeasureExitContentDurationSeconds,
                                 beginTimeSeconds:=MeasureExitRoundsBeginSeconds,
                                 easing:=contentEase)

            StartDoubleAnimation(MeasureFooterHost,
                                 UIElement.OpacityProperty,
                                 1.0,
                                 0.0,
                                 MeasureExitContentDurationSeconds,
                                 beginTimeSeconds:=MeasureExitFooterBeginSeconds,
                                 easing:=contentEase)
            StartDoubleAnimation(MeasureFooterTranslate,
                                 TranslateTransform.YProperty,
                                 0.0,
                                 12.0,
                                 MeasureExitContentDurationSeconds,
                                 beginTimeSeconds:=MeasureExitFooterBeginSeconds,
                                 easing:=contentEase)

            Dim completionDelaySeconds = Math.Max(MeasureExitWatermarkBeginSeconds + MeasureWatermarkRevealDurationSeconds,
                                                  0.32 + MeasureSetupFooterFadeDurationSeconds) + 0.02
            StartMeasureTransitionTimer(completionDelaySeconds, onCompleted)
        End Sub

        Private Sub PrepareMeasureWordTransitionOverlay(isForward As Boolean,
                                                        initialOpacity As Double)
            If MeasureTransitionOverlay Is Nothing Then
                Return
            End If

            MeasureTransitionOverlay.Visibility = Visibility.Visible

            If isForward Then
                PrepareMeasureWordTransitionToken(MeasureTransitionLeftWord,
                                                  MeasureTransitionLeftScale,
                                                  MeasureTransitionLeftTranslate,
                                                  SetupHeroTopLeftWord,
                                                  MeasureHeroLeftWord,
                                                  initialOpacity)
                PrepareMeasureWordTransitionToken(MeasureTransitionRightWord,
                                                  MeasureTransitionRightScale,
                                                  MeasureTransitionRightTranslate,
                                                  SetupHeroRightLabel,
                                                  MeasureHeroRightWord,
                                                  initialOpacity)
            Else
                PrepareMeasureWordTransitionToken(MeasureTransitionLeftWord,
                                                  MeasureTransitionLeftScale,
                                                  MeasureTransitionLeftTranslate,
                                                  MeasureHeroLeftWord,
                                                  SetupHeroTopLeftWord,
                                                  initialOpacity)
                PrepareMeasureWordTransitionToken(MeasureTransitionRightWord,
                                                  MeasureTransitionRightScale,
                                                  MeasureTransitionRightTranslate,
                                                  MeasureHeroRightWord,
                                                  SetupHeroRightLabel,
                                                  initialOpacity)
            End If
        End Sub

        Private Sub PrepareMeasureWordTransitionToken(overlayWord As TextBlock,
                                                      overlayScale As ScaleTransform,
                                                      overlayTranslate As TranslateTransform,
                                                      fromElement As TextBlock,
                                                      toElement As TextBlock,
                                                      initialOpacity As Double)
            If overlayWord Is Nothing OrElse
               overlayScale Is Nothing OrElse
               overlayTranslate Is Nothing OrElse
               fromElement Is Nothing OrElse
               toElement Is Nothing OrElse
               MeasureTransitionOverlay Is Nothing Then
                Return
            End If

            Dim fromPoint = ComputeRelativePoint(fromElement, MeasureTransitionOverlay)
            overlayWord.Text = fromElement.Text
            Canvas.SetLeft(overlayWord, fromPoint.X)
            Canvas.SetTop(overlayWord, fromPoint.Y)
            overlayScale.ScaleX = ComputeOverlayScale(fromElement, overlayWord)
            overlayScale.ScaleY = overlayScale.ScaleX
            overlayTranslate.X = 0
            overlayTranslate.Y = 0
            overlayWord.Opacity = Clamp(initialOpacity, 0, 1)
            overlayWord.Visibility = Visibility.Visible
        End Sub

        Private Sub AnimateMeasureWordTransitionOverlay(isForward As Boolean,
                                                        easing As IEasingFunction,
                                                        beginTimeSeconds As Double,
                                                        fromOpacity As Double,
                                                        toOpacity As Double)
            If MeasureTransitionOverlay Is Nothing Then
                Return
            End If

            If isForward Then
                AnimateMeasureWordTransitionToken(MeasureTransitionLeftWord,
                                                  MeasureTransitionLeftScale,
                                                  MeasureTransitionLeftTranslate,
                                                  SetupHeroTopLeftWord,
                                                  MeasureHeroLeftWord,
                                                  beginTimeSeconds,
                                                  fromOpacity,
                                                  toOpacity,
                                                  easing)
                AnimateMeasureWordTransitionToken(MeasureTransitionRightWord,
                                                  MeasureTransitionRightScale,
                                                  MeasureTransitionRightTranslate,
                                                  SetupHeroRightLabel,
                                                  MeasureHeroRightWord,
                                                  beginTimeSeconds,
                                                  fromOpacity,
                                                  toOpacity,
                                                  easing)
            Else
                AnimateMeasureWordTransitionToken(MeasureTransitionLeftWord,
                                                  MeasureTransitionLeftScale,
                                                  MeasureTransitionLeftTranslate,
                                                  MeasureHeroLeftWord,
                                                  SetupHeroTopLeftWord,
                                                  beginTimeSeconds,
                                                  fromOpacity,
                                                  toOpacity,
                                                  easing)
                AnimateMeasureWordTransitionToken(MeasureTransitionRightWord,
                                                  MeasureTransitionRightScale,
                                                  MeasureTransitionRightTranslate,
                                                  MeasureHeroRightWord,
                                                  SetupHeroRightLabel,
                                                  beginTimeSeconds,
                                                  fromOpacity,
                                                  toOpacity,
                                                  easing)
            End If
        End Sub

        Private Sub AnimateMeasureWordTransitionToken(overlayWord As TextBlock,
                                                      overlayScale As ScaleTransform,
                                                      overlayTranslate As TranslateTransform,
                                                      fromElement As TextBlock,
                                                      toElement As TextBlock,
                                                      beginTimeSeconds As Double,
                                                      fromOpacity As Double,
                                                      targetOpacity As Double,
                                                      easing As IEasingFunction)
            If overlayWord Is Nothing OrElse
               overlayScale Is Nothing OrElse
               overlayTranslate Is Nothing OrElse
               fromElement Is Nothing OrElse
               toElement Is Nothing OrElse
               MeasureTransitionOverlay Is Nothing Then
                Return
            End If

            Dim fromPoint = ComputeRelativePoint(fromElement, MeasureTransitionOverlay)
            Dim toPoint = ComputeRelativePoint(toElement, MeasureTransitionOverlay)
            Dim fromScale = ComputeOverlayScale(fromElement, overlayWord)
            Dim toScale = ComputeOverlayScale(toElement, overlayWord)

            Canvas.SetLeft(overlayWord, fromPoint.X)
            Canvas.SetTop(overlayWord, fromPoint.Y)
            StartDoubleAnimation(overlayScale,
                                 ScaleTransform.ScaleXProperty,
                                 fromScale,
                                 toScale,
                                 MeasureWatermarkRevealDurationSeconds,
                                 beginTimeSeconds:=beginTimeSeconds,
                                 easing:=easing)
            StartDoubleAnimation(overlayScale,
                                 ScaleTransform.ScaleYProperty,
                                 fromScale,
                                 toScale,
                                 MeasureWatermarkRevealDurationSeconds,
                                 beginTimeSeconds:=beginTimeSeconds,
                                 easing:=easing)
            StartDoubleAnimation(overlayTranslate,
                                 TranslateTransform.XProperty,
                                 0.0,
                                 toPoint.X - fromPoint.X,
                                 MeasureWatermarkRevealDurationSeconds,
                                 beginTimeSeconds:=beginTimeSeconds,
                                 easing:=easing)
            StartDoubleAnimation(overlayTranslate,
                                 TranslateTransform.YProperty,
                                 0.0,
                                 toPoint.Y - fromPoint.Y,
                                 MeasureWatermarkRevealDurationSeconds,
                                 beginTimeSeconds:=beginTimeSeconds,
                                 easing:=easing)
            StartDoubleAnimation(overlayWord,
                                 UIElement.OpacityProperty,
                                 Clamp(fromOpacity, 0, 1),
                                 targetOpacity,
                                 MeasureWatermarkRevealDurationSeconds,
                                 beginTimeSeconds:=beginTimeSeconds,
                                 easing:=easing)
        End Sub

        Private Sub HideMeasureTransitionOverlay()
            If MeasureTransitionOverlay Is Nothing Then
                Return
            End If

            MeasureTransitionOverlay.Visibility = Visibility.Collapsed

            ResetMeasureTransitionToken(MeasureTransitionLeftWord, MeasureTransitionLeftScale, MeasureTransitionLeftTranslate)
            ResetMeasureTransitionToken(MeasureTransitionRightWord, MeasureTransitionRightScale, MeasureTransitionRightTranslate)
        End Sub

        Private Shared Sub ResetMeasureTransitionToken(overlayWord As TextBlock,
                                                       overlayScale As ScaleTransform,
                                                       overlayTranslate As TranslateTransform)
            If overlayWord IsNot Nothing Then
                overlayWord.Opacity = 0
                overlayWord.Visibility = Visibility.Collapsed
                Canvas.SetLeft(overlayWord, 0)
                Canvas.SetTop(overlayWord, 0)
            End If

            If overlayScale IsNot Nothing Then
                overlayScale.ScaleX = 1
                overlayScale.ScaleY = 1
            End If

            If overlayTranslate IsNot Nothing Then
                overlayTranslate.X = 0
                overlayTranslate.Y = 0
            End If
        End Sub

        Private Sub ResetMeasureVisuals()
            HideMeasureTransitionOverlay()
            StopMeasureTransitionTimer()
            StopMeasureAnimations()
            DisableMeasureTransitionVisualStability()
            _isMeasureForwardTransitionActive = False
            _isMeasureBackTransitionActive = False

            If MeasureSectionHost IsNot Nothing Then
                MeasureSectionHost.ClearValue(UIElement.VisibilityProperty)
                MeasureSectionHost.ClearValue(UIElement.IsHitTestVisibleProperty)
                MeasureSectionHost.ClearValue(UIElement.IsEnabledProperty)
            End If

            If MeasureFooterHost IsNot Nothing Then
                MeasureFooterHost.ClearValue(UIElement.VisibilityProperty)
                MeasureFooterHost.ClearValue(UIElement.IsHitTestVisibleProperty)
                MeasureFooterHost.ClearValue(UIElement.IsEnabledProperty)
            End If

            If MeasureHeroLeftWord IsNot Nothing Then
                MeasureHeroLeftWord.Opacity = MeasureWatermarkSettledOpacity
            End If

            If MeasureHeroRightWord IsNot Nothing Then
                MeasureHeroRightWord.Opacity = MeasureWatermarkSettledOpacity
            End If

            If MeasureSourceScale IsNot Nothing Then
                MeasureSourceScale.ScaleX = 1
                MeasureSourceScale.ScaleY = 1
            End If

            If MeasureTargetScale IsNot Nothing Then
                MeasureTargetScale.ScaleX = 1
                MeasureTargetScale.ScaleY = 1
            End If

            If MeasureSourceTranslate IsNot Nothing Then
                MeasureSourceTranslate.X = 0
                MeasureSourceTranslate.Y = 0
            End If

            If MeasureTargetTranslate IsNot Nothing Then
                MeasureTargetTranslate.X = 0
                MeasureTargetTranslate.Y = 0
            End If

            If MeasureDataLayer IsNot Nothing Then
                MeasureDataLayer.Opacity = 1
            End If

            If MeasureDataTranslate IsNot Nothing Then
                MeasureDataTranslate.Y = 0
            End If

            If MeasureRoundsList IsNot Nothing Then
                MeasureRoundsList.Opacity = 1
            End If

            If MeasureRoundsTranslate IsNot Nothing Then
                MeasureRoundsTranslate.Y = 0
            End If

            If MeasureFooterHost IsNot Nothing Then
                MeasureFooterHost.Opacity = 1
            End If

            If MeasureFooterTranslate IsNot Nothing Then
                MeasureFooterTranslate.Y = 0
            End If

            SyncMeasureCenterPresentation(animate:=False)
        End Sub

        Private Sub StartMeasureTransitionTimer(durationSeconds As Double,
                                                onCompleted As Action)
            StopMeasureTransitionTimer()
            _measureTransitionCompletion = onCompleted
            _measureTransitionTimer = New DispatcherTimer(DispatcherPriority.Loaded, Dispatcher) With {
                .Interval = TimeSpan.FromSeconds(Math.Max(0.01, durationSeconds))
            }
            AddHandler _measureTransitionTimer.Tick, AddressOf MeasureTransitionTimer_Tick
            _measureTransitionTimer.Start()
        End Sub

        Private Sub MeasureTransitionTimer_Tick(sender As Object, e As EventArgs)
            Dim completion = _measureTransitionCompletion
            StopMeasureTransitionTimer()

            If completion IsNot Nothing Then
                completion.Invoke()
            End If
        End Sub

        Private Sub StopMeasureTransitionTimer()
            If _measureTransitionTimer IsNot Nothing Then
                RemoveHandler _measureTransitionTimer.Tick, AddressOf MeasureTransitionTimer_Tick
                _measureTransitionTimer.Stop()
                _measureTransitionTimer = Nothing
            End If

            _measureTransitionCompletion = Nothing
        End Sub

        Private Sub StopMeasureAnimations()
            StopAnimation(SetupSettledLayer, UIElement.OpacityProperty)
            StopAnimation(SetupUtilityOverlay, UIElement.OpacityProperty)
            StopAnimation(SetupUtilityTranslate, TranslateTransform.YProperty)
            StopAnimation(SetupSourceInfoBandHost, UIElement.OpacityProperty)
            StopAnimation(SetupSourceInfoBandTranslate, TranslateTransform.YProperty)
            StopAnimation(SetupTargetInfoBandHost, UIElement.OpacityProperty)
            StopAnimation(SetupTargetInfoBandTranslate, TranslateTransform.YProperty)
            StopAnimation(SetupFooterHost, UIElement.OpacityProperty)
            StopAnimation(SetupFooterTranslate, TranslateTransform.YProperty)
            StopAnimation(MeasureTransitionLeftWord, UIElement.OpacityProperty)
            StopAnimation(MeasureTransitionLeftScale, ScaleTransform.ScaleXProperty)
            StopAnimation(MeasureTransitionLeftScale, ScaleTransform.ScaleYProperty)
            StopAnimation(MeasureTransitionLeftTranslate, TranslateTransform.XProperty)
            StopAnimation(MeasureTransitionLeftTranslate, TranslateTransform.YProperty)
            StopAnimation(MeasureTransitionRightWord, UIElement.OpacityProperty)
            StopAnimation(MeasureTransitionRightScale, ScaleTransform.ScaleXProperty)
            StopAnimation(MeasureTransitionRightScale, ScaleTransform.ScaleYProperty)
            StopAnimation(MeasureTransitionRightTranslate, TranslateTransform.XProperty)
            StopAnimation(MeasureTransitionRightTranslate, TranslateTransform.YProperty)
            StopAnimation(MeasureHeroLeftWord, UIElement.OpacityProperty)
            StopAnimation(MeasureHeroRightWord, UIElement.OpacityProperty)
            StopAnimation(MeasureSourceScale, ScaleTransform.ScaleXProperty)
            StopAnimation(MeasureSourceScale, ScaleTransform.ScaleYProperty)
            StopAnimation(MeasureTargetScale, ScaleTransform.ScaleXProperty)
            StopAnimation(MeasureTargetScale, ScaleTransform.ScaleYProperty)
            StopAnimation(MeasureSourceTranslate, TranslateTransform.XProperty)
            StopAnimation(MeasureSourceTranslate, TranslateTransform.YProperty)
            StopAnimation(MeasureTargetTranslate, TranslateTransform.XProperty)
            StopAnimation(MeasureTargetTranslate, TranslateTransform.YProperty)
            StopAnimation(MeasureDataLayer, UIElement.OpacityProperty)
            StopAnimation(MeasureDataTranslate, TranslateTransform.YProperty)
            StopAnimation(MeasureRoundsList, UIElement.OpacityProperty)
            StopAnimation(MeasureRoundsTranslate, TranslateTransform.YProperty)
            StopAnimation(MeasureFooterHost, UIElement.OpacityProperty)
            StopAnimation(MeasureFooterTranslate, TranslateTransform.YProperty)
            StopAnimation(MeasureCenterRoundHost, UIElement.OpacityProperty)
            StopAnimation(MeasureCenterRoundTranslate, TranslateTransform.YProperty)
            StopAnimation(MeasureCenterResultHost, UIElement.OpacityProperty)
            StopAnimation(MeasureCenterResultTranslate, TranslateTransform.YProperty)
        End Sub

        Private Shared Function ComputeRelativeOffset(fromElement As FrameworkElement,
                                                      toElement As FrameworkElement,
                                                      relativeTo As UIElement) As Vector
            If fromElement Is Nothing OrElse
               toElement Is Nothing OrElse
               relativeTo Is Nothing Then
                Return New Vector()
            End If

            Try
                Dim fromPoint = fromElement.TranslatePoint(New Point(0, 0), relativeTo)
                Dim toPoint = toElement.TranslatePoint(New Point(0, 0), relativeTo)
                Return Point.Subtract(fromPoint, toPoint)
            Catch
                Return New Vector()
            End Try
        End Function

        Private Shared Function ComputeRelativePoint(fromElement As FrameworkElement,
                                                     relativeTo As UIElement) As Point
            If fromElement Is Nothing OrElse relativeTo Is Nothing Then
                Return New Point()
            End If

            Try
                Return fromElement.TranslatePoint(New Point(0, 0), relativeTo)
            Catch
                Return New Point()
            End Try
        End Function

        Private Shared Function EnsureMinimumTransitionOffset(offset As Vector,
                                                              fallbackVector As Vector,
                                                              minimumDistance As Double) As Vector
            If offset.Length < minimumDistance Then
                Return fallbackVector
            End If

            Return offset
        End Function

        Private Shared Function ComputeOverlayScale(fromText As TextBlock,
                                                    overlayWord As TextBlock) As Double
            If fromText Is Nothing OrElse
               overlayWord Is Nothing OrElse
               overlayWord.FontSize <= 0 Then
                Return 1.0
            End If

            Return Clamp(fromText.FontSize / overlayWord.FontSize, 0.45, 1.6)
        End Function

        Private Shared Function ComputeAnchorScale(fromText As TextBlock,
                                                   toText As TextBlock) As Double
            If fromText Is Nothing OrElse
               toText Is Nothing OrElse
               toText.FontSize <= 0 Then
                Return 1.0
            End If

            Return Clamp(fromText.FontSize / toText.FontSize, 0.72, 1.4)
        End Function

        Private Shared Sub StartDoubleAnimation(target As UIElement,
                                                [property] As DependencyProperty,
                                                fromValue As Double,
                                                toValue As Double,
                                                durationSeconds As Double,
                                                Optional beginTimeSeconds As Double = 0.0,
                                                Optional easing As IEasingFunction = Nothing)
            If target Is Nothing Then
                Return
            End If

            target.BeginAnimation([property], CreateDoubleAnimation(fromValue, toValue, durationSeconds, beginTimeSeconds, easing))
        End Sub

        Private Shared Sub StartDoubleAnimation(target As Animatable,
                                                [property] As DependencyProperty,
                                                fromValue As Double,
                                                toValue As Double,
                                                durationSeconds As Double,
                                                Optional beginTimeSeconds As Double = 0.0,
                                                Optional easing As IEasingFunction = Nothing)
            If target Is Nothing Then
                Return
            End If

            target.BeginAnimation([property], CreateDoubleAnimation(fromValue, toValue, durationSeconds, beginTimeSeconds, easing))
        End Sub

        Private Shared Function CreateDoubleAnimation(fromValue As Double,
                                                      toValue As Double,
                                                      durationSeconds As Double,
                                                      beginTimeSeconds As Double,
                                                      easing As IEasingFunction) As DoubleAnimation
            Return New DoubleAnimation With {
                .From = fromValue,
                .To = toValue,
                .Duration = TimeSpan.FromSeconds(Math.Max(0.001, durationSeconds)),
                .BeginTime = TimeSpan.FromSeconds(Math.Max(0.0, beginTimeSeconds)),
                .EasingFunction = easing,
                .FillBehavior = FillBehavior.HoldEnd
            }
        End Function

        Private Shared Sub StopAnimation(target As UIElement,
                                         [property] As DependencyProperty)
            If target Is Nothing Then
                Return
            End If

            target.BeginAnimation([property], Nothing)
        End Sub

        Private Shared Sub StopAnimation(target As Animatable,
                                         [property] As DependencyProperty)
            If target Is Nothing Then
                Return
            End If

            target.BeginAnimation([property], Nothing)
        End Sub

        Private Sub EnableMeasureTransitionVisualStability()
        End Sub

        Private Sub DisableMeasureTransitionVisualStability()
        End Sub

        Private Function GetMeasureTransitionTextRoots() As IEnumerable(Of DependencyObject)
            Return {
                CType(SetupSectionHost, DependencyObject),
                CType(SetupFooterHost, DependencyObject),
                CType(MeasureSectionHost, DependencyObject),
                CType(MeasureFooterHost, DependencyObject),
                CType(MeasureTransitionOverlay, DependencyObject)
            }
        End Function

        Private Function GetMeasureTransitionCacheHosts() As IEnumerable(Of UIElement)
            Return {
                CType(SetupUtilityOverlay, UIElement),
                CType(SetupSourceInfoBandHost, UIElement),
                CType(SetupTargetInfoBandHost, UIElement),
                CType(SetupFooterHost, UIElement),
                CType(MeasureDataLayer, UIElement),
                CType(MeasureRoundsList, UIElement),
                CType(MeasureFooterHost, UIElement),
                CType(MeasureTransitionLeftWord, UIElement),
                CType(MeasureTransitionRightWord, UIElement),
                CType(MeasureHeroLeftWord, UIElement),
                CType(MeasureHeroRightWord, UIElement)
            }
        End Function

        Private Sub ApplyMeasureTransitionTextRenderingOverride(root As DependencyObject)
            If root Is Nothing Then
                Return
            End If

            Dim pending As New Stack(Of DependencyObject)()
            pending.Push(root)

            Do While pending.Count > 0
                Dim current = pending.Pop()
                If current Is Nothing Then
                    Continue Do
                End If

                If Not _measureTransitionTextRenderingStates.ContainsKey(current) Then
                    _measureTransitionTextRenderingStates(current) = CaptureMeasureTransitionTextRenderingState(current)
                End If

                TextOptions.SetTextRenderingMode(current, TextRenderingMode.Grayscale)
                TextOptions.SetTextHintingMode(current, TextHintingMode.Animated)
                RenderOptions.SetClearTypeHint(current, ClearTypeHint.Auto)

                Dim currentUiElement = TryCast(current, UIElement)
                If currentUiElement IsNot Nothing Then
                    currentUiElement.SnapsToDevicePixels = False
                End If

                Dim currentFrameworkElement = TryCast(current, FrameworkElement)
                If currentFrameworkElement IsNot Nothing Then
                    currentFrameworkElement.UseLayoutRounding = False
                End If

                For childIndex = VisualTreeHelper.GetChildrenCount(current) - 1 To 0 Step -1
                    pending.Push(VisualTreeHelper.GetChild(current, childIndex))
                Next
            Loop
        End Sub

        Private Sub RestoreMeasureTransitionTextRenderingOverrides()
            For Each entry In _measureTransitionTextRenderingStates
                RestoreDependencyProperty(entry.Key,
                                          TextOptions.TextRenderingModeProperty,
                                          entry.Value.HasLocalTextRenderingMode,
                                          entry.Value.TextRenderingMode)
                RestoreDependencyProperty(entry.Key,
                                          TextOptions.TextHintingModeProperty,
                                          entry.Value.HasLocalTextHintingMode,
                                          entry.Value.TextHintingMode)
                RestoreDependencyProperty(entry.Key,
                                          RenderOptions.ClearTypeHintProperty,
                                          entry.Value.HasLocalClearTypeHint,
                                          entry.Value.ClearTypeHint)

                Dim targetUiElement = TryCast(entry.Key, UIElement)
                If targetUiElement IsNot Nothing Then
                    RestoreDependencyProperty(targetUiElement,
                                              UIElement.SnapsToDevicePixelsProperty,
                                              entry.Value.HasLocalSnapsToDevicePixels,
                                              entry.Value.SnapsToDevicePixels)
                End If

                Dim targetFrameworkElement = TryCast(entry.Key, FrameworkElement)
                If targetFrameworkElement IsNot Nothing Then
                    RestoreDependencyProperty(targetFrameworkElement,
                                              FrameworkElement.UseLayoutRoundingProperty,
                                              entry.Value.HasLocalUseLayoutRounding,
                                              entry.Value.UseLayoutRounding)
                End If
            Next

            _measureTransitionTextRenderingStates.Clear()
        End Sub

        Private Shared Function CaptureMeasureTransitionTextRenderingState(target As DependencyObject) As MeasureTransitionTextRenderingState
            Dim uiElement = TryCast(target, UIElement)
            Dim frameworkElement = TryCast(target, FrameworkElement)

            Return New MeasureTransitionTextRenderingState With {
                .HasLocalTextRenderingMode = HasLocalValue(target, TextOptions.TextRenderingModeProperty),
                .TextRenderingMode = TextOptions.GetTextRenderingMode(target),
                .HasLocalTextHintingMode = HasLocalValue(target, TextOptions.TextHintingModeProperty),
                .TextHintingMode = TextOptions.GetTextHintingMode(target),
                .HasLocalClearTypeHint = HasLocalValue(target, RenderOptions.ClearTypeHintProperty),
                .ClearTypeHint = RenderOptions.GetClearTypeHint(target),
                .HasLocalSnapsToDevicePixels = uiElement IsNot Nothing AndAlso HasLocalValue(uiElement, UIElement.SnapsToDevicePixelsProperty),
                .SnapsToDevicePixels = uiElement IsNot Nothing AndAlso uiElement.SnapsToDevicePixels,
                .HasLocalUseLayoutRounding = frameworkElement IsNot Nothing AndAlso HasLocalValue(frameworkElement, FrameworkElement.UseLayoutRoundingProperty),
                .UseLayoutRounding = frameworkElement IsNot Nothing AndAlso frameworkElement.UseLayoutRounding
            }
        End Function

        Private Sub ApplyMeasureTransitionBitmapCache(host As UIElement)
            If host Is Nothing Then
                Return
            End If

            If Not _measureTransitionCacheStates.ContainsKey(host) Then
                _measureTransitionCacheStates(host) = CaptureMeasureTransitionCacheState(host)
            End If

            Dim dpi = VisualTreeHelper.GetDpi(host)
            host.CacheMode = New BitmapCache With {
                .EnableClearType = False,
                .RenderAtScale = Math.Max(1.0, Math.Max(dpi.DpiScaleX, dpi.DpiScaleY))
            }
        End Sub

        Private Sub RestoreMeasureTransitionBitmapCaches()
            For Each entry In _measureTransitionCacheStates
                RestoreDependencyProperty(entry.Key,
                                          UIElement.CacheModeProperty,
                                          entry.Value.HasLocalCacheMode,
                                          entry.Value.CacheMode)
            Next

            _measureTransitionCacheStates.Clear()
        End Sub

        Private Shared Function CaptureMeasureTransitionCacheState(host As UIElement) As MeasureTransitionCacheState
            Return New MeasureTransitionCacheState With {
                .HasLocalCacheMode = HasLocalValue(host, UIElement.CacheModeProperty),
                .CacheMode = host.CacheMode
            }
        End Function

        Private Shared Function HasLocalValue(target As DependencyObject,
                                              [property] As DependencyProperty) As Boolean
            If target Is Nothing OrElse [property] Is Nothing Then
                Return False
            End If

            Return DependencyPropertyHelper.GetValueSource(target, [property]).BaseValueSource = BaseValueSource.Local
        End Function

        Private Shared Sub RestoreDependencyProperty(target As DependencyObject,
                                                     [property] As DependencyProperty,
                                                     hadLocalValue As Boolean,
                                                     value As Object)
            If target Is Nothing OrElse [property] Is Nothing Then
                Return
            End If

            If hadLocalValue Then
                target.SetValue([property], value)
            Else
                target.ClearValue([property])
            End If
        End Sub

        Private Sub SetAnimatedTextRenderingState(isAnimated As Boolean)
        End Sub

        Private Function GetAnimatedTextRoots() As IEnumerable(Of DependencyObject)
            Return {
                CType(IntroOpeningGuideLayer, DependencyObject),
                CType(IntroMorphCanvas, DependencyObject),
                CType(IntroActionHost, DependencyObject),
                CType(SetupUtilityOverlay, DependencyObject),
                CType(SetupSourcePanel, DependencyObject),
                CType(SetupTargetPanel, DependencyObject),
                CType(SetupSourceInfoBandHost, DependencyObject),
                CType(SetupTargetInfoBandHost, DependencyObject),
                CType(SetupFooterHost, DependencyObject)
            }
        End Function

        Private Function GetAnimatedTextCacheHosts() As IEnumerable(Of UIElement)
            Return {
                CType(IntroOpeningGuideLayer, UIElement),
                CType(IntroMorphCanvas, UIElement),
                CType(IntroActionHost, UIElement),
                CType(SetupUtilityOverlay, UIElement),
                CType(SetupSourcePanel, UIElement),
                CType(SetupTargetPanel, UIElement),
                CType(SetupSourceInfoBandHost, UIElement),
                CType(SetupTargetInfoBandHost, UIElement),
                CType(SetupFooterHost, UIElement)
            }
        End Function

        Private Sub ApplyAnimatedTextRenderingOverride(root As DependencyObject)
            If root Is Nothing Then
                Return
            End If

            Dim pending As New Stack(Of DependencyObject)()
            pending.Push(root)

            Do While pending.Count > 0
                Dim current = pending.Pop()
                If current Is Nothing Then
                    Continue Do
                End If

                If Not _animatedTextRenderingStates.ContainsKey(current) Then
                    _animatedTextRenderingStates(current) = CaptureMeasureTransitionTextRenderingState(current)
                End If

                TextOptions.SetTextRenderingMode(current, TextRenderingMode.Grayscale)
                TextOptions.SetTextHintingMode(current, TextHintingMode.Animated)
                RenderOptions.SetClearTypeHint(current, ClearTypeHint.Auto)

                Dim currentUiElement = TryCast(current, UIElement)
                If currentUiElement IsNot Nothing Then
                    currentUiElement.SnapsToDevicePixels = False
                End If

                Dim currentFrameworkElement = TryCast(current, FrameworkElement)
                If currentFrameworkElement IsNot Nothing Then
                    currentFrameworkElement.UseLayoutRounding = False
                End If

                For childIndex = VisualTreeHelper.GetChildrenCount(current) - 1 To 0 Step -1
                    pending.Push(VisualTreeHelper.GetChild(current, childIndex))
                Next
            Loop
        End Sub

        Private Sub RestoreAnimatedTextRenderingOverrides()
            For Each entry In _animatedTextRenderingStates
                RestoreDependencyProperty(entry.Key,
                                          TextOptions.TextRenderingModeProperty,
                                          entry.Value.HasLocalTextRenderingMode,
                                          entry.Value.TextRenderingMode)
                RestoreDependencyProperty(entry.Key,
                                          TextOptions.TextHintingModeProperty,
                                          entry.Value.HasLocalTextHintingMode,
                                          entry.Value.TextHintingMode)
                RestoreDependencyProperty(entry.Key,
                                          RenderOptions.ClearTypeHintProperty,
                                          entry.Value.HasLocalClearTypeHint,
                                          entry.Value.ClearTypeHint)

                Dim targetUiElement = TryCast(entry.Key, UIElement)
                If targetUiElement IsNot Nothing Then
                    RestoreDependencyProperty(targetUiElement,
                                              UIElement.SnapsToDevicePixelsProperty,
                                              entry.Value.HasLocalSnapsToDevicePixels,
                                              entry.Value.SnapsToDevicePixels)
                End If

                Dim targetFrameworkElement = TryCast(entry.Key, FrameworkElement)
                If targetFrameworkElement IsNot Nothing Then
                    RestoreDependencyProperty(targetFrameworkElement,
                                              FrameworkElement.UseLayoutRoundingProperty,
                                              entry.Value.HasLocalUseLayoutRounding,
                                              entry.Value.UseLayoutRounding)
                End If
            Next

            _animatedTextRenderingStates.Clear()
        End Sub

        Private Sub ApplyAnimatedTextBitmapCache(host As UIElement)
            If host Is Nothing Then
                Return
            End If

            If Not _animatedTextCacheStates.ContainsKey(host) Then
                _animatedTextCacheStates(host) = CaptureMeasureTransitionCacheState(host)
            End If

            Dim dpi = VisualTreeHelper.GetDpi(host)
            host.CacheMode = New BitmapCache With {
                .EnableClearType = False,
                .RenderAtScale = Math.Max(1.0, Math.Max(dpi.DpiScaleX, dpi.DpiScaleY))
            }
        End Sub

        Private Sub RestoreAnimatedTextBitmapCaches()
            For Each entry In _animatedTextCacheStates
                RestoreDependencyProperty(entry.Key,
                                          UIElement.CacheModeProperty,
                                          entry.Value.HasLocalCacheMode,
                                          entry.Value.CacheMode)
            Next

            _animatedTextCacheStates.Clear()
        End Sub

        Private Shared Function Clamp01(value As Double) As Double
            Return Clamp(value, 0, 1)
        End Function

        Private Sub ShowIntroMorphCanvas()
            If IntroMorphCanvas Is Nothing Then
                Return
            End If

            IntroMorphCanvas.Visibility = Visibility.Visible
            IntroMorphCanvas.Opacity = 1
        End Sub

        Private Sub HideIntroMorphCanvas(Optional clearTokens As Boolean = False)
            If IntroMorphCanvas IsNot Nothing Then
                IntroMorphCanvas.Opacity = 0
                IntroMorphCanvas.Visibility = Visibility.Hidden
            End If

            If clearTokens Then
                ClearMorphTokens()
            End If
        End Sub

        Private Sub ResetIntroMorphCanvas()
            If IntroMorphCanvas Is Nothing Then
                Return
            End If

            IntroMorphCanvas.Visibility = Visibility.Visible
            IntroMorphCanvas.Opacity = 0
        End Sub

        Private Sub ShowIntroTransitionHosts()
            If IntroSectionHost IsNot Nothing Then
                IntroSectionHost.Visibility = Visibility.Visible
            End If

            If IntroActionSectionHost IsNot Nothing Then
                IntroActionSectionHost.Visibility = Visibility.Visible
            End If
        End Sub

        Private Sub ClearIntroTransitionHosts()
            If IntroSectionHost IsNot Nothing Then
                IntroSectionHost.ClearValue(UIElement.VisibilityProperty)
            End If

            If IntroActionSectionHost IsNot Nothing Then
                IntroActionSectionHost.ClearValue(UIElement.VisibilityProperty)
            End If
        End Sub

        Private Sub HideSetupSettledLayer()
            If SetupSettledLayer Is Nothing Then
                Return
            End If

            SetupSettledLayer.Opacity = 0
            SetupSettledLayer.Visibility = Visibility.Hidden
        End Sub

        Private Sub ShowSetupSettledLayer()
            If SetupSettledLayer Is Nothing Then
                Return
            End If

            SetupSettledLayer.Visibility = Visibility.Visible
            SetupSettledLayer.Opacity = 1
        End Sub

        Private Sub SetTransitionHostVisualState(host As UIElement,
                                                 isInteractive As Boolean)
            If host Is Nothing Then
                Return
            End If

            host.SetValue(UIElement.IsEnabledProperty, True)
            host.SetValue(UIElement.IsHitTestVisibleProperty, isInteractive)
        End Sub

        Private Shared Function Clamp(value As Double,
                                      minimum As Double,
                                      maximum As Double) As Double
            Return Math.Max(minimum, Math.Min(maximum, value))
        End Function

        Private Shared Function EaseOutCubic(value As Double) As Double
            Dim clamped = Clamp01(value)
            Dim inverse = 1.0 - clamped
            Return 1.0 - (inverse * inverse * inverse)
        End Function

        Private Shared Function EaseInOutCubic(value As Double) As Double
            Dim clamped = Clamp01(value)
            If clamped < 0.5 Then
                Return 4.0 * clamped * clamped * clamped
            End If

            Dim inverse = (-2.0 * clamped) + 2.0
            Return 1.0 - ((inverse * inverse * inverse) / 2.0)
        End Function

        Private Shared Function EaseInCubic(value As Double) As Double
            Dim clamped = Clamp01(value)
            Return clamped * clamped * clamped
        End Function

        Private Shared Function EaseInOutSine(value As Double) As Double
            Dim clamped = Clamp01(value)
            Return -(Math.Cos(Math.PI * clamped) - 1.0) / 2.0
        End Function

        Private Shared Function StabilizeLandingProgress(progress As Double,
                                                         threshold As Double) As Double
            Dim clamped = Clamp01(progress)
            If clamped <= threshold Then
                Return clamped
            End If

            Dim tailProgress = Clamp01((clamped - threshold) / Math.Max(1.0 - threshold, 0.0001))
            Dim softened = threshold + ((1.0 - threshold) * EaseInOutSine(tailProgress))
            If softened >= 0.99985 Then
                Return 1.0
            End If

            Return softened
        End Function

        Private Shared Function FinalizeMotionProgress(progress As Double,
                                                       snapThreshold As Double) As Double
            Dim clamped = Clamp01(progress)
            If clamped >= snapThreshold Then
                Return 1.0
            End If

            Return clamped
        End Function

        Private Shared Function Lerp(startValue As Double,
                                     endValue As Double,
                                     progress As Double) As Double
            Return startValue + ((endValue - startValue) * progress)
        End Function

        Private Shared Function Lerp(startPoint As Point,
                                     endPoint As Point,
                                     progress As Double) As Point
            Return New Point(Lerp(startPoint.X, endPoint.X, progress),
                             Lerp(startPoint.Y, endPoint.Y, progress))
        End Function

        Private Shared Function NormalizeDpiText(text As String) As String
            If String.IsNullOrEmpty(text) Then
                Return String.Empty
            End If

            Dim builder As New System.Text.StringBuilder(text.Length)
            For Each ch In text
                If Char.IsDigit(ch) Then
                    builder.Append(ch)
                End If
            Next

            Return builder.ToString()
        End Function

        Private Shared Function CountDigitsBeforeIndex(text As String, index As Integer) As Integer
            If String.IsNullOrEmpty(text) OrElse index <= 0 Then
                Return 0
            End If

            Dim safeIndex = Math.Max(0, Math.Min(index, text.Length))
            Dim count = 0
            For i = 0 To safeIndex - 1
                If Char.IsDigit(text(i)) Then
                    count += 1
                End If
            Next

            Return count
        End Function

        Private NotInheritable Class IntroGlyphAnchor
            Public Property Character As String
            Public Property Position As Point
            Public Property FontFamily As FontFamily
            Public Property FontSize As Double
            Public Property FontStyle As FontStyle
            Public Property FontStretch As FontStretch
            Public Property FontWeight As FontWeight
            Public Property Foreground As Brush
            Public Property TextFormattingMode As TextFormattingMode
            Public Property TextRenderingMode As TextRenderingMode
            Public Property TextHintingMode As TextHintingMode
            Public Property GroupKey As String
            Public Property CharacterIndex As Integer
            Public Property SequenceIndex As Integer
            Public Property Width As Double
            Public Property Height As Double
        End Class

        Private NotInheritable Class MeasureTransitionTextRenderingState
            Public Property HasLocalTextRenderingMode As Boolean
            Public Property TextRenderingMode As TextRenderingMode
            Public Property HasLocalTextHintingMode As Boolean
            Public Property TextHintingMode As TextHintingMode
            Public Property HasLocalClearTypeHint As Boolean
            Public Property ClearTypeHint As ClearTypeHint
            Public Property HasLocalSnapsToDevicePixels As Boolean
            Public Property SnapsToDevicePixels As Boolean
            Public Property HasLocalUseLayoutRounding As Boolean
            Public Property UseLayoutRounding As Boolean
        End Class

        Private NotInheritable Class MeasureTransitionCacheState
            Public Property HasLocalCacheMode As Boolean
            Public Property CacheMode As CacheMode
        End Class

        Private NotInheritable Class IntroMorphToken
            Public Property Element As TextBlock
            Public Property Scale As ScaleTransform
            Public Property StartPosition As Point
            Public Property EndPosition As Point
            Public Property StartScale As Double
            Public Property EndScale As Double
            Public Property EnterDelay As Double
            Public Property MotionDuration As Double
            Public Property ExitStart As Double
            Public Property ExitEnd As Double
            Public Property IsPersistent As Boolean
            Public Property RevealThreshold As Double
            Public Property RevealStartSeconds As Double
            Public Property RevealEndSeconds As Double
        End Class

        Private Enum MorphAnimationMode
            None
            IntroOpening
            IntroToSetupTransition
            SetupToIntroTransition
        End Enum
    End Class
End Namespace
