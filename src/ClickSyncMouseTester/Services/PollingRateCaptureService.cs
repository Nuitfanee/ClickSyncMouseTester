using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;

namespace ClickSyncMouseTester.Services;

[SupportedOSPlatform("windows")]
public class PollingRateCaptureService : IDisposable
{
    private const int MetricsSnapshotBufferCapacity = 64;

    private const int ChartRenderFrameBufferCapacity = 128;

    private const int DefaultPacketBufferCapacity = 16384;

    private const int PacketPumpShutdownJoinTimeoutMilliseconds = 1000;

    private const double DefaultMetricsSnapshotGenerationIntervalMilliseconds = 1000.0 / 30.0;

    private const double DefaultChartRenderFrameGenerationIntervalMilliseconds = 1000.0 / 120.0;

    private const double DefaultRenderHistorySampleIntervalMilliseconds = 1000.0 / 120.0;

    private const double MinimumChartRenderFrameRateHz = 1.0;

    private const double MaximumChartRenderFrameRateHz = 240.0;

    private readonly object _metricsSnapshotBufferSyncRoot;

    private readonly object _chartRenderFrameBufferSyncRoot;

    private readonly object _pausedSnapshotSyncRoot;

    private readonly object _queueProcessingSyncRoot;

    private readonly IRawMouseReportSource _mouseReportSource;

    private readonly IRawInputDeviceCatalog _deviceCatalog;

    private readonly RawMouseSessionRouter _sessionRouter;

    private readonly PollingRateEngine _engine;

    private readonly LatestValueRingBuffer<PollingMetricsSnapshot> _metricsSnapshotBuffer;

    private readonly LatestValueRingBuffer<PollingChartRenderFrame> _chartRenderFrameBuffer;

    private readonly Timer _metricsSnapshotTimer;

    private readonly Timer _chartRenderFrameTimer;

    private readonly BufferedRawMousePacketInbox _packetInbox;

    private readonly Thread _packetPumpThread;

    private double _metricsSnapshotGenerationIntervalMilliseconds;

    private double _chartRenderFrameGenerationIntervalMilliseconds;

    private double _renderHistorySampleIntervalMilliseconds;

    private int _capturePumpsActive;

    private int _acceptedSessionGeneration;

    private int _captureActive;

    private int _packetEnqueuesInFlight;

    private PollingMetricsSnapshot _pausedMetricsSnapshot;

    private PollingChartRenderFrame _pausedChartRenderFrame;

    private bool _hasPausedMetricsSnapshotCache;

    private bool _hasPausedChartRenderFrameCache;

    private long _droppedPacketCount;

    private int _packetConsumerDelayMilliseconds;

    private bool _disposed;

    public event EventHandler DevicesChanged;

    public event EventHandler SelectedDeviceDisconnected;

    public PollingRateCaptureService(IRawInputBroker rawInputBroker)
        : this(rawInputBroker, DefaultPacketBufferCapacity)
    {
    }

    internal PollingRateCaptureService(IRawInputBroker rawInputBroker, int packetBufferCapacity, int packetConsumerDelayMilliseconds = 0)
        : this(rawInputBroker, rawInputBroker, packetBufferCapacity, packetConsumerDelayMilliseconds)
    {
    }

