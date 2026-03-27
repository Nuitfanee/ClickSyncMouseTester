Imports System.Diagnostics
Imports System.Runtime.Versioning
Imports System.Threading
Imports WpfApp1.Models

Namespace Services
    <SupportedOSPlatform("windows")>
    Public Class MousePerformanceCaptureService
        Implements IDisposable

        Private Const DefaultPacketBufferCapacity As Integer = 16384

        Private ReadOnly _syncRoot As New Object()
        Private ReadOnly _queueProcessingSyncRoot As New Object()
        Private ReadOnly _rawInputBroker As IRawInputBroker
        Private ReadOnly _sessionRouter As RawMouseSessionRouter
        Private ReadOnly _engine As MousePerformanceEngine
        Private ReadOnly _analysisOptions As MousePerformanceAnalysisOptions
        Private ReadOnly _packetInbox As BufferedRawMousePacketInbox
        Private ReadOnly _packetPumpThread As Thread
        Private _acceptedSessionGeneration As Integer
        Private _captureActive As Integer
        Private _capturePumpsActive As Integer
        Private _packetEnqueuesInFlight As Integer
        Private _pendingQueuedPacket As QueuedRawMousePacket
        Private _packetConsumerDelayMilliseconds As Integer
        Private _selectedDeviceDisconnected As Boolean
        Private _disposed As Boolean

        Public Event DevicesChanged As EventHandler
        Public Event SelectedDeviceDisconnected As EventHandler

        Public Sub New(rawInputBroker As IRawInputBroker)
            Me.New(rawInputBroker, DefaultPacketBufferCapacity, 0, MousePerformanceAnalysisOptions.Default)
        End Sub

        Friend Sub New(rawInputBroker As IRawInputBroker,
                       packetBufferCapacity As Integer,
                       Optional packetConsumerDelayMilliseconds As Integer = 0,
                       Optional analysisOptions As MousePerformanceAnalysisOptions = Nothing)
            If rawInputBroker Is Nothing Then
                Throw New ArgumentNullException(NameOf(rawInputBroker))
            End If

            _rawInputBroker = rawInputBroker
            _sessionRouter = New RawMouseSessionRouter(_rawInputBroker, RawMouseSessionMode.SpecificDevice)
            _analysisOptions = If(analysisOptions, MousePerformanceAnalysisOptions.Default)
            _engine = New MousePerformanceEngine(_analysisOptions)
            _packetInbox = New BufferedRawMousePacketInbox(Math.Max(1, packetBufferCapacity))
            _packetConsumerDelayMilliseconds = Math.Max(0, packetConsumerDelayMilliseconds)
            _packetPumpThread = New Thread(AddressOf PacketPumpThreadMain) With {
                .IsBackground = True,
                .Name = "MousePerformancePacketPump"
            }

            AddHandler _rawInputBroker.MouseDevicesChanged, AddressOf OnDevicesChanged
            AddHandler _sessionRouter.PacketCaptured, AddressOf OnPacketCaptured
            AddHandler _sessionRouter.SelectedDeviceDisconnected, AddressOf OnSelectedDeviceDisconnected

            _packetPumpThread.Start()
        End Sub

        Public Function GetDevices() As IReadOnlyList(Of RawMouseDeviceInfo)
            Return FilterPhysicalDevices(_rawInputBroker.GetMouseDevices())
        End Function

        Public Function HasAvailableMouseDevice() As Boolean
            Dim devices = GetDevices()
            Return devices IsNot Nothing AndAlso devices.Count > 0
        End Function

        Public ReadOnly Property AnalysisOptions As MousePerformanceAnalysisOptions
            Get
                Return _analysisOptions
            End Get
        End Property

        Public ReadOnly Property CurrentSessionDeviceId As String
            Get
                SyncLock _syncRoot
                    Return _engine.SessionDeviceId
                End SyncLock
            End Get
        End Property

        Public Sub SetCpiState(effectiveCpi As Nullable(Of Double), canComputeVelocity As Boolean)
            SyncLock _syncRoot
                _engine.SetCpiState(effectiveCpi, canComputeVelocity)
            End SyncLock
        End Sub

        Public Function BeginSession(deviceId As String, startFresh As Boolean) As Boolean
            If String.IsNullOrWhiteSpace(deviceId) Then
                Return False
            End If

            Dim deviceExists = GetDevices().Any(Function(item) item IsNot Nothing AndAlso
                                                             String.Equals(item.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            If Not deviceExists Then
                Return False
            End If

            _selectedDeviceDisconnected = False
            Volatile.Write(_captureActive, 0)
            _packetInbox.Drain()

            _sessionRouter.BeginSession(deviceId)
            Volatile.Write(_acceptedSessionGeneration, _sessionRouter.SessionGeneration)

            SyncLock _syncRoot
                _engine.BeginCollecting(deviceId, Stopwatch.GetTimestamp(), startFresh)
            End SyncLock

            Volatile.Write(_capturePumpsActive, 1)
            Volatile.Write(_captureActive, 1)
            Return True
        End Function

        Public Sub PauseSession()
            _sessionRouter.PauseSession()

            ' Flush packets already captured by the previous session generation before we mark the engine as paused.
            ' This avoids losing the final button-only packet when right-click is used to exit collection.
            WaitForPacketEnqueueToSettle()
            FlushQueuedPackets()

            Volatile.Write(_captureActive, 0)
            Volatile.Write(_acceptedSessionGeneration, _sessionRouter.SessionGeneration)
            _packetInbox.Drain()
            Volatile.Write(_capturePumpsActive, 0)

            SyncLock _syncRoot
                _engine.PauseCollecting()
            End SyncLock
        End Sub

        Public Sub StopSession()
            _sessionRouter.StopSession()

            WaitForPacketEnqueueToSettle()
            FlushQueuedPackets()

            Volatile.Write(_captureActive, 0)
            Volatile.Write(_acceptedSessionGeneration, _sessionRouter.SessionGeneration)
            _packetInbox.Drain()
            Volatile.Write(_capturePumpsActive, 0)

            SyncLock _syncRoot
                _engine.StopCollecting()
            End SyncLock
        End Sub

        Public Sub ResetSession()
            Volatile.Write(_captureActive, 0)
            _sessionRouter.StopSession()
            Volatile.Write(_acceptedSessionGeneration, _sessionRouter.SessionGeneration)
            _packetInbox.Drain()
            Volatile.Write(_capturePumpsActive, 0)
            _selectedDeviceDisconnected = False

            SyncLock _syncRoot
                _engine.ResetSession()
            End SyncLock
        End Sub

        Public Function CaptureSnapshot(Optional includeEvents As Boolean = True) As MousePerformanceSnapshot
            Dim hasDevices = HasAvailableMouseDevice()
            SyncLock _syncRoot
                Return _engine.CreateSnapshot(ResolveStatus(hasDevices),
                                              Volatile.Read(_captureActive) <> 0,
                                              includeEvents)
            End SyncLock
        End Function

        Private Function ResolveStatus(hasDevices As Boolean) As MousePerformanceSessionStatus
            If _selectedDeviceDisconnected Then
                Return MousePerformanceSessionStatus.DeviceDisconnected
            End If

            If _engine.IsCollecting Then
                Return MousePerformanceSessionStatus.Collecting
            End If

            If _engine.IsFinalized Then
                Return MousePerformanceSessionStatus.Stopped
            End If

            If _engine.CanContinue Then
                Return MousePerformanceSessionStatus.Paused
            End If

            If Not hasDevices AndAlso Not _engine.HasData Then
                Return MousePerformanceSessionStatus.NoDevice
            End If

            Return MousePerformanceSessionStatus.Ready
        End Function

        Private Sub OnDevicesChanged(sender As Object, e As EventArgs)
            RaiseEvent DevicesChanged(Me, EventArgs.Empty)
        End Sub

        Private Sub OnPacketCaptured(sender As Object, e As RawMousePacketEventArgs)
            If e Is Nothing OrElse e.Packet Is Nothing Then
                Return
            End If

            If Volatile.Read(_captureActive) = 0 Then
                Return
            End If

            Dim acceptedGeneration = Volatile.Read(_acceptedSessionGeneration)
            If e.SessionGeneration <> acceptedGeneration Then
                Return
            End If

            Interlocked.Increment(_packetEnqueuesInFlight)
            Try
                Dim droppedCount = 0
                _packetInbox.Enqueue(e.Packet, acceptedGeneration, droppedCount)
                If droppedCount > 0 Then
                    SyncLock _syncRoot
                        _engine.ReportDroppedPackets(droppedCount)
                    End SyncLock
                End If
            Finally
                Interlocked.Decrement(_packetEnqueuesInFlight)
            End Try
        End Sub

        Private Sub OnSelectedDeviceDisconnected(sender As Object, e As EventArgs)
            WaitForPacketEnqueueToSettle()
            FlushQueuedPackets()

            Volatile.Write(_captureActive, 0)
            Volatile.Write(_acceptedSessionGeneration, _sessionRouter.SessionGeneration)
            _packetInbox.Drain()
            Volatile.Write(_capturePumpsActive, 0)
            _selectedDeviceDisconnected = True

            SyncLock _syncRoot
                _engine.PauseCollecting()
            End SyncLock

            RaiseEvent SelectedDeviceDisconnected(Me, EventArgs.Empty)
        End Sub

        Private Sub PacketPumpThreadMain()
            Do
                Dim queuedPacket As QueuedRawMousePacket = Nothing
                If Not _packetInbox.WaitDequeue(queuedPacket) Then
                    Exit Do
                End If

                Interlocked.Exchange(_pendingQueuedPacket, queuedPacket)

                SyncLock _queueProcessingSyncRoot
                    DrainQueuedPacketsUnsafe()
                End SyncLock
            Loop
        End Sub

        Private Sub FlushQueuedPackets()
            SyncLock _queueProcessingSyncRoot
                DrainQueuedPacketsUnsafe()
            End SyncLock
        End Sub

        Private Sub DrainQueuedPacketsUnsafe()
            Dim pendingQueuedPacket = Interlocked.Exchange(_pendingQueuedPacket, Nothing)
            If pendingQueuedPacket IsNot Nothing Then
                ProcessQueuedPacket(pendingQueuedPacket)
            End If

            Dim queuedPacket As QueuedRawMousePacket = Nothing
            While _packetInbox.TryDequeue(queuedPacket)
                ProcessQueuedPacket(queuedPacket)
            End While
        End Sub

        Private Sub ProcessQueuedPacket(queuedPacket As QueuedRawMousePacket)
            ProcessPacket(queuedPacket)
        End Sub

        Private Sub ProcessPacket(queuedPacket As QueuedRawMousePacket)
            If queuedPacket Is Nothing OrElse queuedPacket.Packet Is Nothing Then
                Return
            End If

            If _packetConsumerDelayMilliseconds > 0 Then
                Thread.Sleep(_packetConsumerDelayMilliseconds)
            End If

            If Volatile.Read(_captureActive) = 0 Then
                Return
            End If

            If queuedPacket.Generation <> Volatile.Read(_acceptedSessionGeneration) Then
                Return
            End If

            SyncLock _syncRoot
                _engine.PushPacket(queuedPacket.Packet)
            End SyncLock
        End Sub

        Private Sub WaitForPacketEnqueueToSettle(Optional timeoutMilliseconds As Integer = 1000)
            Dim timeout = Math.Max(0, timeoutMilliseconds)
            Dim deadline = Environment.TickCount64 + timeout

            While Volatile.Read(_packetEnqueuesInFlight) > 0
                If timeout > 0 AndAlso Environment.TickCount64 >= deadline Then
                    Exit While
                End If

                Thread.Sleep(1)
            End While
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then
                Return
            End If

            _disposed = True
            Volatile.Write(_captureActive, 0)
            Volatile.Write(_capturePumpsActive, 0)

            _packetInbox.Dispose()
            If _packetPumpThread IsNot Nothing AndAlso _packetPumpThread.IsAlive Then
                _packetPumpThread.Join(1000)
            End If

            RemoveHandler _rawInputBroker.MouseDevicesChanged, AddressOf OnDevicesChanged
            RemoveHandler _sessionRouter.PacketCaptured, AddressOf OnPacketCaptured
            RemoveHandler _sessionRouter.SelectedDeviceDisconnected, AddressOf OnSelectedDeviceDisconnected
            _sessionRouter.Dispose()
        End Sub
    End Class
End Namespace
