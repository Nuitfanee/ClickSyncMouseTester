Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.Runtime.Versioning
Imports System.Windows.Media
Imports System.Windows.Threading
Imports WpfApp1.Infrastructure
Imports WpfApp1.Models
Imports WpfApp1.Navigation
Imports WpfApp1.Services

Namespace ViewModels
    <SupportedOSPlatform("windows")>
    Public Class MainWindowViewModel
        Inherits BindableBase
        Implements IDisposable, ICaptureSessionPageViewModel, INavigationResettablePageViewModel

        Private Enum StatusScenario
            Ready
            Measuring
            Paused
            Cleared
            NeedDevice
            DeviceDisconnected
            NoDevices
            RefreshFeedback
        End Enum

        Private NotInheritable Class MetricTextState
            Public Property LastUpdateMilliseconds As Double = Double.NaN
            Public Property HasDisplayedValue As Boolean
        End Class

        Private Const MetricTextUpdateIntervalMilliseconds As Double = 1000.0 / 15.0
        Private Const RefreshFeedbackDurationMilliseconds As Double = 1200.0

        Private ReadOnly _dispatcher As Dispatcher
        Private ReadOnly _captureService As PollingRateCaptureService
        Private ReadOnly _localization As LocalizationManager
        Private ReadOnly _themeManager As ThemeManager
        Private ReadOnly _statusRestoreTimer As DispatcherTimer
        Private ReadOnly _devices As ObservableCollection(Of RawMouseDeviceInfo)
        Private ReadOnly _languageOptions As ObservableCollection(Of LanguageOption)
        Private ReadOnly _toggleLanguageCommand As DelegateCommand
        Private ReadOnly _toggleThemeCommand As DelegateCommand
        Private ReadOnly _refreshDevicesCommand As DelegateCommand
        Private ReadOnly _startCommand As DelegateCommand
        Private ReadOnly _stopCommand As DelegateCommand

        Private _selectedDevice As RawMouseDeviceInfo
        Private _selectedLanguage As LanguageOption
        Private _selectedPollingRateMode As PollingRateMode
        Private _languageToggleText As String
        Private _languageToggleKeyText As String
        Private _themeToggleText As String
        Private _themeToggleKeyText As String
        Private _rateModeDescriptionText As String
        Private _rawCurrentRate As Double
        Private _currentRate As Integer
        Private _peakRate As Integer
        Private _windowJitterMs As Nullable(Of Double)
        Private _captureLatencyP95Ms As Nullable(Of Double)
        Private _droppedPacketCount As Long
        Private _historyPoints As IReadOnlyList(Of PollingHistoryPoint)
        Private _statusPillText As String
        Private _statusMessage As String
        Private _hintText As String
        Private _currentRateText As String
        Private _peakRateText As String
        Private _windowJitterText As String
        Private _captureLatencyP95Text As String
        Private _droppedPacketCountText As String
        Private _startButtonText As String
        Private _selectedDeviceTitle As String
        Private _selectedDeviceMetaText As String
        Private _selectedDevicePathText As String
        Private _isLocked As Boolean
        Private _hasSessionData As Boolean
        Private _sessionDeviceId As String
        Private _pendingDeviceId As String
        Private _pendingSessionReset As Boolean
        Private _clearSelectionOnNextRefresh As Boolean
        Private _latestMetricsSnapshot As PollingMetricsSnapshot
        Private _latestChartRenderFrame As PollingChartRenderFrame
        Private ReadOnly _currentRateTextState As New MetricTextState()
        Private ReadOnly _windowJitterTextState As New MetricTextState()
        Private _statusScenario As StatusScenario
        Private _restoreStatusScenario As StatusScenario
        Private _isPollingUiRenderingSubscribed As Boolean
        Private _isPageActive As Boolean
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

            _captureService = New PollingRateCaptureService(rawInputBroker)
            _localization = LocalizationManager.Instance
            _themeManager = ThemeManager.Instance
            _localization.Initialize()
            _statusRestoreTimer = New DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            _statusRestoreTimer.Interval = TimeSpan.FromMilliseconds(RefreshFeedbackDurationMilliseconds)
            _devices = New ObservableCollection(Of RawMouseDeviceInfo)()
            _languageOptions = New ObservableCollection(Of LanguageOption)(_localization.AvailableLanguages)
            _historyPoints = New List(Of PollingHistoryPoint)()
            _selectedPollingRateMode = PollingRateMode.RawPacketRate
            _captureService.SetMode(_selectedPollingRateMode)

            _toggleLanguageCommand = New DelegateCommand(AddressOf RequestToggleLanguage)
            _toggleThemeCommand = New DelegateCommand(AddressOf RequestToggleTheme)
            _refreshDevicesCommand = New DelegateCommand(AddressOf RequestRefreshDevices, AddressOf CanRefreshDevices)
            _startCommand = New DelegateCommand(AddressOf RequestStart, AddressOf CanStart)
            _stopCommand = New DelegateCommand(AddressOf RequestStop, AddressOf CanStop)

            AddHandler _statusRestoreTimer.Tick, AddressOf OnStatusRestoreTimerTick
            AddHandler _captureService.DevicesChanged, AddressOf OnDevicesChanged
            AddHandler _captureService.SelectedDeviceDisconnected, AddressOf OnSelectedDeviceDisconnected
            AddHandler _localization.LanguageChanged, AddressOf OnLanguageChanged
            AddHandler _themeManager.ThemeChanged, AddressOf OnThemeChanged

            _selectedLanguage = _localization.CurrentLanguage
            UpdateLanguageToggleText()
            UpdateThemeToggleText()
            UpdateRateModeDescriptionText()
            _restoreStatusScenario = StatusScenario.Ready

            SetMetricsToPlaceholder()
            UpdateSelectedDeviceSummary()
            ApplyStatusScenario(StatusScenario.Ready)
            UpdateStartButtonText()
            RefreshDevices(True)
        End Sub

        Public Event EnterLockRequested As EventHandler Implements ICaptureSessionPageViewModel.EnterLockRequested
        Public Event ExitLockRequested As EventHandler(Of CaptureLockRequestEventArgs) Implements ICaptureSessionPageViewModel.ExitLockRequested

        Public ReadOnly Property Devices As ObservableCollection(Of RawMouseDeviceInfo)
            Get
                Return _devices
            End Get
        End Property

        Public ReadOnly Property LanguageOptions As ObservableCollection(Of LanguageOption)
            Get
                Return _languageOptions
            End Get
        End Property

        Public Property LanguageToggleText As String
            Get
                Return _languageToggleText
            End Get
            Private Set(value As String)
                SetProperty(_languageToggleText, value)
            End Set
        End Property

        Public Property LanguageToggleKeyText As String
            Get
                Return _languageToggleKeyText
            End Get
            Private Set(value As String)
                SetProperty(_languageToggleKeyText, value)
            End Set
        End Property

        Public Property ThemeToggleText As String
            Get
                Return _themeToggleText
            End Get
            Private Set(value As String)
                SetProperty(_themeToggleText, value)
            End Set
        End Property

        Public Property ThemeToggleKeyText As String
            Get
                Return _themeToggleKeyText
            End Get
            Private Set(value As String)
                SetProperty(_themeToggleKeyText, value)
            End Set
        End Property

        Public Property RateModeDescriptionText As String
            Get
                Return _rateModeDescriptionText
            End Get
            Private Set(value As String)
                SetProperty(_rateModeDescriptionText, value)
            End Set
        End Property

        Public Property SelectedLanguage As LanguageOption
            Get
                Return _selectedLanguage
            End Get
            Set(value As LanguageOption)
                If value Is Nothing Then
                    Return
                End If

                If SetProperty(_selectedLanguage, value) Then
                    _localization.SetLanguage(value.CultureName)
                End If
            End Set
        End Property

        Public Property SelectedDevice As RawMouseDeviceInfo
            Get
                Return _selectedDevice
            End Get
            Set(value As RawMouseDeviceInfo)
                If SetProperty(_selectedDevice, value) Then
                    UpdateSelectedDeviceSummary()
                    UpdateStartButtonText()
                    RaiseCanExecuteChanges()
                End If
            End Set
        End Property

        Public Property IsRawPacketRateMode As Boolean
            Get
                Return _selectedPollingRateMode = PollingRateMode.RawPacketRate
            End Get
            Set(value As Boolean)
                Dim requestedMode = If(value, PollingRateMode.RawPacketRate, PollingRateMode.MotionReportRate)
                SetPollingRateMode(requestedMode)
            End Set
        End Property

        Public Property RawCurrentRate As Double
            Get
                Return _rawCurrentRate
            End Get
            Private Set(value As Double)
                SetProperty(_rawCurrentRate, value)
            End Set
        End Property

        Public Property CurrentRate As Integer
            Get
                Return _currentRate
            End Get
            Private Set(value As Integer)
                SetProperty(_currentRate, value)
            End Set
        End Property

        Public Property PeakRate As Integer
            Get
                Return _peakRate
            End Get
            Private Set(value As Integer)
                SetProperty(_peakRate, value)
            End Set
        End Property

        Public Property WindowJitterMs As Nullable(Of Double)
            Get
                Return _windowJitterMs
            End Get
            Private Set(value As Nullable(Of Double))
                SetProperty(_windowJitterMs, value)
            End Set
        End Property

        Public Property CaptureLatencyP95Ms As Nullable(Of Double)
            Get
                Return _captureLatencyP95Ms
            End Get
            Private Set(value As Nullable(Of Double))
                SetProperty(_captureLatencyP95Ms, value)
            End Set
        End Property

        Public Property DroppedPacketCount As Long
            Get
                Return _droppedPacketCount
            End Get
            Private Set(value As Long)
                SetProperty(_droppedPacketCount, value)
            End Set
        End Property

        Public Property HistoryPoints As IReadOnlyList(Of PollingHistoryPoint)
            Get
                Return _historyPoints
            End Get
            Private Set(value As IReadOnlyList(Of PollingHistoryPoint))
                If value Is Nothing Then
                    value = New List(Of PollingHistoryPoint)()
                End If

                SetProperty(_historyPoints, value)
            End Set
        End Property

        Public Property StatusPillText As String
            Get
                Return _statusPillText
            End Get
            Private Set(value As String)
                SetProperty(_statusPillText, value)
            End Set
        End Property

        Public Property StatusMessage As String
            Get
                Return _statusMessage
            End Get
            Private Set(value As String)
                SetProperty(_statusMessage, value)
            End Set
        End Property

        Public Property HintText As String
            Get
                Return _hintText
            End Get
            Private Set(value As String)
                SetProperty(_hintText, value)
            End Set
        End Property

        Public Property CurrentRateText As String
            Get
                Return _currentRateText
            End Get
            Private Set(value As String)
                SetProperty(_currentRateText, value)
            End Set
        End Property

        Public Property PeakRateText As String
            Get
                Return _peakRateText
            End Get
            Private Set(value As String)
                SetProperty(_peakRateText, value)
            End Set
        End Property

        Public Property WindowJitterText As String
            Get
                Return _windowJitterText
            End Get
            Private Set(value As String)
                SetProperty(_windowJitterText, value)
            End Set
        End Property

        Public Property CaptureLatencyP95Text As String
            Get
                Return _captureLatencyP95Text
            End Get
            Private Set(value As String)
                SetProperty(_captureLatencyP95Text, value)
            End Set
        End Property

        Public Property DroppedPacketCountText As String
            Get
                Return _droppedPacketCountText
            End Get
            Private Set(value As String)
                SetProperty(_droppedPacketCountText, value)
            End Set
        End Property

        Public Property StartButtonText As String
            Get
                Return _startButtonText
            End Get
            Private Set(value As String)
                SetProperty(_startButtonText, value)
            End Set
        End Property

        Public Property SelectedDeviceTitle As String
            Get
                Return _selectedDeviceTitle
            End Get
            Private Set(value As String)
                SetProperty(_selectedDeviceTitle, value)
            End Set
        End Property

        Public Property SelectedDeviceMetaText As String
            Get
                Return _selectedDeviceMetaText
            End Get
            Private Set(value As String)
                SetProperty(_selectedDeviceMetaText, value)
            End Set
        End Property

        Public Property SelectedDevicePathText As String
            Get
                Return _selectedDevicePathText
            End Get
            Private Set(value As String)
                SetProperty(_selectedDevicePathText, value)
            End Set
        End Property

        Public Property IsLocked As Boolean Implements ICaptureSessionPageViewModel.IsLocked
            Get
                Return _isLocked
            End Get
            Private Set(value As Boolean)
                If SetProperty(_isLocked, value) Then
                    RaisePropertyChanged(NameOf(IsDeviceSelectionEnabled))
                    RaisePropertyChanged(NameOf(IsRateModeSwitchEnabled))
                End If
            End Set
        End Property

        Public ReadOnly Property IsDeviceSelectionEnabled As Boolean
            Get
                Return Not IsLocked
            End Get
        End Property

        Public ReadOnly Property IsRateModeSwitchEnabled As Boolean
            Get
                Return Not IsLocked AndAlso Not _hasSessionData
            End Get
        End Property

        Public Sub SetPageActive(isActive As Boolean)
            If _isPageActive = isActive Then
                Return
            End If

            _isPageActive = isActive
        End Sub

        Public Sub ResetToDefaultState() Implements INavigationResettablePageViewModel.ResetToDefaultState
            UpdatePollingUiRenderingSubscription(forceDetach:=True)
            _statusRestoreTimer.Stop()

            _captureService.StopSession()
            _captureService.SetMode(PollingRateMode.RawPacketRate)
            _captureService.ResetStatistics()

            _selectedPollingRateMode = PollingRateMode.RawPacketRate
            SetHasSessionData(False)
            _sessionDeviceId = Nothing
            _pendingDeviceId = Nothing
            _pendingSessionReset = False
            _clearSelectionOnNextRefresh = False
            _latestMetricsSnapshot = Nothing
            _latestChartRenderFrame = Nothing
            _restoreStatusScenario = StatusScenario.Ready

            SetMetricsToPlaceholder()
            UpdateRateModeDescriptionText()
            RaisePropertyChanged(NameOf(IsRawPacketRateMode))

            SelectedDevice = Nothing
            RefreshDevices(True)
            UpdateStartButtonText()
            RaiseCanExecuteChanges()
        End Sub

        Public ReadOnly Property ToggleLanguageCommand As DelegateCommand
            Get
                Return _toggleLanguageCommand
            End Get
        End Property

        Public ReadOnly Property RefreshDevicesCommand As DelegateCommand
            Get
                Return _refreshDevicesCommand
            End Get
        End Property

        Public ReadOnly Property ToggleThemeCommand As DelegateCommand
            Get
                Return _toggleThemeCommand
            End Get
        End Property

        Public ReadOnly Property StartCommand As DelegateCommand
            Get
                Return _startCommand
            End Get
        End Property

        Public ReadOnly Property StopCommand As DelegateCommand
            Get
                Return _stopCommand
            End Get
        End Property

        Public Sub OnLockEntered() Implements ICaptureSessionPageViewModel.OnLockEntered
            Dim deviceId = _pendingDeviceId
            If String.IsNullOrWhiteSpace(deviceId) Then
                Return
            End If

            If _pendingSessionReset Then
                _captureService.ResetStatistics()
                SetHasSessionData(True)
                _latestMetricsSnapshot = Nothing
            End If

            _sessionDeviceId = deviceId
            _captureService.BeginSession(deviceId)
            IsLocked = True
            UpdatePollingUiRenderingSubscription()

            ApplyLatestBufferedMetricsSnapshot()
            ApplyLatestBufferedChartRenderFrame()
            ApplyStatusScenario(StatusScenario.Measuring)
            UpdateStartButtonText()
            RaiseCanExecuteChanges()
        End Sub

        Public Sub RequestPauseFromView() Implements ICaptureSessionPageViewModel.RequestPauseFromView
            If Not IsLocked Then
                Return
            End If

            RaiseEvent ExitLockRequested(Me, New CaptureLockRequestEventArgs(CaptureUnlockReason.PauseSession))
        End Sub

        Public Sub OnViewUnlockCompleted(reason As CaptureUnlockReason) Implements ICaptureSessionPageViewModel.OnViewUnlockCompleted
            UpdatePollingUiRenderingSubscription(forceDetach:=True)
            IsLocked = False

            Select Case reason
                Case CaptureUnlockReason.ClearSession
                    _captureService.StopSession()
                    ClearSession()
                Case CaptureUnlockReason.DeviceDisconnected
                    _captureService.StopSession()
                    _clearSelectionOnNextRefresh = True
                    RefreshDevices(False)
                    ApplyStatusScenario(StatusScenario.DeviceDisconnected)
                Case Else
                    _captureService.PauseSession()
                    ApplyLatestBufferedMetricsSnapshot()
                    ApplyLatestBufferedChartRenderFrame()
                    ApplyStatusScenario(StatusScenario.Paused)
            End Select

            UpdateStartButtonText()
            RaiseCanExecuteChanges()
        End Sub

        Private Function CanStart() As Boolean
            Return SelectedDevice IsNot Nothing AndAlso Not IsLocked
        End Function

        Private Function CanStop() As Boolean
            Return IsLocked OrElse _hasSessionData
        End Function

        Private Function CanRefreshDevices() As Boolean
            Return Not IsLocked
        End Function

        Private Sub RequestStart()
            If SelectedDevice Is Nothing Then
                ApplyStatusScenario(StatusScenario.NeedDevice)
                Return
            End If

            _pendingDeviceId = SelectedDevice.DeviceId
            _pendingSessionReset = Not _hasSessionData OrElse Not String.Equals(_sessionDeviceId, _pendingDeviceId, StringComparison.OrdinalIgnoreCase)
            RaiseEvent EnterLockRequested(Me, EventArgs.Empty)
        End Sub

        Private Sub RequestStop()
            If IsLocked Then
                RaiseEvent ExitLockRequested(Me, New CaptureLockRequestEventArgs(CaptureUnlockReason.ClearSession))
                Return
            End If

            _captureService.StopSession()
            ClearSession()
        End Sub

        Private Sub RequestToggleLanguage()
            Dim nextLanguage = GetNextLanguage()
            If nextLanguage Is Nothing Then
                Return
            End If

            SelectedLanguage = nextLanguage
        End Sub

        Private Sub RequestToggleTheme()
            _themeManager.ToggleTheme()
        End Sub

        Private Sub OnPollingUiRendering(sender As Object, e As EventArgs)
            If _disposed OrElse Not IsLocked Then
                UpdatePollingUiRenderingSubscription(forceDetach:=True)
                Return
            End If

            Dim metricsSnapshot As PollingMetricsSnapshot = Nothing
            If _captureService.TryReadLatestMetricsSnapshot(metricsSnapshot) AndAlso Not ReferenceEquals(metricsSnapshot, _latestMetricsSnapshot) Then
                _latestMetricsSnapshot = metricsSnapshot
                ApplyMetricsSnapshot(metricsSnapshot)
            End If

            Dim chartRenderFrame As PollingChartRenderFrame = Nothing
            If _captureService.TryReadLatestChartRenderFrame(chartRenderFrame) AndAlso Not ReferenceEquals(chartRenderFrame, _latestChartRenderFrame) Then
                _latestChartRenderFrame = chartRenderFrame
                ApplyChartRenderFrame(chartRenderFrame)
            End If
        End Sub

        Private Sub UpdatePollingUiRenderingSubscription(Optional forceDetach As Boolean = False)
            Dim shouldRender = Not forceDetach AndAlso Not _disposed AndAlso IsLocked

            If Not shouldRender Then
                If _isPollingUiRenderingSubscribed Then
                    RemoveHandler CompositionTarget.Rendering, AddressOf OnPollingUiRendering
                    _isPollingUiRenderingSubscribed = False
                End If

                Return
            End If

            If Not _isPollingUiRenderingSubscribed Then
                AddHandler CompositionTarget.Rendering, AddressOf OnPollingUiRendering
                _isPollingUiRenderingSubscribed = True
            End If
        End Sub

        Private Sub OnDevicesChanged(sender As Object, e As EventArgs)
            RunOnUiThread(Sub() RefreshDevices(True))
        End Sub

        Private Sub OnSelectedDeviceDisconnected(sender As Object, e As EventArgs)
            RunOnUiThread(
                Sub()
                    If IsLocked Then
                        RaiseEvent ExitLockRequested(Me, New CaptureLockRequestEventArgs(CaptureUnlockReason.DeviceDisconnected))
                    Else
                        _captureService.StopSession()
                        _clearSelectionOnNextRefresh = True
                        RefreshDevices(False)
                        ApplyStatusScenario(StatusScenario.DeviceDisconnected)
                        UpdateStartButtonText()
                        RaiseCanExecuteChanges()
                    End If
                End Sub)
        End Sub

        Private Sub OnLanguageChanged(sender As Object, e As EventArgs)
            RunOnUiThread(
                Sub()
                    UpdateSelectedLanguageSelection()
                    UpdateLanguageToggleText()
                    UpdateThemeToggleText()
                    UpdateRateModeDescriptionText()
                    UpdateSelectedDeviceSummary()
                    UpdateStartButtonText()
                    ApplyStatusScenario(_statusScenario)
                End Sub)
        End Sub

        Private Sub OnThemeChanged(sender As Object, e As EventArgs)
            RunOnUiThread(Sub() UpdateThemeToggleText())
        End Sub

        Private Sub RequestRefreshDevices()
            If Not CanRefreshDevices() Then
                Return
            End If

            RefreshDevices(False)
            ShowRefreshFeedback()
        End Sub

        Private Sub RefreshDevices(updateStatus As Boolean)
            Dim deviceList = _captureService.GetDevices()
            Dim previousSelectedId As String = Nothing

            If SelectedDevice IsNot Nothing Then
                previousSelectedId = SelectedDevice.DeviceId
            End If

            _devices.Clear()
            For Each deviceInfo In deviceList
                _devices.Add(deviceInfo)
            Next

            Dim nextSelection As RawMouseDeviceInfo = Nothing
            If _clearSelectionOnNextRefresh Then
                _clearSelectionOnNextRefresh = False
            ElseIf Not String.IsNullOrWhiteSpace(previousSelectedId) Then
                nextSelection = _devices.FirstOrDefault(Function(item) String.Equals(item.DeviceId, previousSelectedId, StringComparison.OrdinalIgnoreCase))
            ElseIf _devices.Count > 0 AndAlso String.IsNullOrWhiteSpace(_sessionDeviceId) Then
                nextSelection = _devices(0)
            End If

            SelectedDevice = nextSelection

            If updateStatus Then
                If _devices.Count = 0 Then
                    ApplyStatusScenario(StatusScenario.NoDevices)
                ElseIf Not _hasSessionData AndAlso Not IsLocked Then
                    ApplyStatusScenario(StatusScenario.Ready)
                End If
            End If

            RaiseCanExecuteChanges()
        End Sub

        Private Sub ApplyMetricsSnapshot(metricsSnapshot As PollingMetricsSnapshot, Optional forceMetricTextUpdate As Boolean = False)
            If metricsSnapshot Is Nothing Then
                Return
            End If

            CurrentRate = metricsSnapshot.CurrentRate
            PeakRate = metricsSnapshot.PeakRate
            WindowJitterMs = metricsSnapshot.WindowJitterMs
            CaptureLatencyP95Ms = metricsSnapshot.CaptureLatencyP95Ms
            DroppedPacketCount = metricsSnapshot.DroppedPacketCount

            UpdateCurrentRateText(metricsSnapshot.CurrentRate, forceMetricTextUpdate)
            UpdateWindowJitterText(metricsSnapshot.WindowJitterMs, forceMetricTextUpdate)
            PeakRateText = metricsSnapshot.PeakRate.ToString(CultureInfo.InvariantCulture)
            DroppedPacketCountText = metricsSnapshot.DroppedPacketCount.ToString(CultureInfo.InvariantCulture)

            If metricsSnapshot.CaptureLatencyP95Ms.HasValue Then
                CaptureLatencyP95Text = metricsSnapshot.CaptureLatencyP95Ms.Value.ToString("0.00", CultureInfo.InvariantCulture)
            Else
                CaptureLatencyP95Text = "--"
            End If
        End Sub

        Private Sub ApplyChartRenderFrame(chartRenderFrame As PollingChartRenderFrame)
            If chartRenderFrame Is Nothing Then
                Return
            End If

            RawCurrentRate = chartRenderFrame.RawCurrentRate
            HistoryPoints = chartRenderFrame.HistoryPoints
        End Sub

        Private Sub ApplyLatestBufferedMetricsSnapshot()
            Dim metricsSnapshot As PollingMetricsSnapshot = Nothing
            If Not _captureService.TryReadLatestMetricsSnapshot(metricsSnapshot) Then
                Return
            End If

            _latestMetricsSnapshot = metricsSnapshot
            ApplyMetricsSnapshot(metricsSnapshot, forceMetricTextUpdate:=True)
        End Sub

        Private Sub ApplyLatestBufferedChartRenderFrame()
            Dim chartRenderFrame As PollingChartRenderFrame = Nothing
            If Not _captureService.TryReadLatestChartRenderFrame(chartRenderFrame) Then
                Return
            End If

            _latestChartRenderFrame = chartRenderFrame
            ApplyChartRenderFrame(chartRenderFrame)
        End Sub

        Private Sub SetMetricsToPlaceholder()
            RawCurrentRate = 0.0
            CurrentRate = 0
            PeakRate = 0
            WindowJitterMs = Nothing
            CaptureLatencyP95Ms = Nothing
            DroppedPacketCount = 0
            HistoryPoints = New List(Of PollingHistoryPoint)()

            CurrentRateText = "--"
            PeakRateText = "--"
            WindowJitterText = "--"
            CaptureLatencyP95Text = "--"
            DroppedPacketCountText = "--"
            ResetMetricTextState(_currentRateTextState)
            ResetMetricTextState(_windowJitterTextState)
        End Sub

        Private Sub UpdateCurrentRateText(targetRate As Integer, forceUpdate As Boolean)
            If Not ShouldUpdateMetricText(_currentRateTextState, forceUpdate) Then
                Return
            End If

            CurrentRateText = Math.Max(0, targetRate).ToString(CultureInfo.InvariantCulture)
        End Sub

        Private Sub UpdateWindowJitterText(targetJitterMs As Nullable(Of Double), forceUpdate As Boolean)
            If Not ShouldUpdateMetricText(_windowJitterTextState, forceUpdate) Then
                Return
            End If

            WindowJitterText = FormatWindowJitter(targetJitterMs)
        End Sub

        Private Function ShouldUpdateMetricText(state As MetricTextState, forceUpdate As Boolean) As Boolean
            If state Is Nothing Then
                Return False
            End If

            Dim nowMs = GetRealtimeMilliseconds()

            If forceUpdate OrElse Not state.HasDisplayedValue Then
                state.HasDisplayedValue = True
                state.LastUpdateMilliseconds = nowMs
                Return True
            End If

            If Not Double.IsNaN(state.LastUpdateMilliseconds) AndAlso
               (nowMs - state.LastUpdateMilliseconds) < MetricTextUpdateIntervalMilliseconds Then
                Return False
            End If

            state.LastUpdateMilliseconds = nowMs
            Return True
        End Function

        Private Shared Function FormatWindowJitter(value As Nullable(Of Double)) As String
            If Not value.HasValue OrElse Double.IsNaN(value.Value) OrElse Double.IsInfinity(value.Value) Then
                Return "--"
            End If

            Return value.Value.ToString("0.00", CultureInfo.InvariantCulture)
        End Function

        Private Shared Sub ResetMetricTextState(state As MetricTextState)
            If state Is Nothing Then
                Return
            End If

            state.LastUpdateMilliseconds = Double.NaN
            state.HasDisplayedValue = False
        End Sub

        Private Sub ClearSession()
            UpdatePollingUiRenderingSubscription(forceDetach:=True)
            _statusRestoreTimer.Stop()
            _captureService.ResetStatistics()
            SetHasSessionData(False)
            _sessionDeviceId = Nothing
            _pendingDeviceId = Nothing
            _pendingSessionReset = False
            _latestMetricsSnapshot = Nothing
            _latestChartRenderFrame = Nothing

            SetMetricsToPlaceholder()
            ApplyStatusScenario(StatusScenario.Cleared)
            UpdateStartButtonText()
            RaiseCanExecuteChanges()
        End Sub

        Private Sub SetHasSessionData(value As Boolean)
            If _hasSessionData = value Then
                Return
            End If

            _hasSessionData = value
            RaisePropertyChanged(NameOf(IsRateModeSwitchEnabled))
        End Sub

        Private Sub SetPollingRateMode(mode As PollingRateMode)
            If mode = _selectedPollingRateMode Then
                Return
            End If

            If Not IsRateModeSwitchEnabled Then
                RaisePropertyChanged(NameOf(IsRawPacketRateMode))
                Return
            End If

            _selectedPollingRateMode = mode
            _captureService.SetMode(mode)
            _captureService.ResetStatistics()
            _latestMetricsSnapshot = Nothing
            _latestChartRenderFrame = Nothing

            SetMetricsToPlaceholder()
            UpdateRateModeDescriptionText()
            RaisePropertyChanged(NameOf(IsRawPacketRateMode))
        End Sub

        Private Sub UpdateRateModeDescriptionText()
            If _selectedPollingRateMode = PollingRateMode.RawPacketRate Then
                RateModeDescriptionText = L("Control.ReportMode.Description.RawPacket")
            Else
                RateModeDescriptionText = L("Control.ReportMode.Description.MotionReport")
            End If
        End Sub

        Private Sub UpdateSelectedDeviceSummary()
            If SelectedDevice Is Nothing Then
                SelectedDeviceTitle = L("Device.None.Title")
                SelectedDeviceMetaText = L("Device.None.Meta")
                SelectedDevicePathText = "--"
                Return
            End If

            SelectedDeviceTitle = SelectedDevice.DisplayName

            Dim details As New List(Of String)()
            If Not String.IsNullOrWhiteSpace(SelectedDevice.VendorProductLabel) Then
                details.Add(SelectedDevice.VendorProductLabel)
            End If

            If SelectedDevice.ButtonCount > 0 Then
                details.Add(L("Device.Detail.Buttons", SelectedDevice.ButtonCount))
            End If

            If SelectedDevice.IsVirtual Then
                details.Add(L("Device.Detail.Virtual"))
            End If

            If details.Count = 0 Then
                SelectedDeviceMetaText = L("Device.Detail.RawInput")
            Else
                SelectedDeviceMetaText = String.Join("  ·  ", details)
            End If

            SelectedDevicePathText = SelectedDevice.PathSummary
        End Sub

        Private Sub UpdateSelectedLanguageSelection()
            Dim currentLanguage = _localization.CurrentLanguage
            If currentLanguage Is Nothing Then
                Return
            End If

            If _selectedLanguage Is Nothing OrElse Not String.Equals(_selectedLanguage.CultureName, currentLanguage.CultureName, StringComparison.OrdinalIgnoreCase) Then
                _selectedLanguage = currentLanguage
                RaisePropertyChanged(NameOf(SelectedLanguage))
            End If
        End Sub

        Private Sub UpdateLanguageToggleText()
            Dim nextLanguage = GetNextLanguage()
            If nextLanguage Is Nothing Then
                LanguageToggleText = String.Empty
                LanguageToggleKeyText = String.Empty
                Return
            End If

            If nextLanguage.CultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase) Then
                LanguageToggleText = L("Language.Toggle.ToChinese")
                LanguageToggleKeyText = LanguageToggleText
                Return
            End If

            If nextLanguage.CultureName.StartsWith("en", StringComparison.OrdinalIgnoreCase) Then
                LanguageToggleText = L("Language.Toggle.ToEnglish")
                LanguageToggleKeyText = LanguageToggleText
                Return
            End If

            LanguageToggleText = nextLanguage.NativeName
            LanguageToggleKeyText = nextLanguage.CultureName.Split("-"c)(0).ToUpperInvariant()
        End Sub

        Private Function GetNextLanguage() As LanguageOption
            If _languageOptions.Count = 0 Then
                Return Nothing
            End If

            Dim currentLanguage = If(_selectedLanguage, _localization.CurrentLanguage)
            If currentLanguage Is Nothing Then
                Return _languageOptions(0)
            End If

            Dim currentIndex = -1
            For index = 0 To _languageOptions.Count - 1
                If String.Equals(_languageOptions(index).CultureName, currentLanguage.CultureName, StringComparison.OrdinalIgnoreCase) Then
                    currentIndex = index
                    Exit For
                End If
            Next

            If currentIndex < 0 Then
                Return _languageOptions(0)
            End If

            Return _languageOptions((currentIndex + 1) Mod _languageOptions.Count)
        End Function

        Private Sub UpdateThemeToggleText()
            Dim nextTheme = GetNextTheme()

            If nextTheme = AppTheme.Light Then
                ThemeToggleText = L("Theme.Toggle.ToLight")
                ThemeToggleKeyText = L("Theme.Toggle.KeyLight")
                Return
            End If

            ThemeToggleText = L("Theme.Toggle.ToDark")
            ThemeToggleKeyText = L("Theme.Toggle.KeyDark")
        End Sub

        Private Function GetNextTheme() As AppTheme
            If _themeManager.CurrentTheme = AppTheme.Dark Then
                Return AppTheme.Light
            End If

            Return AppTheme.Dark
        End Function

        Private Sub UpdateStartButtonText()
            If IsLocked Then
                StartButtonText = L("StartButton.Running")
                Return
            End If

            If Not _hasSessionData OrElse SelectedDevice Is Nothing Then
                StartButtonText = L("StartButton.Start")
                Return
            End If

            If String.Equals(_sessionDeviceId, SelectedDevice.DeviceId, StringComparison.OrdinalIgnoreCase) Then
                StartButtonText = L("StartButton.Continue")
            Else
                StartButtonText = L("StartButton.NewSession")
            End If
        End Sub

        Private Sub ApplyStatusScenario(scenario As StatusScenario)
            If scenario <> StatusScenario.RefreshFeedback AndAlso _statusRestoreTimer.IsEnabled Then
                _statusRestoreTimer.Stop()
            End If

            _statusScenario = scenario

            Select Case scenario
                Case StatusScenario.Ready
                    SetStatus(L("Status.Ready.Pill"),
                              L("Status.Ready.Message"),
                              L("Status.Ready.Hint"))
                Case StatusScenario.Measuring
                    SetStatus(L("Status.Measuring.Pill"),
                              L("Status.Measuring.Message"),
                              L("Status.Measuring.Hint"))
                Case StatusScenario.Paused
                    SetStatus(L("Status.Paused.Pill"),
                              L("Status.Paused.Message"),
                              L("Status.Paused.Hint"))
                Case StatusScenario.Cleared
                    SetStatus(L("Status.Cleared.Pill"),
                              L("Status.Cleared.Message"),
                              L("Status.Cleared.Hint"))
                Case StatusScenario.NeedDevice
                    SetStatus(L("Status.NeedDevice.Pill"),
                              L("Status.NeedDevice.Message"),
                              L("Status.NeedDevice.Hint"))
                Case StatusScenario.DeviceDisconnected
                    SetStatus(L("Status.DeviceDisconnected.Pill"),
                              L("Status.DeviceDisconnected.Message"),
                              L("Status.DeviceDisconnected.Hint"))
                Case StatusScenario.NoDevices
                    SetStatus(L("Status.NoDevices.Pill"),
                              L("Status.NoDevices.Message"),
                              L("Status.NoDevices.Hint"))
                Case StatusScenario.RefreshFeedback
                    Dim detailMessage As String
                    If Devices.Count = 0 Then
                        detailMessage = L("Status.Refresh.Message.None")
                    Else
                        detailMessage = L("Status.Refresh.Message.Count", Devices.Count)
                    End If

                    Dim hint As String
                    If SelectedDevice Is Nothing Then
                        hint = L("Status.Refresh.Hint.NoSelection")
                    Else
                        hint = L("Status.Refresh.Hint.Selected", SelectedDevice.DisplayName)
                    End If

                    SetStatus(L("Status.Refresh.Pill"), detailMessage, hint)
            End Select
        End Sub

        Private Sub SetStatus(pill As String, message As String, hint As String)
            StatusPillText = pill
            StatusMessage = message
            HintText = hint
        End Sub

        Private Sub ShowRefreshFeedback()
            Dim restoreScenario = _statusScenario
            If _statusRestoreTimer.IsEnabled Then
                restoreScenario = _restoreStatusScenario
                _statusRestoreTimer.Stop()
            End If

            _restoreStatusScenario = restoreScenario
            ApplyStatusScenario(StatusScenario.RefreshFeedback)
            _statusRestoreTimer.Start()
        End Sub

        Private Sub OnStatusRestoreTimerTick(sender As Object, e As EventArgs)
            _statusRestoreTimer.Stop()
            ApplyStatusScenario(_restoreStatusScenario)
        End Sub

        Private Sub RaiseCanExecuteChanges()
            _refreshDevicesCommand.RaiseCanExecuteChanged()
            _startCommand.RaiseCanExecuteChanged()
            _stopCommand.RaiseCanExecuteChanged()
        End Sub

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

            UpdatePollingUiRenderingSubscription(forceDetach:=True)
            _statusRestoreTimer.Stop()
            RemoveHandler _statusRestoreTimer.Tick, AddressOf OnStatusRestoreTimerTick
            RemoveHandler _captureService.DevicesChanged, AddressOf OnDevicesChanged
            RemoveHandler _captureService.SelectedDeviceDisconnected, AddressOf OnSelectedDeviceDisconnected
            RemoveHandler _localization.LanguageChanged, AddressOf OnLanguageChanged
            RemoveHandler _themeManager.ThemeChanged, AddressOf OnThemeChanged
            _captureService.Dispose()
        End Sub

        Private Shared Function GetRealtimeMilliseconds() As Double
            Return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency
        End Function
    End Class
End Namespace
