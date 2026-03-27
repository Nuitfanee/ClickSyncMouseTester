Imports System.Threading
Imports WpfApp1.Models

Namespace Services
    Friend NotInheritable Class QueuedRawMousePacket
        Public Sub New(packet As RawMousePacket, generation As Integer)
            Me.Packet = packet
            Me.Generation = generation
        End Sub

        Public ReadOnly Property Packet As RawMousePacket
        Public ReadOnly Property Generation As Integer
    End Class

    Friend NotInheritable Class BufferedRawMousePacketInbox
        Implements IDisposable

        Private ReadOnly _syncRoot As New Object()
        Private ReadOnly _items As Queue(Of QueuedRawMousePacket)
        Private _disposed As Boolean

        Public Sub New(initialCapacity As Integer)
            If initialCapacity <= 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(initialCapacity))
            End If

            _items = New Queue(Of QueuedRawMousePacket)(initialCapacity)
        End Sub

        Public Function Enqueue(packet As RawMousePacket, generation As Integer, ByRef droppedCount As Integer) As Boolean
            droppedCount = 0
            If packet Is Nothing Then
                Return False
            End If

            SyncLock _syncRoot
                If _disposed Then
                    Return False
                End If

                Dim wasEmpty = _items.Count = 0
                _items.Enqueue(New QueuedRawMousePacket(packet, generation))

                If wasEmpty Then
                    Monitor.Pulse(_syncRoot)
                End If

                Return True
            End SyncLock
        End Function

        Public Function TryDequeue(ByRef queuedPacket As QueuedRawMousePacket) As Boolean
            SyncLock _syncRoot
                If _items.Count <= 0 Then
                    queuedPacket = Nothing
                    Return False
                End If

                queuedPacket = _items.Dequeue()
                Return queuedPacket IsNot Nothing
            End SyncLock
        End Function

        Public Function WaitDequeue(ByRef queuedPacket As QueuedRawMousePacket,
                                    Optional timeoutMilliseconds As Integer = Timeout.Infinite) As Boolean
            Dim remainingTimeout = timeoutMilliseconds
            Dim startedAt = If(timeoutMilliseconds = Timeout.Infinite, 0L, Environment.TickCount64)

            SyncLock _syncRoot
                Do
                    If _items.Count > 0 Then
                        queuedPacket = _items.Dequeue()
                        Return queuedPacket IsNot Nothing
                    End If

                    If _disposed Then
                        queuedPacket = Nothing
                        Return False
                    End If

                    If remainingTimeout = 0 Then
                        queuedPacket = Nothing
                        Return False
                    End If

                    If Not Monitor.Wait(_syncRoot, remainingTimeout) Then
                        queuedPacket = Nothing
                        Return False
                    End If

                    If timeoutMilliseconds <> Timeout.Infinite Then
                        Dim elapsed = Environment.TickCount64 - startedAt
                        If elapsed >= timeoutMilliseconds Then
                            remainingTimeout = 0
                        Else
                            remainingTimeout = CInt(timeoutMilliseconds - elapsed)
                        End If
                    End If
                Loop
            End SyncLock
        End Function

        Public Sub Drain()
            SyncLock _syncRoot
                _items.Clear()
            End SyncLock
        End Sub

        Public ReadOnly Property Count As Integer
            Get
                SyncLock _syncRoot
                    Return _items.Count
                End SyncLock
            End Get
        End Property

        Public Sub Dispose() Implements IDisposable.Dispose
            SyncLock _syncRoot
                If _disposed Then
                    Return
                End If

                _disposed = True
                _items.Clear()
                Monitor.PulseAll(_syncRoot)
            End SyncLock
        End Sub
    End Class
End Namespace
