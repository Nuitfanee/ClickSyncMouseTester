#define TRACE
using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;

namespace ClickSyncMouseTester.Services;

[SupportedOSPlatform("windows")]
public class MousePerformanceCaptureService : IDisposable
{
    private sealed class QueueMetricsSnapshot
    {
        public int QueueOverflowCount { get; set; }

        public int QueueHighWatermark { get; set; }

        public int QueueCapacity { get; set; }
    }

    private const int DefaultPacketBufferCapacity = 65536;

    private const int PacketPumpShutdownJoinTimeoutMilliseconds = 1000;

    private readonly object _syncRoot;

    private readonly object _queueProcessingSyncRoot;

    private readonly object _queueMetricsSyncRoot;

    private readonly IRawMouseReportSource _mouseReportSource;

    private readonly IRawInputDeviceCatalog _deviceCatalog;

    private readonly RawMouseSessionRouter _sessionRouter;

    private readonly MousePerformanceEngine _engine;

    private readonly BufferedRawMousePacketInbox _packetInbox;

    private readonly Thread _packetPumpThread;

    private int _acceptedSessionGeneration;

    private int _captureActive;

    private int _packetEnqueuesInFlight;

    private int _packetConsumerDelayMilliseconds;

    private bool _selectedDeviceDisconnected;

    private int _hasAvailableMouseDevice;

    private int _queueOverflowCount;

    private MousePerformanceSnapshot _lastSummarySnapshot;

    private bool _disposed;

