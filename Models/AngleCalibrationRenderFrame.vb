Namespace Models
    Public Class AngleCalibrationRenderFrame
        Private ReadOnly _status As AngleCalibrationStatus
        Private ReadOnly _isLocked As Boolean
        Private ReadOnly _hasData As Boolean
        Private ReadOnly _recommendedAngleDegrees As Nullable(Of Double)
        Private ReadOnly _swipeCount As Integer
        Private ReadOnly _sampleCount As Integer
        Private ReadOnly _stabilityDegrees As Nullable(Of Double)
        Private ReadOnly _traceStrokes As IReadOnlyList(Of AngleCalibrationTraceStroke)
        Private ReadOnly _qualityLevel As AngleCalibrationQualityLevel
        Private ReadOnly _qualityReason As AngleCalibrationQualityReason
        Private ReadOnly _qualityScore As Integer

        Friend Sub New(status As AngleCalibrationStatus,
                       isLocked As Boolean,
                       hasData As Boolean,
                       recommendedAngleDegrees As Nullable(Of Double),
                       swipeCount As Integer,
                       sampleCount As Integer,
                       stabilityDegrees As Nullable(Of Double),
                       traceStrokes As IReadOnlyList(Of AngleCalibrationTraceStroke),
                       qualityLevel As AngleCalibrationQualityLevel,
                       qualityReason As AngleCalibrationQualityReason,
                       qualityScore As Integer)
            _status = status
            _isLocked = isLocked
            _hasData = hasData
            _recommendedAngleDegrees = recommendedAngleDegrees
            _swipeCount = swipeCount
            _sampleCount = sampleCount
            _stabilityDegrees = stabilityDegrees
            _traceStrokes = If(traceStrokes, Array.Empty(Of AngleCalibrationTraceStroke)())
            _qualityLevel = qualityLevel
            _qualityReason = qualityReason
            _qualityScore = Math.Max(0, Math.Min(100, qualityScore))
        End Sub

        Public ReadOnly Property Status As AngleCalibrationStatus
            Get
                Return _status
            End Get
        End Property

        Public ReadOnly Property IsLocked As Boolean
            Get
                Return _isLocked
            End Get
        End Property

        Public ReadOnly Property HasData As Boolean
            Get
                Return _hasData
            End Get
        End Property

        Public ReadOnly Property RecommendedAngleDegrees As Nullable(Of Double)
            Get
                Return _recommendedAngleDegrees
            End Get
        End Property

        Public ReadOnly Property HasRecommendedAngle As Boolean
            Get
                Return _recommendedAngleDegrees.HasValue
            End Get
        End Property

        Public ReadOnly Property SwipeCount As Integer
            Get
                Return _swipeCount
            End Get
        End Property

        Public ReadOnly Property SampleCount As Integer
            Get
                Return _sampleCount
            End Get
        End Property

        Public ReadOnly Property StabilityDegrees As Nullable(Of Double)
            Get
                Return _stabilityDegrees
            End Get
        End Property

        Public ReadOnly Property TraceStrokes As IReadOnlyList(Of AngleCalibrationTraceStroke)
            Get
                Return _traceStrokes
            End Get
        End Property

        Friend ReadOnly Property QualityLevel As AngleCalibrationQualityLevel
            Get
                Return _qualityLevel
            End Get
        End Property

        Friend ReadOnly Property QualityReason As AngleCalibrationQualityReason
            Get
                Return _qualityReason
            End Get
        End Property

        Friend ReadOnly Property QualityScore As Integer
            Get
                Return _qualityScore
            End Get
        End Property
    End Class
End Namespace
