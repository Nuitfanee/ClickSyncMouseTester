using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;

namespace ClickSyncMouseTester.Services;

[SupportedOSPlatform("windows")]
public class SensitivityMatchCaptureService : IDisposable
{
    private const double BindingTimeoutMilliseconds = 10000.0;

    private readonly object _syncRoot;

    private readonly IRawMouseReportSource _mouseReportSource;

    private readonly IRawInputDeviceCatalog _deviceCatalog;

    private readonly SensitivityMatchEngine _engine;

    private readonly Dictionary<string, RawMouseDeviceInfo> _availableDevices;

    private RawMouseDeviceInfo _sourceDevice;

    private RawMouseDeviceInfo _targetDevice;

    private bool _isSourceDisconnected;

    private bool _isTargetDisconnected;

    private SensitivityMatchBindingSlot? _pendingBindingSlot;

    private double _pendingBindingStartMs;

    private SensitivityMatchBindingIssue _lastBindingIssue;

    private SensitivityMatchBindingSlot? _lastBindingIssueSlot;

    private bool _disposed;

    public event EventHandler DevicesChanged;

    public SensitivityMatchCaptureService(IRawInputBroker rawInputBroker)
        : this(rawInputBroker, rawInputBroker)
    {
    }

    internal SensitivityMatchCaptureService(IRawMouseReportSource mouseReportSource, IRawInputDeviceCatalog deviceCatalog)
    {
        _syncRoot = new object();
        _availableDevices = new Dictionary<string, RawMouseDeviceInfo>(StringComparer.OrdinalIgnoreCase);
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
        _engine = new SensitivityMatchEngine();
        RefreshAvailableDevices();
        _mouseReportSource.MousePacketCaptured += OnMousePacketCaptured;
        _deviceCatalog.MouseDevicesChanged += OnMouseDevicesChanged;
    }

    public SensitivityMatchSnapshot CaptureSnapshot()
    {
        return CaptureSnapshot(GetNowMilliseconds());
    }

