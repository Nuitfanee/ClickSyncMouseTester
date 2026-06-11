using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Services;

internal sealed class DeviceSelectionCoordinator
{
    private string _preferredDeviceId = string.Empty;

    public event EventHandler PreferredDeviceChanged;

    public string PreferredDeviceId => _preferredDeviceId;

    public void CommitManualSelection(RawMouseDeviceInfo device)
    {
        if (device == null || string.IsNullOrWhiteSpace(device.DeviceId))
        {
            return;
        }

        SetPreferredDeviceId(device.DeviceId);
    }

    public void SetPreferredDeviceId(string deviceId)
    {
        string normalizedDeviceId = deviceId?.Trim() ?? string.Empty;
        if (string.Equals(_preferredDeviceId, normalizedDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _preferredDeviceId = normalizedDeviceId;
        PreferredDeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    public RawMouseDeviceInfo ResolveSelectionAfterRefresh(
        IReadOnlyList<RawMouseDeviceInfo> devices,
        string previousSelectedId,
        bool allowInitialSelection = true,
        bool preferManualSelection = true)
    {
        if (devices == null || devices.Count == 0)
        {
            return null;
        }

        if (preferManualSelection)
        {
            RawMouseDeviceInfo preferredDevice = FindPreferredDevice(devices);
            if (preferredDevice != null)
            {
                return preferredDevice;
            }
        }

        return RawMouseDeviceSelectionPolicy.ResolveSelectionAfterRefresh(devices, previousSelectedId, allowInitialSelection);
    }

    public RawMouseDeviceInfo ResolvePreferredDevice(IReadOnlyList<RawMouseDeviceInfo> devices)
    {
        if (devices == null || devices.Count == 0)
        {
            return null;
        }

        RawMouseDeviceInfo preferredDevice = FindPreferredDevice(devices);
        return preferredDevice ?? RawMouseDeviceSelectionPolicy.ResolveInitialSelection(devices);
    }

    public RawMouseDeviceInfo FindPreferredDevice(IReadOnlyList<RawMouseDeviceInfo> devices)
    {
        if (devices == null || devices.Count == 0 || string.IsNullOrWhiteSpace(_preferredDeviceId))
        {
            return null;
        }

        for (int deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++)
        {
            RawMouseDeviceInfo device = devices[deviceIndex];
            if (device != null && string.Equals(device.DeviceId, _preferredDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        return null;
    }
}
