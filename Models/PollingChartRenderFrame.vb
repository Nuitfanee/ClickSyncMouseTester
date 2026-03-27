Namespace Models
    Public Class PollingChartRenderFrame
        Private ReadOnly _rawCurrentRate As Double
        Private ReadOnly _historyPoints As IReadOnlyList(Of PollingHistoryPoint)

        Public Sub New(rawCurrentRate As Double, historyPoints As IReadOnlyList(Of PollingHistoryPoint))
            _rawCurrentRate = rawCurrentRate
            _historyPoints = If(historyPoints, Array.Empty(Of PollingHistoryPoint)())
        End Sub

        Public ReadOnly Property RawCurrentRate As Double
            Get
                Return _rawCurrentRate
            End Get
        End Property

        Public ReadOnly Property HistoryPoints As IReadOnlyList(Of PollingHistoryPoint)
            Get
                Return _historyPoints
            End Get
        End Property
    End Class
End Namespace
