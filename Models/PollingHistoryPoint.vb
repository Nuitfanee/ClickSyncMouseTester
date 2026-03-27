Namespace Models
    Public Class PollingHistoryPoint
        Private ReadOnly _timestampMs As Double
        Private ReadOnly _realtimeTimestampMs As Double
        Private ReadOnly _rate As Double

        Public Sub New(timestampMs As Double, realtimeTimestampMs As Double, rate As Double)
            _timestampMs = timestampMs
            _realtimeTimestampMs = realtimeTimestampMs
            _rate = rate
        End Sub

        Public ReadOnly Property TimestampMs As Double
            Get
                Return _timestampMs
            End Get
        End Property

        Public ReadOnly Property RealtimeTimestampMs As Double
            Get
                Return _realtimeTimestampMs
            End Get
        End Property

        Public ReadOnly Property Rate As Double
            Get
                Return _rate
            End Get
        End Property
    End Class
End Namespace
