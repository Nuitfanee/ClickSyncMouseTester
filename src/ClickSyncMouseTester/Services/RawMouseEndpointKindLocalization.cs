using ClickSyncMouseTester.Models;
using System;

namespace ClickSyncMouseTester.Services;

internal static class RawMouseEndpointKindLocalization
{
    public static string Resolve(RawMouseEndpointKind endpointKind, Func<string, string> localize)
    {
        if (localize == null)
        {
            return string.Empty;
        }

        switch (endpointKind)
        {
            case RawMouseEndpointKind.MotionCapable:
                return localize("Device.Detail.Endpoint.Motion");
            case RawMouseEndpointKind.ControlOnly:
                return localize("Device.Detail.Endpoint.ControlOnly");
            case RawMouseEndpointKind.Inactive:
                return localize("Device.Detail.Endpoint.Inactive");
            default:
                return localize("Device.Detail.Endpoint.Unknown");
        }
    }
}
