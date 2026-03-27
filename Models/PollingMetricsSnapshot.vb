Namespace Models
    Public Class PollingMetricsSnapshot
        Private ReadOnly _currentRate As Integer
        Private ReadOnly _peakRate As Integer
        Private ReadOnly _windowJitterMs As Nullable(Of Double)
        Private ReadOnly _droppedPacketCount As Long
        Private ReadOnly _captureLatencyP95Ms As Nullable(Of Double)

        Public Sub New(currentRate As Integer,
                       peakRate As Integer,
                       windowJitterMs As Nullable(Of Double),
                       droppedPacketCount As Long,
                       captureLatencyP95Ms As Nullable(Of Double))
            _currentRate = currentRate
            _peakRate = peakRate
            _windowJitterMs = windowJitterMs
            _droppedPacketCount = droppedPacketCount
            _captureLatencyP95Ms = captureLatencyP95Ms
        End Sub

        Public ReadOnly Property CurrentRate As Integer
            Get
                Return _currentRate
            End Get
        End Property

        Public ReadOnly Property PeakRate As Integer
            Get
                Return _peakRate
            End Get
        End Property

        Public ReadOnly Property WindowJitterMs As Nullable(Of Double)
            Get
                Return _windowJitterMs
            End Get
        End Property

        Public ReadOnly Property DroppedPacketCount As Long
            Get
                Return _droppedPacketCount
            End Get
        End Property

        Public ReadOnly Property CaptureLatencyP95Ms As Nullable(Of Double)
            Get
                Return _captureLatencyP95Ms
            End Get
        End Property
    End Class
End Namespace
