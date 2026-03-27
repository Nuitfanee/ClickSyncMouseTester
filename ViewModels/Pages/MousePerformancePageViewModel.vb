Imports System.Collections.ObjectModel
Imports System.Diagnostics
Imports System.Globalization
Imports System.Runtime.Versioning
Imports System.Threading
Imports System.Windows
Imports System.Windows.Input
Imports System.Windows.Threading
Imports WpfApp1.Infrastructure
Imports WpfApp1.Models
Imports WpfApp1.Navigation
Imports WpfApp1.Services

Namespace ViewModels.Pages
    <SupportedOSPlatform("windows")>
    Public Class MousePerformancePageViewModel
        Inherits BindableBase
        Implements IDisposable, ICaptureSessionPageViewModel, INavigationResettablePageViewModel

        Private Enum StatusScenario
            Ready
            NeedDevice
            Collecting
            Paused
            NoDevice
            DeviceDisconnected
        End Enum

        Private Const DefaultCpiValue As Double = 800.0
        Private Const UiRefreshIntervalMilliseconds As Double = 1000.0 / 60.0
        Private Const EscapeVirtualKey As Integer = &H1B

        Private ReadOnly _dispatcher As Dispatcher
        Private ReadOnly _localization As LocalizationManager
        Private ReadOnly _preferencesStore As IMousePerformancePreferencesStore
        Private ReadOnly _rawInputBroker As IRawInputBroker
        Private ReadOnly _captureService As MousePerformanceCaptureService
        Private ReadOnly _uiTimer As DispatcherTimer
        Private ReadOnly _devices As ObservableCollection(Of RawMouseDeviceInfo)
        Private ReadOnly _collectCommand As DelegateCommand
        Private ReadOnly _resetCommand As DelegateCommand
        Private ReadOnly _plotCommand As DelegateCommand

        Private _selectedDevice As RawMouseDeviceInfo
        Private _selectedDeviceTitle As String
        Private _selectedDeviceMetaText As String
        Private _selectedDevicePathText As String
        Private _cpiText As String
        Private _effectiveCpiValue As Double
        Private _statusPillText As String
        Private _statusMessage As String
        Private _hintText As String
        Private _qualityText As String
        Private _eventCountText As String
        Private _sumXCountText As String
        Private _sumXCmText As String
        Private _sumYCountText As String
        Private _sumYCmText As String
        Private _pathCountText As String
        Private _pathCmText As String
        Private _isLocked As Boolean
        Private _isPlotHighlighted As Boolean
        Private _isPageActive As Boolean
        Private _isChartWindowAttached As Boolean
        Private _plotOpenRequestVersion As Integer
        Private _chartWindowCloseRequestVersion As Integer
        Private _latestSnapshot As MousePerformanceSnapshot
        Private _latestChartSnapshot As MousePerformanceSnapshot
        Private _chartRefreshIntervalTicks As Long
        Private _lastChartSnapshotPushTicks As Long
        Private _pendingDeviceId As String
        Private _pendingStartFresh As Boolean
        Private _pauseGesturePending As Integer
        Private _disposed As Boolean

        Public Sub New(rawInputBroker As IRawInputBroker,
                       Optional preferencesStore As IMousePerformancePreferencesStore = Nothing)
            If Application.Current IsNot Nothing Then
                _dispatcher = Application.Current.Dispatcher
            Else
                _dispatcher = Dispatcher.CurrentDispatcher
            End If

            If rawInputBroker Is Nothing Then
                Throw New ArgumentNullException(NameOf(rawInputBroker))
            End If

            _localization = LocalizationManager.Instance
            _preferencesStore = If(preferencesStore, MousePerformancePreferencesStore.Instance)
            _localization.Initialize()
            _rawInputBroker = rawInputBroker
            _captureService = New MousePerformanceCaptureService(rawInputBroker)
            _uiTimer = New DispatcherTimer(DispatcherPriority.Render, _dispatcher)
            _uiTimer.Interval = TimeSpan.FromMilliseconds(UiRefreshIntervalMilliseconds)
            _devices = New ObservableCollection(Of RawMouseDeviceInfo)()
            _collectCommand = New DelegateCommand(AddressOf RequestCollect, AddressOf CanCollect)
            _resetCommand = New DelegateCommand(AddressOf RequestReset, AddressOf CanReset)
            _plotCommand = New DelegateCommand(AddressOf RequestPlot, AddressOf CanPlot)
            _chartRefreshIntervalTicks = CLng(Math.Max(1.0, Stopwatch.Frequency / _captureService.AnalysisOptions.ChartRefreshMaxHz))
            _effectiveCpiValue = ResolveStoredCpiOrDefault(_preferencesStore.LoadPreferences())
            _cpiText = FormatCpi(_effectiveCpiValue)

            AddHandler _uiTimer.Tick, AddressOf OnUiTimerTick
            AddHandler _localization.LanguageChanged, AddressOf OnLanguageChanged
            AddHandler _captureService.DevicesChanged, AddressOf OnDevicesChanged
            AddHandler _captureService.SelectedDeviceDisconnected, AddressOf OnSelectedDeviceDisconnected
            AddHandler _rawInputBroker.KeyboardInput, AddressOf OnRawKeyboardInput
            AddHandler _rawInputBroker.MouseButtonInput, AddressOf OnRawMouseButtonInput

            _captureService.SetCpiState(_effectiveCpiValue, True)
            SetSummaryPlaceholder()
            RefreshDevices()
            RefreshSnapshot(False)
        End Sub

        Friend ReadOnly Property PreferencesStore As IMousePerformancePreferencesStore
            Get
                Return _preferencesStore
            End Get
        End Property

        Public Event EnterLockRequested As EventHandler Implements ICaptureSessionPageViewModel.EnterLockRequested
        Public Event ExitLockRequested As EventHandler(Of CaptureLockRequestEventArgs) Implements ICaptureSessionPageViewModel.ExitLockRequested

        Public ReadOnly Property Devices As ObservableCollection(Of RawMouseDeviceInfo)
            Get
                Return _devices
            End Get
        End Property

        Public Property SelectedDevice As RawMouseDeviceInfo
            Get
                Return _selectedDevice
            End Get
            Set(value As RawMouseDeviceInfo)
                If SetProperty(_selectedDevice, value) Then
                    UpdateSelectedDeviceSummary()
                    RaiseCanExecuteChanges()
                    ApplyStatusScenario(ResolveStatusScenario(_latestSnapshot))
                End If
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

        Public Property CpiText As String
            Get
                Return _cpiText
            End Get
            Set(value As String)
                Dim normalizedValue = NormalizeCpiText(value)
                If SetProperty(_cpiText, normalizedValue) Then
                    UpdateCpiStateFromText()
                End If
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

        Public Property QualityText As String
            Get
                Return _qualityText
            End Get
            Private Set(value As String)
                SetProperty(_qualityText, value)
            End Set
        End Property

        Public Property EventCountText As String
            Get
                Return _eventCountText
            End Get
            Private Set(value As String)
                SetProperty(_eventCountText, value)
            End Set
        End Property

        Public Property SumXCountText As String
            Get
                Return _sumXCountText
            End Get
            Private Set(value As String)
                SetProperty(_sumXCountText, value)
            End Set
        End Property

        Public Property SumXCmText As String
            Get
                Return _sumXCmText
            End Get
            Private Set(value As String)
                SetProperty(_sumXCmText, value)
            End Set
        End Property

        Public Property SumYCountText As String
            Get
                Return _sumYCountText
            End Get
            Private Set(value As String)
                SetProperty(_sumYCountText, value)
            End Set
        End Property

        Public Property SumYCmText As String
            Get
                Return _sumYCmText
            End Get
            Private Set(value As String)
                SetProperty(_sumYCmText, value)
            End Set
        End Property

        Public Property PathCountText As String
            Get
                Return _pathCountText
            End Get
            Private Set(value As String)
                SetProperty(_pathCountText, value)
            End Set
        End Property

        Public Property PathCmText As String
            Get
                Return _pathCmText
            End Get
            Private Set(value As String)
                SetProperty(_pathCmText, value)
            End Set
        End Property

        Public Property IsLocked As Boolean Implements ICaptureSessionPageViewModel.IsLocked
            Get
                Return _isLocked
            End Get
            Private Set(value As Boolean)
                If SetProperty(_isLocked, value) Then
                    RaisePropertyChanged(NameOf(IsDeviceSelectionEnabled))
                    RaisePropertyChanged(NameOf(AreActionButtonsEnabled))
                    UpdatePlotHighlightState()
                    RaiseCanExecuteChanges()
                    UpdateUiTimer()
                End If
            End Set
        End Property

        Public ReadOnly Property IsDeviceSelectionEnabled As Boolean
            Get
                Return Not IsLocked
            End Get
        End Property

        Public ReadOnly Property AreActionButtonsEnabled As Boolean
            Get
                Return Not IsLocked
            End Get
        End Property

        Public ReadOnly Property IsPlotHighlighted As Boolean
            Get
                Return _isPlotHighlighted
            End Get
        End Property

        Public Property LatestChartSnapshot As MousePerformanceSnapshot
            Get
                Return _latestChartSnapshot
            End Get
            Private Set(value As MousePerformanceSnapshot)
                SetProperty(_latestChartSnapshot, value)
            End Set
        End Property

        Public Property PlotOpenRequestVersion As Integer
            Get
                Return _plotOpenRequestVersion
            End Get
            Private Set(value As Integer)
                SetProperty(_plotOpenRequestVersion, value)
            End Set
        End Property

        Public Property ChartWindowCloseRequestVersion As Integer
            Get
                Return _chartWindowCloseRequestVersion
            End Get
            Private Set(value As Integer)
                SetProperty(_chartWindowCloseRequestVersion, value)
            End Set
        End Property

        Public ReadOnly Property CollectCommand As DelegateCommand
            Get
                Return _collectCommand
            End Get
        End Property

        Public ReadOnly Property ResetCommand As DelegateCommand
            Get
                Return _resetCommand
            End Get
        End Property

        Public ReadOnly Property PlotCommand As DelegateCommand
            Get
                Return _plotCommand
            End Get
        End Property

        Public Sub SetPageActive(isActive As Boolean)
            If _isPageActive = isActive Then
                Return
            End If

            _isPageActive = isActive
            If isActive Then
                RefreshDevices()
                RefreshSnapshot(_isChartWindowAttached)
            Else
                _uiTimer.Stop()
            End If

            UpdateUiTimer()
        End Sub

        Public Sub SetChartWindowAttached(isAttached As Boolean)
            If _isChartWindowAttached = isAttached Then
                Return
            End If

            _isChartWindowAttached = isAttached
            If isAttached Then
                RefreshSnapshot(True)
            End If
        End Sub

        Public Sub CommitCpiInput()
            Dim parsedValue = 0.0
            If TryParsePositiveCpi(_cpiText, parsedValue) Then
                _effectiveCpiValue = parsedValue
                _preferencesStore.SaveLastCpi(parsedValue)
            Else
                SetProperty(_cpiText, FormatCpi(_effectiveCpiValue), NameOf(CpiText))
            End If

            _captureService.SetCpiState(_effectiveCpiValue, True)
            RefreshSnapshot(_isChartWindowAttached)
        End Sub

        Public Sub ResetToDefaultState() Implements INavigationResettablePageViewModel.ResetToDefaultState
            _uiTimer.Stop()
            _captureService.ResetSession()
            _pendingDeviceId = Nothing
            _pendingStartFresh = False
            _isPageActive = False
            _isChartWindowAttached = False
            IsLocked = False

            _effectiveCpiValue = ResolveStoredCpiOrDefault(_preferencesStore.LoadPreferences())
            _captureService.SetCpiState(_effectiveCpiValue, True)
            SelectedDevice = Nothing
            SetProperty(_cpiText, FormatCpi(_effectiveCpiValue), NameOf(CpiText))
            _lastChartSnapshotPushTicks = 0L
            RaiseChartWindowCloseRequest()
            RefreshDevices()
            RefreshSnapshot(False)
        End Sub

        Public Sub OnLockEntered() Implements ICaptureSessionPageViewModel.OnLockEntered
            If String.IsNullOrWhiteSpace(_pendingDeviceId) Then
                Return
            End If

            If Not _captureService.BeginSession(_pendingDeviceId, _pendingStartFresh) Then
                IsLocked = False
                RefreshDevices()
                RefreshSnapshot(_isChartWindowAttached)
                RaiseEvent ExitLockRequested(Me, New CaptureLockRequestEventArgs(CaptureUnlockReason.PauseSession))
                Return
            End If

            IsLocked = True
            _pendingStartFresh = False
            RefreshSnapshot(_isChartWindowAttached)
        End Sub

        Public Sub RequestPauseFromView() Implements ICaptureSessionPageViewModel.RequestPauseFromView
            If Not IsLocked Then
                Return
            End If

            RaiseEvent ExitLockRequested(Me, New CaptureLockRequestEventArgs(CaptureUnlockReason.PauseSession))
        End Sub

        Public Sub OnViewUnlockCompleted(reason As CaptureUnlockReason) Implements ICaptureSessionPageViewModel.OnViewUnlockCompleted
            IsLocked = False
            Interlocked.Exchange(_pauseGesturePending, 0)

            Select Case reason
                Case CaptureUnlockReason.ClearSession
                    _captureService.ResetSession()
                    _isChartWindowAttached = False
                    RaiseChartWindowCloseRequest()
                Case CaptureUnlockReason.DeviceDisconnected
                    ' The capture service already preserves the session and marks the disconnect state.
                Case Else
                    _captureService.PauseSession()
            End Select

            RefreshDevices()
            RefreshSnapshot(_isChartWindowAttached)
        End Sub

        Private Sub RequestCollect()
            If Not CanCollect() Then
                Return
            End If

            If SelectedDevice Is Nothing OrElse String.IsNullOrWhiteSpace(SelectedDevice.DeviceId) Then
                ApplyStatusScenario(ResolveStatusScenario(_latestSnapshot))
                Return
            End If

            Dim canResume = _latestSnapshot IsNot Nothing AndAlso
                            _latestSnapshot.CanContinue AndAlso
                            String.Equals(_latestSnapshot.SessionDeviceId, SelectedDevice.DeviceId, StringComparison.OrdinalIgnoreCase)

            _pendingDeviceId = SelectedDevice.DeviceId
            _pendingStartFresh = Not canResume
            RaiseEvent EnterLockRequested(Me, EventArgs.Empty)
        End Sub

        Private Function CanCollect() As Boolean
            Return Not IsLocked AndAlso SelectedDevice IsNot Nothing
        End Function

        Private Sub RequestReset()
            If IsLocked Then
                RaiseEvent ExitLockRequested(Me, New CaptureLockRequestEventArgs(CaptureUnlockReason.ClearSession))
                Return
            End If

            _captureService.ResetSession()
            _isChartWindowAttached = False
            RaiseChartWindowCloseRequest()
            RefreshDevices()
            RefreshSnapshot(False)
        End Sub

        Private Function CanReset() As Boolean
            Return IsLocked OrElse (_latestSnapshot IsNot Nothing AndAlso _latestSnapshot.HasData)
        End Function

        Private Sub RequestPlot()
            If Not CanPlot() Then
                Return
            End If

            RefreshSnapshot(True)
            PlotOpenRequestVersion += 1
        End Sub

        Private Function CanPlot() As Boolean
            Return _latestSnapshot IsNot Nothing AndAlso _latestSnapshot.HasData
        End Function

        Private Sub RefreshDevices()
            Dim previousSelectedId = If(SelectedDevice?.DeviceId, String.Empty)
            Dim devices = _captureService.GetDevices()

            _devices.Clear()
            If devices IsNot Nothing Then
                For Each device In devices
                    If device IsNot Nothing Then
                        _devices.Add(device)
                    End If
                Next
            End If

            Dim nextSelection As RawMouseDeviceInfo = Nothing
            If Not String.IsNullOrWhiteSpace(previousSelectedId) Then
                nextSelection = _devices.FirstOrDefault(Function(item) String.Equals(item.DeviceId, previousSelectedId, StringComparison.OrdinalIgnoreCase))
            End If

            If nextSelection Is Nothing AndAlso _devices.Count > 0 Then
                nextSelection = _devices(0)
            End If

            SelectedDevice = nextSelection
            RaiseCanExecuteChanges()
        End Sub

        Private Sub RefreshSnapshot(includeEvents As Boolean)
            Dim snapshot = _captureService.CaptureSnapshot(False)
            _latestSnapshot = snapshot
            UpdatePlotHighlightState()
            ApplySnapshot(snapshot)

            If includeEvents OrElse _isChartWindowAttached Then
                RefreshChartSnapshot(snapshot, includeEvents)
            End If
        End Sub

        Private Sub ApplySnapshot(snapshot As MousePerformanceSnapshot)
            If snapshot Is Nothing Then
                SetSummaryPlaceholder()
                ApplyStatusScenario(ResolveStatusScenario(Nothing))
                Return
            End If

            ApplySummary(snapshot.Summary)
            ApplyQuality(snapshot)
            ApplyStatusScenario(ResolveStatusScenario(snapshot))
            RaiseCanExecuteChanges()
        End Sub

        Private Sub ApplySummary(summary As MousePerformanceSummary)
            If summary Is Nothing Then
                SetSummaryPlaceholder()
                Return
            End If

            EventCountText = summary.EventCount.ToString(CultureInfo.InvariantCulture)
            SumXCountText = summary.SumX.ToString(CultureInfo.InvariantCulture)
            SumYCountText = summary.SumY.ToString(CultureInfo.InvariantCulture)
            PathCountText = summary.PathCounts.ToString("0", CultureInfo.InvariantCulture)
            SumXCmText = FormatDistance(summary.SumXCm)
            SumYCmText = FormatDistance(summary.SumYCm)
            PathCmText = FormatDistance(summary.PathCm)
        End Sub

        Private Sub SetSummaryPlaceholder()
            EventCountText = "--"
            SumXCountText = "--"
            SumXCmText = "--"
            SumYCountText = "--"
            SumYCmText = "--"
            PathCountText = "--"
            PathCmText = "--"
            QualityText = L("MousePerformance.Quality.Placeholder")
        End Sub

        Private Sub ApplyQuality(snapshot As MousePerformanceSnapshot)
            If snapshot Is Nothing Then
                QualityText = L("MousePerformance.Quality.Placeholder")
                Return
            End If

            Dim quality = snapshot.DataQuality
            If quality Is Nothing Then
                QualityText = L("MousePerformance.Quality.Placeholder")
                Return
            End If

            Dim qualityLevelText = ResolveQualityLevelText(quality.QualityLevel)
            QualityText = L("MousePerformance.Quality.Format",
                            snapshot.EventCount.ToString(CultureInfo.InvariantCulture),
                            quality.TotalFilteredCount.ToString(CultureInfo.InvariantCulture),
                            quality.DroppedPacketCount.ToString(CultureInfo.InvariantCulture),
                            qualityLevelText)
        End Sub

        Private Sub RefreshChartSnapshot(summarySnapshot As MousePerformanceSnapshot, forceRefresh As Boolean)
            If Not forceRefresh AndAlso Not _isChartWindowAttached Then
                Return
            End If

            If Not forceRefresh Then
                If LatestChartSnapshot IsNot Nothing AndAlso
                   LatestChartSnapshot.SessionRevision = summarySnapshot.SessionRevision Then
                    Return
                End If

                If IsLocked AndAlso Not HasChartRefreshIntervalElapsed() Then
                    Return
                End If
            End If

            LatestChartSnapshot = _captureService.CaptureSnapshot(True)
            _lastChartSnapshotPushTicks = Stopwatch.GetTimestamp()
        End Sub

        Private Function HasChartRefreshIntervalElapsed() As Boolean
            If _lastChartSnapshotPushTicks <= 0L Then
                Return True
            End If

            Return Stopwatch.GetTimestamp() - _lastChartSnapshotPushTicks >= _chartRefreshIntervalTicks
        End Function

        Private Sub UpdatePlotHighlightState()
            Dim shouldHighlight = Not IsLocked AndAlso _latestSnapshot IsNot Nothing AndAlso _latestSnapshot.HasData
            If _isPlotHighlighted = shouldHighlight Then
                Return
            End If

            _isPlotHighlighted = shouldHighlight
            RaisePropertyChanged(NameOf(IsPlotHighlighted))
        End Sub

        Private Shared Function FormatDistance(value As Nullable(Of Double)) As String
            If Not value.HasValue OrElse Double.IsNaN(value.Value) OrElse Double.IsInfinity(value.Value) Then
                Return "--"
            End If

            Return value.Value.ToString("0.0", CultureInfo.InvariantCulture)
        End Function

        Private Function ResolveStatusScenario(snapshot As MousePerformanceSnapshot) As StatusScenario
            If snapshot IsNot Nothing Then
                Select Case snapshot.Status
                    Case MousePerformanceSessionStatus.Collecting
                        Return StatusScenario.Collecting
                    Case MousePerformanceSessionStatus.Paused
                        Return StatusScenario.Paused
                    Case MousePerformanceSessionStatus.Stopped
                        Return StatusScenario.Paused
                    Case MousePerformanceSessionStatus.NoDevice
                        Return StatusScenario.NoDevice
                    Case MousePerformanceSessionStatus.DeviceDisconnected
                        Return StatusScenario.DeviceDisconnected
                End Select
            End If

            If SelectedDevice Is Nothing Then
                If _devices.Count = 0 Then
                    Return StatusScenario.NoDevice
                End If

                Return StatusScenario.NeedDevice
            End If

            Return StatusScenario.Ready
        End Function

        Private Sub ApplyStatusScenario(scenario As StatusScenario)
            Select Case scenario
                Case StatusScenario.Ready
                    SetStatus(L("MousePerformance.Status.Ready.Pill"),
                              L("MousePerformance.Status.Ready.Message"),
                              L("MousePerformance.Status.Ready.Hint"))
                Case StatusScenario.NeedDevice
                    SetStatus(L("MousePerformance.Status.NeedDevice.Pill"),
                              L("MousePerformance.Status.NeedDevice.Message"),
                              L("MousePerformance.Status.NeedDevice.Hint"))
                Case StatusScenario.Collecting
                    SetStatus(L("MousePerformance.Status.Collecting.Pill"),
                              L("MousePerformance.Status.Collecting.Message"),
                              L("MousePerformance.Status.Collecting.Hint"))
                Case StatusScenario.Paused
                    SetStatus(L("MousePerformance.Status.Paused.Pill"),
                              L("MousePerformance.Status.Paused.Message"),
                              L("MousePerformance.Status.Paused.Hint"))
                Case StatusScenario.NoDevice
                    SetStatus(L("MousePerformance.Status.NoDevice.Pill"),
                              L("MousePerformance.Status.NoDevice.Message"),
                              L("MousePerformance.Status.NoDevice.Hint"))
                Case Else
                    SetStatus(L("MousePerformance.Status.DeviceDisconnected.Pill"),
                              L("MousePerformance.Status.DeviceDisconnected.Message"),
                              L("MousePerformance.Status.DeviceDisconnected.Hint"))
            End Select
        End Sub

        Private Sub SetStatus(pill As String, message As String, hint As String)
            StatusPillText = pill
            StatusMessage = message
            HintText = hint
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
            Else
                details.Add(L("Device.Detail.RawInput"))
            End If

            SelectedDeviceMetaText = String.Join("  /  ", details)
            SelectedDevicePathText = SelectedDevice.PathSummary
        End Sub

        Private Sub UpdateCpiStateFromText()
            Dim parsedValue = 0.0
            Dim isValid = TryParsePositiveCpi(_cpiText, parsedValue)
            If isValid Then
                _effectiveCpiValue = parsedValue
            End If

            _captureService.SetCpiState(_effectiveCpiValue, isValid)
            RefreshSnapshot(_isChartWindowAttached)
        End Sub

        Private Sub OnUiTimerTick(sender As Object, e As EventArgs)
            RefreshSnapshot(_isChartWindowAttached)
        End Sub

        Private Sub OnDevicesChanged(sender As Object, e As EventArgs)
            RunOnUiThread(
                Sub()
                    RefreshDevices()
                    RefreshSnapshot(_isChartWindowAttached)
                End Sub)
        End Sub

        Private Sub OnSelectedDeviceDisconnected(sender As Object, e As EventArgs)
            RunOnUiThread(
                Sub()
                    If IsLocked Then
                        RaiseEvent ExitLockRequested(Me, New CaptureLockRequestEventArgs(CaptureUnlockReason.DeviceDisconnected))
                        Return
                    End If

                    RefreshDevices()
                    RefreshSnapshot(_isChartWindowAttached)
                End Sub)
        End Sub

        Private Sub OnRawKeyboardInput(sender As Object, e As RawKeyboardInputEventArgs)
            If e Is Nothing OrElse e.Input Is Nothing OrElse Not e.Input.IsKeyDown Then
                Return
            End If

            If e.Input.VirtualKey <> EscapeVirtualKey Then
                Return
            End If

            RequestPauseFromInputGesture()
        End Sub

        Private Sub OnRawMouseButtonInput(sender As Object, e As RawMouseButtonInputEventArgs)
            If e Is Nothing OrElse e.Input Is Nothing OrElse Not e.Input.IsButtonDown Then
                Return
            End If

            If e.Input.ButtonKind <> MouseButtonKind.RightButton Then
                Return
            End If

            RequestPauseFromInputGesture()
        End Sub

        Private Sub RequestPauseFromInputGesture()
            If Not IsLocked Then
                Return
            End If

            If Interlocked.Exchange(_pauseGesturePending, 1) <> 0 Then
                Return
            End If

            RunOnUiThread(
                Sub()
                    Try
                        If IsLocked Then
                            RaiseEvent ExitLockRequested(Me, New CaptureLockRequestEventArgs(CaptureUnlockReason.PauseSession))
                        End If
                    Finally
                        Interlocked.Exchange(_pauseGesturePending, 0)
                    End Try
                End Sub)
        End Sub

        Private Sub OnLanguageChanged(sender As Object, e As EventArgs)
            RunOnUiThread(
                Sub()
                    UpdateSelectedDeviceSummary()
                    ApplyStatusScenario(ResolveStatusScenario(_latestSnapshot))
                    ApplyQuality(_latestSnapshot)
                End Sub)
        End Sub

        Private Sub RaiseCanExecuteChanges()
            _collectCommand.RaiseCanExecuteChanged()
            _resetCommand.RaiseCanExecuteChanged()
            _plotCommand.RaiseCanExecuteChanged()
        End Sub

        Private Sub RaiseChartWindowCloseRequest()
            ChartWindowCloseRequestVersion += 1
            LatestChartSnapshot = Nothing
            _lastChartSnapshotPushTicks = 0L
        End Sub

        Private Sub UpdateUiTimer()
            If _isPageActive AndAlso IsLocked Then
                If Not _uiTimer.IsEnabled Then
                    _uiTimer.Start()
                End If

                Return
            End If

            If _uiTimer.IsEnabled Then
                _uiTimer.Stop()
            End If
        End Sub

        Private Shared Function NormalizeCpiText(text As String) As String
            If String.IsNullOrEmpty(text) Then
                Return String.Empty
            End If

            Dim builder As New System.Text.StringBuilder(text.Length)
            Dim hasDecimalPoint = False

            For Each ch In text
                If Char.IsDigit(ch) Then
                    builder.Append(ch)
                ElseIf ch = "."c AndAlso Not hasDecimalPoint Then
                    builder.Append(ch)
                    hasDecimalPoint = True
                End If
            Next

            Return builder.ToString()
        End Function

        Private Shared Function TryParsePositiveCpi(text As String, ByRef value As Double) As Boolean
            value = 0.0
            If String.IsNullOrWhiteSpace(text) Then
                Return False
            End If

            If Not Double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, value) Then
                Return False
            End If

            Return value > 0.0 AndAlso Not Double.IsNaN(value) AndAlso Not Double.IsInfinity(value)
        End Function

        Private Shared Function FormatCpi(value As Double) As String
            Return value.ToString("0.##", CultureInfo.InvariantCulture)
        End Function

        Private Shared Function ResolveStoredCpiOrDefault(preferences As MousePerformancePreferences) As Double
            If preferences IsNot Nothing AndAlso
               preferences.LastCpi.HasValue AndAlso
               preferences.LastCpi.Value > 0.0 AndAlso
               Not Double.IsNaN(preferences.LastCpi.Value) AndAlso
               Not Double.IsInfinity(preferences.LastCpi.Value) Then
                Return preferences.LastCpi.Value
            End If

            Return DefaultCpiValue
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

        Private Function ResolveQualityLevelText(level As MousePerformanceDataQualityLevel) As String
            Select Case level
                Case MousePerformanceDataQualityLevel.Good
                    Return L("MousePerformance.Quality.Level.Good")
                Case MousePerformanceDataQualityLevel.Degraded
                    Return L("MousePerformance.Quality.Level.Degraded")
                Case Else
                    Return L("MousePerformance.Quality.Level.None")
            End Select
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then
                Return
            End If

            _disposed = True
            _uiTimer.Stop()
            RemoveHandler _uiTimer.Tick, AddressOf OnUiTimerTick
            RemoveHandler _localization.LanguageChanged, AddressOf OnLanguageChanged
            RemoveHandler _captureService.DevicesChanged, AddressOf OnDevicesChanged
            RemoveHandler _captureService.SelectedDeviceDisconnected, AddressOf OnSelectedDeviceDisconnected
            RemoveHandler _rawInputBroker.KeyboardInput, AddressOf OnRawKeyboardInput
            RemoveHandler _rawInputBroker.MouseButtonInput, AddressOf OnRawMouseButtonInput
            _captureService.Dispose()
        End Sub
    End Class
End Namespace
