Namespace Models
    Public Class AngleCalibrationTraceStroke
        Private ReadOnly _points As IReadOnlyList(Of AngleCalibrationTracePoint)
        Private ReadOnly _isCurrent As Boolean

        Public Sub New(points As IReadOnlyList(Of AngleCalibrationTracePoint), isCurrent As Boolean)
            _points = If(points, Array.Empty(Of AngleCalibrationTracePoint)())
            _isCurrent = isCurrent
        End Sub

        Public ReadOnly Property Points As IReadOnlyList(Of AngleCalibrationTracePoint)
            Get
                Return _points
            End Get
        End Property

        Public ReadOnly Property IsCurrent As Boolean
            Get
                Return _isCurrent
            End Get
        End Property
    End Class
End Namespace
