Namespace Models
    Public Class AngleCalibrationTracePoint
        Private ReadOnly _x As Double
        Private ReadOnly _y As Double

        Public Sub New(x As Double, y As Double)
            _x = x
            _y = y
        End Sub

        Public ReadOnly Property X As Double
            Get
                Return _x
            End Get
        End Property

        Public ReadOnly Property Y As Double
            Get
                Return _y
            End Get
        End Property
    End Class
End Namespace
