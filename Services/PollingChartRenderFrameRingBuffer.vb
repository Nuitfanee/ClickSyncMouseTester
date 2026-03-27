Imports System.Threading
Imports WpfApp1.Models

Namespace Services
    Friend Class PollingChartRenderFrameRingBuffer
        Private ReadOnly _items() As PollingChartRenderFrame
        Private _writeSequence As Integer
        Private _readSequence As Integer

        Public Sub New(capacity As Integer)
            If capacity <= 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(capacity))
            End If

            _items = New PollingChartRenderFrame(capacity - 1) {}
        End Sub

        Public Sub Write(frame As PollingChartRenderFrame)
            If frame Is Nothing Then
                Return
            End If

            Dim nextSequence = _writeSequence + 1
            _items(nextSequence Mod _items.Length) = frame
            Thread.MemoryBarrier()
            Volatile.Write(_writeSequence, nextSequence)
        End Sub

        Public Function TryReadLatest(ByRef frame As PollingChartRenderFrame) As Boolean
            Dim availableSequence = Volatile.Read(_writeSequence)
            If availableSequence = 0 OrElse availableSequence = _readSequence Then
                frame = Nothing
                Return False
            End If

            Thread.MemoryBarrier()
            frame = _items(availableSequence Mod _items.Length)
            _readSequence = availableSequence
            Return frame IsNot Nothing
        End Function

        Public Sub Clear()
            Array.Clear(_items, 0, _items.Length)
            Volatile.Write(_writeSequence, 0)
            _readSequence = 0
        End Sub
    End Class
End Namespace
