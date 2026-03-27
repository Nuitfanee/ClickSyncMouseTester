Imports System.ComponentModel
Imports System.Diagnostics
Imports System.Globalization
Imports System.Runtime.Versioning
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports System.Windows.Shapes
Imports System.Windows.Threading
Imports WpfApp1.ViewModels

Namespace Views.Shell
    <SupportedOSPlatform("windows")>
    Public Class StartupNavigationWindow
        Private Const NavigationBrandOpenDelayMilliseconds As Integer = 250
        Private Const NavigationBrandStepDurationMilliseconds As Integer = 450
        Private Const NavigationBrandCloseResetDelayMilliseconds As Integer = 500
        Private Const NavigationBrandAnimationFrameMilliseconds As Integer = 16
        Private Const StartupCarrierDeferredCloseDelayMilliseconds As Integer = 600

        Private Shared ReadOnly NavigationBrandClosedPrimaryPoints As Point() = ParsePolygonPoints("0,760 0,559.3 0,0 166.5,0 409,0 409,760")
        Private Shared ReadOnly NavigationBrandClosedSecondaryPoints As Point() = ParsePolygonPoints("409,0 409,0 242.5,0 242.5,0 409,0")
        Private Shared ReadOnly NavigationBrandPrimaryOpenFrames As Point()() = {
            NavigationBrandClosedPrimaryPoints,
            ParsePolygonPoints("0,760 0,559.3 0,124.7 166.5,124.7 409,124.7 409,760"),
            ParsePolygonPoints("0,760 0,559.3 0,0 166.5,0 409,559.3 409,760"),
            ParsePolygonPoints("127.2,760 127.2,559.3 0,0 166.5,0 277,559.3 277,760")
        }
        Private Shared ReadOnly NavigationBrandSecondaryOpenFrames As Point()() = {
            NavigationBrandClosedSecondaryPoints,
            ParsePolygonPoints("409,124.7 409,124.7 242.5,124.7 242.5,0 409,0"),
            ParsePolygonPoints("409,508.2 409,508.2 242.5,124.7 242.5,0 409,0"),
            ParsePolygonPoints("293.7,508.2 293.7,508.2 217.6,124.7 242.5,0 409,0")
        }

        Private ReadOnly _shellViewModel As ShellViewModel
        Private ReadOnly _mainWindow As Global.WpfApp1.MainWindow
        Private ReadOnly _navigationLocalTimeZone As TimeZoneInfo = TimeZoneInfo.Local
        Private ReadOnly _navigationLocalClockTimer As DispatcherTimer

        Private _hasStartedPresentation As Boolean
        Private _navigationMenuAnimationVersion As Integer
        Private _isPreparingNavigationCurtainOpen As Boolean
        Private _isStartupInteractionReady As Boolean
        Private _isStartupPresentationComplete As Boolean
        Private _startupCloseTransitionTask As Task
        Private _hasStartedDeferredClose As Boolean
        Private _hasHandedOffToMainWindow As Boolean

        Public Sub New(shellViewModel As ShellViewModel, mainWindow As Global.WpfApp1.MainWindow)
            If shellViewModel Is Nothing Then
                Throw New ArgumentNullException(NameOf(shellViewModel))
            End If

            If mainWindow Is Nothing Then
                Throw New ArgumentNullException(NameOf(mainWindow))
            End If

            InitializeComponent()

            _shellViewModel = shellViewModel
            _mainWindow = mainWindow
            DataContext = _shellViewModel

            _navigationLocalClockTimer = New DispatcherTimer(DispatcherPriority.Background, Dispatcher) With {
                .Interval = TimeSpan.FromSeconds(1)
            }
            AddHandler _navigationLocalClockTimer.Tick, AddressOf OnNavigationLocalClockTick
            UpdateNavigationLocalClockText()
            _navigationLocalClockTimer.Start()
        End Sub

        Protected Overrides Sub OnContentRendered(e As EventArgs)
            MyBase.OnContentRendered(e)

            If _hasStartedPresentation Then
                Return
            End If

            _hasStartedPresentation = True
            Dispatcher.BeginInvoke(New Action(AddressOf BeginStartupPresentation), DispatcherPriority.Loaded)
        End Sub

        Protected Overrides Sub OnClosed(e As EventArgs)
            RemoveHandler _navigationLocalClockTimer.Tick, AddressOf OnNavigationLocalClockTick
            _navigationLocalClockTimer.Stop()

            If Not _hasHandedOffToMainWindow AndAlso _mainWindow IsNot Nothing Then
                _mainWindow.Close()
            End If

            MyBase.OnClosed(e)
        End Sub

        Private Async Sub BeginStartupPresentation()
            Try
                _navigationMenuAnimationVersion += 1
                Dim animationVersion = _navigationMenuAnimationVersion

                Await Dispatcher.InvokeAsync(Sub()
                                                 NavigationCurtainSurface.Visibility = Visibility.Visible
                                                 NavigationCurtainSurface.IsHitTestVisible = True

                                                 If StartupInteractionRoot IsNot Nothing Then
                                                     StartupInteractionRoot.IsHitTestVisible = True
                                                 End If

                                                 NavigationCurtainSurface.UpdateLayout()
                                                 If NavigationMenuItemsControl IsNot Nothing Then
                                                     NavigationMenuItemsControl.UpdateLayout()
                                                 End If
                                                 UpdateNavigationCurtainGuideLayout()
                                                 UpdateNavigationCurtainClip(0.0)
                                                 SetNavigationBrandQuadrantState(NavigationBrandClosedPrimaryPoints,
                                                                                 NavigationBrandClosedSecondaryPoints)
                                                 SetNavigationBrandQuadrantRevealState(False)
                                                 ResetNavigationMenuItemsForNextOpen()
                                                 _isStartupInteractionReady = True
                                              End Sub,
                                             DispatcherPriority.Loaded)

                AnimateNavigationCurtain(True)

                Dim brandTask = AnimateNavigationBrandQuadrantOpenAsync(animationVersion)
                Dim menuTask = AnimateNavigationMenuItemsOpenAsync()
                Await Task.WhenAll(brandTask, menuTask)

                If animationVersion <> _navigationMenuAnimationVersion OrElse
                   Not IsLoaded OrElse
                   _startupCloseTransitionTask IsNot Nothing Then
                    Return
                End If

                Await Dispatcher.InvokeAsync(Sub()
                                                  _isStartupPresentationComplete = True
                                                  NavigationCurtainSurface.IsHitTestVisible = True

                                                 If StartupInteractionRoot IsNot Nothing Then
                                                     StartupInteractionRoot.IsHitTestVisible = True
                                                 End If

                                                  Activate()
                                                  Focus()
                                              End Sub,
                                             DispatcherPriority.Loaded)
            Catch ex As Exception
                Trace.WriteLine($"Startup curtain presentation failed: {ex}")
                RecoverFromStartupPresentationFailure()
            End Try
        End Sub

        Private Sub RecoverFromStartupPresentationFailure()
            Try
                If StartupInteractionRoot IsNot Nothing Then
                    StartupInteractionRoot.IsHitTestVisible = False
                End If

                If NavigationCurtainSurface IsNot Nothing Then
                    NavigationCurtainSurface.IsHitTestVisible = False
                End If

                _isStartupInteractionReady = False
                AlignMainWindowToStartupWindow()
                _mainWindow.ShowInTaskbar = True
                _hasHandedOffToMainWindow = True
                _mainWindow.Activate()
                Close()
            Catch recoveryEx As Exception
                Trace.WriteLine($"Startup presentation recovery failed: {recoveryEx}")

                Dim currentApplication = System.Windows.Application.Current
                If currentApplication IsNot Nothing Then
                    currentApplication.Shutdown()
                End If
            End Try
        End Sub

        Private Sub OnNavigationLocalClockTick(sender As Object, e As EventArgs)
            UpdateNavigationLocalClockText()
        End Sub

        Protected Overrides Sub OnPreviewKeyDown(e As KeyEventArgs)
            If _shellViewModel IsNot Nothing AndAlso
               _shellViewModel.IsNavigationMenuOpen AndAlso
               _isStartupInteractionReady AndAlso
               e.Key = Key.Escape Then
                e.Handled = True
                BeginStartupCloseTransition(Sub()
                                                _shellViewModel.CloseNavigationMenu()
                                            End Sub)
                Return
            End If

            MyBase.OnPreviewKeyDown(e)
        End Sub

        Private Sub NavigationCurtainCloseButton_Click(sender As Object, e As RoutedEventArgs)
            If _shellViewModel Is Nothing OrElse Not _shellViewModel.IsNavigationMenuOpen OrElse Not _isStartupInteractionReady Then
                Return
            End If

            e.Handled = True
            BeginStartupCloseTransition(Sub()
                                            _shellViewModel.CloseNavigationMenu()
                                        End Sub)
        End Sub

        Private Sub GithubLinkButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                Process.Start(New ProcessStartInfo With {
                    .FileName = "https://github.com/Nuitfanee",
                    .UseShellExecute = True
                })
            Catch ex As Exception
                Trace.WriteLine($"Failed to open GitHub link: {ex.Message}")
            End Try
        End Sub

        Private Sub NavigationMenuButton_PreviewMouseLeftButtonDown(sender As Object, e As MouseButtonEventArgs)
            If Not _isStartupInteractionReady Then
                Return
            End If

            Dim menuButton = TryCast(sender, Button)
            Dim descriptor = TryCast(menuButton?.DataContext, Navigation.AppPageDescriptor)
            If descriptor Is Nothing OrElse _shellViewModel Is Nothing Then
                Return
            End If

            e.Handled = True
            SetNavigationMenuHoverSurfaceState(1.0, New Button() {menuButton})
            BeginStartupCloseTransition(Sub()
                                            If _shellViewModel.NavigateToPageCommand IsNot Nothing AndAlso
                                               _shellViewModel.NavigateToPageCommand.CanExecute(descriptor) Then
                                                _shellViewModel.NavigateToPageCommand.Execute(descriptor)
                                            End If
                                        End Sub)
        End Sub

        Private Sub NavigationMenuButton_PreviewKeyDown(sender As Object, e As KeyEventArgs)
            If Not _isStartupInteractionReady OrElse e Is Nothing OrElse (e.Key <> Key.Enter AndAlso e.Key <> Key.Space) Then
                Return
            End If

            Dim menuButton = TryCast(sender, Button)
            Dim descriptor = TryCast(menuButton?.DataContext, Navigation.AppPageDescriptor)
            If descriptor Is Nothing OrElse _shellViewModel Is Nothing Then
                Return
            End If

            e.Handled = True
            SetNavigationMenuHoverSurfaceState(1.0, New Button() {menuButton})
            BeginStartupCloseTransition(Sub()
                                            If _shellViewModel.NavigateToPageCommand IsNot Nothing AndAlso
                                               _shellViewModel.NavigateToPageCommand.CanExecute(descriptor) Then
                                                _shellViewModel.NavigateToPageCommand.Execute(descriptor)
                                            End If
                                        End Sub)
        End Sub

        Private Sub NavigationMenuButton_MouseEnter(sender As Object, e As MouseEventArgs)
            SetNavigationMenuHoverSurfaceState(1.0, New Button() {TryCast(sender, Button)})
        End Sub

        Private Sub NavigationMenuButton_MouseLeave(sender As Object, e As MouseEventArgs)
            SetNavigationMenuHoverSurfaceState(0.0, New Button() {TryCast(sender, Button)})
        End Sub

        Private Sub BeginStartupCloseTransition(action As Action)
            If _startupCloseTransitionTask IsNot Nothing OrElse _mainWindow Is Nothing Then
                Return
            End If

            _startupCloseTransitionTask = RunStartupCloseTransitionAsync(action)
        End Sub

        Private Async Function RunStartupCloseTransitionAsync(action As Action) As Task
            Try
                _isStartupInteractionReady = False

                If StartupInteractionRoot IsNot Nothing Then
                    StartupInteractionRoot.IsHitTestVisible = False
                End If

                If NavigationCurtainSurface IsNot Nothing Then
                    NavigationCurtainSurface.IsHitTestVisible = False
                End If

                AlignMainWindowToStartupWindow()
                _mainWindow.ShowInTaskbar = True
                _hasHandedOffToMainWindow = True
                _mainWindow.Activate()

                If action IsNot Nothing Then
                    action()
                End If

                _navigationMenuAnimationVersion += 1
                Dim animationVersion = _navigationMenuAnimationVersion

                SetNavigationMenuItemsVisibleState()
                AnimateNavigationCurtain(False)
                Dim brandTask = AnimateNavigationBrandQuadrantCloseAsync(animationVersion)

                Dim exitDuration = ResolveDurationResource("MotionNavigationCurtainExitDuration", 800)
                Dim exitDelay = If(exitDuration.HasTimeSpan,
                                   exitDuration.TimeSpan,
                                   TimeSpan.FromMilliseconds(800))
                Await Task.WhenAll(Task.Delay(exitDelay), brandTask)

                HideStartupCarrierWindow()
                BeginDeferredStartupCarrierClose()
            Catch ex As Exception
                Trace.WriteLine($"Startup close transition failed: {ex}")
                _mainWindow.ShowInTaskbar = True
                _hasHandedOffToMainWindow = True
                _mainWindow.Activate()
                Close()
            End Try
        End Function

        Private Sub HideStartupCarrierWindow()
            Topmost = False
            Hide()
        End Sub

        Private Sub BeginDeferredStartupCarrierClose()
            If _hasStartedDeferredClose Then
                Return
            End If

            _hasStartedDeferredClose = True
            CloseStartupCarrierDeferredAsync()
        End Sub

        Private Async Sub CloseStartupCarrierDeferredAsync()
            Try
                Await Task.Delay(StartupCarrierDeferredCloseDelayMilliseconds)
                Close()
            Catch ex As Exception
                Trace.WriteLine($"Deferred startup carrier close failed: {ex}")
                Close()
            End Try
        End Sub

        Private Sub AlignMainWindowToStartupWindow()
            If _mainWindow Is Nothing Then
                Return
            End If

            Dim targetWidth = Math.Max(ActualWidth, Width)
            Dim targetHeight = Math.Max(ActualHeight, Height)

            If targetWidth > 0.0 Then
                _mainWindow.Width = targetWidth
            End If

            If targetHeight > 0.0 Then
                _mainWindow.Height = targetHeight
            End If

            _mainWindow.Left = Left
            _mainWindow.Top = Top
        End Sub

        Private Sub NavigationCurtainSurface_SizeChanged(sender As Object, e As SizeChangedEventArgs)
            If NavigationCurtainSurface Is Nothing Then
                Return
            End If

            UpdateNavigationCurtainGuideLayout()

            If _isPreparingNavigationCurtainOpen Then
                Return
            End If

            If NavigationCurtainSurface.Visibility <> Visibility.Visible Then
                UpdateNavigationCurtainClip(0.0)
                Return
            End If

            UpdateNavigationCurtainClip(NavigationCurtainSurface.ActualHeight)
        End Sub

        Private Sub NavigationBrandQuadrant_SizeChanged(sender As Object, e As SizeChangedEventArgs)
            UpdateNavigationBrandQuadrantClipForSizeChange(e.PreviousSize)
        End Sub

        Private Sub NavigationMenuScrollViewer_SizeChanged(sender As Object, e As SizeChangedEventArgs)
            UpdateNavigationCurtainGuideLayout()
        End Sub
        Private Sub AnimateNavigationCurtain(isOpening As Boolean)
            If NavigationCurtainSurface Is Nothing Then
                Return
            End If

            If isOpening Then
                _isPreparingNavigationCurtainOpen = True
                NavigationCurtainSurface.Visibility = Visibility.Visible
                NavigationCurtainSurface.IsHitTestVisible = True
                NavigationCurtainSurface.UpdateLayout()
                UpdateNavigationCurtainGuideLayout()
            End If

            Dim clipGeometry = TryCast(NavigationCurtainSurface.Clip, RectangleGeometry)
            If clipGeometry Is Nothing Then
                clipGeometry = New RectangleGeometry()
                NavigationCurtainSurface.Clip = clipGeometry
            End If

            Dim width = Math.Max(NavigationCurtainSurface.ActualWidth, Math.Max(ActualWidth, 1.0))
            Dim height = Math.Max(NavigationCurtainSurface.ActualHeight, Math.Max(ActualHeight, 1.0))
            Dim duration = ResolveDurationResource(If(isOpening,
                                                      "MotionNavigationCurtainEnterDuration",
                                                      "MotionNavigationCurtainExitDuration"),
                                                   800)
            Dim easing = ResolveEasingFunctionResource(If(isOpening,
                                                         "MotionNavigationCurtainEnterEase",
                                                         "MotionNavigationCurtainExitEase"))
            Dim currentRect = clipGeometry.Rect
            If currentRect.Width <= 0.0 Then
                currentRect.Width = width
            End If

            clipGeometry.BeginAnimation(RectangleGeometry.RectProperty, Nothing)
            NavigationCurtainSurface.Visibility = Visibility.Visible
            NavigationCurtainSurface.IsHitTestVisible = isOpening

            Dim currentHeight = Math.Max(0.0, Math.Min(currentRect.Height, height))
            If isOpening AndAlso currentHeight >= height Then
                currentHeight = 0.0
            End If

            Dim fromRect = If(isOpening,
                              New Rect(0.0, 0.0, width, currentHeight),
                              New Rect(0.0, 0.0, width, If(currentHeight > 0.0, currentHeight, height)))
            Dim toRect = If(isOpening,
                            New Rect(0.0, 0.0, width, height),
                            New Rect(0.0, 0.0, width, 0.0))
            clipGeometry.Rect = fromRect

            Dim rectAnimation As New RectAnimation With {
                .From = fromRect,
                .To = toRect,
                .Duration = duration
            }

            If easing IsNot Nothing Then
                rectAnimation.EasingFunction = easing
            End If

            clipGeometry.BeginAnimation(RectangleGeometry.RectProperty, rectAnimation)
            _isPreparingNavigationCurtainOpen = False
        End Sub

        Private Async Function AnimateNavigationBrandQuadrantOpenAsync(animationVersion As Integer) As Task
            If NavigationBrandPolygonPrimary Is Nothing OrElse NavigationBrandPolygonSecondary Is Nothing Then
                Return
            End If

            If NavigationBrandQuadrant IsNot Nothing Then
                NavigationBrandQuadrant.UpdateLayout()
            End If

            SetNavigationBrandQuadrantState(NavigationBrandClosedPrimaryPoints, NavigationBrandClosedSecondaryPoints)
            SetNavigationBrandQuadrantRevealState(False)
            Await Task.Delay(NavigationBrandOpenDelayMilliseconds)

            If animationVersion <> _navigationMenuAnimationVersion OrElse Not IsLoaded Then
                Return
            End If

            AnimateNavigationBrandQuadrantRevealOpen()

            Dim currentPrimary = NavigationBrandClosedPrimaryPoints
            Dim currentSecondary = NavigationBrandClosedSecondaryPoints

            For frameIndex = 0 To NavigationBrandPrimaryOpenFrames.Length - 1
                If Not Await AnimateNavigationBrandQuadrantStepAsync(currentPrimary,
                                                                    NavigationBrandPrimaryOpenFrames(frameIndex),
                                                                    currentSecondary,
                                                                    NavigationBrandSecondaryOpenFrames(frameIndex),
                                                                    animationVersion) Then
                    Return
                End If

                currentPrimary = NavigationBrandPrimaryOpenFrames(frameIndex)
                currentSecondary = NavigationBrandSecondaryOpenFrames(frameIndex)
            Next
        End Function

        Private Async Function AnimateNavigationBrandQuadrantCloseAsync(animationVersion As Integer) As Task
            If NavigationBrandPolygonPrimary Is Nothing OrElse NavigationBrandPolygonSecondary Is Nothing Then
                Return
            End If

            Await Task.Delay(NavigationBrandCloseResetDelayMilliseconds)

            If animationVersion <> _navigationMenuAnimationVersion OrElse Not IsLoaded Then
                Return
            End If

            SetNavigationBrandQuadrantState(NavigationBrandClosedPrimaryPoints, NavigationBrandClosedSecondaryPoints)

            Dim exitDuration = ResolveDurationResource("MotionNavigationCurtainExitDuration", 800)
            Dim revealResetDelay = If(exitDuration.HasTimeSpan,
                                      exitDuration.TimeSpan,
                                      TimeSpan.FromMilliseconds(800))
            Dim remainingRevealDelay = revealResetDelay - TimeSpan.FromMilliseconds(NavigationBrandCloseResetDelayMilliseconds)

            If remainingRevealDelay > TimeSpan.Zero Then
                Await Task.Delay(remainingRevealDelay)
            End If

            If animationVersion <> _navigationMenuAnimationVersion OrElse Not IsLoaded Then
                Return
            End If

            SetNavigationBrandQuadrantRevealState(False)
        End Function

        Private Async Function AnimateNavigationBrandQuadrantStepAsync(primaryFrom As Point(),
                                                                       primaryTo As Point(),
                                                                       secondaryFrom As Point(),
                                                                       secondaryTo As Point(),
                                                                       animationVersion As Integer) As Task(Of Boolean)
            Dim stepStopwatch = Stopwatch.StartNew()

            Do
                If animationVersion <> _navigationMenuAnimationVersion OrElse
                   Not IsLoaded OrElse
                   NavigationBrandPolygonPrimary Is Nothing OrElse
                   NavigationBrandPolygonSecondary Is Nothing Then
                    Return False
                End If

                Dim progress = Math.Min(1.0, stepStopwatch.Elapsed.TotalMilliseconds / NavigationBrandStepDurationMilliseconds)
                Dim easedProgress = EvaluateNavigationBrandExpoInOut(progress)

                SetNavigationBrandQuadrantState(InterpolateNavigationBrandPoints(primaryFrom, primaryTo, easedProgress),
                                                InterpolateNavigationBrandPoints(secondaryFrom, secondaryTo, easedProgress))

                If progress >= 1.0 Then
                    Return True
                End If

                Await Task.Delay(NavigationBrandAnimationFrameMilliseconds)
            Loop
        End Function

        Private Async Function AnimateNavigationMenuItemsOpenAsync() As Task
            If NavigationMenuItemsControl Is Nothing Then
                Return
            End If

            NavigationMenuItemsControl.UpdateLayout()
            UpdateNavigationMenuContentAlignment()

            Dim menuButtons As New List(Of Button)(FindVisualChildren(Of Button)(NavigationMenuItemsControl))
            If menuButtons.Count = 0 Then
                Return
            End If

            menuButtons.Sort(Function(left, right) left.TranslatePoint(New Point(0, 0), NavigationMenuItemsControl).Y.CompareTo(right.TranslatePoint(New Point(0, 0), NavigationMenuItemsControl).Y))

            Dim duration = ResolveDurationResource("MotionNavigationItemEnterDuration", 800)
            Dim easing = ResolveEasingFunctionResource("MotionNavigationContentEnterEase")
            Dim baseDelayMilliseconds = ResolveDoubleResource("MotionNavigationItemEnterBaseDelayMs", 400.0)
            Dim staggerRangeMilliseconds = ResolveDoubleResource("MotionNavigationItemEnterStaggerRangeMs", 200.0)
            Dim staggerPower = ResolveDoubleResource("MotionNavigationItemEnterStaggerPower", 1.35)
            Dim maximumCompletion = TimeSpan.Zero

            SetNavigationMenuItemsState(1.0, 0.0, menuButtons)
            SetNavigationMenuTextState(False, menuButtons)
            SetNavigationMenuHoverSurfaceState(0.0, menuButtons)

            For itemIndex = 0 To menuButtons.Count - 1
                Dim menuButton = menuButtons(itemIndex)
                Dim textElements = ResolveNavigationMenuAnimatedTextElements(menuButton)
                If textElements.Count = 0 Then
                    Continue For
                End If

                Dim delay = ResolveNavigationMenuItemEnterDelay(itemIndex,
                                                                menuButtons.Count,
                                                                baseDelayMilliseconds,
                                                                staggerRangeMilliseconds,
                                                                staggerPower)
                Dim durationTimeSpan = If(duration.HasTimeSpan, duration.TimeSpan, TimeSpan.FromMilliseconds(800))
                Dim completion = delay + durationTimeSpan
                If completion > maximumCompletion Then
                    maximumCompletion = completion
                End If

                For Each textElement In textElements
                    Dim translateTransform = EnsureNavigationMenuTextTransform(textElement)
                    Dim hiddenOffsetY = ResolveNavigationMenuTextHiddenOffset(textElement)
                    Dim visibleOpacity = ResolveNavigationMenuTextVisibleOpacity(textElement)

                    textElement.BeginAnimation(UIElement.OpacityProperty, Nothing)
                    textElement.Opacity = 0.0
                    translateTransform.BeginAnimation(TranslateTransform.YProperty, Nothing)
                    translateTransform.Y = hiddenOffsetY

                    Dim opacityAnimation As New DoubleAnimation With {
                        .From = 0.0,
                        .To = visibleOpacity,
                        .BeginTime = delay,
                        .Duration = duration
                    }

                    Dim translateAnimation As New DoubleAnimation With {
                        .From = hiddenOffsetY,
                        .To = 0.0,
                        .BeginTime = delay,
                        .Duration = duration
                    }

                    If easing IsNot Nothing Then
                        opacityAnimation.EasingFunction = easing
                        translateAnimation.EasingFunction = easing
                    End If

                    textElement.BeginAnimation(UIElement.OpacityProperty, opacityAnimation)
                    translateTransform.BeginAnimation(TranslateTransform.YProperty, translateAnimation)
                Next
            Next

            If maximumCompletion > TimeSpan.Zero Then
                Await Task.Delay(maximumCompletion)
            End If
        End Function

        Private Sub UpdateNavigationCurtainGuideLayout()
            If NavigationCurtainLayoutRoot Is Nothing OrElse
               NavigationCurtainTopGuideRow Is Nothing OrElse
               NavigationCurtainLeftGuideColumn Is Nothing Then
                Return
            End If

            Dim layoutWidth = Math.Max(NavigationCurtainLayoutRoot.ActualWidth, 0.0)
            Dim layoutHeight = Math.Max(NavigationCurtainLayoutRoot.ActualHeight, 0.0)
            If layoutWidth <= 0.0 OrElse layoutHeight <= 0.0 Then
                Return
            End If

            Dim frameWidth = ResolveDoubleResource("Layout.Navigation.FrameWidth", 860.0)
            Dim closeRailWidth = ResolveGridLengthResource("Layout.Navigation.CloseRailWidth", 96.0)
            Dim closeRailGap = ResolveGridLengthResource("Layout.Navigation.CloseRailGap", 44.0)
            Dim contentOffsetX = ResolveDoubleResource("Layout.Navigation.ContentOffsetX", 0.0)
            Dim verticalGuideOffsetX = ResolveDoubleResource("Layout.Navigation.VerticalGuideOffsetX", 0.0)
            Dim overlayPadding = ResolveThicknessResource("Layout.Navigation.OverlayPadding")

            Dim leftGuideWidth = ((layoutWidth - frameWidth) / 2.0) + closeRailWidth + closeRailGap + contentOffsetX + verticalGuideOffsetX
            leftGuideWidth = Math.Max(0.0, Math.Min(leftGuideWidth, layoutWidth))

            Dim menuHeight = ResolveNavigationMenuMeasuredHeight(layoutWidth, layoutHeight)
            Dim minimumTopGuideHeight = ResolveNavigationCloseRailMinimumHeight()
            Dim topGuideHeight = 0.0

            If menuHeight > 0.0 Then
                topGuideHeight = (layoutHeight - Math.Min(menuHeight, layoutHeight)) / 2.0
            ElseIf NavigationCurtainTopGuideRow.Height.IsAbsolute Then
                topGuideHeight = Math.Max(NavigationCurtainTopGuideRow.Height.Value, 0.0)
            End If

            topGuideHeight = Math.Max(topGuideHeight, minimumTopGuideHeight)
            topGuideHeight = Math.Max(0.0, Math.Min(topGuideHeight, layoutHeight))

            NavigationCurtainLeftGuideColumn.Width = New GridLength(leftGuideWidth, GridUnitType.Pixel)
            NavigationCurtainTopGuideRow.Height = New GridLength(topGuideHeight, GridUnitType.Pixel)

            If NavigationCurtainVerticalGuide IsNot Nothing Then
                NavigationCurtainVerticalGuide.Margin = New Thickness(overlayPadding.Left + leftGuideWidth, 0.0, 0.0, 0.0)
            End If

            UpdateNavigationMenuContentAlignment()
        End Sub
        Private Sub UpdateNavigationMenuContentAlignment()
            If NavigationCurtainLayoutRoot Is Nothing OrElse
               NavigationMenuScrollViewer Is Nothing OrElse
               NavigationMenuItemsControl Is Nothing OrElse
               NavigationGithubLinkButton Is Nothing Then
                Return
            End If

            NavigationMenuItemsControl.UpdateLayout()

            Dim baseMargin = ResolveThicknessResource("Layout.Navigation.ContentStartMargin.Localized")
            Dim menuOriginX = NavigationMenuScrollViewer.TranslatePoint(New Point(0, 0), NavigationCurtainLayoutRoot).X
            Dim githubButtonX = NavigationGithubLinkButton.TranslatePoint(New Point(0, 0), NavigationCurtainLayoutRoot).X
            Dim githubIconWidth = 26.0
            Dim githubIconInset = Math.Max(0.0, (NavigationGithubLinkButton.ActualWidth - githubIconWidth) / 2.0)
            Dim targetLeft = Math.Max(0.0, githubButtonX + githubIconInset - menuOriginX)

            For Each menuButton In FindVisualChildren(Of Button)(NavigationMenuItemsControl)
                Dim content = ResolveNavigationMenuAnimatedContent(menuButton)
                If content Is Nothing Then
                    Continue For
                End If

                content.Margin = New Thickness(targetLeft,
                                               baseMargin.Top,
                                               baseMargin.Right,
                                               baseMargin.Bottom)
            Next
        End Sub

        Private Function ResolveNavigationMenuMeasuredHeight(layoutWidth As Double, layoutHeight As Double) As Double
            Dim measureWidth = layoutWidth
            If NavigationMenuScrollViewer IsNot Nothing AndAlso NavigationMenuScrollViewer.ActualWidth > 0.0 Then
                measureWidth = NavigationMenuScrollViewer.ActualWidth
            End If

            Dim contentHeight = 0.0
            If NavigationMenuItemsControl IsNot Nothing Then
                NavigationMenuItemsControl.Measure(New Size(Math.Max(measureWidth, 1.0), Double.PositiveInfinity))
                contentHeight = Math.Max(NavigationMenuItemsControl.DesiredSize.Height,
                                         NavigationMenuItemsControl.ActualHeight)
            End If

            Dim visibleHeightCap = layoutHeight
            If NavigationMenuScrollViewer IsNot Nothing Then
                Dim maxHeight = NavigationMenuScrollViewer.MaxHeight
                If Not Double.IsNaN(maxHeight) AndAlso
                   Not Double.IsInfinity(maxHeight) AndAlso
                   maxHeight > 0.0 Then
                    visibleHeightCap = Math.Min(visibleHeightCap, maxHeight)
                End If
            End If

            If contentHeight > 0.0 Then
                Return Math.Max(0.0, Math.Min(contentHeight, visibleHeightCap))
            End If

            Dim viewportHeight = 0.0
            If NavigationMenuScrollViewer IsNot Nothing Then
                viewportHeight = Math.Max(NavigationMenuScrollViewer.ActualHeight,
                                          NavigationMenuScrollViewer.DesiredSize.Height)
            End If

            Return Math.Max(0.0, Math.Min(viewportHeight, visibleHeightCap))
        End Function

        Private Function ResolveNavigationCloseRailMinimumHeight() As Double
            Dim closeButtonHeight = 76.0

            If NavigationCurtainCloseButton Is Nothing Then
                Return closeButtonHeight
            End If

            If NavigationCurtainCloseButton.ActualHeight > 0.0 Then
                closeButtonHeight = NavigationCurtainCloseButton.ActualHeight
            ElseIf Not Double.IsNaN(NavigationCurtainCloseButton.Height) AndAlso
                   NavigationCurtainCloseButton.Height > 0.0 Then
                closeButtonHeight = NavigationCurtainCloseButton.Height
            End If

            Return closeButtonHeight
        End Function

        Private Sub UpdateNavigationLocalClockText()
            If NavigationLocalClockText Is Nothing Then
                Return
            End If

            Dim localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _navigationLocalTimeZone)
            NavigationLocalClockText.Text = localNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
        End Sub

        Private Sub UpdateNavigationCurtainClip(visibleHeight As Double)
            If NavigationCurtainSurface Is Nothing Then
                Return
            End If

            Dim clipGeometry = TryCast(NavigationCurtainSurface.Clip, RectangleGeometry)
            If clipGeometry Is Nothing Then
                Return
            End If

            clipGeometry.BeginAnimation(RectangleGeometry.RectProperty, Nothing)
            clipGeometry.Rect = New Rect(0.0,
                                         0.0,
                                         Math.Max(NavigationCurtainSurface.ActualWidth, 0.0),
                                         Math.Max(visibleHeight, 0.0))
        End Sub

        Private Sub ResetNavigationMenuItemsForNextOpen()
            SetNavigationMenuItemsState(1.0, 0.0)
            SetNavigationMenuTextState(False)
            SetNavigationMenuHoverSurfaceState(0.0)
        End Sub

        Private Sub SetNavigationMenuItemsVisibleState(Optional menuButtons As IEnumerable(Of Button) = Nothing)
            SetNavigationMenuItemsState(1.0, 0.0, menuButtons)
            SetNavigationMenuTextState(True, menuButtons)
            SetNavigationMenuHoverSurfaceState(0.0, menuButtons)
        End Sub

        Private Sub SetNavigationMenuItemsState(scaleValue As Double,
                                                translateY As Double,
                                                Optional menuButtons As IEnumerable(Of Button) = Nothing)
            If NavigationMenuItemsControl Is Nothing Then
                Return
            End If

            Dim buttons = If(menuButtons,
                             New List(Of Button)(FindVisualChildren(Of Button)(NavigationMenuItemsControl)))

            For Each menuButton In buttons
                Dim content = ResolveNavigationMenuAnimatedContent(menuButton)
                If content Is Nothing Then
                    Continue For
                End If

                Dim scaleTransform As ScaleTransform = Nothing
                Dim translateTransform As TranslateTransform = Nothing
                EnsureNavigationMenuItemTransforms(content, scaleTransform, translateTransform)

                content.BeginAnimation(UIElement.OpacityProperty, Nothing)
                scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, Nothing)
                scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, Nothing)
                translateTransform.BeginAnimation(TranslateTransform.YProperty, Nothing)

                content.Opacity = 1.0
                scaleTransform.ScaleX = scaleValue
                scaleTransform.ScaleY = scaleValue
                translateTransform.Y = translateY
            Next
        End Sub

        Private Sub SetNavigationMenuTextState(isVisible As Boolean,
                                               Optional menuButtons As IEnumerable(Of Button) = Nothing)
            If NavigationMenuItemsControl Is Nothing Then
                Return
            End If

            Dim buttons = If(menuButtons,
                             New List(Of Button)(FindVisualChildren(Of Button)(NavigationMenuItemsControl)))

            For Each menuButton In buttons
                Dim textElements = ResolveNavigationMenuAnimatedTextElements(menuButton)
                For Each textElement In textElements
                    Dim translateTransform = EnsureNavigationMenuTextTransform(textElement)
                    textElement.BeginAnimation(UIElement.OpacityProperty, Nothing)
                    textElement.Opacity = If(isVisible, ResolveNavigationMenuTextVisibleOpacity(textElement), 0.0)
                    translateTransform.BeginAnimation(TranslateTransform.YProperty, Nothing)
                    translateTransform.Y = If(isVisible, 0.0, ResolveNavigationMenuTextHiddenOffset(textElement))
                Next
            Next
        End Sub

        Private Sub SetNavigationMenuHoverSurfaceState(targetOpacity As Double,
                                                       Optional menuButtons As IEnumerable(Of Button) = Nothing)
            If NavigationMenuItemsControl Is Nothing Then
                Return
            End If

            Dim buttons = If(menuButtons,
                             New List(Of Button)(FindVisualChildren(Of Button)(NavigationMenuItemsControl)))

            For Each menuButton In buttons
                Dim hoverSurface = ResolveNavigationMenuHoverSurface(menuButton)
                If hoverSurface Is Nothing Then
                    Continue For
                End If

                hoverSurface.BeginAnimation(UIElement.OpacityProperty, Nothing)
                hoverSurface.Opacity = targetOpacity
            Next
        End Sub

        Private Function ResolveNavigationMenuAnimatedContent(menuButton As Button) As FrameworkElement
            If menuButton Is Nothing Then
                Return Nothing
            End If

            Return FindNamedDescendant(Of FrameworkElement)(menuButton, "MenuItemAnimatedContent")
        End Function

        Private Function ResolveNavigationMenuAnimatedTextElements(menuButton As Button) As List(Of FrameworkElement)
            Dim textElements As New List(Of FrameworkElement)
            Dim indexText = FindNamedDescendant(Of TextBlock)(menuButton, "MenuIndexTextBlock")
            Dim metaText = FindNamedDescendant(Of TextBlock)(menuButton, "MenuMetaTextBlock")
            Dim titleText = FindNamedDescendant(Of TextBlock)(menuButton, "MenuTitleTextBlock")

            If indexText IsNot Nothing Then
                textElements.Add(indexText)
            End If

            If metaText IsNot Nothing Then
                textElements.Add(metaText)
            End If

            If titleText IsNot Nothing Then
                textElements.Add(titleText)
            End If

            Return textElements
        End Function

        Private Function ResolveNavigationMenuHoverSurface(menuButton As Button) As Border
            If menuButton Is Nothing Then
                Return Nothing
            End If

            Return FindNamedDescendant(Of Border)(menuButton, "HoverSurface")
        End Function
        Private Shared Function ResolveNavigationMenuTextVisibleOpacity(textElement As FrameworkElement) As Double
            If textElement Is Nothing Then
                Return 1.0
            End If

            Select Case textElement.Name
                Case "MenuIndexTextBlock", "MenuMetaTextBlock"
                    Return 0.62
                Case Else
                    Return 1.0
            End Select
        End Function

        Private Function EnsureNavigationMenuTextTransform(textElement As FrameworkElement) As TranslateTransform
            If textElement Is Nothing Then
                Return New TranslateTransform()
            End If

            Dim translateTransform = TryCast(textElement.RenderTransform, TranslateTransform)
            If translateTransform IsNot Nothing AndAlso Not translateTransform.IsFrozen Then
                Return translateTransform
            End If

            Dim x = 0.0
            Dim y = 0.0

            If translateTransform IsNot Nothing Then
                x = translateTransform.X
                y = translateTransform.Y
            End If

            Dim runtimeTranslateTransform As New TranslateTransform(x, y)
            textElement.RenderTransform = runtimeTranslateTransform
            Return runtimeTranslateTransform
        End Function

        Private Function ResolveNavigationMenuTextHiddenOffset(textElement As FrameworkElement) As Double
            If textElement Is Nothing Then
                Return -76.0
            End If

            Dim measuredHeight = Math.Max(textElement.ActualHeight, textElement.DesiredSize.Height)
            If measuredHeight <= 0.0 Then
                Dim textBlock = TryCast(textElement, TextBlock)
                If textBlock IsNot Nothing Then
                    measuredHeight = Math.Max(textBlock.FontSize * 0.64, textBlock.FontSize)
                End If
            End If

            If measuredHeight <= 0.0 Then
                measuredHeight = 76.0
            End If

            Return -Math.Ceiling(measuredHeight * 1.02)
        End Function

        Private Function ResolveNavigationMenuItemEnterDelay(itemIndex As Integer,
                                                             itemCount As Integer,
                                                             baseDelayMilliseconds As Double,
                                                             staggerRangeMilliseconds As Double,
                                                             staggerPower As Double) As TimeSpan
            If itemCount <= 1 Then
                Return TimeSpan.FromMilliseconds(Math.Max(0.0, baseDelayMilliseconds))
            End If

            Dim clampedIndex = Math.Max(0, Math.Min(itemIndex, itemCount - 1))
            Dim revealIndex = (itemCount - 1) - clampedIndex
            Dim normalized = revealIndex / CDbl(itemCount - 1)
            Dim adjustedPower = Math.Max(0.01, staggerPower)
            Dim weighted = 1.0 - Math.Pow(1.0 - normalized, adjustedPower)

            Return TimeSpan.FromMilliseconds(Math.Max(0.0,
                                                      baseDelayMilliseconds + (staggerRangeMilliseconds * weighted)))
        End Function

        Private Sub EnsureNavigationMenuItemTransforms(element As FrameworkElement,
                                                       ByRef scaleTransform As ScaleTransform,
                                                       ByRef translateTransform As TranslateTransform)
            element.RenderTransformOrigin = New Point(0.5, 0.5)

            Dim transformGroup = TryCast(element.RenderTransform, TransformGroup)
            If transformGroup IsNot Nothing AndAlso
               transformGroup.Children.Count >= 2 AndAlso
               TypeOf transformGroup.Children(0) Is ScaleTransform AndAlso
               TypeOf transformGroup.Children(1) Is TranslateTransform Then
                scaleTransform = CType(transformGroup.Children(0), ScaleTransform)
                translateTransform = CType(transformGroup.Children(1), TranslateTransform)
                Return
            End If

            Dim scaleX = 1.0
            Dim scaleY = 1.0
            Dim translateX = 0.0
            Dim translateY = 0.0
            Dim existingScaleTransform = TryCast(element.RenderTransform, ScaleTransform)
            Dim existingTranslateTransform = TryCast(element.RenderTransform, TranslateTransform)

            If existingScaleTransform IsNot Nothing Then
                scaleX = existingScaleTransform.ScaleX
                scaleY = existingScaleTransform.ScaleY
            End If

            If existingTranslateTransform IsNot Nothing Then
                translateX = existingTranslateTransform.X
                translateY = existingTranslateTransform.Y
            End If

            scaleTransform = New ScaleTransform(scaleX, scaleY)
            translateTransform = New TranslateTransform(translateX, translateY)
            transformGroup = New TransformGroup()
            transformGroup.Children.Add(scaleTransform)
            transformGroup.Children.Add(translateTransform)
            element.RenderTransform = transformGroup
        End Sub

        Private Sub SetNavigationBrandQuadrantState(primaryPoints As Point(), secondaryPoints As Point())
            If NavigationBrandPolygonPrimary Is Nothing OrElse NavigationBrandPolygonSecondary Is Nothing Then
                Return
            End If

            NavigationBrandPolygonPrimary.Points = New PointCollection(primaryPoints)
            NavigationBrandPolygonSecondary.Points = New PointCollection(secondaryPoints)
        End Sub

        Private Sub AnimateNavigationBrandQuadrantRevealOpen()
            Dim clipGeometry = EnsureNavigationBrandQuadrantClipGeometry()
            If clipGeometry Is Nothing Then
                Return
            End If

            Dim size = GetNavigationBrandQuadrantClipSize()
            Dim fromRect = CreateNavigationBrandQuadrantClipRect(0.0, size)
            Dim toRect = CreateNavigationBrandQuadrantClipRect(1.0, size)
            Dim duration = ResolveDurationResource("MotionNavigationCurtainEnterDuration", 800)
            Dim easing = ResolveEasingFunctionResource("MotionNavigationContentEnterEase")
            Dim animation As New RectAnimation With {
                .From = fromRect,
                .To = toRect,
                .Duration = duration
            }

            If easing IsNot Nothing Then
                animation.EasingFunction = easing
            End If

            clipGeometry.BeginAnimation(RectangleGeometry.RectProperty, Nothing)
            clipGeometry.Rect = fromRect
            clipGeometry.BeginAnimation(RectangleGeometry.RectProperty, animation)
        End Sub

        Private Sub SetNavigationBrandQuadrantRevealState(isVisible As Boolean)
            Dim clipGeometry = EnsureNavigationBrandQuadrantClipGeometry()
            If clipGeometry Is Nothing Then
                Return
            End If

            clipGeometry.BeginAnimation(RectangleGeometry.RectProperty, Nothing)
            clipGeometry.Rect = CreateNavigationBrandQuadrantClipRect(If(isVisible, 1.0, 0.0), GetNavigationBrandQuadrantClipSize())
        End Sub

        Private Function EnsureNavigationBrandQuadrantClipGeometry() As RectangleGeometry
            If NavigationBrandQuadrant Is Nothing Then
                Return Nothing
            End If

            Dim clipGeometry = TryCast(NavigationBrandQuadrant.Clip, RectangleGeometry)
            If clipGeometry Is Nothing Then
                clipGeometry = New RectangleGeometry()
                NavigationBrandQuadrant.Clip = clipGeometry
            End If

            Return clipGeometry
        End Function

        Private Sub UpdateNavigationBrandQuadrantClipForSizeChange(previousSize As Size)
            Dim clipGeometry = EnsureNavigationBrandQuadrantClipGeometry()
            If clipGeometry Is Nothing Then
                Return
            End If

            Dim currentSize = GetNavigationBrandQuadrantClipSize()
            If currentSize.Width <= 0.0 OrElse currentSize.Height <= 0.0 Then
                Return
            End If

            Dim progress = ResolveNavigationBrandQuadrantRevealProgress(clipGeometry.Rect, previousSize, currentSize)
            Dim currentRect = CreateNavigationBrandQuadrantClipRect(progress, currentSize)

            clipGeometry.BeginAnimation(RectangleGeometry.RectProperty, Nothing)
            clipGeometry.Rect = currentRect

            If progress >= 1.0 Then
                Return
            End If

            Dim totalDuration = ResolveDurationResource("MotionNavigationCurtainEnterDuration", 800)
            Dim totalMilliseconds = If(totalDuration.HasTimeSpan, totalDuration.TimeSpan.TotalMilliseconds, 800.0)
            Dim remainingMilliseconds = Math.Max(1.0, totalMilliseconds * (1.0 - progress))
            Dim easing = ResolveEasingFunctionResource("MotionNavigationContentEnterEase")
            Dim animation As New RectAnimation With {
                .From = currentRect,
                .To = CreateNavigationBrandQuadrantClipRect(1.0, currentSize),
                .Duration = TimeSpan.FromMilliseconds(remainingMilliseconds)
            }

            If easing IsNot Nothing Then
                animation.EasingFunction = easing
            End If

            clipGeometry.BeginAnimation(RectangleGeometry.RectProperty, animation)
        End Sub

        Private Shared Function ResolveNavigationBrandQuadrantRevealProgress(currentRect As Rect,
                                                                             previousSize As Size,
                                                                             currentSize As Size) As Double
            Dim referenceHeight = Math.Max(If(previousSize.Height > 0.0, previousSize.Height, currentSize.Height), 0.0)
            If referenceHeight <= 0.0 Then
                Return 0.0
            End If

            Return Clamp01(currentRect.Height / referenceHeight)
        End Function

        Private Shared Function CreateNavigationBrandQuadrantClipRect(progress As Double, size As Size) As Rect
            Dim clampedProgress = Clamp01(progress)
            Dim width = Math.Max(size.Width, 0.0)
            Dim height = Math.Max(size.Height, 0.0)
            Dim revealedHeight = height * clampedProgress
            Return New Rect(0.0, height - revealedHeight, width, revealedHeight)
        End Function

        Private Function GetNavigationBrandQuadrantClipSize() As Size
            If NavigationBrandQuadrant Is Nothing Then
                Return New Size(0.0, 0.0)
            End If

            Dim width = Math.Max(NavigationBrandQuadrant.ActualWidth, NavigationBrandQuadrant.RenderSize.Width)
            Dim height = Math.Max(NavigationBrandQuadrant.ActualHeight, NavigationBrandQuadrant.RenderSize.Height)
            Return New Size(Math.Max(width, 0.0), Math.Max(height, 0.0))
        End Function

        Private Shared Function Clamp01(value As Double) As Double
            Return Math.Max(0.0, Math.Min(1.0, value))
        End Function

        Private Shared Function InterpolateNavigationBrandPoints(fromPoints As Point(),
                                                                 toPoints As Point(),
                                                                 progress As Double) As Point()
            Dim pointCount = Math.Min(fromPoints.Length, toPoints.Length)
            If pointCount <= 0 Then
                Return Array.Empty(Of Point)()
            End If

            Dim interpolatedPoints(pointCount - 1) As Point
            For pointIndex = 0 To pointCount - 1
                Dim fromPoint = fromPoints(pointIndex)
                Dim toPoint = toPoints(pointIndex)
                interpolatedPoints(pointIndex) = New Point(fromPoint.X + ((toPoint.X - fromPoint.X) * progress),
                                                           fromPoint.Y + ((toPoint.Y - fromPoint.Y) * progress))
            Next

            Return interpolatedPoints
        End Function

        Private Shared Function EvaluateNavigationBrandExpoInOut(progress As Double) As Double
            If progress <= 0.0 Then
                Return 0.0
            End If

            If progress >= 1.0 Then
                Return 1.0
            End If

            If progress < 0.5 Then
                Return Math.Pow(2.0, (20.0 * progress) - 10.0) / 2.0
            End If

            Return (2.0 - Math.Pow(2.0, (-20.0 * progress) + 10.0)) / 2.0
        End Function

        Private Shared Function ParsePolygonPoints(pointData As String) As Point()
            If String.IsNullOrWhiteSpace(pointData) Then
                Return Array.Empty(Of Point)()
            End If

            Dim pointTokens = pointData.Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
            Dim parsedPoints(pointTokens.Length - 1) As Point

            For pointIndex = 0 To pointTokens.Length - 1
                Dim coordinateTokens = pointTokens(pointIndex).Split(","c)
                parsedPoints(pointIndex) = New Point(Double.Parse(coordinateTokens(0), CultureInfo.InvariantCulture),
                                                     Double.Parse(coordinateTokens(1), CultureInfo.InvariantCulture))
            Next

            Return parsedPoints
        End Function

        Private Iterator Function FindVisualChildren(Of T As DependencyObject)(root As DependencyObject) As IEnumerable(Of T)
            If root Is Nothing Then
                Return
            End If

            Dim childCount = VisualTreeHelper.GetChildrenCount(root)
            For childIndex = 0 To childCount - 1
                Dim child = VisualTreeHelper.GetChild(root, childIndex)
                Dim typedChild = TryCast(child, T)
                If typedChild IsNot Nothing Then
                    Yield typedChild
                End If

                For Each nestedChild In FindVisualChildren(Of T)(child)
                    Yield nestedChild
                Next
            Next
        End Function

        Private Function FindNamedDescendant(Of T As FrameworkElement)(root As DependencyObject,
                                                                       elementName As String) As T
            If root Is Nothing OrElse String.IsNullOrWhiteSpace(elementName) Then
                Return Nothing
            End If

            Dim childCount = VisualTreeHelper.GetChildrenCount(root)
            For childIndex = 0 To childCount - 1
                Dim child = VisualTreeHelper.GetChild(root, childIndex)
                Dim typedChild = TryCast(child, T)
                If typedChild IsNot Nothing AndAlso String.Equals(typedChild.Name, elementName, StringComparison.Ordinal) Then
                    Return typedChild
                End If

                Dim namedChild = FindNamedDescendant(Of T)(child, elementName)
                If namedChild IsNot Nothing Then
                    Return namedChild
                End If
            Next

            Return Nothing
        End Function

        Private Function ResolveDoubleResource(resourceKey As String, fallback As Double) As Double
            Dim resource = TryFindResource(resourceKey)
            If TypeOf resource Is Double Then
                Return CDbl(resource)
            End If

            Return fallback
        End Function

        Private Function ResolveGridLengthResource(resourceKey As String, fallback As Double) As Double
            Dim resource = TryFindResource(resourceKey)
            If TypeOf resource Is GridLength Then
                Dim gridLength = CType(resource, GridLength)
                If gridLength.IsAbsolute Then
                    Return gridLength.Value
                End If
            End If

            Return fallback
        End Function

        Private Function ResolveThicknessResource(resourceKey As String) As Thickness
            Dim resource = TryFindResource(resourceKey)
            If TypeOf resource Is Thickness Then
                Return CType(resource, Thickness)
            End If

            Return New Thickness(0.0)
        End Function

        Private Function ResolveDurationResource(resourceKey As String, fallbackMilliseconds As Double) As Duration
            Dim resource = TryFindResource(resourceKey)
            If TypeOf resource Is Duration Then
                Return CType(resource, Duration)
            End If

            Return New Duration(TimeSpan.FromMilliseconds(fallbackMilliseconds))
        End Function

        Private Function ResolveEasingFunctionResource(resourceKey As String) As IEasingFunction
            Return TryCast(TryFindResource(resourceKey), IEasingFunction)
        End Function
    End Class
End Namespace