    public SensitivityMatchSnapshot CaptureSnapshot(double nowMs)
    {
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            ApplyBindingTimeoutIfNeeded(nowMs);
            _engine.Update(nowMs);
            return new SensitivityMatchSnapshot(_availableDevices.Count, _sourceDevice, _targetDevice, _isSourceDisconnected, _isTargetDisconnected, _pendingBindingSlot, _lastBindingIssue, _lastBindingIssueSlot, _engine.CreateCurrentRoundState(), _engine.CompletedRounds, _engine.LastRoundFailureReason, _engine.LastRoundFailureIndex, _engine.GetFinalScale(), _engine.GetFinalRecommendedTargetDpi(), _engine.GetConsistencyPercent(), _engine.GetConsistencyLevel(), _engine.ResultsExpired);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    public bool BeginBinding(SensitivityMatchBindingSlot slot)
    {
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            if (_availableDevices.Count < 2)
            {
                return false;
            }
            _pendingBindingSlot = slot;
            _pendingBindingStartMs = GetNowMilliseconds();
            _lastBindingIssue = SensitivityMatchBindingIssue.None;
            _lastBindingIssueSlot = null;
            _engine.CancelActiveRound();
            _engine.ResetMeasurements();
            if (slot == SensitivityMatchBindingSlot.SourceMouse)
            {
                _sourceDevice = null;
                _isSourceDisconnected = false;
            }
            else
            {
                _targetDevice = null;
                _isTargetDisconnected = false;
            }
            return true;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    public void UnbindSlot(SensitivityMatchBindingSlot slot)
    {
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            if (_pendingBindingSlot.HasValue && _pendingBindingSlot.Value == slot)
            {
                _pendingBindingSlot = null;
            }
            _lastBindingIssue = SensitivityMatchBindingIssue.None;
            _lastBindingIssueSlot = null;
            _engine.CancelActiveRound();
            _engine.ResetMeasurements();
            if (slot == SensitivityMatchBindingSlot.SourceMouse)
            {
                _sourceDevice = null;
                _isSourceDisconnected = false;
            }
            else
            {
                _targetDevice = null;
                _isTargetDisconnected = false;
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

    public bool StartRound(int sourceDpi, int targetCurrentDpi)
    {
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            if (_availableDevices.Count < 2 || _pendingBindingSlot.HasValue || _sourceDevice == null || _targetDevice == null || _isSourceDisconnected || _isTargetDisconnected)
            {
                return false;
            }
            return _engine.StartRound(sourceDpi, targetCurrentDpi, GetNowMilliseconds());
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    public void ResetMeasurements()
    {
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            _engine.ResetMeasurements();
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    public void ResetAll()
    {
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            _pendingBindingSlot = null;
            _lastBindingIssue = SensitivityMatchBindingIssue.None;
            _lastBindingIssueSlot = null;
            _sourceDevice = null;
            _targetDevice = null;
            _isSourceDisconnected = false;
            _isTargetDisconnected = false;
            _engine.Reset();
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    public void CancelTransientActivity()
    {
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            _pendingBindingSlot = null;
            _engine.CancelActiveRound();
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    private void OnMousePacketCaptured(object sender, RawMousePacketEventArgs e)
    {
        if (e == null || e.Packet == null)
        {
            return;
        }
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            if (_disposed)
            {
                return;
            }
            if (_pendingBindingSlot.HasValue)
            {
                TryBindPendingSlot(e.Packet);
            }
            else if (_engine.HasActiveRound)
            {
                if (_sourceDevice != null && string.Equals(_sourceDevice.DeviceId, e.Packet.DeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    _engine.PushPacket(SensitivityMatchBindingSlot.SourceMouse, e.Packet);
                }
                else if (_targetDevice != null && string.Equals(_targetDevice.DeviceId, e.Packet.DeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    _engine.PushPacket(SensitivityMatchBindingSlot.TargetMouse, e.Packet);
                }
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

    private void TryBindPendingSlot(RawMousePacket packet)
    {
        if (packet == null || !_pendingBindingSlot.HasValue || (packet.DeltaX == 0 && packet.DeltaY == 0))
        {
            return;
        }
        RawMouseDeviceInfo capturedDevice = null;
        if (!_availableDevices.TryGetValue(packet.DeviceId, out capturedDevice) || capturedDevice == null)
        {
            return;
        }
        RawMouseDeviceInfo alreadyBoundOtherDevice = ((_pendingBindingSlot.Value == SensitivityMatchBindingSlot.SourceMouse) ? _targetDevice : _sourceDevice);
        if (alreadyBoundOtherDevice == null || !string.Equals(alreadyBoundOtherDevice.DeviceId, capturedDevice.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            _engine.CancelActiveRound();
            _engine.ResetMeasurements();
            _lastBindingIssue = SensitivityMatchBindingIssue.None;
            _lastBindingIssueSlot = null;
            if (_pendingBindingSlot.Value == SensitivityMatchBindingSlot.SourceMouse)
            {
                _sourceDevice = capturedDevice;
                _isSourceDisconnected = false;
            }
            else
            {
                _targetDevice = capturedDevice;
                _isTargetDisconnected = false;
            }
            _pendingBindingSlot = null;
        }
    }

    private void OnMouseDevicesChanged(object sender, EventArgs e)
    {
        RefreshAvailableDevices();
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            bool deviceDisconnected = false;
            if (_sourceDevice != null && !_availableDevices.ContainsKey(_sourceDevice.DeviceId))
            {
                _isSourceDisconnected = true;
                deviceDisconnected = true;
            }
            if (_targetDevice != null && !_availableDevices.ContainsKey(_targetDevice.DeviceId))
            {
                _isTargetDisconnected = true;
                deviceDisconnected = true;
            }
            if (deviceDisconnected)
            {
                _pendingBindingSlot = null;
                _engine.CancelActiveRound();
                _engine.InvalidateResults();
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshAvailableDevices()
    {
        IReadOnlyList<RawMouseDeviceInfo> availableDevices = MouseDeviceFiltering.FilterSelectableMotionDevices(_deviceCatalog.GetMouseDevices(), _deviceCatalog.GetMouseEndpointActivitySnapshots());
        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            _availableDevices.Clear();
            foreach (RawMouseDeviceInfo device in availableDevices)
            {
                if (!string.IsNullOrWhiteSpace(device.DeviceId))
                {
                    _availableDevices[device.DeviceId] = device;
                }
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

    private void ApplyBindingTimeoutIfNeeded(double nowMs)
    {
        if (_pendingBindingSlot.HasValue)
        {
            double elapsedBindingMilliseconds = nowMs - _pendingBindingStartMs;
            if (!double.IsNaN(elapsedBindingMilliseconds) && !double.IsInfinity(elapsedBindingMilliseconds) && !(elapsedBindingMilliseconds < BindingTimeoutMilliseconds))
            {
                _lastBindingIssue = SensitivityMatchBindingIssue.TimedOut;
                _lastBindingIssueSlot = _pendingBindingSlot;
                _pendingBindingSlot = null;
            }
        }
    }

    private static double GetNowMilliseconds()
    {
        return (double)Stopwatch.GetTimestamp() * 1000.0 / (double)Stopwatch.Frequency;
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
}





