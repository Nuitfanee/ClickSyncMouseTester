Imports System.Diagnostics
Imports System.Runtime.Versioning
Imports System.Threading
Imports WpfApp1.Models

Namespace Services
    <SupportedOSPlatform("windows")>
    Public Class AngleCalibrationCaptureService
        Implements IDisposable

        Private Const RenderFrameBufferCapacity As Integer = 128
        Private Const DefaultPacketBufferCapacity As Integer = 16384
        Private Const DefaultRenderFrameGenerationIntervalMilliseconds As Double = 1000.0 / 60.0

        Private ReadOnly _syncRoot As New Object()
        Private ReadOnly _queueProcessingSyncRoot As New Object()
        Private ReadOnly _rawInputBroker As IRawInputBroker
        Private ReadOnly _sessionRouter As RawMouseSessionRouter
        Private ReadOnly _engine As AngleCalibrationEngine
        Private ReadOnly _renderFrameBuffer As AngleCalibrationRenderFrameRingBuffer
        Private ReadOnly _renderFrameTimer As Timer
        Private ReadOnly _packetInbox As BufferedRawMousePacketInbox
        Private ReadOnly _packetPumpThread As Thread
        Private _renderFrameGenerationIntervalMilliseconds As Double = DefaultRenderFrameGenerationIntervalMilliseconds
        Private _capturePumpsActive As Integer
        Private _acceptedSessionGeneration As Integer
        Private _captureActive As Integer
        Private _packetEnqueuesInFlight As Integer
        Private _pendingQueuedPacket As QueuedRawMousePacket
        Private _packetConsumerDelayMilliseconds As Integer
        Private _disposed As Boolean

        Public Event DevicesChanged As EventHandler
        Public Event SelectedDeviceDisconnected As EventHandler

        Public Sub New(rawInputBroker As IRawInputBroker)
            Me.New(rawInputBroker, DefaultPacketBufferCapacity, 0)
        End Sub

        Friend Sub New(rawInputBroker As IRawInputBroker, packetBufferCapacity As Integer, Optional packetConsumerDelayMilliseconds As Integer = 0)
            If rawInputBroker Is Nothing Then
                Throw New ArgumentNullException(NameOf(rawInputBroker))
            End If

            _rawInputBroker = rawInputBroker
            _sessionRouter = New RawMouseSessionRouter(_rawInputBroker, RawMouseSessionMode.FirstActiveDevice)
            _engine = New AngleCalibrationEngine()
            _renderFrameBuffer = New AngleCalibrationRenderFrameRingBuffer(RenderFrameBufferCapacity)
            _renderFrameTimer = New Timer(AddressOf OnRenderFrameTimerTick, Nothing, Timeout.Infinite, Timeout.Infinite)
            _packetInbox = New BufferedRawMousePacketInbox(Math.Max(1, packetBufferCapacity))
            _packetConsumerDelayMilliseconds = Math.Max(0, packetConsumerDelayMilliseconds)
            _packetPumpThread = New Thread(AddressOf PacketPumpThreadMain) With {
                .IsBackground = True,
                .Name = "AngleCalibrationPacketPump"
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

        Public ReadOnly Property CurrentSessionDeviceId As String
            Get
                Return _sessionRouter.CurrentDeviceId
            End Get
        End Property

        Public Function BeginSession() As Boolean
            If Not HasAvailableMouseDevice() Then
                Volatile.Write(_captureActive, 0)
                _packetInbox.Drain()
                Volatile.Write(_capturePumpsActive, 0)

                SyncLock _syncRoot
                    _engine.SetLocked(False, GetNowMilliseconds())
                End SyncLock

                UpdateRenderFrameTimer()
                CaptureRenderFrame()
                Return False
            End If

            Volatile.Write(_captureActive, 0)
            _packetInbox.Drain()

            _sessionRouter.BeginSession()
            Volatile.Write(_acceptedSessionGeneration, _sessionRouter.SessionGeneration)

            Dim nowMs = GetNowMilliseconds()
            SyncLock _syncRoot
                _engine.SetLocked(True, nowMs)
            End SyncLock

            Volatile.Write(_capturePumpsActive, 1)
            UpdateRenderFrameTimer()
            Volatile.Write(_captureActive, 1)
            CaptureRenderFrame()

            Return True
        End Function

        Public Sub PauseSession()
            _sessionRouter.PauseSession()

            WaitForPacketEnqueueToSettle()
            FlushQueuedPackets()

            Volatile.Write(_captureActive, 0)
            Volatile.Write(_acceptedSessionGeneration, _sessionRouter.SessionGeneration)
            _packetInbox.Drain()
            Volatile.Write(_capturePumpsActive, 0)

            Dim nowMs = GetNowMilliseconds()
            SyncLock _syncRoot
                _engine.SetLocked(False, nowMs)
            End SyncLock

            UpdateRenderFrameTimer()
            CaptureRenderFrame()
        End Sub

        Public Sub StopSession()
            _sessionRouter.StopSession()

            WaitForPacketEnqueueToSettle()
            FlushQueuedPackets()

            Volatile.Write(_captureActive, 0)
            Volatile.Write(_acceptedSessionGeneration, _sessionRouter.SessionGeneration)
            _packetInbox.Drain()
            Volatile.Write(_capturePumpsActive, 0)

            Dim nowMs = GetNowMilliseconds()
            SyncLock _syncRoot
                _engine.SetLocked(False, nowMs)
            End SyncLock

            UpdateRenderFrameTimer()
            CaptureRenderFrame()
        End Sub

        Public Sub ResetSession()
            Volatile.Write(_captureActive, 0)
            _packetInbox.Drain()
            Volatile.Write(_capturePumpsActive, 0)

            SyncLock _syncRoot
                _engine.Reset()
                _renderFrameBuffer.Clear()
            End SyncLock

            UpdateRenderFrameTimer()
            CaptureRenderFrame()
        End Sub

        Public Function CaptureRenderFrame() As AngleCalibrationRenderFrame
            SyncLock _syncRoot
                Return CaptureRenderFrameCore(GetNowMilliseconds())
            End SyncLock
        End Function

        Public Function TryReadLatestRenderFrame(ByRef renderFrame As AngleCalibrationRenderFrame) As Boolean
            SyncLock _syncRoot
                Return _renderFrameBuffer.TryReadLatest(renderFrame)
            End SyncLock
        End Function

        Private Function CaptureRenderFrameCore(nowMs As Double) As AngleCalibrationRenderFrame
            Dim renderFrame = _engine.CreateRenderFrame(nowMs)
            _renderFrameBuffer.Write(renderFrame)
            Return renderFrame
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

            Dim nowMs = GetNowMilliseconds()
            SyncLock _syncRoot
                _engine.SetLocked(False, nowMs)
            End SyncLock

            UpdateRenderFrameTimer()
            CaptureRenderFrame()
            RaiseEvent SelectedDeviceDisconnected(Me, EventArgs.Empty)
        End Sub

        Private Sub OnRenderFrameTimerTick(state As Object)
            If _disposed OrElse Volatile.Read(_capturePumpsActive) = 0 Then
                Return
            End If

            SyncLock _syncRoot
                CaptureRenderFrameCore(GetNowMilliseconds())
            End SyncLock
        End Sub

        Private Sub UpdateRenderFrameTimer()
            Dim dueTime = Timeout.InfiniteTimeSpan
            Dim period = Timeout.InfiniteTimeSpan

            If Volatile.Read(_capturePumpsActive) <> 0 Then
                period = TimeSpan.FromMilliseconds(_renderFrameGenerationIntervalMilliseconds)
                dueTime = TimeSpan.Zero
            End If

            _renderFrameTimer.Change(dueTime, period)
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
                ProcessPacket(pendingQueuedPacket)
            End If

            Dim queuedPacket As QueuedRawMousePacket = Nothing
            While _packetInbox.TryDequeue(queuedPacket)
                ProcessPacket(queuedPacket)
            End While
        End Sub

        Private Sub ProcessPacket(queuedPacket As QueuedRawMousePacket)
            If queuedPacket Is Nothing OrElse queuedPacket.Packet Is Nothing Then
                Return
            End If

            If _packetConsumerDelayMilliseconds > 0 Then
                Thread.Sleep(_packetConsumerDelayMilliseconds)
            End If

            SyncLock _syncRoot
                If Volatile.Read(_captureActive) = 0 Then
                    Return
                End If

                If queuedPacket.Generation <> Volatile.Read(_acceptedSessionGeneration) Then
                    Return
                End If

                _engine.PushPacket(queuedPacket.Packet)
            End SyncLock
        End Sub

        Private Shared Function GetNowMilliseconds() As Double
            Return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency
        End Function

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

            _renderFrameTimer.Change(Timeout.Infinite, Timeout.Infinite)
            _renderFrameTimer.Dispose()

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