    public string CurrentSessionDeviceId
    {
        get
        {
            object syncRoot = _syncRoot;
            bool lockTaken = false;
            try
            {
                Monitor.Enter(syncRoot, ref lockTaken);
                return _engine.SessionDeviceId;
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(syncRoot);
                }
            }
        }
    }

    public event EventHandler DevicesChanged;

    public event EventHandler SelectedDeviceDisconnected;

    public MousePerformanceCaptureService(IRawInputBroker rawInputBroker)
        : this(rawInputBroker, DefaultPacketBufferCapacity, 0, MousePerformanceAnalysisOptions.Default)
    {
    }

    internal MousePerformanceCaptureService(IRawInputBroker rawInputBroker, int packetBufferCapacity, int packetConsumerDelayMilliseconds = 0, MousePerformanceAnalysisOptions analysisOptions = null)
        : this(rawInputBroker, rawInputBroker, packetBufferCapacity, packetConsumerDelayMilliseconds, analysisOptions)
    {
    }

    internal MousePerformanceCaptureService(IRawMouseReportSource mouseReportSource, IRawInputDeviceCatalog deviceCatalog, int packetBufferCapacity, int packetConsumerDelayMilliseconds = 0, MousePerformanceAnalysisOptions analysisOptions = null)
    {
        _syncRoot = new object();
        _queueProcessingSyncRoot = new object();
        _queueMetricsSyncRoot = new object();
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
        MousePerformanceAnalysisOptions analysisOptions2 = analysisOptions ?? MousePerformanceAnalysisOptions.Default;
        _engine = new MousePerformanceEngine(analysisOptions2);
        _packetInbox = new BufferedRawMousePacketInbox(Math.Max(1, packetBufferCapacity));
        _packetConsumerDelayMilliseconds = Math.Max(0, packetConsumerDelayMilliseconds);
        _packetPumpThread = new Thread(PacketPumpThreadMain)
        {
            IsBackground = true,
            Name = "MousePerformancePacketPump"
        };
        _deviceCatalog.MouseDevicesChanged += OnDevicesChanged;
        _sessionRouter.PacketCaptured += OnPacketCaptured;
        _sessionRouter.SelectedDeviceDisconnected += OnSelectedDeviceDisconnected;
        RefreshAvailableMouseDeviceState();
        _packetPumpThread.Start();
    }

    public IReadOnlyList<RawMouseDeviceInfo> GetDevices()
    {
        IReadOnlyList<RawMouseDeviceInfo> filteredDevices = MouseDeviceFiltering.FilterSelectableMotionDevices(_deviceCatalog.GetMouseDevices(), _deviceCatalog.GetMouseEndpointActivitySnapshots());
        UpdateAvailableMouseDeviceState(filteredDevices);
        return filteredDevices;
    }

    public bool HasAvailableMouseDevice()
    {
        return Volatile.Read(in _hasAvailableMouseDevice) != 0;
    }

    public void RequestDeviceRefresh(bool force = false)
    {
        _deviceCatalog.RequestMouseDevicesRefresh(force);
    }

    public void SetCpiState(double? effectiveCpi, bool canComputeVelocity)
    {
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            _engine.SetCpiState(effectiveCpi, canComputeVelocity);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    public bool BeginSession(string deviceId, bool startFresh)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }
        _deviceCatalog.RequestMouseDevicesRefresh(force: true);
        if (!MouseDeviceFiltering.ContainsSelectableMotionDevice(_deviceCatalog.GetMouseDevices(), _deviceCatalog.GetMouseEndpointActivitySnapshots(), deviceId))
        {
            return false;
        }
        _selectedDeviceDisconnected = false;
        Volatile.Write(ref _captureActive, 0);
        _packetInbox.Drain();
        Volatile.Write(ref _lastSummarySnapshot, null);
        if (startFresh)
        {
            ResetQueueMetrics();
        }
        _sessionRouter.BeginSession(deviceId);
        Volatile.Write(ref _acceptedSessionGeneration, _sessionRouter.SessionGeneration);
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            _engine.BeginCollecting(deviceId, Stopwatch.GetTimestamp(), startFresh);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
        Volatile.Write(ref _captureActive, 1);
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
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            _engine.PauseCollecting();
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    public void StopSession()
    {
        _sessionRouter.StopSession();
        WaitForPacketEnqueueToSettle();
        FlushQueuedPackets();
        Volatile.Write(ref _captureActive, 0);
        Volatile.Write(ref _acceptedSessionGeneration, _sessionRouter.SessionGeneration);
        _packetInbox.Drain();
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            _engine.StopCollecting();
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    public void ResetSession()
    {
        Volatile.Write(ref _captureActive, 0);
        _sessionRouter.StopSession();
        Volatile.Write(ref _acceptedSessionGeneration, _sessionRouter.SessionGeneration);
        WaitForPacketEnqueueToSettle();
        object queueProcessingSyncRoot = _queueProcessingSyncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(queueProcessingSyncRoot, ref lockTaken);
            _packetInbox.Drain();
            _selectedDeviceDisconnected = false;
            ResetQueueMetrics();
            object syncRoot = _syncRoot;
            bool lockTaken2 = false;
            try
            {
                Monitor.Enter(syncRoot, ref lockTaken2);
                _engine.ResetSession();
                Volatile.Write(ref _lastSummarySnapshot, null);
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
    }

    public MousePerformanceSnapshot CaptureSummarySnapshot()
    {
        return CaptureSnapshotCore(includeAnalysis: false);
    }

    public MousePerformanceSnapshot CaptureAnalysisSnapshot()
    {
        return CaptureSnapshotCore(includeAnalysis: true);
    }

    private MousePerformanceSnapshot CaptureSnapshotCore(bool includeAnalysis)
    {
        bool hasDevices = Volatile.Read(in _hasAvailableMouseDevice) != 0;
        QueueMetricsSnapshot queueMetricsSnapshot = GetQueueMetricsSnapshot();
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            if (!includeAnalysis && Volatile.Read(in _captureActive) != 0)
            {
                Monitor.TryEnter(syncRoot, ref lockTaken);
                if (!lockTaken)
                {
                    MousePerformanceSnapshot cachedSnapshot = Volatile.Read(ref _lastSummarySnapshot);
                    if (cachedSnapshot != null)
                    {
                        return cachedSnapshot;
                    }
                }
            }
            if (!lockTaken)
            {
                Monitor.Enter(syncRoot, ref lockTaken);
            }

            MousePerformanceSnapshot snapshot;
            if (includeAnalysis)
            {
                snapshot = _engine.CreateAnalysisSnapshot(ResolveStatus(hasDevices), Volatile.Read(in _captureActive) != 0, queueMetricsSnapshot.QueueOverflowCount, queueMetricsSnapshot.QueueHighWatermark, queueMetricsSnapshot.QueueCapacity);
            }
            else
            {
                snapshot = _engine.CreateSummarySnapshot(ResolveStatus(hasDevices), Volatile.Read(in _captureActive) != 0, queueMetricsSnapshot.QueueOverflowCount, queueMetricsSnapshot.QueueHighWatermark, queueMetricsSnapshot.QueueCapacity);
                Volatile.Write(ref _lastSummarySnapshot, snapshot);
            }
            return snapshot;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    private MousePerformanceSessionStatus ResolveStatus(bool hasDevices)
    {
        if (_selectedDeviceDisconnected)
        {
            return MousePerformanceSessionStatus.DeviceDisconnected;
        }
        if (_engine.IsCollecting)
        {
            return MousePerformanceSessionStatus.Collecting;
        }
        if (_engine.IsFinalized)
        {
            return MousePerformanceSessionStatus.Stopped;
        }
        if (_engine.CanContinue)
        {
            return MousePerformanceSessionStatus.Paused;
        }
        if (!hasDevices && !_engine.HasData)
        {
            return MousePerformanceSessionStatus.NoDevice;
        }
        return MousePerformanceSessionStatus.Ready;
    }

    private void OnDevicesChanged(object sender, EventArgs e)
    {
        RefreshAvailableMouseDeviceState();
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
            if (e.SessionGeneration != acceptedSessionGeneration)
            {
                return;
            }
            int droppedCount = 0;
            _packetInbox.Enqueue(e.Packet, acceptedSessionGeneration, ref droppedCount);
            if (droppedCount <= 0)
            {
                return;
            }
            RecordQueueOverflow(droppedCount);
            object syncRoot = _syncRoot;
            bool lockTaken = false;
            try
            {
                Monitor.Enter(syncRoot, ref lockTaken);
                _engine.ReportDroppedPackets(droppedCount);
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(syncRoot);
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
        _selectedDeviceDisconnected = true;
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            _engine.PauseCollecting();
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
        SelectedDeviceDisconnected?.Invoke(this, EventArgs.Empty);
    }

    private void PacketPumpThreadMain()
    {
        while (_packetInbox.WaitForData())
        {
            try
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
            catch (Exception ex)
            {
                Exception arg = ex;
                Trace.TraceError($"MousePerformance packet pump failed while draining the inbox: {arg}");
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
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            _engine.PushPacket(queuedPacket.Packet);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    private void ResetQueueMetrics()
    {
        object queueMetricsSyncRoot = _queueMetricsSyncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(queueMetricsSyncRoot, ref lockTaken);
            _queueOverflowCount = 0;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(queueMetricsSyncRoot);
            }
        }
        _packetInbox.ResetHighWatermark();
    }

    private void RecordQueueOverflow(int count)
    {
        if (count <= 0)
        {
            return;
        }
        object queueMetricsSyncRoot = _queueMetricsSyncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(queueMetricsSyncRoot, ref lockTaken);
            _queueOverflowCount += count;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(queueMetricsSyncRoot);
            }
        }
    }

    private QueueMetricsSnapshot GetQueueMetricsSnapshot()
    {
        QueueMetricsSnapshot queueMetricsSnapshot = new QueueMetricsSnapshot();
        object queueMetricsSyncRoot = _queueMetricsSyncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(queueMetricsSyncRoot, ref lockTaken);
            queueMetricsSnapshot.QueueOverflowCount = _queueOverflowCount;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(queueMetricsSyncRoot);
            }
        }
        queueMetricsSnapshot.QueueHighWatermark = _packetInbox.HighWatermark;
        queueMetricsSnapshot.QueueCapacity = _packetInbox.Capacity;
        return queueMetricsSnapshot;
    }

    private void RefreshAvailableMouseDeviceState()
    {
        UpdateAvailableMouseDeviceState(MouseDeviceFiltering.FilterSelectableMotionDevices(_deviceCatalog.GetMouseDevices(), _deviceCatalog.GetMouseEndpointActivitySnapshots()));
    }

    private void UpdateAvailableMouseDeviceState(IReadOnlyList<RawMouseDeviceInfo> devices)
    {
        Volatile.Write(ref _hasAvailableMouseDevice, (devices != null && devices.Count > 0) ? 1 : 0);
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
            _packetConsumerDelayMilliseconds = 0;
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





