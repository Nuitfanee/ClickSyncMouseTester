Imports WpfApp1.Models

Namespace Services
    Friend NotInheritable Class MousePerformanceEventBuffer
        Private Const ChunkSize As Integer = 2048

        Private ReadOnly _chunks As New List(Of MousePerformanceEvent())()
        Private _count As Integer

        Public ReadOnly Property Count As Integer
            Get
                Return _count
            End Get
        End Property

        Public Sub Clear()
            _chunks.Clear()
            _count = 0
        End Sub

        Public Sub Add(item As MousePerformanceEvent)
            If item Is Nothing Then
                Return
            End If

            Dim chunkIndex = _count \ ChunkSize
            Dim offset = _count Mod ChunkSize
            If chunkIndex >= _chunks.Count Then
                _chunks.Add(New MousePerformanceEvent(ChunkSize - 1) {})
            End If

            _chunks(chunkIndex)(offset) = item
            _count += 1
        End Sub

        Default Public ReadOnly Property Item(index As Integer) As MousePerformanceEvent
            Get
                If index < 0 OrElse index >= _count Then
                    Throw New ArgumentOutOfRangeException(NameOf(index))
                End If

                Return _chunks(index \ ChunkSize)(index Mod ChunkSize)
            End Get
        End Property

        Public Function CreateReadOnlyView(snapshotCount As Integer) As IReadOnlyList(Of MousePerformanceEvent)
            Dim clampedCount = Math.Max(0, Math.Min(snapshotCount, _count))
            Dim chunkSnapshot = _chunks.ToArray()
            Return New MousePerformanceEventReadOnlyView(chunkSnapshot, clampedCount)
        End Function

        Private NotInheritable Class MousePerformanceEventReadOnlyView
            Implements IReadOnlyList(Of MousePerformanceEvent)

            Private ReadOnly _chunks As MousePerformanceEvent()()
            Private ReadOnly _count As Integer

            Public Sub New(chunks As MousePerformanceEvent()(), count As Integer)
                _chunks = If(chunks, Array.Empty(Of MousePerformanceEvent())())
                _count = count
            End Sub

            Default Public ReadOnly Property Item(index As Integer) As MousePerformanceEvent Implements IReadOnlyList(Of MousePerformanceEvent).Item
                Get
                    If index < 0 OrElse index >= _count Then
                        Throw New ArgumentOutOfRangeException(NameOf(index))
                    End If

                    Return ResolveItem(index)
                End Get
            End Property

            Public ReadOnly Property Count As Integer Implements IReadOnlyCollection(Of MousePerformanceEvent).Count
                Get
                    Return _count
                End Get
            End Property

            Public Function GetEnumerator() As IEnumerator(Of MousePerformanceEvent) Implements IEnumerable(Of MousePerformanceEvent).GetEnumerator
                Return New Enumerator(_chunks, _count)
            End Function

            Private Function GetUntypedEnumerator() As IEnumerator Implements IEnumerable.GetEnumerator
                Return GetEnumerator()
            End Function

            Private Function ResolveItem(index As Integer) As MousePerformanceEvent
                Dim chunkIndex = index \ ChunkSize
                Dim offset = index Mod ChunkSize
                If chunkIndex < 0 OrElse chunkIndex >= _chunks.Length OrElse _chunks(chunkIndex) Is Nothing Then
                    Throw New InvalidOperationException("The event buffer snapshot is not available.")
                End If

                Return _chunks(chunkIndex)(offset)
            End Function

            Private NotInheritable Class Enumerator
                Implements IEnumerator(Of MousePerformanceEvent)

                Private ReadOnly _chunks As MousePerformanceEvent()()
                Private ReadOnly _count As Integer
                Private _index As Integer

                Public Sub New(chunks As MousePerformanceEvent()(), count As Integer)
                    _chunks = If(chunks, Array.Empty(Of MousePerformanceEvent())())
                    _count = count
                    _index = -1
                End Sub

                Public ReadOnly Property Current As MousePerformanceEvent Implements IEnumerator(Of MousePerformanceEvent).Current
                    Get
                    If _index < 0 OrElse _index >= _count Then
                        Throw New InvalidOperationException()
                    End If

                    Dim chunkIndex = _index \ ChunkSize
                    Dim offset = _index Mod ChunkSize
                    If chunkIndex < 0 OrElse chunkIndex >= _chunks.Length OrElse _chunks(chunkIndex) Is Nothing Then
                        Throw New InvalidOperationException("The event buffer snapshot is not available.")
                    End If

                    Return _chunks(chunkIndex)(offset)
                End Get
            End Property

                Private ReadOnly Property UntypedCurrent As Object Implements IEnumerator.Current
                    Get
                        Return Current
                    End Get
                End Property

                Public Function MoveNext() As Boolean Implements IEnumerator.MoveNext
                    If _index >= _count Then
                        Return False
                    End If

                    _index += 1
                    Return _index < _count
                End Function

                Public Sub Reset() Implements IEnumerator.Reset
                    _index = -1
                End Sub

                Public Sub Dispose() Implements IDisposable.Dispose
                End Sub
            End Class
        End Class
    End Class
End Namespace
