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
    Public Class SensitivityMatchingPageViewModel
        Inherits BindableBase
        Implements IDisposable, INavigationResettablePageViewModel

        Private Const UiRefreshIntervalMilliseconds As Double = 1000.0 / 60.0
        Private Const MinimumDpi As Integer = 50
        Private Const MaximumDpi As Integer = 50000

        Private ReadOnly _dispatcher As Dispatcher
        Private ReadOnly _localization As LocalizationManager
        Private ReadOnly _captureService As SensitivityMatchCaptureService
        Private ReadOnly _uiTimer As DispatcherTimer
        Private ReadOnly _rounds As ObservableCollection(Of SensitivityMatchRoundCardViewModel)
        Private ReadOnly _sourceInfoBand As SensitivityMatchInfoBandViewModel
        Private ReadOnly _targetInfoBand As SensitivityMatchInfoBandViewModel
        Private ReadOnly _startSetupCommand As DelegateCommand
        Private ReadOnly _backCommand As DelegateCommand
        Private ReadOnly _continueFromSetupCommand As DelegateCommand
        Private ReadOnly _bindSlotCommand As DelegateCommand
        Private ReadOnly _startRoundCommand As DelegateCommand
        Private ReadOnly _copyRecommendedDpiCommand As DelegateCommand
        Private ReadOnly _remeasureCommand As DelegateCommand
        Private ReadOnly _rebindCommand As DelegateCommand

        Private _latestSnapshot As SensitivityMatchSnapshot
        Private _currentStep As SensitivityMatchWizardStep
        Private _sourceDpiText As String
        Private _targetCurrentDpiText As String
        Private _statusPillText As String
        Private _statusMessage As String
        Private _hintText As String
        Private _availableDeviceCountText As String
        Private _sourceDeviceTitle As String
        Private _sourceDeviceMetaText As String
        Private _sourceBindingStateText As String
        Private _sourceBindButtonText As String
        Private _targetDeviceTitle As String
        Private _targetDeviceMetaText As String
        Private _targetBindingStateText As String
        Private _targetBindButtonText As String
        Private _recommendedTargetDpiText As String
        Private _scaleText As String
        Private _consistencyText As String
        Private _startRoundButtonText As String
        Private _overallProgressValue As Double
        Private _sourceProgressValue As Double
        Private _targetProgressValue As Double
        Private _overallProgressText As String
        Private _sourceProgressText As String
        Private _targetProgressText As String
        Private _measureHeroPrimaryText As String
        Private _measureHeroLabelText As String
        Private _measureHeroSupportText As String
        Private _measureSourceValueText As String
        Private _measureTargetValueText As String
        Private _isSourceBindingPending As Boolean
        Private _isTargetBindingPending As Boolean
        Private _isMeasureActiveState As Boolean
        Private _isMeasureFailureState As Boolean
        Private _isMeasureExpiredState As Boolean
        Private _hasMinimumDeviceCount As Boolean
        Private _hasFinalRecommendation As Boolean
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

            _localization = LocalizationManager.Instance
            _localization.Initialize()
            _captureService = New SensitivityMatchCaptureService(rawInputBroker)
            _uiTimer = New DispatcherTimer(DispatcherPriority.Render, _dispatcher)
            _uiTimer.Interval = TimeSpan.FromMilliseconds(UiRefreshIntervalMilliseconds)
            _rounds = New ObservableCollection(Of SensitivityMatchRoundCardViewModel)()
            _sourceInfoBand = New SensitivityMatchInfoBandViewModel() With {
                .BandHorizontalAlignment = HorizontalAlignment.Left,
                .BandScaleX = 1
            }
            _targetInfoBand = New SensitivityMatchInfoBandViewModel() With {
                .BandHorizontalAlignment = HorizontalAlignment.Right,
                .BandScaleX = -1
            }
            For roundIndex = 1 To 3
                _rounds.Add(New SensitivityMatchRoundCardViewModel())
            Next

            _startSetupCommand = New DelegateCommand(AddressOf RequestStartSetup, AddressOf CanStartSetup)
            _backCommand = New DelegateCommand(AddressOf RequestBack, AddressOf CanGoBack)
            _continueFromSetupCommand = New DelegateCommand(AddressOf RequestContinueFromSetup, AddressOf CanContinueFromSetup)
            _bindSlotCommand = New DelegateCommand(AddressOf RequestBindSlot, Nothing)
            _startRoundCommand = New DelegateCommand(AddressOf RequestStartRound, AddressOf CanStartRound)
            _copyRecommendedDpiCommand = New DelegateCommand(AddressOf RequestCopyRecommendedDpi, AddressOf CanCopyRecommendedDpi)
            _remeasureCommand = New DelegateCommand(AddressOf RequestRemeasure, AddressOf CanRemeasure)
            _rebindCommand = New DelegateCommand(AddressOf RequestRebind)

            AddHandler _localization.LanguageChanged, AddressOf OnLanguageChanged
            AddHandler _uiTimer.Tick, AddressOf OnUiTimerTick
            AddHandler _captureService.DevicesChanged, AddressOf OnDevicesChanged

            _currentStep = SensitivityMatchWizardStep.Intro
            _latestSnapshot = _captureService.CaptureSnapshot()

            RefreshFromSnapshot(allowAutoStepAdvance:=False)
        End Sub

        Public ReadOnly Property Rounds As ObservableCollection(Of SensitivityMatchRoundCardViewModel)
            Get
                Return _rounds
            End Get
        End Property

        Public ReadOnly Property SourceInfoBand As SensitivityMatchInfoBandViewModel
            Get
                Return _sourceInfoBand
            End Get
        End Property

        Public ReadOnly Property TargetInfoBand As SensitivityMatchInfoBandViewModel
            Get
                Return _targetInfoBand
            End Get
        End Property

        Public Property CurrentStep As SensitivityMatchWizardStep
            Get
                Return _currentStep
            End Get
            Private Set(value As SensitivityMatchWizardStep)
                If SetProperty(_currentStep, value) Then
                    RaisePropertyChanged(NameOf(IsIntroStep))
                    RaisePropertyChanged(NameOf(IsSetupStep))
                    RaisePropertyChanged(NameOf(IsMeasureStep))
                    RaiseCanExecuteChanges()
                End If
            End Set
        End Property

        Public ReadOnly Property IsIntroStep As Boolean
            Get
                Return CurrentStep = SensitivityMatchWizardStep.Intro
            End Get
        End Property

        Public ReadOnly Property IsSetupStep As Boolean
            Get
                Return CurrentStep = SensitivityMatchWizardStep.Setup
            End Get
        End Property

        Public ReadOnly Property IsMeasureStep As Boolean
            Get
                Return CurrentStep = SensitivityMatchWizardStep.Measure
            End Get
        End Property

        Public Property SourceDpiText As String
            Get
                Return _sourceDpiText
            End Get
            Set(value As String)
                Dim normalizedValue = NormalizeDpiText(value)
                If SetProperty(_sourceDpiText, normalizedValue) Then
                    OnDpiInputsChanged()
                End If
            End Set
        End Property

        Public Property TargetCurrentDpiText As String
            Get
                Return _targetCurrentDpiText
            End Get
            Set(value As String)
                Dim normalizedValue = NormalizeDpiText(value)
                If SetProperty(_targetCurrentDpiText, normalizedValue) Then
                    OnDpiInputsChanged()
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

        Public Property AvailableDeviceCountText As String
            Get
                Return _availableDeviceCountText
            End Get
            Private Set(value As String)
                SetProperty(_availableDeviceCountText, value)
            End Set
        End Property

        Public Property SourceDeviceTitle As String
            Get
                Return _sourceDeviceTitle
            End Get
            Private Set(value As String)
                SetProperty(_sourceDeviceTitle, value)
            End Set
        End Property

        Public Property SourceDeviceMetaText As String
            Get
                Return _sourceDeviceMetaText
            End Get
            Private Set(value As String)
                SetProperty(_sourceDeviceMetaText, value)
            End Set
        End Property

        Public Property SourceBindingStateText As String
            Get
                Return _sourceBindingStateText
            End Get
            Private Set(value As String)
                SetProperty(_sourceBindingStateText, value)
            End Set
        End Property

        Public Property SourceBindButtonText As String
            Get
                Return _sourceBindButtonText
            End Get
            Private Set(value As String)
                SetProperty(_sourceBindButtonText, value)
            End Set
        End Property

        Public Property TargetDeviceTitle As String
            Get
                Return _targetDeviceTitle
            End Get
            Private Set(value As String)
                SetProperty(_targetDeviceTitle, value)
            End Set
        End Property

        Public Property TargetDeviceMetaText As String
            Get
                Return _targetDeviceMetaText
            End Get
            Private Set(value As String)
                SetProperty(_targetDeviceMetaText, value)
            End Set
        End Property

        Public Property TargetBindingStateText As String
            Get
                Return _targetBindingStateText
            End Get
            Private Set(value As String)
                SetProperty(_targetBindingStateText, value)
            End Set
        End Property

        Public Property TargetBindButtonText As String
            Get
                Return _targetBindButtonText
            End Get
            Private Set(value As String)
                SetProperty(_targetBindButtonText, value)
            End Set
        End Property

        Public Property RecommendedTargetDpiText As String
            Get
                Return _recommendedTargetDpiText
            End Get
            Private Set(value As String)
                SetProperty(_recommendedTargetDpiText, value)
            End Set
        End Property

        Public Property ScaleText As String
            Get
                Return _scaleText
            End Get
            Private Set(value As String)
                SetProperty(_scaleText, value)
            End Set
        End Property

        Public Property ConsistencyText As String
            Get
                Return _consistencyText
            End Get
            Private Set(value As String)
                SetProperty(_consistencyText, value)
            End Set
        End Property

        Public Property StartRoundButtonText As String
            Get
                Return _startRoundButtonText
            End Get
            Private Set(value As String)
                SetProperty(_startRoundButtonText, value)
            End Set
        End Property

        Public Property OverallProgressValue As Double
            Get
                Return _overallProgressValue
            End Get
            Private Set(value As Double)
                SetProperty(_overallProgressValue, value)
            End Set
        End Property

        Public Property SourceProgressValue As Double
            Get
                Return _sourceProgressValue
            End Get
            Private Set(value As Double)
                SetProperty(_sourceProgressValue, value)
            End Set
        End Property

        Public Property TargetProgressValue As Double
            Get
                Return _targetProgressValue
            End Get
            Private Set(value As Double)
                SetProperty(_targetProgressValue, value)
            End Set
        End Property

        Public Property OverallProgressText As String
            Get
                Return _overallProgressText
            End Get
            Private Set(value As String)
                SetProperty(_overallProgressText, value)
            End Set
        End Property

        Public Property SourceProgressText As String
            Get
                Return _sourceProgressText
            End Get
            Private Set(value As String)
                SetProperty(_sourceProgressText, value)
            End Set
        End Property

        Public Property TargetProgressText As String
            Get
                Return _targetProgressText
            End Get
            Private Set(value As String)
                SetProperty(_targetProgressText, value)
            End Set
        End Property

        Public Property MeasureHeroPrimaryText As String
            Get
                Return _measureHeroPrimaryText
            End Get
            Private Set(value As String)
                SetProperty(_measureHeroPrimaryText, value)
            End Set
        End Property

        Public Property MeasureHeroLabelText As String
            Get
                Return _measureHeroLabelText
            End Get
            Private Set(value As String)
                SetProperty(_measureHeroLabelText, value)
            End Set
        End Property

        Public Property MeasureHeroSupportText As String
            Get
                Return _measureHeroSupportText
            End Get
            Private Set(value As String)
                SetProperty(_measureHeroSupportText, value)
            End Set
        End Property

        Public Property MeasureSourceValueText As String
            Get
                Return _measureSourceValueText
            End Get
            Private Set(value As String)
                SetProperty(_measureSourceValueText, value)
            End Set
        End Property

        Public Property MeasureTargetValueText As String
            Get
                Return _measureTargetValueText
            End Get
            Private Set(value As String)
                SetProperty(_measureTargetValueText, value)
            End Set
        End Property

        Public Property IsSourceBindingPending As Boolean
            Get
                Return _isSourceBindingPending
            End Get
            Private Set(value As Boolean)
                SetProperty(_isSourceBindingPending, value)
            End Set
        End Property

        Public Property IsTargetBindingPending As Boolean
            Get
                Return _isTargetBindingPending
            End Get
            Private Set(value As Boolean)
                SetProperty(_isTargetBindingPending, value)
            End Set
        End Property

        Public Property IsMeasureActiveState As Boolean
            Get
                Return _isMeasureActiveState
            End Get
            Private Set(value As Boolean)
                SetProperty(_isMeasureActiveState, value)
            End Set
        End Property

        Public Property IsMeasureFailureState As Boolean
            Get
                Return _isMeasureFailureState
            End Get
            Private Set(value As Boolean)
                SetProperty(_isMeasureFailureState, value)
            End Set
        End Property

        Public Property IsMeasureExpiredState As Boolean
            Get
                Return _isMeasureExpiredState
            End Get
            Private Set(value As Boolean)
                SetProperty(_isMeasureExpiredState, value)
            End Set
        End Property

        Public Property HasMinimumDeviceCount As Boolean
            Get
                Return _hasMinimumDeviceCount
            End Get
            Private Set(value As Boolean)
                SetProperty(_hasMinimumDeviceCount, value)
            End Set
        End Property

        Public Property HasFinalRecommendation As Boolean
            Get
                Return _hasFinalRecommendation
            End Get
            Private Set(value As Boolean)
                SetProperty(_hasFinalRecommendation, value)
            End Set
        End Property

        Public ReadOnly Property StartSetupCommand As DelegateCommand
            Get
                Return _startSetupCommand
            End Get
        End Property

        Public ReadOnly Property BackCommand As DelegateCommand
            Get
                Return _backCommand
            End Get
        End Property

        Public ReadOnly Property ContinueFromSetupCommand As DelegateCommand
            Get
                Return _continueFromSetupCommand
            End Get
        End Property

        Public ReadOnly Property IsContinueFromSetupEnabled As Boolean
            Get
                Return CanContinueFromSetup()
            End Get
        End Property

        Public ReadOnly Property BindSlotCommand As DelegateCommand
            Get
                Return _bindSlotCommand
            End Get
        End Property

        Public ReadOnly Property StartRoundCommand As DelegateCommand
            Get
                Return _startRoundCommand
            End Get
        End Property

        Public ReadOnly Property CopyRecommendedDpiCommand As DelegateCommand
            Get
                Return _copyRecommendedDpiCommand
            End Get
        End Property

        Public ReadOnly Property RemeasureCommand As DelegateCommand
            Get
                Return _remeasureCommand
            End Get
        End Property

        Public ReadOnly Property RebindCommand As DelegateCommand
            Get
                Return _rebindCommand
            End Get
        End Property

        Public Sub SetPageActive(isActive As Boolean)
            If _isPageActive = isActive Then
                Return
            End If

            _isPageActive = isActive
            If isActive Then
                RefreshFromSnapshot(allowAutoStepAdvance:=True)
            Else
                _uiTimer.Stop()
                _captureService.CancelTransientActivity()
                RefreshFromSnapshot(allowAutoStepAdvance:=False)
            End If
        End Sub

        Public Sub ResetToDefaultState() Implements INavigationResettablePageViewModel.ResetToDefaultState
            _uiTimer.Stop()
            _captureService.CancelTransientActivity()
            _captureService.ResetAll()

            _isPageActive = False
            SetProperty(_sourceDpiText, String.Empty, NameOf(SourceDpiText))
            SetProperty(_targetCurrentDpiText, String.Empty, NameOf(TargetCurrentDpiText))

            CurrentStep = SensitivityMatchWizardStep.Intro
            RefreshFromSnapshot(allowAutoStepAdvance:=False)
        End Sub

        Public Sub PrepareMeasureEntryPreview()
            If _latestSnapshot Is Nothing Then
                Return
            End If

            Dim previewRoundIndex = Math.Max(1, Math.Min(_rounds.Count, _latestSnapshot.CompletedRoundCount + 1))
            StatusPillText = String.Empty
            StatusMessage = L("SensitivityMatch.Status.MeasureReady.Message", previewRoundIndex)
            HintText = L("SensitivityMatch.Status.MeasureReady.Hint")
            RefreshMeasurePresentation()
        End Sub

        Private Sub RequestStartSetup()
            If Not CanStartSetup() Then
                Return
            End If

            CurrentStep = SensitivityMatchWizardStep.Setup
            RefreshStatus()
        End Sub

        Private Function CanStartSetup() As Boolean
            Return True
        End Function

        Private Sub RequestBack()
            If Not CanGoBack() Then
                Return
            End If

            Select Case CurrentStep
                Case SensitivityMatchWizardStep.Setup
                    _captureService.ResetAll()
                    CurrentStep = SensitivityMatchWizardStep.Intro
                Case SensitivityMatchWizardStep.Measure
                    _captureService.ResetMeasurements()
                    CurrentStep = SensitivityMatchWizardStep.Setup
            End Select

            RefreshFromSnapshot(allowAutoStepAdvance:=False)
        End Sub

        Private Function CanGoBack() As Boolean
            Return CurrentStep <> SensitivityMatchWizardStep.Intro
        End Function

        Private Sub RequestContinueFromSetup()
            If Not CanContinueFromSetup() Then
                Return
            End If

            CurrentStep = SensitivityMatchWizardStep.Measure
            RefreshStatus()
            RaiseCanExecuteChanges()
        End Sub

        Private Function CanContinueFromSetup() As Boolean
            Return CurrentStep = SensitivityMatchWizardStep.Setup AndAlso
                   HasMinimumDeviceCount AndAlso
                   Not IsSourceBindingPending AndAlso
                   Not IsTargetBindingPending AndAlso
                   _latestSnapshot IsNot Nothing AndAlso
                   _latestSnapshot.HasSourceDevice AndAlso
                   _latestSnapshot.HasTargetDevice AndAlso
                   Not _latestSnapshot.IsSourceDisconnected AndAlso
                   Not _latestSnapshot.IsTargetDisconnected AndAlso
                   HasValidDpiInputs()
        End Function

        Private Sub RequestBindSlot(parameter As Object)
            Dim slot = ResolveBindingSlot(parameter)
            If Not slot.HasValue Then
                Return
            End If

            If IsSlotBound(slot.Value) AndAlso Not IsSlotPending(slot.Value) Then
                _captureService.UnbindSlot(slot.Value)
                RefreshFromSnapshot(allowAutoStepAdvance:=False)
                Return
            End If

            If Not _captureService.BeginBinding(slot.Value) Then
                RefreshFromSnapshot(allowAutoStepAdvance:=False)
                Return
            End If

            RefreshFromSnapshot(allowAutoStepAdvance:=False)
        End Sub

        Private Function IsSlotBound(slot As SensitivityMatchBindingSlot) As Boolean
            If _latestSnapshot Is Nothing Then
                Return False
            End If

            If slot = SensitivityMatchBindingSlot.SourceMouse Then
                Return _latestSnapshot.HasSourceDevice
            End If

            Return _latestSnapshot.HasTargetDevice
        End Function

        Private Function IsSlotPending(slot As SensitivityMatchBindingSlot) As Boolean
            If slot = SensitivityMatchBindingSlot.SourceMouse Then
                Return IsSourceBindingPending
            End If

            Return IsTargetBindingPending
        End Function

        Private Sub RequestStartRound()
            If Not CanStartRound() Then
                Return
            End If

            Dim sourceDpi = 0
            Dim targetCurrentDpi = 0
            If Not TryParseDpi(SourceDpiText, sourceDpi) OrElse Not TryParseDpi(TargetCurrentDpiText, targetCurrentDpi) Then
                RefreshStatus()
                Return
            End If

            If Not _captureService.StartRound(sourceDpi, targetCurrentDpi) Then
                RefreshFromSnapshot(allowAutoStepAdvance:=True)
                Return
            End If

            RefreshFromSnapshot(allowAutoStepAdvance:=True)
        End Sub

        Private Function CanStartRound() As Boolean
            Return CurrentStep = SensitivityMatchWizardStep.Measure AndAlso
                   HasMinimumDeviceCount AndAlso
                   Not IsSourceBindingPending AndAlso
                   Not IsTargetBindingPending AndAlso
                   _latestSnapshot IsNot Nothing AndAlso
                   Not _latestSnapshot.HasActiveRound AndAlso
                   _latestSnapshot.CompletedRoundCount < 3 AndAlso
                   _latestSnapshot.HasSourceDevice AndAlso
                   _latestSnapshot.HasTargetDevice AndAlso
                   Not _latestSnapshot.IsSourceDisconnected AndAlso
                   Not _latestSnapshot.IsTargetDisconnected AndAlso
                   Not _latestSnapshot.ResultsExpired AndAlso
                   HasValidDpiInputs()
        End Function

        Private Sub RequestCopyRecommendedDpi()
            If Not CanCopyRecommendedDpi() OrElse _latestSnapshot Is Nothing OrElse Not _latestSnapshot.FinalRecommendedTargetDpi.HasValue Then
                Return
            End If

            Clipboard.SetText(_latestSnapshot.FinalRecommendedTargetDpi.Value.ToString(CultureInfo.InvariantCulture))
        End Sub

        Private Function CanCopyRecommendedDpi() As Boolean
            Return HasFinalRecommendation AndAlso
                   _latestSnapshot IsNot Nothing AndAlso
                   Not _latestSnapshot.ResultsExpired
        End Function

        Private Sub RequestRemeasure()
            If Not CanRemeasure() Then
                Return
            End If

            _captureService.ResetMeasurements()
            CurrentStep = SensitivityMatchWizardStep.Measure
            RefreshFromSnapshot(allowAutoStepAdvance:=False)
        End Sub

        Private Function CanRemeasure() As Boolean
            Return _latestSnapshot IsNot Nothing AndAlso
                   _latestSnapshot.HasSourceDevice AndAlso
                   _latestSnapshot.HasTargetDevice AndAlso
                   Not _latestSnapshot.IsSourceDisconnected AndAlso
                   Not _latestSnapshot.IsTargetDisconnected AndAlso
                   HasValidDpiInputs()
        End Function

        Private Sub RequestRebind()
            _captureService.ResetAll()
            CurrentStep = SensitivityMatchWizardStep.Setup
            RefreshFromSnapshot(allowAutoStepAdvance:=False)
        End Sub

        Private Sub OnDpiInputsChanged()
            _captureService.ResetMeasurements()
            RefreshFromSnapshot(allowAutoStepAdvance:=False)
        End Sub

        Private Sub OnUiTimerTick(sender As Object, e As EventArgs)
            RefreshFromSnapshot(allowAutoStepAdvance:=True)
        End Sub

        Private Sub OnDevicesChanged(sender As Object, e As EventArgs)
            RunOnUiThread(Sub() RefreshFromSnapshot(allowAutoStepAdvance:=True))
        End Sub

        Private Sub OnLanguageChanged(sender As Object, e As EventArgs)
            RunOnUiThread(Sub() RefreshFromSnapshot(allowAutoStepAdvance:=False))
        End Sub

        Private Sub RefreshFromSnapshot(allowAutoStepAdvance As Boolean)
            _latestSnapshot = _captureService.CaptureSnapshot()

            HasMinimumDeviceCount = _latestSnapshot IsNot Nothing AndAlso _latestSnapshot.HasMinimumDeviceCount
            HasFinalRecommendation = _latestSnapshot IsNot Nothing AndAlso _latestSnapshot.HasFinalRecommendation
            IsSourceBindingPending = _latestSnapshot IsNot Nothing AndAlso
                                     _latestSnapshot.PendingBindingSlot.HasValue AndAlso
                                     _latestSnapshot.PendingBindingSlot.Value = SensitivityMatchBindingSlot.SourceMouse
            IsTargetBindingPending = _latestSnapshot IsNot Nothing AndAlso
                                     _latestSnapshot.PendingBindingSlot.HasValue AndAlso
                                     _latestSnapshot.PendingBindingSlot.Value = SensitivityMatchBindingSlot.TargetMouse

            RefreshDeviceBindingText()
            RefreshProgressText()
            RefreshRoundCards()
            RefreshResultText()
            RefreshStatus()
            RefreshMeasurePresentation()
            SyncUiTimerState()
            RaiseCanExecuteChanges()
        End Sub

        Private Sub RefreshDeviceBindingText()
            AvailableDeviceCountText = L("SensitivityMatch.Setup.DeviceCount", If(_latestSnapshot Is Nothing, 0, _latestSnapshot.AvailableDeviceCount))

            UpdateBindingCard(_latestSnapshot?.SourceDevice,
                              _latestSnapshot IsNot Nothing AndAlso _latestSnapshot.IsSourceDisconnected,
                              IsSourceBindingPending,
                              SensitivityMatchBindingSlot.SourceMouse)

            UpdateBindingCard(_latestSnapshot?.TargetDevice,
                              _latestSnapshot IsNot Nothing AndAlso _latestSnapshot.IsTargetDisconnected,
                              IsTargetBindingPending,
                              SensitivityMatchBindingSlot.TargetMouse)
        End Sub

        Private Sub UpdateBindingCard(device As RawMouseDeviceInfo,
                                      isDisconnected As Boolean,
                                      isPending As Boolean,
                                      slot As SensitivityMatchBindingSlot)
            Dim emptyTitleKey = If(slot = SensitivityMatchBindingSlot.SourceMouse,
                                   "SensitivityMatch.Binding.Source.Empty",
                                   "SensitivityMatch.Binding.Target.Empty")
            Dim waitingStateKey = If(slot = SensitivityMatchBindingSlot.SourceMouse,
                                     "SensitivityMatch.Binding.Source.Waiting",
                                     "SensitivityMatch.Binding.Target.Waiting")
            Dim boundStateKey = If(slot = SensitivityMatchBindingSlot.SourceMouse,
                                   "SensitivityMatch.Binding.Source.Bound",
                                   "SensitivityMatch.Binding.Target.Bound")
            Dim disconnectedStateKey = If(slot = SensitivityMatchBindingSlot.SourceMouse,
                                          "SensitivityMatch.Binding.Source.Disconnected",
                                          "SensitivityMatch.Binding.Target.Disconnected")
            Dim labelKey = If(slot = SensitivityMatchBindingSlot.SourceMouse,
                              "SensitivityMatch.Binding.Source.Title",
                              "SensitivityMatch.Binding.Target.Title")

            Dim titleText = If(device Is Nothing, L(emptyTitleKey), device.DisplayName)
            Dim isUnbound = device Is Nothing
            Dim metaText = If(isUnbound,
                              String.Empty,
                              If(device Is Nothing, L("SensitivityMatch.Binding.EmptyMeta"), BuildDeviceMeta(device)))
            Dim stateText = If(isPending,
                               L(waitingStateKey),
                               If(device Is Nothing,
                                  If(isUnbound, String.Empty, L("SensitivityMatch.Binding.Unbound")),
                                  If(isDisconnected, L(disconnectedStateKey), L(boundStateKey))))
            Dim buttonText = If(device Is Nothing,
                                L("SensitivityMatch.Button.ClickBindMouse"),
                                L("SensitivityMatch.Button.CancelBindMouse"))

            If slot = SensitivityMatchBindingSlot.SourceMouse Then
                SourceDeviceTitle = titleText
                SourceDeviceMetaText = metaText
                SourceBindingStateText = stateText
                SourceBindButtonText = buttonText
                SourceInfoBand.LabelText = L(labelKey)
                SourceInfoBand.DeviceTitle = titleText
                SourceInfoBand.BindingStateText = stateText
                SourceInfoBand.MetaText = metaText
            Else
                TargetDeviceTitle = titleText
                TargetDeviceMetaText = metaText
                TargetBindingStateText = stateText
                TargetBindButtonText = buttonText
                TargetInfoBand.LabelText = L(labelKey)
                TargetInfoBand.DeviceTitle = titleText
                TargetInfoBand.BindingStateText = stateText
                TargetInfoBand.MetaText = metaText
            End If
        End Sub

        Private Sub RefreshProgressText()
            Dim currentRound = If(_latestSnapshot Is Nothing, Nothing, _latestSnapshot.CurrentRound)
            Dim hasExpiredMeasureState = _latestSnapshot IsNot Nothing AndAlso
                                         (_latestSnapshot.ResultsExpired OrElse
                                          _latestSnapshot.IsSourceDisconnected OrElse
                                          _latestSnapshot.IsTargetDisconnected)

            If hasExpiredMeasureState Then
                OverallProgressValue = 0.0
                SourceProgressValue = 0.0
                TargetProgressValue = 0.0
                OverallProgressText = L("SensitivityMatch.Measure.Progress.Pending")
                SourceProgressText = L("SensitivityMatch.Measure.SourceProgress.Pending")
                TargetProgressText = L("SensitivityMatch.Measure.TargetProgress.Pending")
                StartRoundButtonText = ResolveStartRoundButtonText()
                Return
            End If

            If currentRound Is Nothing Then
                OverallProgressValue = 0.0
                SourceProgressValue = 0.0
                TargetProgressValue = 0.0
                OverallProgressText = L("SensitivityMatch.Measure.Progress.Pending")
                SourceProgressText = L("SensitivityMatch.Measure.SourceProgress.Pending")
                TargetProgressText = L("SensitivityMatch.Measure.TargetProgress.Pending")
                StartRoundButtonText = ResolveStartRoundButtonText()
                Return
            End If

            OverallProgressValue = currentRound.OverallProgress * 100.0
            SourceProgressValue = currentRound.SourceProgress * 100.0
            TargetProgressValue = currentRound.TargetProgress * 100.0
            OverallProgressText = L("SensitivityMatch.Measure.Progress.Value", CInt(Math.Round(OverallProgressValue)))
            SourceProgressText = L("SensitivityMatch.Measure.SourceProgress.Value", CInt(Math.Round(SourceProgressValue)))
            TargetProgressText = L("SensitivityMatch.Measure.TargetProgress.Value", CInt(Math.Round(TargetProgressValue)))
            StartRoundButtonText = ResolveStartRoundButtonText()
        End Sub

        Private Sub RefreshRoundCards()
            Dim hasExpiredMeasureState = _latestSnapshot IsNot Nothing AndAlso
                                         (_latestSnapshot.ResultsExpired OrElse
                                          _latestSnapshot.IsSourceDisconnected OrElse
                                          _latestSnapshot.IsTargetDisconnected)

            For roundIndex = 1 To _rounds.Count
                Dim currentRoundIndex = roundIndex
                Dim card = _rounds(currentRoundIndex - 1)
                card.Title = L("SensitivityMatch.Measure.RoundTitle", currentRoundIndex)
                card.IsCompleted = False
                card.IsCurrent = False
                card.IsFailed = False
                card.ShowProgressTrack = False
                card.SourceTrackProgressValue = 0.0
                card.TargetTrackProgressValue = 0.0
                card.TrackProgressValue = 0.0
                card.TrackCaptionText = String.Empty
                card.ValueText = "--"
                card.DetailText = L("SensitivityMatch.Measure.Round.Pending")
                card.StatusText = L("SensitivityMatch.Measure.Round.Status.Pending")

                Dim completed = If(_latestSnapshot Is Nothing,
                                   Nothing,
                                   _latestSnapshot.CompletedRounds.FirstOrDefault(Function(item) item.RoundIndex = currentRoundIndex))
                If completed IsNot Nothing Then
                    card.IsCompleted = True
                    card.StatusText = L("SensitivityMatch.Measure.Round.Status.Completed")
                    card.ValueText = FormatScale(completed.Scale)
                    card.DetailText = L("SensitivityMatch.Measure.Round.Result", completed.RecommendedTargetDpi)
                    Continue For
                End If

                If _latestSnapshot IsNot Nothing AndAlso
                   _latestSnapshot.HasActiveRound AndAlso
                   _latestSnapshot.CurrentRound.RoundIndex = currentRoundIndex Then
                    card.IsCurrent = True
                    card.StatusText = If(_latestSnapshot.CurrentRound.Stage = SensitivityMatchRoundStage.Stabilizing,
                                         L("SensitivityMatch.Measure.Round.Status.Finishing"),
                                         L("SensitivityMatch.Measure.Round.Status.Active"))
                    card.ValueText = L("SensitivityMatch.Measure.Round.Progress",
                                       CInt(Math.Round(_latestSnapshot.CurrentRound.OverallProgress * 100.0)))
                    card.DetailText = L("SensitivityMatch.Measure.Round.ProgressDetail",
                                        CInt(Math.Round(_latestSnapshot.CurrentRound.SourceProgress * 100.0)),
                                        CInt(Math.Round(_latestSnapshot.CurrentRound.TargetProgress * 100.0)))
                    card.ShowProgressTrack = Not hasExpiredMeasureState
                    card.SourceTrackProgressValue = _latestSnapshot.CurrentRound.SourceProgress * 100.0
                    card.TargetTrackProgressValue = _latestSnapshot.CurrentRound.TargetProgress * 100.0
                    card.TrackProgressValue = _latestSnapshot.CurrentRound.OverallProgress * 100.0
                    card.TrackCaptionText = card.DetailText
                    Continue For
                End If

                If _latestSnapshot IsNot Nothing AndAlso
                   _latestSnapshot.LastRoundFailureReason <> SensitivityMatchRoundFailureReason.None AndAlso
                   _latestSnapshot.LastRoundFailureIndex = currentRoundIndex Then
                    card.IsFailed = True
                    card.StatusText = L("SensitivityMatch.Measure.Round.Status.Failed")
                    card.ValueText = L("SensitivityMatch.Measure.Round.Failed")
                    card.DetailText = ResolveRoundFailureText(_latestSnapshot.LastRoundFailureReason)
                    Continue For
                End If

                If _latestSnapshot IsNot Nothing AndAlso
                   Not hasExpiredMeasureState AndAlso
                   currentRoundIndex = _latestSnapshot.CompletedRoundCount + 1 Then
                    card.StatusText = L("SensitivityMatch.Measure.Round.Status.Ready")
                    card.DetailText = L("SensitivityMatch.Measure.Round.Ready")
                    card.ShowProgressTrack = True
                    card.TrackCaptionText = card.DetailText
                End If
            Next
        End Sub

        Private Sub RefreshResultText()
            If _latestSnapshot Is Nothing OrElse Not _latestSnapshot.HasFinalRecommendation Then
                RecommendedTargetDpiText = "--"
                ScaleText = "--"
                ConsistencyText = "--"
                Return
            End If

            RecommendedTargetDpiText = _latestSnapshot.FinalRecommendedTargetDpi.Value.ToString(CultureInfo.InvariantCulture)
            ScaleText = FormatScale(_latestSnapshot.FinalScale.Value)
            ConsistencyText = ResolveConsistencyText(_latestSnapshot.ConsistencyPercent, _latestSnapshot.ConsistencyLevel)
        End Sub

        Private Sub RefreshStatus()
            If IsIntroStep Then
                StatusPillText = L("SensitivityMatch.Status.Intro.Pill")
                StatusMessage = L("SensitivityMatch.Status.Intro.Message")
                HintText = L("SensitivityMatch.Status.Intro.Hint")
                Return
            End If

            If IsSetupStep Then
                ApplySetupStatus()
                Return
            End If

            If IsMeasureStep Then
                ApplyMeasureStatus()
                Return
            End If
        End Sub

        Private Sub ApplySetupStatus()
            If _latestSnapshot Is Nothing OrElse Not _latestSnapshot.HasMinimumDeviceCount Then
                StatusPillText = L("SensitivityMatch.Status.NeedDevices.Pill")
                StatusMessage = L("SensitivityMatch.Status.NeedDevices.Message")
                HintText = L("SensitivityMatch.Status.NeedDevices.Hint")
                Return
            End If

            If IsSourceBindingPending Then
                StatusPillText = L("SensitivityMatch.Status.Binding.Pill")
                StatusMessage = L("SensitivityMatch.Status.Binding.SourceMessage")
                HintText = L("SensitivityMatch.Status.Binding.Hint")
                Return
            End If

            If IsTargetBindingPending Then
                StatusPillText = L("SensitivityMatch.Status.Binding.Pill")
                StatusMessage = L("SensitivityMatch.Status.Binding.TargetMessage")
                HintText = L("SensitivityMatch.Status.Binding.Hint")
                Return
            End If

            If _latestSnapshot.LastBindingIssue = SensitivityMatchBindingIssue.TimedOut Then
                StatusPillText = L("SensitivityMatch.Status.BindingTimeout.Pill")
                StatusMessage = L("SensitivityMatch.Status.BindingTimeout.Message")
                HintText = L("SensitivityMatch.Status.BindingTimeout.Hint")
                Return
            End If

            If _latestSnapshot.LastBindingIssue = SensitivityMatchBindingIssue.ConflictWithOtherSlot Then
                StatusPillText = L("SensitivityMatch.Status.BindingConflict.Pill")
                StatusMessage = L("SensitivityMatch.Status.BindingConflict.Message")
                HintText = L("SensitivityMatch.Status.BindingConflict.Hint")
                Return
            End If

            If Not HasValidDpiInputs() Then
                StatusPillText = L("SensitivityMatch.Status.NeedDpi.Pill")
                StatusMessage = L("SensitivityMatch.Status.NeedDpi.Message")
                HintText = L("SensitivityMatch.Status.NeedDpi.Hint", MinimumDpi, MaximumDpi)
                Return
            End If

            If Not _latestSnapshot.HasSourceDevice OrElse Not _latestSnapshot.HasTargetDevice Then
                StatusPillText = L("SensitivityMatch.Status.NeedBinding.Pill")
                StatusMessage = L("SensitivityMatch.Status.NeedBinding.Message")
                HintText = L("SensitivityMatch.Status.NeedBinding.Hint")
                Return
            End If

            If _latestSnapshot.IsSourceDisconnected OrElse _latestSnapshot.IsTargetDisconnected Then
                StatusPillText = L("SensitivityMatch.Status.BindingDisconnected.Pill")
                StatusMessage = L("SensitivityMatch.Status.BindingDisconnected.Message")
                HintText = L("SensitivityMatch.Status.BindingDisconnected.Hint")
                Return
            End If

            StatusPillText = String.Empty
            StatusMessage = L("SensitivityMatch.Status.SetupReady.Message")
            HintText = L("SensitivityMatch.Status.SetupReady.Hint")
        End Sub

        Private Sub ApplyMeasureStatus()
            If _latestSnapshot Is Nothing Then
                Return
            End If

            If _latestSnapshot.HasFinalRecommendation AndAlso Not _latestSnapshot.HasActiveRound Then
                ApplyResultStatus()
                Return
            End If

            If _latestSnapshot.ResultsExpired OrElse _latestSnapshot.IsSourceDisconnected OrElse _latestSnapshot.IsTargetDisconnected Then
                StatusPillText = L("SensitivityMatch.Status.MeasureExpired.Pill")
                StatusMessage = L("SensitivityMatch.Status.MeasureExpired.Message")
                HintText = L("SensitivityMatch.Status.MeasureExpired.Hint")
                Return
            End If

            If _latestSnapshot.HasActiveRound Then
                If _latestSnapshot.CurrentRound.Stage = SensitivityMatchRoundStage.Stabilizing Then
                    StatusPillText = L("SensitivityMatch.Status.Stabilizing.Pill")
                    StatusMessage = L("SensitivityMatch.Status.Stabilizing.Message", _latestSnapshot.CurrentRound.RoundIndex)
                    HintText = L("SensitivityMatch.Status.Stabilizing.Hint")
                Else
                    StatusPillText = L("SensitivityMatch.Status.Measuring.Pill")
                    StatusMessage = L("SensitivityMatch.Status.Measuring.Message", _latestSnapshot.CurrentRound.RoundIndex)
                    HintText = L("SensitivityMatch.Status.Measuring.Hint")
                End If
                Return
            End If

            If _latestSnapshot.LastRoundFailureReason <> SensitivityMatchRoundFailureReason.None Then
                StatusPillText = L("SensitivityMatch.Status.RoundFailed.Pill")
                StatusMessage = ResolveRoundFailureText(_latestSnapshot.LastRoundFailureReason)
                HintText = L("SensitivityMatch.Status.RoundFailed.Hint")
                Return
            End If

            StatusPillText = L("SensitivityMatch.Status.MeasureReady.Pill")
            StatusMessage = L("SensitivityMatch.Status.MeasureReady.Message", _latestSnapshot.CompletedRoundCount + 1)
            HintText = L("SensitivityMatch.Status.MeasureReady.Hint")
        End Sub

        Private Sub ApplyResultStatus()
            If _latestSnapshot Is Nothing OrElse Not _latestSnapshot.HasFinalRecommendation Then
                StatusPillText = L("SensitivityMatch.Status.ResultPending.Pill")
                StatusMessage = L("SensitivityMatch.Status.ResultPending.Message")
                HintText = L("SensitivityMatch.Status.ResultPending.Hint")
                Return
            End If

            If _latestSnapshot.ResultsExpired Then
                StatusPillText = L("SensitivityMatch.Status.ResultExpired.Pill")
                StatusMessage = L("SensitivityMatch.Status.ResultExpired.Message")
                HintText = L("SensitivityMatch.Status.ResultExpired.Hint")
                Return
            End If

            StatusPillText = L("SensitivityMatch.Status.ResultReady.Pill")
            StatusMessage = L("SensitivityMatch.Status.ResultReady.Message")
            HintText = ResolveConsistencyHint(_latestSnapshot.ConsistencyLevel)
        End Sub

        Private Sub RefreshMeasurePresentation()
            Dim currentRound = If(_latestSnapshot Is Nothing, Nothing, _latestSnapshot.CurrentRound)
            Dim isExpired = _latestSnapshot IsNot Nothing AndAlso
                            (_latestSnapshot.ResultsExpired OrElse
                             _latestSnapshot.IsSourceDisconnected OrElse
                             _latestSnapshot.IsTargetDisconnected)
            Dim isActive = currentRound IsNot Nothing AndAlso Not isExpired
            Dim isFailed = Not isActive AndAlso
                           Not isExpired AndAlso
                           _latestSnapshot IsNot Nothing AndAlso
                           _latestSnapshot.LastRoundFailureReason <> SensitivityMatchRoundFailureReason.None

            IsMeasureExpiredState = isExpired
            IsMeasureActiveState = isActive
            IsMeasureFailureState = isFailed

            If isActive Then
                MeasureHeroPrimaryText = currentRound.RoundIndex.ToString("00", CultureInfo.InvariantCulture)
                MeasureHeroLabelText = String.Empty
                MeasureHeroSupportText = If(currentRound.Stage = SensitivityMatchRoundStage.Stabilizing,
                                            StatusMessage,
                                            String.Empty)
                MeasureSourceValueText = FormatPercentageText(currentRound.SourceProgress * 100.0)
                MeasureTargetValueText = FormatPercentageText(currentRound.TargetProgress * 100.0)
                Return
            End If

            MeasureSourceValueText = "--"
            MeasureTargetValueText = "--"

            If isExpired Then
                MeasureHeroPrimaryText = "--"
                MeasureHeroLabelText = String.Empty
                MeasureHeroSupportText = StatusMessage
                Return
            End If

            If isFailed Then
                Dim failedRoundIndex = Math.Max(1,
                                                If(_latestSnapshot.LastRoundFailureIndex > 0,
                                                   _latestSnapshot.LastRoundFailureIndex,
                                                   _latestSnapshot.CompletedRoundCount + 1))
                MeasureHeroPrimaryText = failedRoundIndex.ToString("00", CultureInfo.InvariantCulture)
                MeasureHeroLabelText = String.Empty
                MeasureHeroSupportText = StatusMessage
                Return
            End If

            Dim nextRoundIndex = 1
            If _latestSnapshot IsNot Nothing Then
                nextRoundIndex = Math.Max(1, Math.Min(_rounds.Count, _latestSnapshot.CompletedRoundCount + 1))
            End If

            MeasureHeroPrimaryText = nextRoundIndex.ToString("00", CultureInfo.InvariantCulture)
            MeasureHeroLabelText = String.Empty
            MeasureHeroSupportText = StatusMessage
        End Sub

        Private Function ResolveStartRoundButtonText() As String
            If _latestSnapshot IsNot Nothing AndAlso _latestSnapshot.HasFinalRecommendation Then
                Return L("SensitivityMatch.Button.Remeasure")
            End If

            If _latestSnapshot IsNot Nothing AndAlso
               (_latestSnapshot.ResultsExpired OrElse
                _latestSnapshot.IsSourceDisconnected OrElse
                _latestSnapshot.IsTargetDisconnected) Then
                Dim expiredRoundIndex = Math.Max(1, Math.Min(_rounds.Count, _latestSnapshot.CompletedRoundCount + 1))
                Return L("SensitivityMatch.Button.StartRound", expiredRoundIndex)
            End If

            If _latestSnapshot IsNot Nothing AndAlso _latestSnapshot.HasActiveRound Then
                If _latestSnapshot.CurrentRound.Stage = SensitivityMatchRoundStage.Stabilizing Then
                    Return L("SensitivityMatch.Button.Stabilizing")
                End If

                Return L("SensitivityMatch.Button.Measuring")
            End If

            If _latestSnapshot IsNot Nothing AndAlso _latestSnapshot.LastRoundFailureReason <> SensitivityMatchRoundFailureReason.None Then
                Return L("SensitivityMatch.Button.RetryRound")
            End If

            Dim nextRoundIndex = If(_latestSnapshot Is Nothing, 1, _latestSnapshot.CompletedRoundCount + 1)
            Return L("SensitivityMatch.Button.StartRound", nextRoundIndex)
        End Function

        Private Function ResolveBindingSlot(parameter As Object) As Nullable(Of SensitivityMatchBindingSlot)
            If parameter Is Nothing Then
                Return Nothing
            End If

            If TypeOf parameter Is SensitivityMatchBindingSlot Then
                Return DirectCast(parameter, SensitivityMatchBindingSlot)
            End If

            Dim parsed As SensitivityMatchBindingSlot
            If [Enum].TryParse(parameter.ToString(), True, parsed) Then
                Return parsed
            End If

            Return Nothing
        End Function

        Private Function HasValidDpiInputs() As Boolean
            Dim sourceDpi = 0
            Dim targetCurrentDpi = 0
            Return TryParseDpi(SourceDpiText, sourceDpi) AndAlso TryParseDpi(TargetCurrentDpiText, targetCurrentDpi)
        End Function

        Private Shared Function TryParseDpi(text As String, ByRef value As Integer) As Boolean
            value = 0
            If String.IsNullOrWhiteSpace(text) Then
                Return False
            End If

            If Not Integer.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, value) Then
                Return False
            End If

            Return value >= MinimumDpi AndAlso value <= MaximumDpi
        End Function

        Private Function BuildDeviceMeta(device As RawMouseDeviceInfo) As String
            If device Is Nothing Then
                Return L("SensitivityMatch.Binding.EmptyMeta")
            End If

            Dim parts As New List(Of String)()
            If Not String.IsNullOrWhiteSpace(device.VendorProductLabel) Then
                parts.Add(device.VendorProductLabel)
            End If

            If device.ButtonCount > 0 Then
                parts.Add(L("Device.Detail.Buttons", device.ButtonCount))
            End If

            If device.IsVirtual Then
                parts.Add(L("Device.Detail.Virtual"))
            Else
                parts.Add(L("Device.Detail.RawInput"))
            End If

            If parts.Count = 0 Then
                Return L("SensitivityMatch.Binding.EmptyMeta")
            End If

            Return String.Join("  /  ", parts)
        End Function

        Private Function ResolveRoundFailureText(reason As SensitivityMatchRoundFailureReason) As String
            Select Case reason
                Case SensitivityMatchRoundFailureReason.Timeout
                    Return L("SensitivityMatch.Measure.Fail.Timeout")
                Case SensitivityMatchRoundFailureReason.TooFast
                    Return L("SensitivityMatch.Measure.Fail.TooFast")
                Case SensitivityMatchRoundFailureReason.InsufficientPackets
                    Return L("SensitivityMatch.Measure.Fail.InsufficientPackets")
                Case SensitivityMatchRoundFailureReason.ExcessiveCurvature
                    Return L("SensitivityMatch.Measure.Fail.Curved")
                Case SensitivityMatchRoundFailureReason.DirectionMismatch
                    Return L("SensitivityMatch.Measure.Fail.DirectionMismatch")
                Case SensitivityMatchRoundFailureReason.Unsynchronized
                    Return L("SensitivityMatch.Measure.Fail.Unsynchronized")
                Case Else
                    Return L("SensitivityMatch.Measure.Fail.Unknown")
            End Select
        End Function

        Private Function ResolveConsistencyBadge(level As SensitivityMatchConsistencyLevel) As String
            Select Case level
                Case SensitivityMatchConsistencyLevel.Excellent
                    Return L("SensitivityMatch.Result.Consistency.Excellent")
                Case SensitivityMatchConsistencyLevel.Good
                    Return L("SensitivityMatch.Result.Consistency.Good")
                Case SensitivityMatchConsistencyLevel.Fair
                    Return L("SensitivityMatch.Result.Consistency.Fair")
                Case SensitivityMatchConsistencyLevel.Poor
                    Return L("SensitivityMatch.Result.Consistency.Poor")
                Case Else
                    Return L("SensitivityMatch.Result.Pending")
            End Select
        End Function

        Private Function ResolveConsistencyHint(level As SensitivityMatchConsistencyLevel) As String
            Select Case level
                Case SensitivityMatchConsistencyLevel.Excellent
                    Return L("SensitivityMatch.Result.Hint.Excellent")
                Case SensitivityMatchConsistencyLevel.Good
                    Return L("SensitivityMatch.Result.Hint.Good")
                Case SensitivityMatchConsistencyLevel.Fair
                    Return L("SensitivityMatch.Result.Hint.Fair")
                Case SensitivityMatchConsistencyLevel.Poor
                    Return L("SensitivityMatch.Result.Hint.Poor")
                Case Else
                    Return L("SensitivityMatch.Status.ResultPending.Hint")
            End Select
        End Function

        Private Shared Function FormatScale(value As Double) As String
            Return value.ToString("0.000x", CultureInfo.InvariantCulture)
        End Function

        Private Shared Function FormatPercentageText(value As Double) As String
            Return CInt(Math.Round(value)).ToString(CultureInfo.InvariantCulture) & "%"
        End Function

        Private Function ResolveConsistencyText(consistencyPercent As Nullable(Of Double),
                                                consistencyLevel As SensitivityMatchConsistencyLevel) As String
            If Not consistencyPercent.HasValue Then
                Return "--"
            End If

            Return String.Format(CultureInfo.InvariantCulture,
                                 "{0}  /  {1:0.00}%",
                                 ResolveConsistencyBadge(consistencyLevel),
                                 consistencyPercent.Value)
        End Function

        Private Function L(key As String, ParamArray args() As Object) As String
            Return _localization.GetString(key, args)
        End Function

        Private Sub RaiseCanExecuteChanges()
            _startSetupCommand.RaiseCanExecuteChanged()
            _backCommand.RaiseCanExecuteChanged()
            _continueFromSetupCommand.RaiseCanExecuteChanged()
            _startRoundCommand.RaiseCanExecuteChanged()
            _copyRecommendedDpiCommand.RaiseCanExecuteChanged()
            _remeasureCommand.RaiseCanExecuteChanged()
            RaisePropertyChanged(NameOf(IsContinueFromSetupEnabled))
        End Sub

        Private Sub SyncUiTimerState()
            If Not _isPageActive Then
                _uiTimer.Stop()
                Return
            End If

            Dim shouldRunTimer = _latestSnapshot IsNot Nothing AndAlso
                                 (_latestSnapshot.HasPendingBinding OrElse _latestSnapshot.HasActiveRound)

            If shouldRunTimer Then
                If Not _uiTimer.IsEnabled Then
                    _uiTimer.Start()
                End If

                Return
            End If

            If _uiTimer.IsEnabled Then
                _uiTimer.Stop()
            End If
        End Sub

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

        Private Sub RunOnUiThread(action As Action)
            If action Is Nothing Then
                Return
            End If

            If _dispatcher.CheckAccess() Then
                action()
                Return
            End If

            _dispatcher.BeginInvoke(action)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then
                Return
            End If

            _disposed = True
            _uiTimer.Stop()
            RemoveHandler _localization.LanguageChanged, AddressOf OnLanguageChanged
            RemoveHandler _uiTimer.Tick, AddressOf OnUiTimerTick
            RemoveHandler _captureService.DevicesChanged, AddressOf OnDevicesChanged
            _captureService.Dispose()
        End Sub
    End Class
End Namespace