    internal PollingRateCaptureService(IRawMouseReportSource mouseReportSource, IRawInputDeviceCatalog deviceCatalog, int packetBufferCapacity, int packetConsumerDelayMilliseconds = 0)
    {
        _metricsSnapshotBufferSyncRoot = new object();
        _chartRenderFrameBufferSyncRoot = new object();
        _pausedSnapshotSyncRoot = new object();
        _queueProcessingSyncRoot = new object();
        _metricsSnapshotGenerationIntervalMilliseconds = DefaultMetricsSnapshotGenerationIntervalMilliseconds;
        _chartRenderFrameGenerationIntervalMilliseconds = DefaultChartRenderFrameGenerationIntervalMilliseconds;
        _renderHistorySampleIntervalMilliseconds = DefaultRenderHistorySampleIntervalMilliseconds;
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
        _sessionRouter = new RawMouseSessionRouter(_mouseReportSource, _deviceCatalog, RawMouseSessionMode.SpecificDevice);
        _engine = new PollingRateEngine();
        _metricsSnapshotBuffer = new LatestValueRingBuffer<PollingMetricsSnapshot>(MetricsSnapshotBufferCapacity);
        _chartRenderFrameBuffer = new LatestValueRingBuffer<PollingChartRenderFrame>(ChartRenderFrameBufferCapacity);
        _metricsSnapshotTimer = new Timer(OnMetricsSnapshotTimerTick, null, -1, -1);
        _chartRenderFrameTimer = new Timer(OnChartRenderFrameTimerTick, null, -1, -1);
        _packetInbox = new BufferedRawMousePacketInbox(Math.Max(1, packetBufferCapacity));
        _packetConsumerDelayMilliseconds = Math.Max(0, packetConsumerDelayMilliseconds);
        _packetPumpThread = new Thread(PacketPumpThreadMain)
        {
            IsBackground = true,
            Name = "PollingRatePacketPump"
        };
        _engine.SetRenderHistorySampleInterval(_renderHistorySampleIntervalMilliseconds);
        _deviceCatalog.MouseDevicesChanged += OnDevicesChanged;
        _sessionRouter.PacketCaptured += OnPacketCaptured;
        _sessionRouter.SelectedDeviceDisconnected += OnSelectedDeviceDisconnected;
        _packetPumpThread.Start();
    }

    public IReadOnlyList<RawMouseDeviceInfo> GetDevices()
    {
        return MouseDeviceFiltering.FilterSelectableMotionDevices(_deviceCatalog.GetMouseDevices(), _deviceCatalog.GetMouseEndpointActivitySnapshots());
    }

    public void BeginSession(string deviceId)
    {
        Volatile.Write(ref _captureActive, 0);
        _packetInbox.Drain();
        ClearPausedSnapshotCache();
        _sessionRouter.BeginSession(deviceId);
        Volatile.Write(ref _acceptedSessionGeneration, _sessionRouter.SessionGeneration);
        double nowMilliseconds = GetNowMilliseconds();
        bool hasTargetDevice = !string.IsNullOrWhiteSpace(deviceId);
        if (hasTargetDevice)
        {
            _engine.BeginActiveSegment(nowMilliseconds);
        }
        else
        {
            _engine.EndActiveSegment(nowMilliseconds);
        }
        Volatile.Write(ref _capturePumpsActive, hasTargetDevice ? 1 : 0);
        UpdateMetricsSnapshotTimer();
        UpdateChartRenderFrameTimer();
        Volatile.Write(ref _captureActive, hasTargetDevice ? 1 : 0);
        CaptureMetricsSnapshot();
        CaptureChartRenderFrame();
    }

    public void PauseSession()
    {
        PollingMetricsSnapshot frozenMetricsSnapshot = ReadLatestBufferedMetricsSnapshot();
        PollingChartRenderFrame frozenChartRenderFrame = ReadLatestBufferedChartRenderFrame();

        _sessionRouter.PauseSession();
        Volatile.Write(ref _capturePumpsActive, 0);
        Volatile.Write(ref _captureActive, 0);
        Volatile.Write(ref _acceptedSessionGeneration, _sessionRouter.SessionGeneration);
        WaitForPacketEnqueueToSettle();
        FlushQueuedPackets();
        StopCaptureTimers();
        double nowMilliseconds = GetNowMilliseconds();
        SetPausedSnapshotCache(frozenMetricsSnapshot, frozenChartRenderFrame);
        _packetInbox.Drain();
        _engine.EndActiveSegment(nowMilliseconds);
    }

