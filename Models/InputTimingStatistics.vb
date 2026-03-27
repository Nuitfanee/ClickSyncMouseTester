Namespace Models
    Public Class InputTimingStatistics
        Private _downCount As Integer
        Private _upCount As Integer
        Private _doubleClickCount As Integer
        Private _lastDownTimestampMs As Double
        Private _downStartTimestampMs As Double
        Private _currentDownDownMs As Nullable(Of Double)
        Private _minimumDownDownMs As Nullable(Of Double)
        Private _currentDownUpMs As Nullable(Of Double)
        Private _minimumDownUpMs As Nullable(Of Double)
        Private _downDownSum As Double
        Private _downDownSampleCount As Integer
        Private _downUpSum As Double
        Private _downUpSampleCount As Integer
        Private _hasPreviousDown As Boolean
        Private _isPressed As Boolean

        Public Sub New()
            Reset()
        End Sub

        Public ReadOnly Property DownCount As Integer
            Get
                Return _downCount
            End Get
        End Property

        Public ReadOnly Property UpCount As Integer
            Get
                Return _upCount
            End Get
        End Property

        Public ReadOnly Property DoubleClickCount As Integer
            Get
                Return _doubleClickCount
            End Get
        End Property

        Public ReadOnly Property CurrentDownDownMs As Nullable(Of Double)
            Get
                Return _currentDownDownMs
            End Get
        End Property

        Public ReadOnly Property MinimumDownDownMs As Nullable(Of Double)
            Get
                Return _minimumDownDownMs
            End Get
        End Property

        Public ReadOnly Property AverageDownDownMs As Nullable(Of Double)
            Get
                If _downDownSampleCount <= 0 Then
                    Return Nothing
                End If

                Return _downDownSum / _downDownSampleCount
            End Get
        End Property

        Public ReadOnly Property CurrentDownUpMs As Nullable(Of Double)
            Get
                Return _currentDownUpMs
            End Get
        End Property

        Public ReadOnly Property MinimumDownUpMs As Nullable(Of Double)
            Get
                Return _minimumDownUpMs
            End Get
        End Property

        Public ReadOnly Property AverageDownUpMs As Nullable(Of Double)
            Get
                If _downUpSampleCount <= 0 Then
                    Return Nothing
                End If

                Return _downUpSum / _downUpSampleCount
            End Get
        End Property

        Public ReadOnly Property IsPressed As Boolean
            Get
                Return _isPressed
            End Get
        End Property

        Public Function RegisterDown(timestampMs As Double, doubleClickThresholdMs As Double) As Boolean
            If _isPressed Then
                Return False
            End If

            Dim triggeredDoubleClick = False

            _isPressed = True
            _downStartTimestampMs = timestampMs

            If _hasPreviousDown Then
                Dim downDownMs = timestampMs - _lastDownTimestampMs
                _currentDownDownMs = downDownMs

                If downDownMs > 0.2 AndAlso downDownMs < 3000.0 Then
                    If Not _minimumDownDownMs.HasValue OrElse downDownMs < _minimumDownDownMs.Value Then
                        _minimumDownDownMs = downDownMs
                    End If

                    _downDownSum += downDownMs
                    _downDownSampleCount += 1
                End If

                If downDownMs > 0.0 AndAlso downDownMs < doubleClickThresholdMs Then
                    _doubleClickCount += 1
                    triggeredDoubleClick = True
                End If
            Else
                _currentDownDownMs = Nothing
            End If

            _downCount += 1
            _lastDownTimestampMs = timestampMs
            _hasPreviousDown = True

            Return triggeredDoubleClick
        End Function

        Public Sub RegisterUp(timestampMs As Double)
            If _isPressed AndAlso Not Double.IsNaN(_downStartTimestampMs) Then
                Dim downUpMs = timestampMs - _downStartTimestampMs
                _currentDownUpMs = downUpMs

                If downUpMs > 0.2 AndAlso downUpMs < 5000.0 Then
                    If Not _minimumDownUpMs.HasValue OrElse downUpMs < _minimumDownUpMs.Value Then
                        _minimumDownUpMs = downUpMs
                    End If

                    _downUpSum += downUpMs
                    _downUpSampleCount += 1
                End If
            End If

            _isPressed = False
            _downStartTimestampMs = Double.NaN
            _upCount += 1
        End Sub

        Public Sub Reset()
            _downCount = 0
            _upCount = 0
            _doubleClickCount = 0
            _lastDownTimestampMs = Double.NaN
            _downStartTimestampMs = Double.NaN
            _currentDownDownMs = Nothing
            _minimumDownDownMs = Nothing
            _currentDownUpMs = Nothing
            _minimumDownUpMs = Nothing
            _downDownSum = 0.0
            _downDownSampleCount = 0
            _downUpSum = 0.0
            _downUpSampleCount = 0
            _hasPreviousDown = False
            _isPressed = False
        End Sub

        Public Sub ResetPressedState()
            _isPressed = False
            _downStartTimestampMs = Double.NaN
        End Sub
    End Class
End Namespace
