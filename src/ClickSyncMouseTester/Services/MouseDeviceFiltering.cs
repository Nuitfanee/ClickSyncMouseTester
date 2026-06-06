using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Services;

internal sealed class MouseDeviceFiltering
{
    private static readonly RawMouseDeviceCatalog Catalog = new RawMouseDeviceCatalog();

    public static IReadOnlyList<RawMouseDeviceInfo> FilterSelectableMotionDevices(IEnumerable<RawMouseDeviceInfo> devices, IReadOnlyDictionary<string, RawMouseEndpointActivitySnapshot> activitySnapshots)
    {
        if (devices == null)
        {
            return Array.Empty<RawMouseDeviceInfo>();
        }

        return Catalog.CreateSelectableDevices(devices, activitySnapshots);
    }

    public static bool ContainsSelectableMotionDevice(IEnumerable<RawMouseDeviceInfo> devices, IReadOnlyDictionary<string, RawMouseEndpointActivitySnapshot> activitySnapshots, string deviceId)
    {
        return Catalog.ContainsSelectableDevice(devices, activitySnapshots, deviceId);
    }
}





