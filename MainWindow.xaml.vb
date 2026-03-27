Imports System.Diagnostics
Imports System.ComponentModel
Imports System.Globalization
Imports System.Runtime.InteropServices
Imports System.Runtime.Versioning
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Input
Imports System.Windows.Interop
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports System.Windows.Shell
Imports System.Windows.Shapes
Imports System.Windows.Threading
Imports WpfApp1.Models
Imports WpfApp1.Navigation
Imports WpfApp1.Services
Imports WpfApp1.Views.Shell
Imports WpfApp1.ViewModels

<SupportedOSPlatform("windows")>
Class MainWindow
    Private Const CursorClipHalfSize As Integer = 12
    Private Const CursorVisibilityLoopLimit As Integer = 24
    Private Const NavigationBrandOpenDelayMilliseconds As Integer = 250
    Private Const NavigationBrandStepDurationMilliseconds As Integer = 450
    Private Const NavigationBrandCloseResetDelayMilliseconds As Integer = 500
    Private Const NavigationBrandAnimationFrameMilliseconds As Integer = 16
    Private Shared ReadOnly SquareWindowCornerRadius As New CornerRadius(0)
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

    Private Enum CaptureLockFailureReason
        None
        ActivateWindowFailed
        MissingCaptureSurface
        SetCursorPositionFailed
        SetClipCursorFailed
        UnexpectedError
    End Enum

    Private ReadOnly _shellViewModel As ShellViewModel
    Private ReadOnly _themeManager As ThemeManager
    Private ReadOnly _navigationLocalTimeZone As TimeZoneInfo = TimeZoneInfo.Local
    Private ReadOnly _navigationLocalClockTimer As DispatcherTimer
    Private _activeCapturePage As ICaptureSessionPageViewModel
    Private _savedClipRect As NativeMethods.RECT
    Private _hasSavedClipRect As Boolean
    Private _cursorHidden As Boolean
    Private _isCaptureLocked As Boolean
    Private _lastRenderingDiagnostics As String
    Private _navigationMenuAnimationVersion As Integer
    Private _windowSource As HwndSource
    Private _isPreparingNavigationCurtainOpen As Boolean
    Private _navigationMenuClosingHighlightPageKey As Nullable(Of AppPageKey)
    Private _hasPlayedWindowStartupAnimation As Boolean
    Private ReadOnly _usesStartupMenuHandoff As Boolean
    Private _hasPreparedStartupMenuHandoffState As Boolean

    Public Sub New()
        Me.New(Nothing, False)
    End Sub

    Public Sub New(shellViewModel As ShellViewModel, usesStartupMenuHandoff As Boolean)
        InitializeComponent()

        _themeManager = ThemeManager.Instance
        _shellViewModel = If(shellViewModel, New ShellViewModel())
        _usesStartupMenuHandoff = usesStartupMenuHandoff
        DataContext = _shellViewModel

        AddHandler _shellViewModel.CurrentPageChanged, AddressOf OnCurrentPageChanged
        AddHandler _shellViewModel.PropertyChanged, AddressOf OnShellViewModelPropertyChanged
        AddHandler _themeManager.ThemeChanged, AddressOf OnThemeChanged

        _navigationLocalClockTimer = New DispatcherTimer(DispatcherPriority.Background, Dispatcher) With {
            .Interval = TimeSpan.FromSeconds(1)
        }
        AddHandler _navigationLocalClockTimer.Tick, AddressOf OnNavigationLocalClockTick
        UpdateNavigationLocalClockText()
        _navigationLocalClockTimer.Start()

        AttachCapturePageHandlers(_shellViewModel.ActiveCapturePage)
    End Sub

    Private Sub OnCurrentPageChanged(sender As Object, e As EventArgs)
        AttachCapturePageHandlers(_shellViewModel.ActiveCapturePage)
        Dispatcher.BeginInvoke(New Action(AddressOf AnimateCurrentPageHost))
    End Sub

    Private Sub AttachCapturePageHandlers(nextPage As ICaptureSessionPageViewModel)
        If ReferenceEquals(_activeCapturePage, nextPage) Then
            Return
        End If

        If _activeCapturePage IsNot Nothing Then
            RemoveHandler _activeCapturePage.EnterLockRequested, AddressOf OnEnterLockRequested
            RemoveHandler _activeCapturePage.ExitLockRequested, AddressOf OnExitLockRequested
        End If

        _activeCapturePage = nextPage

        If _activeCapturePage IsNot Nothing Then
            AddHandler _activeCapturePage.EnterLockRequested, AddressOf OnEnterLockRequested
            AddHandler _activeCapturePage.ExitLockRequested, AddressOf OnExitLockRequested
        End If
    End Sub

    Private Sub OnEnterLockRequested(sender As Object, e As EventArgs)
        Dim failureReason = CaptureLockFailureReason.None
        If TryBeginCaptureLock(failureReason) Then
            If _activeCapturePage IsNot Nothing Then
                _activeCapturePage.OnLockEntered()
            End If
            Return
        End If

        EndCaptureLock()
        Trace.WriteLine($"Capture lock failed: {failureReason}.")

        Dim localization = LocalizationManager.Instance
        Dim dialog As New AppAlertDialog(localization.GetString("Dialog.LockFailed.Title"),
                                         ResolveCaptureLockFailureMessage(localization, failureReason),
                                         localization.GetString("Dialog.Common.Confirm")) With {
            .Owner = Me
        }
        dialog.ShowDialog()
    End Sub

    Private Sub OnExitLockRequested(sender As Object, e As CaptureLockRequestEventArgs)
        EndCaptureLock()
        If _activeCapturePage IsNot Nothing Then
            _activeCapturePage.OnViewUnlockCompleted(e.Reason)
        End If
    End Sub

    Private Function TryBeginCaptureLock(ByRef failureReason As CaptureLockFailureReason) As Boolean
        failureReason = CaptureLockFailureReason.None

        Try
            If Not Activate() Then
                failureReason = CaptureLockFailureReason.ActivateWindowFailed
                Return False
            End If

            Focus()

            Dim captureSurface = ResolveCaptureLockSurface()
            If captureSurface Is Nothing Then
                failureReason = CaptureLockFailureReason.MissingCaptureSurface
                Return False
            End If

            captureSurface.Focus()

            SaveCurrentClipRect()

            Dim lockRect = BuildCursorLockRect(captureSurface)
            Dim centerX = (lockRect.Left + lockRect.Right) \ 2
            Dim centerY = (lockRect.Top + lockRect.Bottom) \ 2

            If Not NativeMethods.SetCursorPos(centerX, centerY) Then
                Trace.WriteLine($"SetCursorPos failed with Win32 error {Marshal.GetLastWin32Error()}.")
                failureReason = CaptureLockFailureReason.SetCursorPositionFailed
                Return False
            End If

            If Not NativeMethods.SetClipCursor(lockRect) Then
                Trace.WriteLine($"SetClipCursor failed with Win32 error {Marshal.GetLastWin32Error()}.")
                failureReason = CaptureLockFailureReason.SetClipCursorFailed
                Return False
            End If

            HideCursor()
            _isCaptureLocked = True
            Return True
        Catch ex As Exception
            Trace.WriteLine($"Unexpected capture lock failure: {ex}")
            failureReason = CaptureLockFailureReason.UnexpectedError
            Return False
        End Try
    End Function

    Private Sub SaveCurrentClipRect()
        _hasSavedClipRect = NativeMethods.GetClipCursor(_savedClipRect)
    End Sub

    Private Function BuildCursorLockRect(captureSurface As FrameworkElement) As NativeMethods.RECT
        Dim targetCenter As Point

        If captureSurface IsNot Nothing AndAlso captureSurface.ActualWidth > 0 AndAlso captureSurface.ActualHeight > 0 Then
            targetCenter = captureSurface.PointToScreen(New Point(captureSurface.ActualWidth / 2.0, captureSurface.ActualHeight / 2.0))
        Else
            targetCenter = PointToScreen(New Point(ActualWidth / 2.0, ActualHeight / 2.0))
        End If

        Dim centerX = CInt(Math.Round(targetCenter.X))
        Dim centerY = CInt(Math.Round(targetCenter.Y))

        Return New NativeMethods.RECT With {
            .Left = centerX - CursorClipHalfSize,
            .Top = centerY - CursorClipHalfSize,
            .Right = centerX + CursorClipHalfSize,
            .Bottom = centerY + CursorClipHalfSize
        }
    End Function

    Private Function ResolveCaptureLockFailureMessage(localization As LocalizationManager,
                                                      failureReason As CaptureLockFailureReason) As String
        Dim baseMessage = localization.GetString("Dialog.LockFailed.Message")
        Dim detailKey As String = Nothing

        Select Case failureReason
            Case CaptureLockFailureReason.ActivateWindowFailed
                detailKey = "Dialog.LockFailed.Detail.ActivateWindow"
            Case CaptureLockFailureReason.MissingCaptureSurface
                detailKey = "Dialog.LockFailed.Detail.MissingCaptureSurface"
            Case CaptureLockFailureReason.SetCursorPositionFailed
                detailKey = "Dialog.LockFailed.Detail.SetCursorPos"
            Case CaptureLockFailureReason.SetClipCursorFailed
                detailKey = "Dialog.LockFailed.Detail.SetClipCursor"
            Case CaptureLockFailureReason.UnexpectedError
                detailKey = "Dialog.LockFailed.Detail.Unknown"
        End Select

        If String.IsNullOrWhiteSpace(detailKey) Then
            Return baseMessage
        End If

        Dim detailMessage = localization.GetString(detailKey)
        If String.IsNullOrWhiteSpace(detailMessage) OrElse String.Equals(detailMessage, detailKey, StringComparison.Ordinal) Then
            Return baseMessage
        End If

        Return baseMessage & Environment.NewLine & Environment.NewLine & detailMessage
    End Function

    Private Sub EndCaptureLock()
        If _hasSavedClipRect Then
            NativeMethods.SetClipCursor(_savedClipRect)
        Else
            NativeMethods.ClearClipCursor(IntPtr.Zero)
        End If

        _hasSavedClipRect = False
        ShowCursorIfNeeded()
        _isCaptureLocked = False
    End Sub

    Private Sub HideCursor()
        If _cursorHidden Then
            Return
        End If

        Dim attempts = 0
        While attempts < CursorVisibilityLoopLimit AndAlso NativeMethods.ShowCursor(False) >= 0
            attempts += 1
        End While

        _cursorHidden = True
    End Sub

    Private Sub ShowCursorIfNeeded()
        If Not _cursorHidden Then
            Return
        End If

        Dim attempts = 0
        While attempts < CursorVisibilityLoopLimit AndAlso NativeMethods.ShowCursor(True) < 0
            attempts += 1
        End While

        _cursorHidden = False
    End Sub

    Protected Overrides Sub OnSourceInitialized(e As EventArgs)
        MyBase.OnSourceInitialized(e)

        _windowSource = TryCast(PresentationSource.FromVisual(Me), HwndSource)
        If _windowSource IsNot Nothing Then
            _windowSource.AddHook(AddressOf WndProc)
        End If

        UpdateWindowControlGlyph()
        ApplyWindowCornerPreference()
        ApplyWindowChromeTheme()
        UpdateRenderingDiagnostics()
    End Sub

    Protected Overrides Sub OnContentRendered(e As EventArgs)
        MyBase.OnContentRendered(e)

        If _usesStartupMenuHandoff Then
            PrepareStartupMenuHandoffState()
            Return
        End If

        If _hasPlayedWindowStartupAnimation Then
            Return
        End If

        _hasPlayedWindowStartupAnimation = True
        Dispatcher.BeginInvoke(New Action(AddressOf AnimateWindowStartupPresentation),
                               Threading.DispatcherPriority.Loaded)
    End Sub

    Protected Overrides Sub OnLocationChanged(e As EventArgs)
        MyBase.OnLocationChanged(e)
        UpdateRenderingDiagnostics()
    End Sub

    Protected Overrides Sub OnStateChanged(e As EventArgs)
        MyBase.OnStateChanged(e)
        UpdateWindowControlGlyph()
        ApplyWindowCornerPreference()
    End Sub

    Protected Overrides Sub OnPreviewKeyDown(e As KeyEventArgs)
        If _shellViewModel IsNot Nothing AndAlso _shellViewModel.IsNavigationMenuOpen AndAlso e.Key = Key.Escape Then
            e.Handled = True
            ClearNavigationMenuClosingHighlight()
            _shellViewModel.CloseNavigationMenu()
            Return
        End If

        If _isCaptureLocked AndAlso e.Key = Key.Escape Then
            e.Handled = True
            _activeCapturePage?.RequestPauseFromView()
            Return
        End If

        MyBase.OnPreviewKeyDown(e)
    End Sub

    Protected Overrides Sub OnPreviewMouseRightButtonDown(e As MouseButtonEventArgs)
        If _isCaptureLocked Then
            e.Handled = True
            _activeCapturePage?.RequestPauseFromView()
            Return
        End If

        MyBase.OnPreviewMouseRightButtonDown(e)
    End Sub

    Protected Overrides Sub OnDeactivated(e As EventArgs)
        MyBase.OnDeactivated(e)

        If _isCaptureLocked Then
            _activeCapturePage?.RequestPauseFromView()
        End If
    End Sub

    Private Sub NavigationBackdrop_MouseLeftButtonDown(sender As Object, e As MouseButtonEventArgs)
        If e IsNot Nothing Then
            e.Handled = True
        End If
    End Sub

    Private Sub NavigationMenuToggleButton_Click(sender As Object, e As RoutedEventArgs)
        If _shellViewModel Is Nothing Then
            Return
        End If

        If _shellViewModel.IsNavigationMenuOpen Then
            ClearNavigationMenuClosingHighlight()
            _shellViewModel.CloseNavigationMenu()
        Else
            ClearNavigationMenuClosingHighlight()
            _shellViewModel.OpenNavigationMenu()
        End If
    End Sub

    Private Sub NavigationCurtainCloseButton_Click(sender As Object, e As RoutedEventArgs)
        If _shellViewModel Is Nothing OrElse Not _shellViewModel.IsNavigationMenuOpen Then
            Return
        End If

        ClearNavigationMenuClosingHighlight()
        _shellViewModel.CloseNavigationMenu()
        e.Handled = True
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
        Dim menuButton = TryCast(sender, Button)
        RememberNavigationMenuClosingHighlight(menuButton)
        SetNavigationMenuHoverSurfaceState(1.0, New Button() {menuButton})
    End Sub

    Private Sub NavigationMenuButton_PreviewKeyDown(sender As Object, e As KeyEventArgs)
        If e Is Nothing OrElse (e.Key <> Key.Enter AndAlso e.Key <> Key.Space) Then
            Return
        End If

        Dim menuButton = TryCast(sender, Button)
        RememberNavigationMenuClosingHighlight(menuButton)
        SetNavigationMenuHoverSurfaceState(1.0, New Button() {menuButton})
    End Sub

    Private Sub NavigationMenuButton_MouseEnter(sender As Object, e As MouseEventArgs)
        AnimateNavigationMenuHoverSurface(TryCast(sender, Button), 1.0)
    End Sub

    Private Sub NavigationMenuButton_MouseLeave(sender As Object, e As MouseEventArgs)
        AnimateNavigationMenuHoverSurface(TryCast(sender, Button), 0.0)
    End Sub

    Private Async Sub OnShellViewModelPropertyChanged(sender As Object, e As PropertyChangedEventArgs)
        If e IsNot Nothing AndAlso Not String.IsNullOrEmpty(e.PropertyName) Then
            If Not String.Equals(e.PropertyName, NameOf(ShellViewModel.IsNavigationMenuOpen), StringComparison.Ordinal) Then
                Return
            End If
        End If

        _navigationMenuAnimationVersion += 1
        Dim animationVersion = _navigationMenuAnimationVersion
        Dim isOpening = _shellViewModel IsNot Nothing AndAlso _shellViewModel.IsNavigationMenuOpen

        Await Dispatcher.InvokeAsync(Sub()
                                         NavigationCurtainSurface?.UpdateLayout()

                                         If NavigationMenuItemsControl IsNot Nothing Then
                                             NavigationMenuItemsControl.UpdateLayout()
                                         End If
                                     End Sub)

        If animationVersion <> _navigationMenuAnimationVersion Then
            Return
        End If

        If isOpening Then
            ClearNavigationMenuClosingHighlight()
            AnimateNavigationCurtain(True)
            AnimateNavigationBrandQuadrant(True, animationVersion)
            ResetNavigationMenuItemsForNextOpen()
            AnimateNavigationMenuItems(True)
            SyncNavigationMenuHoverSurfaces()
            Return
        End If

        SetNavigationMenuItemsVisibleState()

        If NavigationCurtainSurface IsNot Nothing Then
            NavigationCurtainSurface.IsHitTestVisible = False
        End If

        AnimateNavigationCurtain(False)
        AnimateNavigationBrandQuadrant(False, animationVersion)

        Dim exitDuration = ResolveDurationResource("MotionNavigationCurtainExitDuration", 800)
        Dim exitDelay = If(exitDuration.HasTimeSpan,
                           exitDuration.TimeSpan,
                           TimeSpan.FromMilliseconds(800))
        Await Task.Delay(exitDelay)

        If animationVersion <> _navigationMenuAnimationVersion OrElse (_shellViewModel IsNot Nothing AndAlso _shellViewModel.IsNavigationMenuOpen) Then
            Return
        End If

        Await Dispatcher.InvokeAsync(Sub()
                                         If NavigationCurtainSurface IsNot Nothing Then
                                             NavigationCurtainSurface.Visibility = Visibility.Collapsed
                                             NavigationCurtainSurface.IsHitTestVisible = False
                                         End If

                                         UpdateNavigationCurtainClip(0.0)
                                         ResetNavigationMenuItemsForNextOpen()
                                     End Sub)
    End Sub

    Protected Overrides Sub OnClosed(e As EventArgs)
        RemoveHandler _shellViewModel.CurrentPageChanged, AddressOf OnCurrentPageChanged
        RemoveHandler _shellViewModel.PropertyChanged, AddressOf OnShellViewModelPropertyChanged
        RemoveHandler _themeManager.ThemeChanged, AddressOf OnThemeChanged
        RemoveHandler _navigationLocalClockTimer.Tick, AddressOf OnNavigationLocalClockTick

        _navigationLocalClockTimer.Stop()

        AttachCapturePageHandlers(Nothing)

        If _windowSource IsNot Nothing Then
            _windowSource.RemoveHook(AddressOf WndProc)
            _windowSource = Nothing
        End If

        EndCaptureLock()
        _shellViewModel.Dispose()

        MyBase.OnClosed(e)
    End Sub

    Private Sub OnNavigationLocalClockTick(sender As Object, e As EventArgs)
        UpdateNavigationLocalClockText()
    End Sub

    Private Function WndProc(hwnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As IntPtr, ByRef handled As Boolean) As IntPtr
        If msg = NativeMethods.WM_GETMINMAXINFO Then
            WindowSizingHelper.ApplyWindowBounds(Me, hwnd, lParam)
            handled = True
            Return IntPtr.Zero
        End If

        If msg = NativeMethods.WM_DISPLAYCHANGE OrElse msg = NativeMethods.WM_DPICHANGED Then
            Dispatcher.BeginInvoke(New Action(AddressOf UpdateRenderingDiagnostics))
        End If

        Return IntPtr.Zero
    End Function

    Private Sub OnThemeChanged(sender As Object, e As EventArgs)
        Dispatcher.BeginInvoke(New Action(AddressOf ApplyWindowChromeTheme))
    End Sub

    Private Sub AnimateCurrentPageHost()
        If CurrentPageHost Is Nothing OrElse Not IsLoaded Then
            Return
        End If

        Dim translate = TryCast(CurrentPageHost.RenderTransform, TranslateTransform)
        If translate Is Nothing Then
            translate = New TranslateTransform()
            CurrentPageHost.RenderTransform = translate
        End If

        Dim duration = ResolveDurationResource("MotionPageSwapDuration", 180)
        Dim easing = ResolveEasingFunctionResource("MotionOutEase")

        translate.BeginAnimation(TranslateTransform.YProperty, Nothing)
        CurrentPageHost.Opacity = 1.0

        Dim offsetAnimation As New DoubleAnimation With {
            .From = 14.0,
            .To = 0.0,
            .Duration = duration
        }

        If easing IsNot Nothing Then
            offsetAnimation.EasingFunction = easing
        End If

        translate.BeginAnimation(TranslateTransform.YProperty, offsetAnimation)
    End Sub

    Private Sub AnimateWindowStartupPresentation()
        If WindowPresentationSurface Is Nothing OrElse WindowContentSurface Is Nothing Then
            Return
        End If

        Dim duration = ResolveDurationResource("MotionWindowStartupDuration", 340)
        Dim fadeEasing = ResolveEasingFunctionResource("MotionInOutEase")
        Dim translateEasing = ResolveEasingFunctionResource("MotionOutEase")
        Dim contentBeginTime = TimeSpan.FromMilliseconds(55)

        WindowPresentationSurface.BeginAnimation(UIElement.OpacityProperty, Nothing)
        WindowPresentationSurface.Opacity = 0.0

        Dim shellOpacityAnimation As New DoubleAnimation With {
            .From = WindowPresentationSurface.Opacity,
            .To = 1.0,
            .Duration = duration
        }

        If fadeEasing IsNot Nothing Then
            shellOpacityAnimation.EasingFunction = fadeEasing
        End If

        WindowPresentationSurface.BeginAnimation(UIElement.OpacityProperty, shellOpacityAnimation)

        WindowContentSurface.BeginAnimation(UIElement.OpacityProperty, Nothing)
        WindowContentSurface.Opacity = 0.0

        Dim contentOpacityAnimation As New DoubleAnimation With {
            .From = WindowContentSurface.Opacity,
            .To = 1.0,
            .BeginTime = contentBeginTime,
            .Duration = duration
        }

        If fadeEasing IsNot Nothing Then
            contentOpacityAnimation.EasingFunction = fadeEasing
        End If

        WindowContentSurface.BeginAnimation(UIElement.OpacityProperty, contentOpacityAnimation)

        If WindowContentStartupTransform IsNot Nothing Then
            WindowContentStartupTransform.BeginAnimation(TranslateTransform.YProperty, Nothing)
            WindowContentStartupTransform.Y = 12.0

            Dim translateAnimation As New DoubleAnimation With {
                .From = WindowContentStartupTransform.Y,
                .To = 0.0,
                .BeginTime = contentBeginTime,
                .Duration = duration
            }

            If translateEasing IsNot Nothing Then
                translateAnimation.EasingFunction = translateEasing
            End If

            WindowContentStartupTransform.BeginAnimation(TranslateTransform.YProperty, translateAnimation)
        End If
    End Sub

    Private Sub PrepareStartupMenuHandoffState()
        If _hasPreparedStartupMenuHandoffState Then
            Return
        End If

        _hasPreparedStartupMenuHandoffState = True

        If WindowPresentationSurface IsNot Nothing Then
            WindowPresentationSurface.BeginAnimation(UIElement.OpacityProperty, Nothing)
            WindowPresentationSurface.Opacity = 1.0
        End If

        If WindowContentSurface IsNot Nothing Then
            WindowContentSurface.BeginAnimation(UIElement.OpacityProperty, Nothing)
            WindowContentSurface.Opacity = 1.0
        End If

        If WindowContentStartupTransform IsNot Nothing Then
            WindowContentStartupTransform.BeginAnimation(TranslateTransform.YProperty, Nothing)
            WindowContentStartupTransform.Y = 0.0
        End If

        If _shellViewModel Is Nothing OrElse Not _shellViewModel.IsNavigationMenuOpen Then
            Return
        End If

        NavigationCurtainSurface.Visibility = Visibility.Visible
        NavigationCurtainSurface.IsHitTestVisible = True
        NavigationCurtainSurface.UpdateLayout()
        UpdateNavigationCurtainGuideLayout()
        UpdateNavigationCurtainClip(Math.Max(NavigationCurtainSurface.ActualHeight, Math.Max(ActualHeight, 1.0)))
        SetNavigationBrandQuadrantState(NavigationBrandPrimaryOpenFrames(NavigationBrandPrimaryOpenFrames.Length - 1),
                                        NavigationBrandSecondaryOpenFrames(NavigationBrandSecondaryOpenFrames.Length - 1))
        SetNavigationBrandQuadrantRevealState(True)
        SetNavigationMenuItemsVisibleState()
    End Sub

    Private Sub NavigationCurtainSurface_SizeChanged(sender As Object, e As SizeChangedEventArgs)
        If NavigationCurtainSurface Is Nothing Then
            Return
        End If

        UpdateNavigationCurtainGuideLayout()

        If _isPreparingNavigationCurtainOpen Then
            Return
        End If

        If _shellViewModel Is Nothing OrElse Not _shellViewModel.IsNavigationMenuOpen Then
            UpdateNavigationCurtainClip(0.0)
            Return
        End If

        Dim targetHeight = 0.0

        If NavigationCurtainSurface.Visibility = Visibility.Visible Then
            targetHeight = NavigationCurtainSurface.ActualHeight
        End If

        UpdateNavigationCurtainClip(targetHeight)
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

        clipGeometry.BeginAnimation(RectangleGeometry.RectProperty, Nothing)

        NavigationCurtainSurface.Visibility = Visibility.Visible
        NavigationCurtainSurface.IsHitTestVisible = isOpening

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

        If isOpening Then
            _isPreparingNavigationCurtainOpen = False
        End If
    End Sub

    Private Async Sub AnimateNavigationBrandQuadrant(isOpening As Boolean, animationVersion As Integer)
        If NavigationBrandPolygonPrimary Is Nothing OrElse NavigationBrandPolygonSecondary Is Nothing Then
            Return
        End If

        Try
            If isOpening Then
                If NavigationBrandQuadrant IsNot Nothing Then
                    NavigationBrandQuadrant.UpdateLayout()
                End If

                SetNavigationBrandQuadrantState(NavigationBrandClosedPrimaryPoints, NavigationBrandClosedSecondaryPoints)
                SetNavigationBrandQuadrantRevealState(False)
                Await Task.Delay(NavigationBrandOpenDelayMilliseconds)

                If animationVersion <> _navigationMenuAnimationVersion OrElse
                   _shellViewModel Is Nothing OrElse
                   Not _shellViewModel.IsNavigationMenuOpen Then
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

                Return
            End If

            Await Task.Delay(NavigationBrandCloseResetDelayMilliseconds)

            If animationVersion <> _navigationMenuAnimationVersion OrElse
               (_shellViewModel IsNot Nothing AndAlso _shellViewModel.IsNavigationMenuOpen) Then
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

            If animationVersion <> _navigationMenuAnimationVersion OrElse
               (_shellViewModel IsNot Nothing AndAlso _shellViewModel.IsNavigationMenuOpen) Then
                Return
            End If

            SetNavigationBrandQuadrantRevealState(False)
        Catch ex As Exception
            Trace.WriteLine($"Navigation brand animation failed: {ex.Message}")
        End Try
    End Sub

    Private Async Function AnimateNavigationBrandQuadrantStepAsync(primaryFrom As Point(),
                                                                   primaryTo As Point(),
                                                                   secondaryFrom As Point(),
                                                                   secondaryTo As Point(),
                                                                   animationVersion As Integer) As Task(Of Boolean)
        Dim stepStopwatch = Stopwatch.StartNew()

        Do
            If animationVersion <> _navigationMenuAnimationVersion OrElse
               _shellViewModel Is Nothing OrElse
               Not _shellViewModel.IsNavigationMenuOpen OrElse
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
        clipGeometry.Rect = CreateNavigationBrandQuadrantClipRect(If(isVisible, 1.0, 0.0),
                                                                  GetNavigationBrandQuadrantClipSize())
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

    Private Function CreateNavigationBrandQuadrantHiddenClipRect() As Rect
        Return CreateNavigationBrandQuadrantClipRect(0.0, GetNavigationBrandQuadrantClipSize())
    End Function

    Private Function CreateNavigationBrandQuadrantVisibleClipRect() As Rect
        Return CreateNavigationBrandQuadrantClipRect(1.0, GetNavigationBrandQuadrantClipSize())
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

        If _shellViewModel Is Nothing OrElse Not _shellViewModel.IsNavigationMenuOpen OrElse progress >= 1.0 Then
            Return
        End If

        Dim totalDuration = ResolveDurationResource("MotionNavigationCurtainEnterDuration", 800)
        Dim totalMilliseconds = If(totalDuration.HasTimeSpan,
                                   totalDuration.TimeSpan.TotalMilliseconds,
                                   800.0)
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
        Dim referenceHeight = Math.Max(If(previousSize.Height > 0.0,
                                          previousSize.Height,
                                          currentSize.Height),
                                       0.0)

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
        Return New Rect(0.0,
                        height - revealedHeight,
                        width,
                        revealedHeight)
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

        ' The close rail must be based on the menu content height, not the ScrollViewer viewport.
        ' During resize, the viewport can transiently report the whole available height, which collapses
        ' the top guide and pushes the close button into a clipped layout slot.
        Dim menuHeight = ResolveNavigationMenuMeasuredHeight(layoutWidth, layoutHeight)
        Dim minimumTopGuideHeight = ResolveNavigationCloseRailMinimumHeight()

        Dim topGuideHeight = 0.0
        If menuHeight > 0.0 Then
            topGuideHeight = (layoutHeight - Math.Min(menuHeight, layoutHeight)) / 2.0
        ElseIf _shellViewModel IsNot Nothing AndAlso _shellViewModel.IsNavigationMenuOpen Then
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

    Private Sub AnimateNavigationMenuItems(isOpening As Boolean)
        If NavigationMenuItemsControl Is Nothing Then
            Return
        End If

        NavigationMenuItemsControl.UpdateLayout()
        UpdateNavigationMenuContentAlignment()

        Dim menuButtons As List(Of Button) = New List(Of Button)(FindVisualChildren(Of Button)(NavigationMenuItemsControl))
        If menuButtons.Count = 0 Then
            Return
        End If

        menuButtons.Sort(Function(left, right) left.TranslatePoint(New Point(0, 0), NavigationMenuItemsControl).Y.CompareTo(right.TranslatePoint(New Point(0, 0), NavigationMenuItemsControl).Y))

        If Not isOpening Then
            SetNavigationMenuItemsVisibleState(menuButtons)
            Return
        End If

        Dim duration = ResolveDurationResource("MotionNavigationItemEnterDuration", 800)
        Dim easing = ResolveEasingFunctionResource("MotionNavigationContentEnterEase")
        Dim baseDelayMilliseconds = ResolveDoubleResource("MotionNavigationItemEnterBaseDelayMs", 400.0)
        Dim staggerRangeMilliseconds = ResolveDoubleResource("MotionNavigationItemEnterStaggerRangeMs", 200.0)
        Dim staggerPower = ResolveDoubleResource("MotionNavigationItemEnterStaggerPower", 1.35)

        SetNavigationMenuItemsState(1.0, 0.0, menuButtons)

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
    End Sub

    Private Sub ResetNavigationMenuItemsForNextOpen()
        ClearNavigationMenuClosingHighlight()
        SetNavigationMenuItemsState(1.0, 0.0)
        SetNavigationMenuTextState(False)
        SetNavigationMenuHoverSurfaceState(0.0)
    End Sub

    Private Sub SetNavigationMenuItemsVisibleState()
        SetNavigationMenuItemsState(1.0, 0.0)
        SetNavigationMenuTextState(True)
        SyncNavigationMenuHoverSurfaces()
    End Sub

    Private Sub SetNavigationMenuItemsVisibleState(menuButtons As IEnumerable(Of Button))
        SetNavigationMenuItemsState(1.0, 0.0, menuButtons)
        SetNavigationMenuTextState(True, menuButtons)
        SyncNavigationMenuHoverSurfaces(menuButtons)
    End Sub

    Private Shared Sub BeginDoubleAnimation(target As DependencyObject,
                                            [property] As DependencyProperty,
                                            fromValue As Double,
                                            toValue As Double,
                                            duration As Duration,
                                            easing As IEasingFunction)
        If target Is Nothing Then
            Return
        End If

        Dim animation As New DoubleAnimation With {
            .From = fromValue,
            .To = toValue,
            .Duration = duration
        }

        If easing IsNot Nothing Then
            animation.EasingFunction = easing
        End If

        Dim uiTarget = TryCast(target, UIElement)
        If uiTarget IsNot Nothing Then
            uiTarget.BeginAnimation([property], Nothing)
            uiTarget.BeginAnimation([property], animation)
            Return
        End If

        Dim animatableTarget = TryCast(target, Animatable)
        If animatableTarget IsNot Nothing Then
            animatableTarget.BeginAnimation([property], Nothing)
            animatableTarget.BeginAnimation([property], animation)
        End If
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

    Private Sub AnimateNavigationMenuHoverSurface(menuButton As Button, targetOpacity As Double)
        Dim hoverSurface = ResolveNavigationMenuHoverSurface(menuButton)
        If hoverSurface Is Nothing Then
            Return
        End If

        If targetOpacity <= 0.0 AndAlso ShouldKeepNavigationMenuHoverSurfaceVisible(menuButton) Then
            targetOpacity = 1.0
        End If

        hoverSurface.BeginAnimation(UIElement.OpacityProperty, Nothing)

        Dim duration = ResolveDurationResource("MotionMediumDuration", 200)
        Dim easing = ResolveEasingFunctionResource("MotionInOutEase")
        Dim animation As New DoubleAnimation With {
            .From = hoverSurface.Opacity,
            .To = targetOpacity,
            .Duration = duration
        }

        If easing IsNot Nothing Then
            animation.EasingFunction = easing
        End If

        hoverSurface.BeginAnimation(UIElement.OpacityProperty, animation)
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

    Private Sub SyncNavigationMenuHoverSurfaces(Optional menuButtons As IEnumerable(Of Button) = Nothing)
        If NavigationMenuItemsControl Is Nothing Then
            Return
        End If

        Dim buttons = If(menuButtons,
                         New List(Of Button)(FindVisualChildren(Of Button)(NavigationMenuItemsControl)))

        For Each menuButton In buttons
            Dim targetOpacity = If(menuButton IsNot Nothing AndAlso (menuButton.IsMouseOver OrElse ShouldKeepNavigationMenuHoverSurfaceVisible(menuButton)), 1.0, 0.0)
            SetNavigationMenuHoverSurfaceState(targetOpacity, New Button() {menuButton})
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

    Private Sub RememberNavigationMenuClosingHighlight(menuButton As Button)
        Dim pageKey = ResolveNavigationMenuPageKey(menuButton)
        If Not pageKey.HasValue Then
            Return
        End If

        _navigationMenuClosingHighlightPageKey = pageKey.Value
    End Sub

    Private Sub ClearNavigationMenuClosingHighlight()
        _navigationMenuClosingHighlightPageKey = Nothing
    End Sub

    Private Function ResolveNavigationMenuPageKey(menuButton As Button) As Nullable(Of AppPageKey)
        If menuButton Is Nothing Then
            Return Nothing
        End If

        Dim pageDescriptor = TryCast(menuButton.CommandParameter, AppPageDescriptor)
        If pageDescriptor Is Nothing Then
            Return Nothing
        End If

        Return pageDescriptor.PageKey
    End Function

    Private Function ShouldKeepNavigationMenuHoverSurfaceVisible(menuButton As Button) As Boolean
        If menuButton Is Nothing OrElse Not _navigationMenuClosingHighlightPageKey.HasValue Then
            Return False
        End If

        Dim pageKey = ResolveNavigationMenuPageKey(menuButton)
        Return pageKey.HasValue AndAlso pageKey.Value = _navigationMenuClosingHighlightPageKey.Value
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

    Private Function ResolveCaptureLockSurface() As FrameworkElement
        If CurrentPageHost Is Nothing Then
            Return Nothing
        End If

        CurrentPageHost.ApplyTemplate()

        Dim captureSurfaceHost = FindCaptureSurfaceHost(CurrentPageHost)
        If captureSurfaceHost Is Nothing Then
            Return Nothing
        End If

        Return captureSurfaceHost.CaptureLockSurface
    End Function

    Private Function FindCaptureSurfaceHost(root As DependencyObject) As ICaptureSurfaceHost
        If root Is Nothing Then
            Return Nothing
        End If

        Dim captureSurfaceHost = TryCast(root, ICaptureSurfaceHost)
        If captureSurfaceHost IsNot Nothing Then
            Return captureSurfaceHost
        End If

        Dim childCount = VisualTreeHelper.GetChildrenCount(root)
        For childIndex = 0 To childCount - 1
            Dim child = VisualTreeHelper.GetChild(root, childIndex)
            Dim nestedCaptureSurfaceHost = FindCaptureSurfaceHost(child)
            If nestedCaptureSurfaceHost IsNot Nothing Then
                Return nestedCaptureSurfaceHost
            End If
        Next

        Return Nothing
    End Function

    Private Sub MaximizeRestoreButton_Click(sender As Object, e As RoutedEventArgs)
        If WindowState = WindowState.Maximized Then
            SystemCommands.RestoreWindow(Me)
        Else
            SystemCommands.MaximizeWindow(Me)
        End If

        UpdateWindowControlGlyph()
    End Sub

    Private Sub MinimizeButton_Click(sender As Object, e As RoutedEventArgs)
        SystemCommands.MinimizeWindow(Me)
    End Sub

    Private Sub CloseButton_Click(sender As Object, e As RoutedEventArgs)
        SystemCommands.CloseWindow(Me)
    End Sub

    Private Sub UpdateWindowControlGlyph()
        If MaximizeRestoreGlyph Is Nothing Then
            Return
        End If

        MaximizeRestoreGlyph.Text = If(WindowState = WindowState.Maximized, ChrW(&HE923), ChrW(&HE922))
    End Sub

    Private Sub ApplyWindowChromeTheme()
        Dim windowHandle = New WindowInteropHelper(Me).Handle
        If windowHandle = IntPtr.Zero Then
            Return
        End If

        Dim isDark = _themeManager.CurrentTheme = AppTheme.Dark
        Dim captionColor = ResolveColorResource("GlassTitleBarBackgroundColor", ResolveColorResource("WindowBackgroundColor", If(isDark, Colors.Black, Colors.White)))
        Dim textColor = ResolveColorResource("TextStrongColor", If(isDark, Colors.White, Colors.Black))
        Dim borderColor = ResolveColorResource("WindowOuterBorderColor", If(isDark, Colors.White, Colors.Black))

        NativeMethods.TrySetImmersiveDarkMode(windowHandle, isDark)
        NativeMethods.TrySetSystemBackdropType(windowHandle, NativeMethods.DWMSBT_MAINWINDOW)
        NativeMethods.TrySetWindowColorAttribute(windowHandle, NativeMethods.DWMWA_CAPTION_COLOR, ToColorRef(captionColor))
        NativeMethods.TrySetWindowColorAttribute(windowHandle, NativeMethods.DWMWA_TEXT_COLOR, ToColorRef(textColor))
        NativeMethods.TrySetWindowColorAttribute(windowHandle, NativeMethods.DWMWA_BORDER_COLOR, ToColorRef(borderColor))
    End Sub

    Private Sub ApplyWindowCornerPreference()
        Dim chrome = WindowChrome.GetWindowChrome(Me)
        If chrome IsNot Nothing Then
            chrome.CornerRadius = SquareWindowCornerRadius
        End If

        Dim windowHandle = New WindowInteropHelper(Me).Handle
        If windowHandle = IntPtr.Zero Then
            Return
        End If

        NativeMethods.TrySetWindowCornerPreference(windowHandle, NativeMethods.DWMWCP_DONOTROUND)
    End Sub

    Private Function ResolveColorResource(resourceKey As String, fallback As Color) As Color
        Dim resource = TryFindResource(resourceKey)
        If TypeOf resource Is Color Then
            Return CType(resource, Color)
        End If

        Dim brush = TryCast(resource, SolidColorBrush)
        If brush IsNot Nothing Then
            Return brush.Color
        End If

        Return fallback
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

    Private Shared Function ToColorRef(color As Color) As Integer
        Return CInt(color.R) Or (CInt(color.G) << 8) Or (CInt(color.B) << 16)
    End Function

    Private Sub UpdateRenderingDiagnostics()
        Dim diagnostics = TextRenderingDiagnostics.BuildSummary(Me)
        If StringComparer.Ordinal.Equals(diagnostics, _lastRenderingDiagnostics) Then
            Return
        End If

        _lastRenderingDiagnostics = diagnostics
        Trace.WriteLine(diagnostics)
    End Sub
End Class
