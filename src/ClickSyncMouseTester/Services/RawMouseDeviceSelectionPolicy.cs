using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Services;

internal static class RawMouseDeviceSelectionPolicy
{
    public static RawMouseDeviceInfo ResolveSelectionAfterRefresh(IReadOnlyList<RawMouseDeviceInfo> devices, string previousSelectedId, bool allowInitialSelection = true)
    {
        if (devices == null || devices.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(previousSelectedId))
        {
            RawMouseDeviceInfo previousSelection = FindByDeviceId(devices, previousSelectedId);
            if (previousSelection != null)
            {
                return previousSelection;
            }
        }

        if (!allowInitialSelection)
        {
            return null;
        }

        return ResolveInitialSelection(devices);
    }

    public static RawMouseDeviceInfo ResolveInitialSelection(IReadOnlyList<RawMouseDeviceInfo> devices)
    {
        if (devices == null || devices.Count == 0)
        {
            return null;
        }

        for (int deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++)
        {
            if (devices[deviceIndex] != null)
            {
                return devices[deviceIndex];
            }
        }

        return null;
    }

    private static RawMouseDeviceInfo FindByDeviceId(IReadOnlyList<RawMouseDeviceInfo> devices, string deviceId)
    {
        for (int deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++)
        {
            RawMouseDeviceInfo device = devices[deviceIndex];
            if (device != null && string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        return null;
    }
}
