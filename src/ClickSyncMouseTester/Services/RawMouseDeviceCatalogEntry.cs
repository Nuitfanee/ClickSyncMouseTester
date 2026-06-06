using ClickSyncMouseTester.Models;

namespace ClickSyncMouseTester.Services;

internal sealed class RawMouseDeviceCatalogEntry
{
    public RawMouseDeviceInfo SourceDevice { get; }

    public RawMouseEndpointActivitySnapshot Activity { get; }

    public RawMouseDeviceInfo Device { get; set; }

    public RawMouseEndpointKind? ForcedEndpointKind { get; set; }

    public RawMouseDeviceCatalogEntry(RawMouseDeviceInfo sourceDevice, RawMouseEndpointActivitySnapshot activity, RawMouseDeviceInfo device)
    {
        SourceDevice = sourceDevice;
        Activity = activity;
        Device = device;
    }
}
