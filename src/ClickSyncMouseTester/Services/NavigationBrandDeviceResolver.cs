using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Services;

internal sealed class NavigationBrandDeviceResolver
{
    private readonly IRawInputDeviceCatalog _deviceCatalog;
    private readonly DeviceSelectionCoordinator _deviceSelectionCoordinator;

    public NavigationBrandDeviceResolver(IRawInputDeviceCatalog deviceCatalog, DeviceSelectionCoordinator deviceSelectionCoordinator = null)
    {
        _deviceCatalog = deviceCatalog ?? throw new ArgumentNullException(nameof(deviceCatalog));
        _deviceSelectionCoordinator = deviceSelectionCoordinator;
    }

    public RawMouseDeviceInfo ResolvePreferredDevice()
    {
        _deviceCatalog.RequestMouseDevicesRefresh();
        IReadOnlyList<RawMouseDeviceInfo> selectableDevices = MouseDeviceFiltering.FilterSelectableMotionDevices(
            _deviceCatalog.GetMouseDevices(),
            _deviceCatalog.GetMouseEndpointActivitySnapshots());

        if (_deviceSelectionCoordinator != null)
        {
            return _deviceSelectionCoordinator.ResolvePreferredDevice(selectableDevices);
        }

        return RawMouseDeviceSelectionPolicy.ResolveInitialSelection(selectableDevices);
    }
}
