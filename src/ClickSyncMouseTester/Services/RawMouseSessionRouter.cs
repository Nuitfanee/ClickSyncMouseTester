using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;

namespace ClickSyncMouseTester.Services;

[SupportedOSPlatform("windows")]
public class RawMouseSessionRouter : IDisposable
{
    private readonly object _syncRoot;

    private readonly IRawMouseReportSource _mouseReportSource;

    private readonly IRawInputDeviceCatalog _deviceCatalog;

    private readonly RawMouseSessionMode _mode;

    private string _selectedDeviceId;

    private string _latchedDeviceId;

    private int _sessionGeneration;

    private bool _sessionActive;

    private int _packetDispatchesInFlight;

    private bool _disposed;

    public int SessionGeneration
    {
        get
        {
            object syncRoot = _syncRoot;
            bool lockTaken = false;
            try
            {
                Monitor.Enter(syncRoot, ref lockTaken);
                return _sessionGeneration;
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

    public string CurrentDeviceId
    {
        get
        {
            object syncRoot = _syncRoot;
            bool lockTaken = false;
            try
            {
                Monitor.Enter(syncRoot, ref lockTaken);
                if (_mode == RawMouseSessionMode.FirstActiveDevice)
                {
                    return _latchedDeviceId;
                }
                return _selectedDeviceId;
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

    public event EventHandler<RawMousePacketEventArgs> PacketCaptured;

    public event EventHandler SelectedDeviceDisconnected;

    public RawMouseSessionRouter(IRawInputBroker broker, RawMouseSessionMode mode)
        : this(broker, broker, mode)
    {
    }

    public RawMouseSessionRouter(IRawMouseReportSource mouseReportSource, IRawInputDeviceCatalog deviceCatalog, RawMouseSessionMode mode)
    {
        _syncRoot = new object();
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
        _mode = mode;
        _mouseReportSource.MousePacketCaptured += OnMousePacketCaptured;
        _deviceCatalog.MouseDevicesChanged += OnMouseDevicesChanged;
    }

    public void BeginSession(string deviceId = null)
    {
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            _sessionGeneration++;
            switch (_mode)
            {
                case RawMouseSessionMode.SpecificDevice:
                    _selectedDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
                    _latchedDeviceId = _selectedDeviceId;
                    _sessionActive = !string.IsNullOrWhiteSpace(_selectedDeviceId);
                    break;
                case RawMouseSessionMode.FirstActiveDevice:
                    _selectedDeviceId = null;
                    _latchedDeviceId = null;
                    _sessionActive = true;
                    break;
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

    public void PauseSession()
    {
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            _sessionGeneration++;
            _sessionActive = false;
            if (_mode == RawMouseSessionMode.FirstActiveDevice)
            {
                _latchedDeviceId = null;
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
        WaitForPacketDispatchToSettle();
    }

    public void StopSession()
    {
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            _sessionGeneration++;
            _selectedDeviceId = null;
            _latchedDeviceId = null;
            _sessionActive = false;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
        WaitForPacketDispatchToSettle();
    }

    private void OnMousePacketCaptured(object sender, RawMousePacketEventArgs e)
    {
        if (e == null || e.Packet == null)
        {
            return;
        }
        RawMousePacket packet = null;
        int sessionGeneration = 0;
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            if (!_sessionActive)
            {
                return;
            }
            switch (_mode)
            {
                case RawMouseSessionMode.SpecificDevice:
                    if (string.IsNullOrWhiteSpace(_selectedDeviceId) || !string.Equals(e.Packet.DeviceId, _selectedDeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    break;
                case RawMouseSessionMode.FirstActiveDevice:
                    if (string.IsNullOrWhiteSpace(_latchedDeviceId))
                    {
                        if (e.Packet.DeltaX == 0 && e.Packet.DeltaY == 0)
                        {
                            return;
                        }
                        _latchedDeviceId = e.Packet.DeviceId;
                    }
                    else if (!string.Equals(e.Packet.DeviceId, _latchedDeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    break;
            }
            packet = e.Packet;
            sessionGeneration = _sessionGeneration;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
        Interlocked.Increment(ref _packetDispatchesInFlight);
        try
        {
            PacketCaptured?.Invoke(this, new RawMousePacketEventArgs(packet, sessionGeneration));
        }
        finally
        {
            Interlocked.Decrement(ref _packetDispatchesInFlight);
        }
    }

    private void OnMouseDevicesChanged(object sender, EventArgs e)
    {
        IReadOnlyList<RawMouseDeviceInfo> mouseDevices = MouseDeviceFiltering.FilterSelectableMotionDevices(_deviceCatalog.GetMouseDevices(), _deviceCatalog.GetMouseEndpointActivitySnapshots());
        bool selectedDeviceDisconnected = false;
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            string activeDeviceId = null;
            if (_mode == RawMouseSessionMode.FirstActiveDevice)
            {
                activeDeviceId = _latchedDeviceId;
                if (string.IsNullOrWhiteSpace(activeDeviceId) && _sessionActive && mouseDevices.Count == 0)
                {
                    _sessionGeneration++;
                    _sessionActive = false;
                    selectedDeviceDisconnected = true;
                }
            }
            else
            {
                activeDeviceId = _selectedDeviceId;
            }

            if (!string.IsNullOrWhiteSpace(activeDeviceId) && !mouseDevices.Any(device => device != null && string.Equals(device.DeviceId, activeDeviceId, StringComparison.OrdinalIgnoreCase)))
            {
                _sessionGeneration++;
                _sessionActive = false;
                _latchedDeviceId = null;
                if (_mode == RawMouseSessionMode.SpecificDevice)
                {
                    _selectedDeviceId = null;
                }
                selectedDeviceDisconnected = true;
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }

        if (selectedDeviceDisconnected)
        {
            WaitForPacketDispatchToSettle();
            SelectedDeviceDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _mouseReportSource.MousePacketCaptured -= OnMousePacketCaptured;
            _deviceCatalog.MouseDevicesChanged -= OnMouseDevicesChanged;
        }
    }

    void IDisposable.Dispose()
    {
        this.Dispose();
    }

    private void WaitForPacketDispatchToSettle(int timeoutMilliseconds = 1000)
    {
        int timeout = Math.Max(0, timeoutMilliseconds);
        long deadlineTick = Environment.TickCount64 + timeout;
        while (Interlocked.CompareExchange(ref _packetDispatchesInFlight, 0, 0) > 0 && (timeout <= 0 || Environment.TickCount64 < deadlineTick))
        {
            Thread.Sleep(1);
        }
    }

}





