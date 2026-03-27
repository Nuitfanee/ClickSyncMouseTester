Imports System.Collections.ObjectModel
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
    Public Class KeyDetectionPageViewModel
        Inherits BindableBase
        Implements IDisposable, INavigationResettablePageViewModel

        Private Const DefaultDoubleClickThresholdMs As Double = 80.0
        Private Const MinDoubleClickThresholdMs As Double = 1.0
        Private Const MaxDoubleClickThresholdMs As Double = 1000.0
        Private Const ScrollPulseDurationMilliseconds As Double = 150.0

        Private ReadOnly _dispatcher As Dispatcher
        Private ReadOnly _rawInputBroker As IRawInputBroker
        Private ReadOnly _localization As LocalizationManager
        Private _inputService As RawInputKeyDetectionService
        Private ReadOnly _buttonCards As ObservableCollection(Of KeyDetectionButtonCardViewModel)
        Private ReadOnly _cardByButton As Dictionary(Of MouseButtonKind, KeyDetectionButtonCardViewModel)
        Private ReadOnly _customKeyStats As InputTimingStatistics
        Private ReadOnly _resetStatisticsCommand As DelegateCommand
        Private ReadOnly _toggleKeyPickModeCommand As DelegateCommand
        Private ReadOnly _resetCustomKeyCommand As DelegateCommand
        Private ReadOnly _scrollUpPulseTimer As DispatcherTimer
        Private ReadOnly _scrollDownPulseTimer As DispatcherTimer

        Private _mouseDoubleClickThresholdText As String
        Private _mouseDoubleClickThresholdMs As Double
        Private _keyDoubleClickThresholdText As String
        Private _keyDoubleClickThresholdMs As Double
        Private _scrollUpCount As Integer
        Private _scrollDownCount As Integer
        Private _scrollUpCountText As String
        Private _scrollDownCountText As String
        Private _isScrollUpPulseActive As Boolean
        Private _isScrollDownPulseActive As Boolean
        Private _customKeyStatusText As String
        Private _customKeySelectionText As String
        Private _customKeyStatusValueText As String
        Private _customKeySelectionValueText As String
        Private _customKeyPickButtonText As String
        Private _customKeyDownCountText As String
        Private _customKeyUpCountText As String
        Private _customKeyDoubleClickCountText As String
        Private _customKeyCurrentDownDownText As String
        Private _customKeyMinimumDownDownText As String
        Private _customKeyAverageDownDownText As String
        Private _customKeyCurrentDownUpText As String
        Private _customKeyMinimumDownUpText As String
        Private _customKeyAverageDownUpText As String
        Private _isCustomKeyDown As Boolean
        Private _isPickingCustomKey As Boolean
        Private _isPageActive As Boolean
        Private _isWindowActive As Boolean
        Private _isTextEntryActive As Boolean
        Private _selectedCustomKey As KeyDetectionCustomKey
        Private _pendingIgnoredCustomKeyRelease As KeyDetectionCustomKey
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
            _buttonCards = New ObservableCollection(Of KeyDetectionButtonCardViewModel)()
            _cardByButton = New Dictionary(Of MouseButtonKind, KeyDetectionButtonCardViewModel)()
            _customKeyStats = New InputTimingStatistics()
            _resetStatisticsCommand = New DelegateCommand(AddressOf RequestResetStatistics)
            _toggleKeyPickModeCommand = New DelegateCommand(AddressOf RequestToggleKeyPickMode)
            _resetCustomKeyCommand = New DelegateCommand(AddressOf RequestResetCustomKey)
            _scrollUpPulseTimer = New DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            _scrollUpPulseTimer.Interval = TimeSpan.FromMilliseconds(ScrollPulseDurationMilliseconds)
            _scrollDownPulseTimer = New DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            _scrollDownPulseTimer.Interval = TimeSpan.FromMilliseconds(ScrollPulseDurationMilliseconds)

            AddHandler _localization.LanguageChanged, AddressOf OnLanguageChanged
            AddHandler _scrollUpPulseTimer.Tick, AddressOf OnScrollUpPulseTimerTick
            AddHandler _scrollDownPulseTimer.Tick, AddressOf OnScrollDownPulseTimerTick

            _mouseDoubleClickThresholdMs = DefaultDoubleClickThresholdMs
            _mouseDoubleClickThresholdText = FormatThreshold(_mouseDoubleClickThresholdMs)
            _keyDoubleClickThresholdMs = DefaultDoubleClickThresholdMs
            _keyDoubleClickThresholdText = FormatThreshold(_keyDoubleClickThresholdMs)
            _isWindowActive = True

            InitializeButtonCards()
            SyncScrollDisplay()
            SyncCustomKeyStats()
            RefreshLocalization()
        End Sub

        Public ReadOnly Property ButtonCards As ObservableCollection(Of KeyDetectionButtonCardViewModel)
            Get
                Return _buttonCards
            End Get
        End Property

        Public Property MouseDoubleClickThresholdText As String
            Get
                Return _mouseDoubleClickThresholdText
            End Get
            Set(value As String)
                SetProperty(_mouseDoubleClickThresholdText, value)
            End Set
        End Property

        Public Property KeyDoubleClickThresholdText As String
            Get
                Return _keyDoubleClickThresholdText
            End Get
            Set(value As String)
                SetProperty(_keyDoubleClickThresholdText, value)
            End Set
        End Property

        Public Property ScrollUpCountText As String
            Get
                Return _scrollUpCountText
            End Get
            Private Set(value As String)
                SetProperty(_scrollUpCountText, value)
            End Set
        End Property

        Public Property ScrollDownCountText As String
            Get
                Return _scrollDownCountText
            End Get
            Private Set(value As String)
                SetProperty(_scrollDownCountText, value)
            End Set
        End Property

        Public Property IsScrollUpPulseActive As Boolean
            Get
                Return _isScrollUpPulseActive
            End Get
            Private Set(value As Boolean)
                SetProperty(_isScrollUpPulseActive, value)
            End Set
        End Property

        Public Property IsScrollDownPulseActive As Boolean
            Get
                Return _isScrollDownPulseActive
            End Get
            Private Set(value As Boolean)
                SetProperty(_isScrollDownPulseActive, value)
            End Set
        End Property

        Public Property CustomKeyStatusText As String
            Get
                Return _customKeyStatusText
            End Get
            Private Set(value As String)
                SetProperty(_customKeyStatusText, value)
            End Set
        End Property

        Public Property CustomKeySelectionText As String
            Get
                Return _customKeySelectionText
            End Get
            Private Set(value As String)
                SetProperty(_customKeySelectionText, value)
            End Set
        End Property

        Public Property CustomKeyStatusValueText As String
            Get
                Return _customKeyStatusValueText
            End Get
            Private Set(value As String)
                SetProperty(_customKeyStatusValueText, value)
            End Set
        End Property

        Public Property CustomKeySelectionValueText As String
            Get
                Return _customKeySelectionValueText
            End Get
            Private Set(value As String)
                SetProperty(_customKeySelectionValueText, value)
            End Set
        End Property

        Public Property CustomKeyPickButtonText As String
            Get
                Return _customKeyPickButtonText
            End Get
            Private Set(value As String)
                SetProperty(_customKeyPickButtonText, value)
            End Set
        End Property

        Public Property CustomKeyDownCountText As String
            Get
                Return _customKeyDownCountText
            End Get
            Private Set(value As String)
                SetProperty(_customKeyDownCountText, value)
            End Set
        End Property

        Public Property CustomKeyUpCountText As String
            Get
                Return _customKeyUpCountText
            End Get
            Private Set(value As String)
                SetProperty(_customKeyUpCountText, value)
            End Set
        End Property

        Public Property CustomKeyDoubleClickCountText As String
            Get
                Return _customKeyDoubleClickCountText
            End Get
            Private Set(value As String)
                SetProperty(_customKeyDoubleClickCountText, value)
            End Set
        End Property

        Public Property CustomKeyCurrentDownDownText As String
            Get
                Return _customKeyCurrentDownDownText
            End Get
            Private Set(value As String)
                SetProperty(_customKeyCurrentDownDownText, value)
            End Set
        End Property

        Public Property CustomKeyMinimumDownDownText As String
            Get
                Return _customKeyMinimumDownDownText
            End Get
            Private Set(value As String)
                SetProperty(_customKeyMinimumDownDownText, value)
            End Set
        End Property

        Public Property CustomKeyAverageDownDownText As String
            Get
                Return _customKeyAverageDownDownText
            End Get
            Private Set(value As String)
                SetProperty(_customKeyAverageDownDownText, value)
            End Set
        End Property

        Public Property CustomKeyCurrentDownUpText As String
            Get
                Return _customKeyCurrentDownUpText
            End Get
            Private Set(value As String)
                SetProperty(_customKeyCurrentDownUpText, value)
            End Set
        End Property

        Public Property CustomKeyMinimumDownUpText As String
            Get
                Return _customKeyMinimumDownUpText
            End Get
            Private Set(value As String)
                SetProperty(_customKeyMinimumDownUpText, value)
            End Set
        End Property

        Public Property CustomKeyAverageDownUpText As String
            Get
                Return _customKeyAverageDownUpText
            End Get
            Private Set(value As String)
                SetProperty(_customKeyAverageDownUpText, value)
            End Set
        End Property

        Public Property IsCustomKeyDown As Boolean
            Get
                Return _isCustomKeyDown
            End Get
            Private Set(value As Boolean)
                SetProperty(_isCustomKeyDown, value)
            End Set
        End Property

        Public ReadOnly Property ResetStatisticsCommand As DelegateCommand
            Get
                Return _resetStatisticsCommand
            End Get
        End Property

        Public ReadOnly Property ToggleKeyPickModeCommand As DelegateCommand
            Get
                Return _toggleKeyPickModeCommand
            End Get
        End Property

        Public ReadOnly Property ResetCustomKeyCommand As DelegateCommand
            Get
                Return _resetCustomKeyCommand
            End Get
        End Property

        Public Sub SetPageActive(isActive As Boolean)
            If _isPageActive = isActive Then
                Return
            End If

            _isPageActive = isActive
            If isActive Then
                EnsureInputService()
            Else
                ResetTransientInputState()
            End If
        End Sub

        Public Sub ResetToDefaultState() Implements INavigationResettablePageViewModel.ResetToDefaultState
            ReleaseInputService()

            _mouseDoubleClickThresholdMs = DefaultDoubleClickThresholdMs
            MouseDoubleClickThresholdText = FormatThreshold(_mouseDoubleClickThresholdMs)
            _keyDoubleClickThresholdMs = DefaultDoubleClickThresholdMs
            KeyDoubleClickThresholdText = FormatThreshold(_keyDoubleClickThresholdMs)

            For Each card In _buttonCards
                card.ResetStatistics()
            Next

            _scrollUpCount = 0
            _scrollDownCount = 0
            _scrollUpPulseTimer.Stop()
            _scrollDownPulseTimer.Stop()
            IsScrollUpPulseActive = False
            IsScrollDownPulseActive = False
            SyncScrollDisplay()

            _selectedCustomKey = Nothing
            _pendingIgnoredCustomKeyRelease = Nothing
            _isPickingCustomKey = False
            _isPageActive = False
            _isWindowActive = True
            _isTextEntryActive = False

            _customKeyStats.Reset()
            SyncCustomKeyStats()
            UpdatePickButtonText()
            UpdateCustomKeySelectionText()
            UpdateCustomKeyStatusIdle()
        End Sub

        Public Sub SetWindowActive(isActive As Boolean)
            If _isWindowActive = isActive Then
                Return
            End If

            _isWindowActive = isActive
            If Not isActive Then
                ResetTransientInputState()
            End If
        End Sub

        Public Sub SetTextEntryActive(isActive As Boolean)
            _isTextEntryActive = isActive
        End Sub

        Public Sub CommitMouseDoubleClickThresholdInput()
            _mouseDoubleClickThresholdMs = NormalizeThreshold(MouseDoubleClickThresholdText)
            MouseDoubleClickThresholdText = FormatThreshold(_mouseDoubleClickThresholdMs)
        End Sub

        Public Sub CommitKeyDoubleClickThresholdInput()
            _keyDoubleClickThresholdMs = NormalizeThreshold(KeyDoubleClickThresholdText)
            KeyDoubleClickThresholdText = FormatThreshold(_keyDoubleClickThresholdMs)
        End Sub

        Private Sub InitializeButtonCards()
            AddCard(MouseButtonKind.LeftButton)
            AddCard(MouseButtonKind.MiddleButton)
            AddCard(MouseButtonKind.RightButton)
            AddCard(MouseButtonKind.ForwardButton)
            AddCard(MouseButtonKind.BackButton)
        End Sub

        Private Sub AddCard(buttonKind As MouseButtonKind)
            Dim card As New KeyDetectionButtonCardViewModel(buttonKind)
            _buttonCards.Add(card)
            _cardByButton(buttonKind) = card
        End Sub

        Private Sub OnMouseButtonInput(sender As Object, e As RawMouseButtonInputEventArgs)
            If e Is Nothing OrElse e.Input Is Nothing Then
                Return
            End If

            RunOnUiThread(Sub() HandleMouseButtonInput(e.Input))
        End Sub

        Private Sub HandleMouseButtonInput(input As RawMouseButtonInput)
            If input Is Nothing OrElse Not CanConsumePointerInput() Then
                Return
            End If

            If Not _cardByButton.ContainsKey(input.ButtonKind) Then
                Return
            End If

            Dim card = _cardByButton(input.ButtonKind)
            If input.IsButtonDown Then
                card.RegisterDown(input.TimestampMs, _mouseDoubleClickThresholdMs)
            Else
                card.RegisterUp(input.TimestampMs)
            End If
        End Sub

        Private Sub OnMouseWheelInput(sender As Object, e As RawMouseWheelInputEventArgs)
            If e Is Nothing OrElse e.Input Is Nothing Then
                Return
            End If

            RunOnUiThread(Sub() HandleMouseWheelInput(e.Input))
        End Sub

        Private Sub HandleMouseWheelInput(input As RawMouseWheelInput)
            If input Is Nothing OrElse Not CanConsumePointerInput() Then
                Return
            End If

            If input.Delta > 0 Then
                _scrollUpCount += 1
                SyncScrollDisplay()
                PulseScrollIndicator(isScrollUp:=True)
            ElseIf input.Delta < 0 Then
                _scrollDownCount += 1
                SyncScrollDisplay()
                PulseScrollIndicator(isScrollUp:=False)
            End If
        End Sub

        Private Sub OnKeyboardInput(sender As Object, e As RawKeyboardInputEventArgs)
            If e Is Nothing OrElse e.Input Is Nothing Then
                Return
            End If

            RunOnUiThread(Sub() HandleKeyboardInput(e.Input))
        End Sub

        Private Sub HandleKeyboardInput(input As RawKeyboardInput)
            If input Is Nothing Then
                Return
            End If

            If _isPickingCustomKey Then
                HandlePickingKeyboardInput(input)
                Return
            End If

            If Not CanConsumeKeyboardInput() Then
                Return
            End If

            If _pendingIgnoredCustomKeyRelease IsNot Nothing AndAlso _pendingIgnoredCustomKeyRelease.Matches(input) Then
                If Not input.IsKeyDown Then
                    _pendingIgnoredCustomKeyRelease = Nothing
                End If

                Return
            End If

            If _selectedCustomKey Is Nothing OrElse Not _selectedCustomKey.Matches(input) Then
                Return
            End If

            If input.IsKeyDown Then
                _customKeyStats.RegisterDown(input.TimestampMs, _keyDoubleClickThresholdMs)
                SyncCustomKeyStats()
                UpdateCustomKeyStatusDown()
            Else
                _customKeyStats.RegisterUp(input.TimestampMs)
                SyncCustomKeyStats()
                UpdateCustomKeyStatusUp()
            End If
        End Sub

        Private Sub HandlePickingKeyboardInput(input As RawKeyboardInput)
            If input Is Nothing Then
                Return
            End If

            If Not input.IsKeyDown Then
                If _pendingIgnoredCustomKeyRelease IsNot Nothing AndAlso _pendingIgnoredCustomKeyRelease.Matches(input) Then
                    _pendingIgnoredCustomKeyRelease = Nothing
                End If

                Return
            End If

            Dim pickedKey = KeyDetectionCustomKey.FromInput(input)
            _pendingIgnoredCustomKeyRelease = pickedKey

            If input.VirtualKey = &H1B Then
                _isPickingCustomKey = False
                UpdatePickButtonText()
                UpdateCustomKeyStatusPickCanceled()
                Return
            End If

            Dim selectedChanged = _selectedCustomKey Is Nothing OrElse Not _selectedCustomKey.Matches(input)
            _selectedCustomKey = pickedKey
            _isPickingCustomKey = False
            UpdateCustomKeySelectionText()

            If selectedChanged Then
                ResetCustomKeyStatisticsOnly()
            Else
                UpdateCustomKeyStatusSelected()
            End If

            UpdatePickButtonText()
            UpdateCustomKeyStatusSelected()
        End Sub

        Private Function CanConsumePointerInput() As Boolean
            Return _isPageActive AndAlso _isWindowActive
        End Function

        Private Function CanConsumeKeyboardInput() As Boolean
            Return _isPageActive AndAlso _isWindowActive AndAlso Not _isTextEntryActive
        End Function

        Private Sub RequestResetStatistics()
            For Each card In _buttonCards
                card.ResetStatistics()
            Next

            _scrollUpCount = 0
            _scrollDownCount = 0
            IsScrollUpPulseActive = False
            IsScrollDownPulseActive = False
            _scrollUpPulseTimer.Stop()
            _scrollDownPulseTimer.Stop()
            SyncScrollDisplay()

            ResetCustomKeyStatisticsOnly()
            UpdateCustomKeyStatusByCurrentState()
        End Sub

        Private Sub RequestToggleKeyPickMode()
            _isPickingCustomKey = Not _isPickingCustomKey
            UpdatePickButtonText()

            If _isPickingCustomKey Then
                UpdateCustomKeyStatusPicking()
            Else
                UpdateCustomKeyStatusPickExited()
            End If
        End Sub

        Private Sub RequestResetCustomKey()
            _selectedCustomKey = Nothing
            _pendingIgnoredCustomKeyRelease = Nothing
            _isPickingCustomKey = False
            UpdatePickButtonText()
            UpdateCustomKeySelectionText()
            ResetCustomKeyStatisticsOnly()
            UpdateCustomKeyStatusIdle()
        End Sub

        Private Sub ResetCustomKeyStatisticsOnly()
            _customKeyStats.Reset()
            SyncCustomKeyStats()
        End Sub

        Private Sub SyncScrollDisplay()
            ScrollUpCountText = FormatCount(_scrollUpCount)
            ScrollDownCountText = FormatCount(_scrollDownCount)
        End Sub

        Private Sub SyncCustomKeyStats()
            CustomKeyDownCountText = FormatCount(_customKeyStats.DownCount)
            CustomKeyUpCountText = FormatCount(_customKeyStats.UpCount)
            CustomKeyDoubleClickCountText = FormatCount(_customKeyStats.DoubleClickCount)
            CustomKeyCurrentDownDownText = FormatMilliseconds(_customKeyStats.CurrentDownDownMs)
            CustomKeyMinimumDownDownText = FormatMilliseconds(_customKeyStats.MinimumDownDownMs)
            CustomKeyAverageDownDownText = FormatMilliseconds(_customKeyStats.AverageDownDownMs)
            CustomKeyCurrentDownUpText = FormatMilliseconds(_customKeyStats.CurrentDownUpMs)
            CustomKeyMinimumDownUpText = FormatMilliseconds(_customKeyStats.MinimumDownUpMs)
            CustomKeyAverageDownUpText = FormatMilliseconds(_customKeyStats.AverageDownUpMs)
            IsCustomKeyDown = _customKeyStats.IsPressed
        End Sub

        Private Sub PulseScrollIndicator(isScrollUp As Boolean)
            If isScrollUp Then
                _scrollUpPulseTimer.Stop()
                IsScrollUpPulseActive = True
                _scrollUpPulseTimer.Start()
                Return
            End If

            _scrollDownPulseTimer.Stop()
            IsScrollDownPulseActive = True
            _scrollDownPulseTimer.Start()
        End Sub

        Private Sub OnScrollUpPulseTimerTick(sender As Object, e As EventArgs)
            _scrollUpPulseTimer.Stop()
            IsScrollUpPulseActive = False
        End Sub

        Private Sub OnScrollDownPulseTimerTick(sender As Object, e As EventArgs)
            _scrollDownPulseTimer.Stop()
            IsScrollDownPulseActive = False
        End Sub

        Private Sub OnLanguageChanged(sender As Object, e As EventArgs)
            RunOnUiThread(AddressOf RefreshLocalization)
        End Sub

        Private Sub RefreshLocalization()
            For Each card In _buttonCards
                card.RefreshLocalization(_localization)
            Next

            UpdateCustomKeySelectionText()
            UpdatePickButtonText()
            UpdateCustomKeyStatusByCurrentState()
        End Sub

        Private Sub UpdateCustomKeySelectionText()
            If _selectedCustomKey Is Nothing Then
                CustomKeySelectionText = L("KeyDetection.CustomKey.Selection.Empty")
                CustomKeySelectionValueText = L("KeyDetection.CustomKey.Selection.Placeholder")
                Return
            End If

            CustomKeySelectionText = L("KeyDetection.CustomKey.Selection.Value", _selectedCustomKey.DisplayName)
            CustomKeySelectionValueText = _selectedCustomKey.DisplayName
        End Sub

        Private Sub UpdatePickButtonText()
            CustomKeyPickButtonText = If(_isPickingCustomKey,
                                         L("KeyDetection.CustomKey.Button.Exit"),
                                         L("KeyDetection.CustomKey.Button.Pick"))
        End Sub

        Private Sub UpdateCustomKeyStatusByCurrentState()
            If _isPickingCustomKey Then
                UpdateCustomKeyStatusPicking()
                Return
            End If

            If _customKeyStats.IsPressed AndAlso _selectedCustomKey IsNot Nothing Then
                UpdateCustomKeyStatusDown()
                Return
            End If

            If _selectedCustomKey IsNot Nothing Then
                UpdateCustomKeyStatusSelected()
                Return
            End If

            UpdateCustomKeyStatusIdle()
        End Sub

        Private Sub UpdateCustomKeyStatusIdle()
            CustomKeyStatusText = L("KeyDetection.CustomKey.Status.Idle")
            CustomKeyStatusValueText = L("KeyDetection.CustomKey.StatusValue.Idle")
        End Sub

        Private Sub UpdateCustomKeyStatusPicking()
            CustomKeyStatusText = L("KeyDetection.CustomKey.Status.Picking")
            CustomKeyStatusValueText = L("KeyDetection.CustomKey.StatusValue.Picking")
        End Sub

        Private Sub UpdateCustomKeyStatusPickCanceled()
            CustomKeyStatusText = L("KeyDetection.CustomKey.Status.PickCanceled")
            CustomKeyStatusValueText = L("KeyDetection.CustomKey.StatusValue.PickCanceled")
        End Sub

        Private Sub UpdateCustomKeyStatusPickExited()
            If _selectedCustomKey IsNot Nothing Then
                CustomKeyStatusText = L("KeyDetection.CustomKey.Status.Selected", _selectedCustomKey.DisplayName)
                CustomKeyStatusValueText = L("KeyDetection.CustomKey.StatusValue.Selected", _selectedCustomKey.DisplayName)
            Else
                CustomKeyStatusText = L("KeyDetection.CustomKey.Status.PickExited")
                CustomKeyStatusValueText = L("KeyDetection.CustomKey.StatusValue.PickExited")
            End If
        End Sub

        Private Sub UpdateCustomKeyStatusSelected()
            If _selectedCustomKey Is Nothing Then
                UpdateCustomKeyStatusIdle()
                Return
            End If

            CustomKeyStatusText = L("KeyDetection.CustomKey.Status.Selected", _selectedCustomKey.DisplayName)
            CustomKeyStatusValueText = L("KeyDetection.CustomKey.StatusValue.Selected", _selectedCustomKey.DisplayName)
        End Sub

        Private Sub UpdateCustomKeyStatusDown()
            If _selectedCustomKey Is Nothing Then
                UpdateCustomKeyStatusIdle()
                Return
            End If

            CustomKeyStatusText = L("KeyDetection.CustomKey.Status.Down", _selectedCustomKey.DisplayName)
            CustomKeyStatusValueText = L("KeyDetection.CustomKey.StatusValue.Down", _selectedCustomKey.DisplayName)
        End Sub

        Private Sub UpdateCustomKeyStatusUp()
            If _selectedCustomKey Is Nothing Then
                UpdateCustomKeyStatusIdle()
                Return
            End If

            CustomKeyStatusText = L("KeyDetection.CustomKey.Status.Up", _selectedCustomKey.DisplayName)
            CustomKeyStatusValueText = L("KeyDetection.CustomKey.StatusValue.Up", _selectedCustomKey.DisplayName)
        End Sub

        Private Sub ResetTransientInputState()
            For Each card In _buttonCards
                card.ResetPressedState()
            Next

            _customKeyStats.ResetPressedState()
            SyncCustomKeyStats()
            UpdateCustomKeyStatusByCurrentState()
        End Sub

        Private Function NormalizeThreshold(value As String) As Double
            Dim parsed As Double
            If TryParseThreshold(value, parsed) AndAlso parsed > MinDoubleClickThresholdMs AndAlso parsed < MaxDoubleClickThresholdMs Then
                Return parsed
            End If

            Return DefaultDoubleClickThresholdMs
        End Function

        Private Sub EnsureInputService()
            If _inputService IsNot Nothing Then
                Return
            End If

            Dim inputService As New RawInputKeyDetectionService(_rawInputBroker)
            AddHandler inputService.MouseButtonInput, AddressOf OnMouseButtonInput
            AddHandler inputService.MouseWheelInput, AddressOf OnMouseWheelInput
            AddHandler inputService.KeyboardInput, AddressOf OnKeyboardInput
            _inputService = inputService
        End Sub

        Private Sub ReleaseInputService()
            If _inputService Is Nothing Then
                Return
            End If

            RemoveHandler _inputService.MouseButtonInput, AddressOf OnMouseButtonInput
            RemoveHandler _inputService.MouseWheelInput, AddressOf OnMouseWheelInput
            RemoveHandler _inputService.KeyboardInput, AddressOf OnKeyboardInput
            _inputService.Dispose()
            _inputService = Nothing
        End Sub

        Private Shared Function TryParseThreshold(value As String, ByRef parsedValue As Double) As Boolean
            If Double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, parsedValue) Then
                Return True
            End If

            Return Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, parsedValue)
        End Function

        Private Shared Function FormatThreshold(value As Double) As String
            Return value.ToString("0.##", CultureInfo.InvariantCulture)
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

            _scrollUpPulseTimer.Stop()
            _scrollDownPulseTimer.Stop()
            RemoveHandler _localization.LanguageChanged, AddressOf OnLanguageChanged
            RemoveHandler _scrollUpPulseTimer.Tick, AddressOf OnScrollUpPulseTimerTick
            RemoveHandler _scrollDownPulseTimer.Tick, AddressOf OnScrollDownPulseTimerTick

            For Each card In _buttonCards
                card.Dispose()
            Next

            ReleaseInputService()
        End Sub
    End Class
End Namespace
