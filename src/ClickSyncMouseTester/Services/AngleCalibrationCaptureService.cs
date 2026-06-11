using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;

namespace ClickSyncMouseTester.Services;

[SupportedOSPlatform("windows")]
public class AngleCalibrationCaptureService : IDisposable
{
    private const int RenderFrameBufferCapacity = 128;

    private const int DefaultPacketBufferCapacity = 65536;

    private const int PacketPumpShutdownJoinTimeoutMilliseconds = 1000;

    private const double DefaultRenderFrameGenerationIntervalMilliseconds = 16.666666666666668;

    private readonly object _syncRoot;

    private readonly object _queueProcessingSyncRoot;

    private readonly IRawMouseReportSource _mouseReportSource;

    private readonly IRawInputDeviceCatalog _deviceCatalog;

    private readonly RawMouseSessionRouter _sessionRouter;

    private readonly AngleCalibrationEngine _engine;

    private readonly AngleCalibrationRenderFrameRingBuffer _renderFrameBuffer;

    private readonly Timer _renderFrameTimer;

    private readonly BufferedRawMousePacketInbox _packetInbox;

    private readonly Thread _packetPumpThread;

    private double _renderFrameGenerationIntervalMilliseconds;

    private int _capturePumpsActive;

    private int _acceptedSessionGeneration;

    private int _captureActive;

    private int _packetEnqueuesInFlight;

    private int _packetConsumerDelayMilliseconds;

    private int _droppedPacketCount;

    private bool _disposed;

    public string CurrentSessionDeviceId => _sessionRouter.CurrentDeviceId;

    public int DroppedPacketCount => Math.Max(0, Interlocked.Add(ref _droppedPacketCount, 0));

    public event EventHandler DevicesChanged;

    public event EventHandler SelectedDeviceDisconnected;

    public AngleCalibrationCaptureService(IRawInputBroker rawInputBroker)
        : this(rawInputBroker, DefaultPacketBufferCapacity)
    {
    }

    internal AngleCalibrationCaptureService(IRawInputBroker rawInputBroker, int packetBufferCapacity, int packetConsumerDelayMilliseconds = 0)
        : this(rawInputBroker, rawInputBroker, packetBufferCapacity, packetConsumerDelayMilliseconds)
    {
    }

    internal AngleCalibrationCaptureService(IRawMouseReportSource mouseReportSource, IRawInputDeviceCatalog deviceCatalog, int packetBufferCapacity, int packetConsumerDelayMilliseconds = 0)
    {
        _syncRoot = new object();
        _queueProcessingSyncRoot = new object();
        _renderFrameGenerationIntervalMilliseconds = 16.666666666666668;
        if (mouseReportSource == null)
        {
            throw new ArgumentNullException(nameof(mouseReportSource));
        }
        if (deviceCatalog == null)
        {
            throw new ArgumentNullException(nameof(deviceCatalog));
        }
        _mouseReportSource = mouseReportSource;
        _deviceCatalog = deviceCatalog;
        _sessionRouter = new RawMouseSessionRouter(_mouseReportSource, _deviceCatalog, RawMouseSessionMode.FirstActiveDevice);
        _engine = new AngleCalibrationEngine();
        _renderFrameBuffer = new AngleCalibrationRenderFrameRingBuffer(128);
        _renderFrameTimer = new Timer(OnRenderFrameTimerTick, null, -1, -1);
        _packetInbox = new BufferedRawMousePacketInbox(Math.Max(1, packetBufferCapacity));
        _packetConsumerDelayMilliseconds = Math.Max(0, packetConsumerDelayMilliseconds);
        _packetPumpThread = new Thread(PacketPumpThreadMain)
        {
            IsBackground = true,
            Name = "AngleCalibrationPacketPump"
        };
        _deviceCatalog.MouseDevicesChanged += OnDevicesChanged;
        _sessionRouter.PacketCaptured += OnPacketCaptured;
        _sessionRouter.SelectedDeviceDisconnected += OnSelectedDeviceDisconnected;
        _packetPumpThread.Start();
    }

    public IReadOnlyList<RawMouseDeviceInfo> GetDevices()
    {
        return MouseDeviceFiltering.FilterSelectableMotionDevices(_deviceCatalog.GetMouseDevices(), _deviceCatalog.GetMouseEndpointActivitySnapshots());
    }

    public bool HasAvailableMouseDevice()
    {
        IReadOnlyList<RawMouseDeviceInfo> devices = GetDevices();
        if (devices != null)
        {
            return devices.Count > 0;
        }
        return false;
    }

