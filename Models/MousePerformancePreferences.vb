Namespace Models
    Public Class MousePerformanceChartWindowPlacement
        Private ReadOnly _hasSavedBounds As Boolean
        Private ReadOnly _left As Double
        Private ReadOnly _top As Double
        Private ReadOnly _width As Double
        Private ReadOnly _height As Double
        Private ReadOnly _isMaximized As Boolean

        Public Sub New(hasSavedBounds As Boolean,
                       left As Double,
                       top As Double,
                       width As Double,
                       height As Double,
                       isMaximized As Boolean)
            _hasSavedBounds = hasSavedBounds
            _left = left
            _top = top
            _width = width
            _height = height
            _isMaximized = isMaximized
        End Sub

        Public ReadOnly Property HasSavedBounds As Boolean
            Get
                Return _hasSavedBounds
            End Get
        End Property

        Public ReadOnly Property Left As Double
            Get
                Return _left
            End Get
        End Property

        Public ReadOnly Property Top As Double
            Get
                Return _top
            End Get
        End Property

        Public ReadOnly Property Width As Double
            Get
                Return _width
            End Get
        End Property

        Public ReadOnly Property Height As Double
            Get
                Return _height
            End Get
        End Property

        Public ReadOnly Property IsMaximized As Boolean
            Get
                Return _isMaximized
            End Get
        End Property
    End Class

    Public Class MousePerformancePreferences
        Private ReadOnly _lastCpi As Nullable(Of Double)
        Private ReadOnly _chartPlotType As MousePerformancePlotType
        Private ReadOnly _chartShowStem As Boolean
        Private ReadOnly _chartShowLines As Boolean
        Private ReadOnly _chartWindowPlacement As MousePerformanceChartWindowPlacement

        Public Sub New(lastCpi As Nullable(Of Double),
                       chartPlotType As MousePerformancePlotType,
                       chartShowStem As Boolean,
                       chartShowLines As Boolean,
                       chartWindowPlacement As MousePerformanceChartWindowPlacement)
            _lastCpi = lastCpi
            _chartPlotType = chartPlotType
            _chartShowStem = chartShowStem
            _chartShowLines = chartShowLines
            _chartWindowPlacement = If(chartWindowPlacement,
                                       New MousePerformanceChartWindowPlacement(False, 0.0, 0.0, 0.0, 0.0, False))
        End Sub

        Public ReadOnly Property LastCpi As Nullable(Of Double)
            Get
                Return _lastCpi
            End Get
        End Property

        Public ReadOnly Property ChartPlotType As MousePerformancePlotType
            Get
                Return _chartPlotType
            End Get
        End Property

        Public ReadOnly Property ChartShowStem As Boolean
            Get
                Return _chartShowStem
            End Get
        End Property

        Public ReadOnly Property ChartShowLines As Boolean
            Get
                Return _chartShowLines
            End Get
        End Property

        Public ReadOnly Property ChartWindowPlacement As MousePerformanceChartWindowPlacement
            Get
                Return _chartWindowPlacement
            End Get
        End Property
    End Class
End Namespace