    private PollingMetricsSnapshot ReadLatestBufferedMetricsSnapshot()
    {
        PollingMetricsSnapshot metricsSnapshot = null;
        object metricsSnapshotBufferSyncRoot = _metricsSnapshotBufferSyncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(metricsSnapshotBufferSyncRoot, ref lockTaken);
            _metricsSnapshotBuffer.TryPeekLatest(ref metricsSnapshot);
            return metricsSnapshot;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(metricsSnapshotBufferSyncRoot);
            }
        }
    }

    private PollingChartRenderFrame ReadLatestBufferedChartRenderFrame()
    {
        PollingChartRenderFrame chartRenderFrame = null;
        object chartRenderFrameBufferSyncRoot = _chartRenderFrameBufferSyncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(chartRenderFrameBufferSyncRoot, ref lockTaken);
            _chartRenderFrameBuffer.TryPeekLatest(ref chartRenderFrame);
            return chartRenderFrame;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(chartRenderFrameBufferSyncRoot);
            }
        }
    }


    public void StopSession()
    {
        _sessionRouter.StopSession();
        WaitForPacketEnqueueToSettle();
        FlushQueuedPackets();
        ClearPausedSnapshotCache();
        Volatile.Write(ref _captureActive, 0);
        Volatile.Write(ref _acceptedSessionGeneration, _sessionRouter.SessionGeneration);
        _packetInbox.Drain();
        StopCaptureTimers();
        Volatile.Write(ref _capturePumpsActive, 0);
        double nowMilliseconds = GetNowMilliseconds();
        _engine.EndActiveSegment(nowMilliseconds);
        CaptureMetricsSnapshot();
        CaptureChartRenderFrame();
    }

    public void ResetStatistics()
    {
        Volatile.Write(ref _captureActive, 0);
        WaitForPacketEnqueueToSettle();
        object queueProcessingSyncRoot = _queueProcessingSyncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(queueProcessingSyncRoot, ref lockTaken);
            _packetInbox.Drain();
            ClearPausedSnapshotCache();
            StopCaptureTimers();
            Volatile.Write(ref _capturePumpsActive, 0);
            _engine.EndActiveSegment();
            _engine.Reset();
            object metricsSnapshotBufferSyncRoot = _metricsSnapshotBufferSyncRoot;
            bool lockTaken3 = false;
            try
            {
                Monitor.Enter(metricsSnapshotBufferSyncRoot, ref lockTaken3);
                _metricsSnapshotBuffer.Clear();
            }
            finally
            {
                if (lockTaken3)
                {
                    Monitor.Exit(metricsSnapshotBufferSyncRoot);
                }
            }
            object chartRenderFrameBufferSyncRoot = _chartRenderFrameBufferSyncRoot;
            bool lockTaken4 = false;
            try
            {
                Monitor.Enter(chartRenderFrameBufferSyncRoot, ref lockTaken4);
                _chartRenderFrameBuffer.Clear();
            }
            finally
            {
                if (lockTaken4)
                {
                    Monitor.Exit(chartRenderFrameBufferSyncRoot);
                }
            }
            Interlocked.Exchange(ref _droppedPacketCount, 0L);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(queueProcessingSyncRoot);
            }
        }
    }

    public void SetMode(PollingRateMode mode)
    {
        _engine.SetMode(mode);
    }

    public void SetChartRenderFrameRateHz(double frameRateHz)
    {
        if (_disposed)
        {
            return;
        }

        double intervalMilliseconds = NormalizeChartRenderFrameIntervalMilliseconds(frameRateHz);
        bool changed = false;
        if (Math.Abs(_chartRenderFrameGenerationIntervalMilliseconds - intervalMilliseconds) >= 0.001
            || Math.Abs(_renderHistorySampleIntervalMilliseconds - intervalMilliseconds) >= 0.001)
        {
            _chartRenderFrameGenerationIntervalMilliseconds = intervalMilliseconds;
            _renderHistorySampleIntervalMilliseconds = intervalMilliseconds;
            _engine.SetRenderHistorySampleInterval(_renderHistorySampleIntervalMilliseconds);
            changed = true;
        }

        if (changed && !_disposed)
        {
            UpdateChartRenderFrameTimer();
        }
    }

    public PollingChartRenderFrame CaptureChartRenderFrame()
    {
        PollingChartRenderFrame chartRenderFrame = null;
        if (TryGetPausedChartRenderFrame(ref chartRenderFrame))
        {
            return chartRenderFrame;
        }
        return CaptureChartRenderFrameCore(GetNowMilliseconds());
    }

    public PollingMetricsSnapshot CaptureMetricsSnapshot()
    {
        PollingMetricsSnapshot metricsSnapshot = null;
        if (TryGetPausedMetricsSnapshot(ref metricsSnapshot))
        {
            return metricsSnapshot;
        }
        return CaptureMetricsSnapshotCore(GetNowMilliseconds());
    }

    public bool TryReadLatestMetricsSnapshot(ref PollingMetricsSnapshot metricsSnapshot)
    {
        if (TryGetPausedMetricsSnapshot(ref metricsSnapshot))
        {
            return true;
        }
        object metricsSnapshotBufferSyncRoot = _metricsSnapshotBufferSyncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(metricsSnapshotBufferSyncRoot, ref lockTaken);
            return _metricsSnapshotBuffer.TryReadLatest(ref metricsSnapshot);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(metricsSnapshotBufferSyncRoot);
            }
        }
    }

    public bool TryReadLatestChartRenderFrame(ref PollingChartRenderFrame chartRenderFrame)
    {
        if (TryGetPausedChartRenderFrame(ref chartRenderFrame))
        {
            return true;
        }
        object chartRenderFrameBufferSyncRoot = _chartRenderFrameBufferSyncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(chartRenderFrameBufferSyncRoot, ref lockTaken);
            return _chartRenderFrameBuffer.TryReadLatest(ref chartRenderFrame);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(chartRenderFrameBufferSyncRoot);
            }
        }
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
        ClearPausedSnapshotCache();
        Volatile.Write(ref _captureActive, 0);
        Volatile.Write(ref _acceptedSessionGeneration, _sessionRouter.SessionGeneration);
        _packetInbox.Drain();
        StopCaptureTimers();
        Volatile.Write(ref _capturePumpsActive, 0);
        double nowMilliseconds = GetNowMilliseconds();
        _engine.EndActiveSegment(nowMilliseconds);
        CaptureMetricsSnapshot();
        CaptureChartRenderFrame();
        SelectedDeviceDisconnected?.Invoke(this, EventArgs.Empty);
    }

    private void OnMetricsSnapshotTimerTick(object state)
    {
        if (!_disposed && Volatile.Read(in _capturePumpsActive) != 0)
        {
            CaptureMetricsSnapshotCore(GetNowMilliseconds());
        }
    }

    private void OnChartRenderFrameTimerTick(object state)
    {
        if (!_disposed && Volatile.Read(in _capturePumpsActive) != 0)
        {
            CaptureChartRenderFrameCore(GetNowMilliseconds());
        }
    }

    private void UpdateMetricsSnapshotTimer()
    {
        TimeSpan dueTime = Timeout.InfiniteTimeSpan;
        TimeSpan period = Timeout.InfiniteTimeSpan;
        if (Volatile.Read(in _capturePumpsActive) != 0)
        {
            period = TimeSpan.FromMilliseconds(_metricsSnapshotGenerationIntervalMilliseconds);
            dueTime = TimeSpan.Zero;
        }
        _metricsSnapshotTimer.Change(dueTime, period);
    }

    private void UpdateChartRenderFrameTimer()
    {
        TimeSpan dueTime = Timeout.InfiniteTimeSpan;
        TimeSpan period = Timeout.InfiniteTimeSpan;
        if (Volatile.Read(in _capturePumpsActive) != 0)
        {
            period = TimeSpan.FromMilliseconds(_chartRenderFrameGenerationIntervalMilliseconds);
            dueTime = TimeSpan.Zero;
        }
        _chartRenderFrameTimer.Change(dueTime, period);
    }

    private PollingChartRenderFrame CaptureChartRenderFrameCore(double nowMs)
    {
        PollingChartRenderFrame pollingChartRenderFrame = _engine.CreateChartRenderFrame(nowMs);
        object chartRenderFrameBufferSyncRoot = _chartRenderFrameBufferSyncRoot;
        bool lockTaken2 = false;
        try
        {
            Monitor.Enter(chartRenderFrameBufferSyncRoot, ref lockTaken2);
            _chartRenderFrameBuffer.Write(pollingChartRenderFrame);
            return pollingChartRenderFrame;
        }
        finally
        {
            if (lockTaken2)
            {
                Monitor.Exit(chartRenderFrameBufferSyncRoot);
            }
        }
    }

    private PollingMetricsSnapshot CaptureMetricsSnapshotCore(double nowMs)
    {
        PollingMetricsSnapshot pollingMetricsSnapshot = _engine.CreateMetricsSnapshot(nowMs, Interlocked.Read(in _droppedPacketCount));
        object metricsSnapshotBufferSyncRoot = _metricsSnapshotBufferSyncRoot;
        bool lockTaken2 = false;
        try
        {
            Monitor.Enter(metricsSnapshotBufferSyncRoot, ref lockTaken2);
            _metricsSnapshotBuffer.Write(pollingMetricsSnapshot);
            return pollingMetricsSnapshot;
        }
        finally
        {
            if (lockTaken2)
            {
                Monitor.Exit(metricsSnapshotBufferSyncRoot);
            }
        }
    }

    private void SetPausedSnapshotCache(PollingMetricsSnapshot metricsSnapshot, PollingChartRenderFrame chartRenderFrame)
    {
        object pausedSnapshotSyncRoot = _pausedSnapshotSyncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(pausedSnapshotSyncRoot, ref lockTaken);
            _pausedMetricsSnapshot = metricsSnapshot;
            _pausedChartRenderFrame = chartRenderFrame;
            _hasPausedMetricsSnapshotCache = metricsSnapshot != null;
            _hasPausedChartRenderFrameCache = chartRenderFrame != null;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(pausedSnapshotSyncRoot);
            }
        }
    }

    private void ClearPausedSnapshotCache()
    {
        object pausedSnapshotSyncRoot = _pausedSnapshotSyncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(pausedSnapshotSyncRoot, ref lockTaken);
            _pausedMetricsSnapshot = null;
            _pausedChartRenderFrame = null;
            _hasPausedMetricsSnapshotCache = false;
            _hasPausedChartRenderFrameCache = false;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(pausedSnapshotSyncRoot);
            }
        }
    }

    private bool TryGetPausedMetricsSnapshot(ref PollingMetricsSnapshot metricsSnapshot)
    {
        object pausedSnapshotSyncRoot = _pausedSnapshotSyncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(pausedSnapshotSyncRoot, ref lockTaken);
            if (!_hasPausedMetricsSnapshotCache)
            {
                metricsSnapshot = null;
                return false;
            }
            metricsSnapshot = _pausedMetricsSnapshot;
            return true;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(pausedSnapshotSyncRoot);
            }
        }
    }

    private bool TryGetPausedChartRenderFrame(ref PollingChartRenderFrame chartRenderFrame)
    {
        object pausedSnapshotSyncRoot = _pausedSnapshotSyncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(pausedSnapshotSyncRoot, ref lockTaken);
            if (!_hasPausedChartRenderFrameCache)
            {
                chartRenderFrame = null;
                return false;
            }
            chartRenderFrame = _pausedChartRenderFrame;
            return true;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(pausedSnapshotSyncRoot);
            }
        }
    }

    private void StopCaptureTimers()
    {
        _metricsSnapshotTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _chartRenderFrameTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
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
        if (Volatile.Read(in _captureActive) == 0 || queuedPacket.Generation != Volatile.Read(in _acceptedSessionGeneration))
        {
            return;
        }
        _engine.PushPacket(queuedPacket.Packet);
    }

    private static double GetNowMilliseconds()
    {
        return (double)Stopwatch.GetTimestamp() * 1000.0 / (double)Stopwatch.Frequency;
    }

    private static double NormalizeChartRenderFrameIntervalMilliseconds(double frameRateHz)
    {
        if (double.IsNaN(frameRateHz) || double.IsInfinity(frameRateHz) || frameRateHz <= 0.0)
        {
            return DefaultChartRenderFrameGenerationIntervalMilliseconds;
        }

        double clampedFrameRateHz = Math.Max(MinimumChartRenderFrameRateHz, Math.Min(MaximumChartRenderFrameRateHz, frameRateHz));
        return 1000.0 / clampedFrameRateHz;
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
            _metricsSnapshotTimer.Change(-1, -1);
            _metricsSnapshotTimer.Dispose();
            _chartRenderFrameTimer.Change(-1, -1);
            _chartRenderFrameTimer.Dispose();
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