    public bool BeginSession()
    {
        if (!HasAvailableMouseDevice())
        {
            Volatile.Write(ref _captureActive, 0);
            _packetInbox.Drain();
            Volatile.Write(ref _capturePumpsActive, 0);
            object syncRoot = _syncRoot;
            bool lockTaken = false;
            try
            {
                Monitor.Enter(syncRoot, ref lockTaken);
                _engine.SetLocked(isLocked: false, GetNowMilliseconds());
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(syncRoot);
                }
            }
            UpdateRenderFrameTimer();
            CaptureRenderFrame();
            return false;
        }
        Volatile.Write(ref _captureActive, 0);
        _packetInbox.Drain();
        _sessionRouter.BeginSession();
        Volatile.Write(ref _acceptedSessionGeneration, _sessionRouter.SessionGeneration);
        double nowMilliseconds = GetNowMilliseconds();
        object syncRoot2 = _syncRoot;
        bool lockTaken2 = false;
        try
        {
            Monitor.Enter(syncRoot2, ref lockTaken2);
            _engine.SetLocked(isLocked: true, nowMilliseconds);
        }
        finally
        {
            if (lockTaken2)
            {
                Monitor.Exit(syncRoot2);
            }
        }
        Volatile.Write(ref _capturePumpsActive, 1);
        UpdateRenderFrameTimer();
        Volatile.Write(ref _captureActive, 1);
        CaptureRenderFrame();
        return true;
    }

    public void PauseSession()
    {
        _sessionRouter.PauseSession();
        WaitForPacketEnqueueToSettle();
        FlushQueuedPackets();
        Volatile.Write(ref _captureActive, 0);
        Volatile.Write(ref _acceptedSessionGeneration, _sessionRouter.SessionGeneration);
        _packetInbox.Drain();
        Volatile.Write(ref _capturePumpsActive, 0);
        double nowMilliseconds = GetNowMilliseconds();
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            _engine.SetLocked(isLocked: false, nowMilliseconds);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
        UpdateRenderFrameTimer();
        CaptureRenderFrame();
    }

    public void StopSession()
    {
        _sessionRouter.StopSession();
        WaitForPacketEnqueueToSettle();
        FlushQueuedPackets();
        Volatile.Write(ref _captureActive, 0);
        Volatile.Write(ref _acceptedSessionGeneration, _sessionRouter.SessionGeneration);
        _packetInbox.Drain();
        Volatile.Write(ref _capturePumpsActive, 0);
        double nowMilliseconds = GetNowMilliseconds();
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            _engine.SetLocked(isLocked: false, nowMilliseconds);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
        UpdateRenderFrameTimer();
        CaptureRenderFrame();
    }

    public void ResetSession()
    {
        Volatile.Write(ref _captureActive, 0);
        WaitForPacketEnqueueToSettle();
        object queueProcessingSyncRoot = _queueProcessingSyncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(queueProcessingSyncRoot, ref lockTaken);
            _packetInbox.Drain();
            Volatile.Write(ref _capturePumpsActive, 0);
            Interlocked.Exchange(ref _droppedPacketCount, 0);
            object syncRoot = _syncRoot;
            bool lockTaken2 = false;
            try
            {
                Monitor.Enter(syncRoot, ref lockTaken2);
                _engine.Reset();
                _renderFrameBuffer.Clear();
            }
            finally
            {
                if (lockTaken2)
                {
                    Monitor.Exit(syncRoot);
                }
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(queueProcessingSyncRoot);
            }
        }
        UpdateRenderFrameTimer();
        CaptureRenderFrame();
    }

    public AngleCalibrationRenderFrame CaptureRenderFrame()
    {
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            return CaptureRenderFrameCore(GetNowMilliseconds());
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    public bool TryReadLatestRenderFrame(ref AngleCalibrationRenderFrame renderFrame)
    {
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            return _renderFrameBuffer.TryReadLatest(ref renderFrame);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    private AngleCalibrationRenderFrame CaptureRenderFrameCore(double nowMs)
    {
        AngleCalibrationRenderFrame angleCalibrationRenderFrame = _engine.CreateRenderFrame(nowMs);
        _renderFrameBuffer.Write(angleCalibrationRenderFrame);
        return angleCalibrationRenderFrame;
    }

    private void OnDevicesChanged(object sender, EventArgs e)
    {
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPacketCaptured(object sender, RawMousePacketEventArgs e)
    {
        if (e == null || e.Packet == null)
        {
            return;
        }
        Interlocked.Increment(ref _packetEnqueuesInFlight);
        try
        {
            if (Volatile.Read(in _captureActive) == 0)
            {
                return;
            }
            int acceptedSessionGeneration = Volatile.Read(ref _acceptedSessionGeneration);
            if (e.SessionGeneration == acceptedSessionGeneration)
            {
                int droppedCount = 0;
                _packetInbox.Enqueue(e.Packet, acceptedSessionGeneration, ref droppedCount);
                if (droppedCount > 0)
                {
                    Interlocked.Add(ref _droppedPacketCount, droppedCount);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _packetEnqueuesInFlight);
        }
    }

    private void OnSelectedDeviceDisconnected(object sender, EventArgs e)
    {
        WaitForPacketEnqueueToSettle();
        FlushQueuedPackets();
        Volatile.Write(ref _captureActive, 0);
        Volatile.Write(ref _acceptedSessionGeneration, _sessionRouter.SessionGeneration);
        _packetInbox.Drain();
        Volatile.Write(ref _capturePumpsActive, 0);
        double nowMilliseconds = GetNowMilliseconds();
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            _engine.SetLocked(isLocked: false, nowMilliseconds);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
        UpdateRenderFrameTimer();
        CaptureRenderFrame();
        SelectedDeviceDisconnected?.Invoke(this, EventArgs.Empty);
    }

    private void OnRenderFrameTimerTick(object state)
    {
        if (_disposed || Volatile.Read(in _capturePumpsActive) == 0)
        {
            return;
        }
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            CaptureRenderFrameCore(GetNowMilliseconds());
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    private void UpdateRenderFrameTimer()
    {
        TimeSpan dueTime = Timeout.InfiniteTimeSpan;
        TimeSpan period = Timeout.InfiniteTimeSpan;
        if (Volatile.Read(in _capturePumpsActive) != 0)
        {
            period = TimeSpan.FromMilliseconds(_renderFrameGenerationIntervalMilliseconds);
            dueTime = TimeSpan.Zero;
        }
        _renderFrameTimer.Change(dueTime, period);
    }

    private void PacketPumpThreadMain()
    {
        while (_packetInbox.WaitForData())
        {
            object queueProcessingSyncRoot = _queueProcessingSyncRoot;
            bool lockTaken = false;
            try
            {
                Monitor.Enter(queueProcessingSyncRoot, ref lockTaken);
                DrainQueuedPacketsUnsafe();
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(queueProcessingSyncRoot);
                }
            }
        }
    }

    private void FlushQueuedPackets()
    {
        object queueProcessingSyncRoot = _queueProcessingSyncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(queueProcessingSyncRoot, ref lockTaken);
            DrainQueuedPacketsUnsafe();
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(queueProcessingSyncRoot);
            }
        }
    }

    private void DrainQueuedPacketsUnsafe()
    {
        QueuedRawMousePacket queuedPacket = QueuedRawMousePacket.Empty;
        while (_packetInbox.TryDequeue(ref queuedPacket))
        {
            ProcessPacket(queuedPacket);
        }
    }

    private void ProcessPacket(QueuedRawMousePacket queuedPacket)
    {
        if (!queuedPacket.HasPacket || Volatile.Read(in _captureActive) == 0 || queuedPacket.Generation != Volatile.Read(in _acceptedSessionGeneration))
        {
            return;
        }
        if (_packetConsumerDelayMilliseconds > 0)
        {
            Thread.Sleep(_packetConsumerDelayMilliseconds);
        }
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            if (Volatile.Read(in _captureActive) != 0 && queuedPacket.Generation == Volatile.Read(in _acceptedSessionGeneration))
            {
                _engine.PushPacket(queuedPacket.Packet);
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    private static double GetNowMilliseconds()
    {
        return (double)Stopwatch.GetTimestamp() * 1000.0 / (double)Stopwatch.Frequency;
    }

    private void WaitForPacketEnqueueToSettle(int timeoutMilliseconds = 1000)
    {
        int timeout = Math.Max(0, timeoutMilliseconds);
        long deadlineTick = Environment.TickCount64 + timeout;
        while (Volatile.Read(ref _packetEnqueuesInFlight) > 0 && (timeout <= 0 || Environment.TickCount64 < deadlineTick))
        {
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Volatile.Write(ref _captureActive, 0);
            Volatile.Write(ref _capturePumpsActive, 0);
            _packetConsumerDelayMilliseconds = 0;
            _renderFrameTimer.Change(-1, -1);
            _renderFrameTimer.Dispose();
            _packetInbox.Complete();
            bool packetPumpStopped = true;
            if (_packetPumpThread != null && _packetPumpThread.IsAlive)
            {
                packetPumpStopped = _packetPumpThread.Join(PacketPumpShutdownJoinTimeoutMilliseconds);
            }
            if (packetPumpStopped)
            {
                _packetInbox.Dispose();
            }
            _deviceCatalog.MouseDevicesChanged -= OnDevicesChanged;
            _sessionRouter.PacketCaptured -= OnPacketCaptured;
            _sessionRouter.SelectedDeviceDisconnected -= OnSelectedDeviceDisconnected;
            _sessionRouter.Dispose();
        }
    }

    void IDisposable.Dispose()
    {
        this.Dispose();
    }
}





