Imports System.Runtime.Versioning
Imports System.Windows.Threading
Imports WpfApp1.Infrastructure
Imports WpfApp1.Models
Imports WpfApp1.Services

Namespace ViewModels.Pages
    <SupportedOSPlatform("windows")>
    Public Class KeyDetectionButtonCardViewModel
        Inherits BindableBase
        Implements IDisposable

        Private Const DoublePulseDurationMilliseconds As Double = 350.0

        Private ReadOnly _buttonKind As MouseButtonKind
        Private ReadOnly _stats As InputTimingStatistics
        Private ReadOnly _doublePulseTimer As DispatcherTimer

        Private _title As String
        Private _doubleClickCountText As String
        Private _downCountText As String
        Private _upCountText As String
        Private _currentDownDownText As String
        Private _minimumDownDownText As String
        Private _averageDownDownText As String
        Private _currentDownUpText As String
        Private _minimumDownUpText As String
        Private _averageDownUpText As String
        Private _isPressed As Boolean
        Private _isDoublePulseActive As Boolean
        Private _disposed As Boolean

        Public Sub New(buttonKind As MouseButtonKind)
            _buttonKind = buttonKind
            _stats = New InputTimingStatistics()
            _doublePulseTimer = New DispatcherTimer()
            _doublePulseTimer.Interval = TimeSpan.FromMilliseconds(DoublePulseDurationMilliseconds)
            AddHandler _doublePulseTimer.Tick, AddressOf OnDoublePulseTimerTick

            SyncDisplay()
        End Sub

        Public ReadOnly Property ButtonKind As MouseButtonKind
            Get
                Return _buttonKind
            End Get
        End Property

        Public Property Title As String
            Get
                Return _title
            End Get
            Private Set(value As String)
                SetProperty(_title, value)
            End Set
        End Property

        Public Property DoubleClickCountText As String
            Get
                Return _doubleClickCountText
            End Get
            Private Set(value As String)
                SetProperty(_doubleClickCountText, value)
            End Set
        End Property

        Public Property DownCountText As String
            Get
                Return _downCountText
            End Get
            Private Set(value As String)
                SetProperty(_downCountText, value)
            End Set
        End Property

        Public Property UpCountText As String
            Get
                Return _upCountText
            End Get
            Private Set(value As String)
                SetProperty(_upCountText, value)
            End Set
        End Property

        Public Property CurrentDownDownText As String
            Get
                Return _currentDownDownText
            End Get
            Private Set(value As String)
                SetProperty(_currentDownDownText, value)
            End Set
        End Property

        Public Property MinimumDownDownText As String
            Get
                Return _minimumDownDownText
            End Get
            Private Set(value As String)
                SetProperty(_minimumDownDownText, value)
            End Set
        End Property

        Public Property AverageDownDownText As String
            Get
                Return _averageDownDownText
            End Get
            Private Set(value As String)
                SetProperty(_averageDownDownText, value)
            End Set
        End Property

        Public Property CurrentDownUpText As String
            Get
                Return _currentDownUpText
            End Get
            Private Set(value As String)
                SetProperty(_currentDownUpText, value)
            End Set
        End Property

        Public Property MinimumDownUpText As String
            Get
                Return _minimumDownUpText
            End Get
            Private Set(value As String)
                SetProperty(_minimumDownUpText, value)
            End Set
        End Property

        Public Property AverageDownUpText As String
            Get
                Return _averageDownUpText
            End Get
            Private Set(value As String)
                SetProperty(_averageDownUpText, value)
            End Set
        End Property

        Public Property IsPressed As Boolean
            Get
                Return _isPressed
            End Get
            Private Set(value As Boolean)
                SetProperty(_isPressed, value)
            End Set
        End Property

        Public Property IsDoublePulseActive As Boolean
            Get
                Return _isDoublePulseActive
            End Get
            Private Set(value As Boolean)
                SetProperty(_isDoublePulseActive, value)
            End Set
        End Property

        Public Sub RefreshLocalization(localization As LocalizationManager)
            If localization Is Nothing Then
                Return
            End If

            Title = localization.GetString(ResolveTitleKey())
        End Sub

        Public Sub RegisterDown(timestampMs As Double, doubleClickThresholdMs As Double)
            Dim triggeredDoubleClick = _stats.RegisterDown(timestampMs, doubleClickThresholdMs)
            SyncDisplay()

            If triggeredDoubleClick Then
                PulseDoubleHighlight()
            End If
        End Sub

        Public Sub RegisterUp(timestampMs As Double)
            _stats.RegisterUp(timestampMs)
            SyncDisplay()
        End Sub

        Public Sub ResetStatistics()
            _stats.Reset()
            _doublePulseTimer.Stop()
            IsDoublePulseActive = False
            SyncDisplay()
        End Sub

        Public Sub ResetPressedState()
            _stats.ResetPressedState()
            SyncDisplay()
        End Sub

        Private Sub SyncDisplay()
            DoubleClickCountText = FormatCount(_stats.DoubleClickCount)
            DownCountText = FormatCount(_stats.DownCount)
            UpCountText = FormatCount(_stats.UpCount)
            CurrentDownDownText = FormatMilliseconds(_stats.CurrentDownDownMs)
            MinimumDownDownText = FormatMilliseconds(_stats.MinimumDownDownMs)
            AverageDownDownText = FormatMilliseconds(_stats.AverageDownDownMs)
            CurrentDownUpText = FormatMilliseconds(_stats.CurrentDownUpMs)
            MinimumDownUpText = FormatMilliseconds(_stats.MinimumDownUpMs)
            AverageDownUpText = FormatMilliseconds(_stats.AverageDownUpMs)
            IsPressed = _stats.IsPressed
        End Sub

        Private Function ResolveTitleKey() As String
            Select Case ButtonKind
                Case MouseButtonKind.LeftButton
                    Return "KeyDetection.Mouse.Left"
                Case MouseButtonKind.MiddleButton
                    Return "KeyDetection.Mouse.Middle"
                Case MouseButtonKind.RightButton
                    Return "KeyDetection.Mouse.Right"
                Case MouseButtonKind.ForwardButton
                    Return "KeyDetection.Mouse.Forward"
                Case Else
                    Return "KeyDetection.Mouse.Back"
            End Select
        End Function

        Private Sub PulseDoubleHighlight()
            _doublePulseTimer.Stop()
            IsDoublePulseActive = True
            _doublePulseTimer.Start()
        End Sub

        Private Sub OnDoublePulseTimerTick(sender As Object, e As EventArgs)
            _doublePulseTimer.Stop()
            IsDoublePulseActive = False
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then
                Return
            End If

            _disposed = True
            _doublePulseTimer.Stop()
            RemoveHandler _doublePulseTimer.Tick, AddressOf OnDoublePulseTimerTick
        End Sub
    End Class
End Namespace
