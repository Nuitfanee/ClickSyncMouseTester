using ClickSyncMouseTester.Models;

namespace ClickSyncMouseTester.Services;

internal sealed class RawMouseEndpointState
{
    public RawMouseDeviceInfo DeviceInfo { get; }

    public RawMouseEndpointState(RawMouseDeviceInfo deviceInfo)
    {
        DeviceInfo = deviceInfo;
    }
}
