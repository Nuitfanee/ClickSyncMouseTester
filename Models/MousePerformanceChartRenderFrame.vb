Namespace Models
    Public Enum MousePerformancePlotType
        XCountVsTime = 0
        YCountVsTime = 1
        XYCountVsTime = 2
        IntervalVsTime = 3
        XVelocityVsTime = 4
        YVelocityVsTime = 5
        XYVelocityVsTime = 6
        XVsY = 7
        FrequencyVsTime = 8
        XSumVsTime = 9
        YSumVsTime = 10
        XYSumVsTime = 11
    End Enum

    Public Enum MousePerformanceChartSeriesKind
        Scatter = 0
        Line = 1
        Stem = 2
    End Enum

    Public Enum MousePerformanceChartSeriesPalette
        Primary = 0
        Secondary = 1
        Accent = 2
        Neutral = 3
    End Enum

    Public Class MousePerformanceChartPoint
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

    Public Class MousePerformanceChartSeries
        Private ReadOnly _kind As MousePerformanceChartSeriesKind
        Private ReadOnly _palette As MousePerformanceChartSeriesPalette
        Private ReadOnly _points As IReadOnlyList(Of MousePerformanceChartPoint)

        Public Sub New(kind As MousePerformanceChartSeriesKind,
                       palette As MousePerformanceChartSeriesPalette,
                       points As IReadOnlyList(Of MousePerformanceChartPoint))
            _kind = kind
            _palette = palette
            _points = If(points, Array.Empty(Of MousePerformanceChartPoint)())
        End Sub

        Public ReadOnly Property Kind As MousePerformanceChartSeriesKind
            Get
                Return _kind
            End Get
        End Property

        Public ReadOnly Property Palette As MousePerformanceChartSeriesPalette
            Get
                Return _palette
            End Get
        End Property

        Public ReadOnly Property Points As IReadOnlyList(Of MousePerformanceChartPoint)
            Get
                Return _points
            End Get
        End Property
    End Class

    Public Class MousePerformanceChartRenderFrame
        Private ReadOnly _plotType As MousePerformancePlotType
        Private ReadOnly _title As String
        Private ReadOnly _subtitle As String
        Private ReadOnly _description As String
        Private ReadOnly _xAxisTitle As String
        Private ReadOnly _yAxisTitle As String
        Private ReadOnly _isAvailable As Boolean
        Private ReadOnly _message As String
        Private ReadOnly _startIndex As Integer
        Private ReadOnly _endIndex As Integer
        Private ReadOnly _showStem As Boolean
        Private ReadOnly _showLines As Boolean
        Private ReadOnly _xMinimum As Double
        Private ReadOnly _xMaximum As Double
        Private ReadOnly _yMinimum As Double
        Private ReadOnly _yMaximum As Double
        Private ReadOnly _series As IReadOnlyList(Of MousePerformanceChartSeries)

        Public Sub New(plotType As MousePerformancePlotType,
                       title As String,
                       subtitle As String,
                       description As String,
                       xAxisTitle As String,
                       yAxisTitle As String,
                       isAvailable As Boolean,
                       message As String,
                       startIndex As Integer,
                       endIndex As Integer,
                       showStem As Boolean,
                       showLines As Boolean,
                       xMinimum As Double,
                       xMaximum As Double,
                       yMinimum As Double,
                       yMaximum As Double,
                       series As IReadOnlyList(Of MousePerformanceChartSeries))
            _plotType = plotType
            _title = If(title, String.Empty)
            _subtitle = If(subtitle, String.Empty)
            _description = If(description, String.Empty)
            _xAxisTitle = If(xAxisTitle, String.Empty)
            _yAxisTitle = If(yAxisTitle, String.Empty)
            _isAvailable = isAvailable
            _message = If(message, String.Empty)
            _startIndex = Math.Max(0, startIndex)
            _endIndex = Math.Max(0, endIndex)
            _showStem = showStem
            _showLines = showLines
            _xMinimum = xMinimum
            _xMaximum = xMaximum
            _yMinimum = yMinimum
            _yMaximum = yMaximum
            _series = If(series, Array.Empty(Of MousePerformanceChartSeries)())
        End Sub

        Public ReadOnly Property PlotType As MousePerformancePlotType
            Get
                Return _plotType
            End Get
        End Property

        Public ReadOnly Property Title As String
            Get
                Return _title
            End Get
        End Property

        Public ReadOnly Property Subtitle As String
            Get
                Return _subtitle
            End Get
        End Property

        Public ReadOnly Property Description As String
            Get
                Return _description
            End Get
        End Property

        Public ReadOnly Property XAxisTitle As String
            Get
                Return _xAxisTitle
            End Get
        End Property

        Public ReadOnly Property YAxisTitle As String
            Get
                Return _yAxisTitle
            End Get
        End Property

        Public ReadOnly Property IsAvailable As Boolean
            Get
                Return _isAvailable
            End Get
        End Property

        Public ReadOnly Property Message As String
            Get
                Return _message
            End Get
        End Property

        Public ReadOnly Property StartIndex As Integer
            Get
                Return _startIndex
            End Get
        End Property

        Public ReadOnly Property EndIndex As Integer
            Get
                Return _endIndex
            End Get
        End Property

        Public ReadOnly Property ShowStem As Boolean
            Get
                Return _showStem
            End Get
        End Property

        Public ReadOnly Property ShowLines As Boolean
            Get
                Return _showLines
            End Get
        End Property

        Public ReadOnly Property XMinimum As Double
            Get
                Return _xMinimum
            End Get
        End Property

        Public ReadOnly Property XMaximum As Double
            Get
                Return _xMaximum
            End Get
        End Property

        Public ReadOnly Property YMinimum As Double
            Get
                Return _yMinimum
            End Get
        End Property

        Public ReadOnly Property YMaximum As Double
            Get
                Return _yMaximum
            End Get
        End Property

        Public ReadOnly Property Series As IReadOnlyList(Of MousePerformanceChartSeries)
            Get
                Return _series
            End Get
        End Property
    End Class
End Namespace
