using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Services;

public interface IRawInputDeviceCatalog
{
    event EventHandler MouseDevicesChanged;

    IReadOnlyList<RawMouseDeviceInfo> GetMouseDevices();

    IReadOnlyDictionary<string, RawMouseEndpointActivitySnapshot> GetMouseEndpointActivitySnapshots();

    void RequestMouseDevicesRefresh(bool force = false);
}
