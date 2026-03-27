Imports System.Globalization
Imports System.Runtime.Versioning
Imports System.Windows
Imports System.Windows.Threading
Imports WpfApp1.Infrastructure
Imports WpfApp1.Models
Imports WpfApp1.Navigation
Imports WpfApp1.Services

Namespace ViewModels.Pages
    <SupportedOSPlatform("windows")>
    Public Class AngleCalibrationPageViewModel
        Inherits BindableBase
        Implements IDisposable, ICaptureSessionPageViewModel, INavigationResettablePageViewModel

        Private Enum StatusScenario
            Ready
            AwaitingInput
            Measuring
            Paused
            ResultReady
            NoDevice
            DeviceDisconnected
        End Enum

        Private Const ResultSwipeThreshold As Integer = 30
        Private Const UiRefreshIntervalMilliseconds As Double = 1000.0 / 60.0

        Private ReadOnly _dispatcher As Dispatcher
        Private ReadOnly _rawInputBroker As IRawInputBroker
        Private ReadOnly _localization As LocalizationManager
        Private ReadOnly _uiTimer As DispatcherTimer
        Private ReadOnly _captureSurfaceCommand As DelegateCommand
        Private ReadOnly _copyAngleCommand As DelegateCommand
        Private ReadOnly _resetCommand As DelegateCommand
        Private _captureService As AngleCalibrationCaptureService
        Private _renderFrame As AngleCalibrationRenderFrame
        Private _recommendedAngleText As String
        Private _swipeCountText As String
        Private _sampleCountText As String
        Private _stabilityText As String
        Private _topHintText As String
        Private _qualityHintText As String
        Private _isLocked As Boolean
        Private _isPageActive As Boolean
        Private _isDeviceDisconnected As Boolean
        Private _disposed As Boolean

        Public Sub New(rawInputBroker As IRawInputBroker)
            If Application.Current IsNot Nothing Then
                _dispatcher = Application.Current.Dispatcher
            Else
                _dispatcher = Dispatcher.CurrentDispatcher
            End If

            If rawInputBroker Is Nothing Then
                Throw New ArgumentNullException(NameOf(rawInputBroker))
            End If

            _rawInputBroker = rawInputBroker
            _localization = LocalizationManager.Instance
            _localization.Initialize()
            _uiTimer = New DispatcherTimer(DispatcherPriority.Render, _dispatcher)
            _uiTimer.Interval = TimeSpan.FromMilliseconds(UiRefreshIntervalMilliseconds)

            _captureSurfaceCommand = New DelegateCommand(AddressOf RequestCaptureSurfaceInteraction)
            _copyAngleCommand = New DelegateCommand(AddressOf RequestCopyAngle, AddressOf CanCopyAngle)
            _resetCommand = New DelegateCommand(AddressOf RequestReset)
            _renderFrame = CreateEmptyRenderFrame()

            AddHandler _localization.LanguageChanged, AddressOf OnLanguageChanged
            AddHandler _uiTimer.Tick, AddressOf OnUiTimerTick

            RefreshFrameText(_renderFrame)
            ApplyStatusScenario(StatusScenario.Ready)
        End Sub

        Public Event EnterLockRequested As EventHandler Implements ICaptureSessionPageViewModel.EnterLockRequested
        Public Event ExitLockRequested As EventHandler(Of CaptureLockRequestEventArgs) Implements ICaptureSessionPageViewModel.ExitLockRequested

        Public Property RenderFrame As AngleCalibrationRenderFrame
            Get
                Return _renderFrame
            End Get
            Private Set(value As AngleCalibrationRenderFrame)
                If value Is Nothing Then
                    value = CreateEmptyRenderFrame()
                End If

                SetProperty(_renderFrame, value)
            End Set
        End Property

        Public Property RecommendedAngleText As String
            Get
                Return _recommendedAngleText
            End Get
            Private Set(value As String)
                SetProperty(_recommendedAngleText, value)
            End Set
        End Property

        Public Property SwipeCountText As String
            Get
                Return _swipeCountText
            End Get
            Private Set(value As String)
                SetProperty(_swipeCountText, value)
            End Set
        End Property

        Public Property SampleCountText As String
            Get
                Return _sampleCountText
            End Get
            Private Set(value As String)
                SetProperty(_sampleCountText, value)
            End Set
        End Property

        Public Property StabilityText As String
            Get
                Return _stabilityText
            End Get
            Private Set(value As String)
                SetProperty(_stabilityText, value)
            End Set
        End Property

        Public Property TopHintText As String
            Get
                Return _topHintText
            End Get
            Private Set(value As String)
                SetProperty(_topHintText, value)
            End Set
        End Property

        Public Property QualityHintText As String
            Get
                Return _qualityHintText
            End Get
            Private Set(value As String)
                SetProperty(_qualityHintText, value)
            End Set
        End Property

        Public Property IsLocked As Boolean Implements ICaptureSessionPageViewModel.IsLocked
            Get
                Return _isLocked
            End Get
            Private Set(value As Boolean)
                SetProperty(_isLocked, value)
            End Set
        End Property

        Public ReadOnly Property CaptureSurfaceCommand As DelegateCommand
            Get
                Return _captureSurfaceCommand
            End Get
        End Property

        Public ReadOnly Property ResetCommand As DelegateCommand
            Get
                Return _resetCommand
            End Get
        End Property

        Public ReadOnly Property CopyAngleCommand As DelegateCommand
            Get
                Return _copyAngleCommand
            End Get
        End Property

        Public ReadOnly Property HasRecommendedAngle As Boolean
            Get
                Return RenderFrame IsNot Nothing AndAlso RenderFrame.HasRecommendedAngle
            End Get
        End Property

        Public ReadOnly Property HasQualityHint As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(QualityHintText)
            End Get
        End Property

        Public Sub SetPageActive(isActive As Boolean)
            If _isPageActive = isActive Then
                Return
            End If

            _isPageActive = isActive
            If isActive Then
                EnsureCaptureService()
                PullLatestRenderFrame(forceCapture:=True, updateScenario:=True)
                RefreshAvailableDeviceState()
                UpdateUiTimer()
            Else
                _uiTimer.Stop()
            End If
        End Sub

        Public Sub ResetToDefaultState() Implements INavigationResettablePageViewModel.ResetToDefaultState
            _uiTimer.Stop()

            If _captureService IsNot Nothing Then
                _captureService.StopSession()
                _captureService.ResetSession()
            End If

            ReleaseCaptureService()

            _isPageActive = False
            _isDeviceDisconnected = False
            IsLocked = False
            RenderFrame = CreateEmptyRenderFrame()
            RefreshFrameText(RenderFrame)
            ApplyStatusScenario(StatusScenario.Ready)
        End Sub

        Public Sub OnLockEntered() Implements ICaptureSessionPageViewModel.OnLockEntered
            EnsureCaptureService()
            If _captureService Is Nothing Then
                Return
            End If

            If Not _captureService.BeginSession() Then
                IsLocked = False
                _isDeviceDisconnected = False
                ApplyStatusScenario(StatusScenario.NoDevice)
                RaiseEvent ExitLockRequested(Me, New CaptureLockRequestEventArgs(CaptureUnlockReason.PauseSession))
                Return
            End If

            _isDeviceDisconnected = False
            IsLocked = True
            PullLatestRenderFrame(forceCapture:=True, updateScenario:=True)
            UpdateUiTimer()
        End Sub

        Public Sub RequestPauseFromView() Implements ICaptureSessionPageViewModel.RequestPauseFromView
            If Not IsLocked Then
                Return
            End If

            RaiseEvent ExitLockRequested(Me, New CaptureLockRequestEventArgs(CaptureUnlockReason.PauseSession))
        End Sub

        Public Sub OnViewUnlockCompleted(reason As CaptureUnlockReason) Implements ICaptureSessionPageViewModel.OnViewUnlockCompleted
            IsLocked = False

            If _captureService Is Nothing Then
                ApplyScenarioFromFrame()
                Return
            End If

            Select Case reason
                Case CaptureUnlockReason.ClearSession
                    _captureService.StopSession()
                    _captureService.ResetSession()
                    _isDeviceDisconnected = False
                    PullLatestRenderFrame(forceCapture:=True, updateScenario:=False)
                    RefreshAvailableDeviceState()
                Case CaptureUnlockReason.DeviceDisconnected
                    _captureService.StopSession()
                    _isDeviceDisconnected = True
                    PullLatestRenderFrame(forceCapture:=True, updateScenario:=False)
                    ApplyStatusScenario(StatusScenario.DeviceDisconnected)
                Case Else
                    _captureService.PauseSession()
                    _isDeviceDisconnected = False
                    PullLatestRenderFrame(forceCapture:=True, updateScenario:=True)
            End Select

            UpdateUiTimer()
        End Sub

        Private Sub EnsureCaptureService()
            If _captureService IsNot Nothing Then
                Return
            End If

            Dim captureService As New AngleCalibrationCaptureService(_rawInputBroker)
            AddHandler captureService.DevicesChanged, AddressOf OnDevicesChanged
            AddHandler captureService.SelectedDeviceDisconnected, AddressOf OnSelectedDeviceDisconnected
            _captureService = captureService
        End Sub

        Private Sub ReleaseCaptureService()
            If _captureService Is Nothing Then
                Return
            End If

            RemoveHandler _captureService.DevicesChanged, AddressOf OnDevicesChanged
            RemoveHandler _captureService.SelectedDeviceDisconnected, AddressOf OnSelectedDeviceDisconnected
            _captureService.Dispose()
            _captureService = Nothing
        End Sub

        Private Sub RequestCaptureSurfaceInteraction()
            If IsLocked Then
                Return
            End If

            EnsureCaptureService()
            If _captureService Is Nothing Then
                Return
            End If

            If Not _captureService.HasAvailableMouseDevice() Then
                ApplyStatusScenario(StatusScenario.NoDevice)
                Return
            End If

            _isDeviceDisconnected = False
            RaiseEvent EnterLockRequested(Me, EventArgs.Empty)
        End Sub

        Private Sub RequestReset()
            If IsLocked Then
                RaiseEvent ExitLockRequested(Me, New CaptureLockRequestEventArgs(CaptureUnlockReason.ClearSession))
                Return
            End If

            EnsureCaptureService()
            If _captureService IsNot Nothing Then
                _captureService.StopSession()
                _captureService.ResetSession()
                _isDeviceDisconnected = False
                PullLatestRenderFrame(forceCapture:=True, updateScenario:=False)
            Else
                RenderFrame = CreateEmptyRenderFrame()
                RefreshFrameText(RenderFrame)
            End If

            RefreshAvailableDeviceState()
        End Sub

        Private Function CanCopyAngle() As Boolean
            Return HasRecommendedAngle
        End Function

        Private Sub RequestCopyAngle()
            If Not CanCopyAngle() Then
                Return
            End If

            Clipboard.SetText(RecommendedAngleText)
        End Sub

        Private Sub OnDevicesChanged(sender As Object, e As EventArgs)
            RunOnUiThread(AddressOf RefreshAvailableDeviceState)
        End Sub

        Private Sub OnSelectedDeviceDisconnected(sender As Object, e As EventArgs)
            RunOnUiThread(
                Sub()
                    If IsLocked Then
                        RaiseEvent ExitLockRequested(Me, New CaptureLockRequestEventArgs(CaptureUnlockReason.DeviceDisconnected))
                        Return
                    End If

                    _isDeviceDisconnected = True
                    PullLatestRenderFrame(forceCapture:=True, updateScenario:=False)
                    ApplyStatusScenario(StatusScenario.DeviceDisconnected)
                End Sub)
        End Sub

        Private Sub OnLanguageChanged(sender As Object, e As EventArgs)
            RunOnUiThread(
                Sub()
                    RefreshFrameText(RenderFrame)
                    ApplyScenarioFromFrame()
                End Sub)
        End Sub

        Private Sub OnUiTimerTick(sender As Object, e As EventArgs)
            If _captureService Is Nothing Then
                Return
            End If

            PullLatestRenderFrame(forceCapture:=IsLocked, updateScenario:=Not _isDeviceDisconnected)
        End Sub

        Private Sub PullLatestRenderFrame(forceCapture As Boolean, updateScenario As Boolean)
            If _captureService Is Nothing Then
                Return
            End If

            Dim nextFrame As AngleCalibrationRenderFrame = Nothing
            If Not _captureService.TryReadLatestRenderFrame(nextFrame) AndAlso forceCapture Then
                nextFrame = _captureService.CaptureRenderFrame()
            End If

            If nextFrame Is Nothing Then
                Return
            End If

            RenderFrame = nextFrame
            RefreshFrameText(nextFrame)

            If updateScenario Then
                ApplyScenarioFromFrame()
            End If
        End Sub

        Private Sub RefreshAvailableDeviceState()
            If _captureService Is Nothing Then
                Return
            End If

            If IsLocked Then
                Return
            End If

            Dim hasDevice = _captureService.HasAvailableMouseDevice()
            If _isDeviceDisconnected AndAlso hasDevice Then
                _isDeviceDisconnected = False
            End If

            If _isDeviceDisconnected Then
                ApplyStatusScenario(StatusScenario.DeviceDisconnected)
                Return
            End If

            If Not hasDevice AndAlso (RenderFrame Is Nothing OrElse Not RenderFrame.HasData) Then
                ApplyStatusScenario(StatusScenario.NoDevice)
            ElseIf hasDevice AndAlso (RenderFrame Is Nothing OrElse Not RenderFrame.HasData) Then
                ApplyStatusScenario(StatusScenario.Ready)
            Else
                ApplyScenarioFromFrame()
            End If
        End Sub

        Private Sub ApplyScenarioFromFrame()
            If _isDeviceDisconnected Then
                ApplyStatusScenario(StatusScenario.DeviceDisconnected)
                Return
            End If

            Dim frame = RenderFrame
            Dim hasData = frame IsNot Nothing AndAlso frame.HasData
            Dim hasDevice = _captureService IsNot Nothing AndAlso _captureService.HasAvailableMouseDevice()
            Dim hasSessionDevice = _captureService IsNot Nothing AndAlso
                                   Not String.IsNullOrWhiteSpace(_captureService.CurrentSessionDeviceId)

            If Not hasData Then
                If IsLocked Then
                    ApplyStatusScenario(If(hasSessionDevice, StatusScenario.Measuring, StatusScenario.AwaitingInput))
                Else
                    ApplyStatusScenario(If(hasDevice, StatusScenario.Ready, StatusScenario.NoDevice))
                End If
                Return
            End If

            If frame.HasRecommendedAngle Then
                ApplyStatusScenario(StatusScenario.ResultReady)
            ElseIf IsLocked Then
                ApplyStatusScenario(StatusScenario.Measuring)
            Else
                ApplyStatusScenario(StatusScenario.Paused)
            End If
        End Sub

        Private Sub ApplyStatusScenario(scenario As StatusScenario)
            Select Case scenario
                Case StatusScenario.Ready
                    TopHintText = L("AngleCalibration.Hint.Ready")
                Case StatusScenario.AwaitingInput
                    TopHintText = L("AngleCalibration.Hint.AwaitingInput")
                Case StatusScenario.Measuring
                    TopHintText = L("AngleCalibration.Hint.Measuring")
                Case StatusScenario.Paused
                    TopHintText = L("AngleCalibration.Hint.Paused")
                Case StatusScenario.ResultReady
                    TopHintText = If(IsLocked,
                                     L("AngleCalibration.Hint.ResultReady.Active"),
                                     L("AngleCalibration.Hint.ResultReady.Paused"))
                Case StatusScenario.NoDevice
                    TopHintText = L("AngleCalibration.Hint.NoDevice")
                Case StatusScenario.DeviceDisconnected
                    TopHintText = L("AngleCalibration.Hint.DeviceDisconnected")
            End Select
        End Sub

        Private Sub RefreshFrameText(frame As AngleCalibrationRenderFrame)
            If frame Is Nothing OrElse Not frame.HasRecommendedAngle OrElse Not frame.RecommendedAngleDegrees.HasValue Then
                RecommendedAngleText = "--"
            Else
                RecommendedAngleText = frame.RecommendedAngleDegrees.Value.ToString("0.0", CultureInfo.InvariantCulture)
            End If

            Dim swipeCount = If(frame Is Nothing, 0, frame.SwipeCount)
            Dim sampleCount = If(frame Is Nothing, 0, frame.SampleCount)
            Dim stability = If(frame Is Nothing, CType(Nothing, Nullable(Of Double)), frame.StabilityDegrees)

            SwipeCountText = String.Format(CultureInfo.InvariantCulture, "{0} /{1}", swipeCount, ResultSwipeThreshold)
            SampleCountText = sampleCount.ToString(CultureInfo.InvariantCulture)

            If Not stability.HasValue OrElse Double.IsNaN(stability.Value) OrElse Double.IsInfinity(stability.Value) Then
                StabilityText = "--"
            Else
                StabilityText = String.Format(CultureInfo.InvariantCulture, "±{0:0.0}°", stability.Value)
            End If

            QualityHintText = ResolveQualityHintText(frame)

            RaisePropertyChanged(NameOf(HasRecommendedAngle))
            RaisePropertyChanged(NameOf(HasQualityHint))
            _copyAngleCommand.RaiseCanExecuteChanged()
        End Sub

        Private Function ResolveQualityHintText(frame As AngleCalibrationRenderFrame) As String
            If frame Is Nothing OrElse Not frame.HasData Then
                Return String.Empty
            End If

            Select Case frame.QualityReason
                Case AngleCalibrationQualityReason.InsufficientProgress
                    Return L("AngleCalibration.Quality.InsufficientProgress")
                Case AngleCalibrationQualityReason.Imbalance
                    Return L("AngleCalibration.Quality.Imbalance")
                Case AngleCalibrationQualityReason.HighDispersion
                    Return L("AngleCalibration.Quality.HighDispersion")
                Case AngleCalibrationQualityReason.TooManyOutliers
                    Return L("AngleCalibration.Quality.Outliers")
                Case AngleCalibrationQualityReason.Good
                    If frame.QualityLevel = AngleCalibrationQualityLevel.Excellent Then
                        Return L("AngleCalibration.Quality.Excellent")
                    ElseIf frame.QualityLevel = AngleCalibrationQualityLevel.Good Then
                        Return L("AngleCalibration.Quality.Good")
                    End If

                    Return L("AngleCalibration.Quality.Fair")
                Case Else
                    Return String.Empty
            End Select
        End Function

        Private Sub UpdateUiTimer()
            Dim shouldRun = _isPageActive AndAlso _captureService IsNot Nothing AndAlso (IsLocked OrElse (RenderFrame IsNot Nothing AndAlso RenderFrame.HasData))
            If shouldRun AndAlso Not _uiTimer.IsEnabled Then
                _uiTimer.Start()
            ElseIf Not shouldRun AndAlso _uiTimer.IsEnabled Then
                _uiTimer.Stop()
            End If
        End Sub

        Private Shared Function CreateEmptyRenderFrame() As AngleCalibrationRenderFrame
            Return New AngleCalibrationRenderFrame(AngleCalibrationStatus.Empty,
                                                   False,
                                                   False,
                                                   Nothing,
                                                   0,
                                                   0,
                                                   Nothing,
                                                   Array.Empty(Of AngleCalibrationTraceStroke)(),
                                                   AngleCalibrationQualityLevel.None,
                                                   AngleCalibrationQualityReason.None,
                                                   0)
        End Function

        Private Sub RunOnUiThread(action As Action)
            If action Is Nothing Then
                Return
            End If

            If _dispatcher.CheckAccess() Then
                action()
            Else
                _dispatcher.BeginInvoke(action)
            End If
        End Sub

        Private Function L(key As String, ParamArray args() As Object) As String
            Return _localization.GetString(key, args)
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then
                Return
            End If

            _disposed = True

            _uiTimer.Stop()
            RemoveHandler _localization.LanguageChanged, AddressOf OnLanguageChanged
            RemoveHandler _uiTimer.Tick, AddressOf OnUiTimerTick
            ReleaseCaptureService()
        End Sub
    End Class
End Namespace

