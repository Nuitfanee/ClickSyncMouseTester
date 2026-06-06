using ClickSyncMouseTester.Models;
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ClickSyncMouseTester.Services;

internal sealed class RawMouseDeviceDescriptorFactory
{
    private readonly RawMouseDeviceMetadataResolver _metadataResolver;

    public RawMouseDeviceDescriptorFactory(RawMouseDeviceMetadataResolver metadataResolver)
    {
        _metadataResolver = metadataResolver ?? throw new ArgumentNullException(nameof(metadataResolver));
    }

    public RawMouseDeviceInfo BuildDeviceInfo(nint deviceHandle)
    {
        string devicePath = ReadDevicePath(deviceHandle);
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return null;
        }

        int? vendorId = ReadHexToken(devicePath, "VID");
        int? productId = ReadHexToken(devicePath, "PID");
        int buttonCount = ReadButtonCount(deviceHandle);
        RawMouseDeviceMetadata metadata = _metadataResolver.ResolveMetadata(devicePath);
        string displayName = metadata.DisplayName;
        string physicalDeviceKey = metadata.PhysicalDeviceKey;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = vendorId.HasValue && productId.HasValue
                ? $"HID Mouse (VID_{vendorId.Value:X4}/PID_{productId.Value:X4})"
                : "HID Mouse";
        }

        return new RawMouseDeviceInfo(devicePath, displayName, vendorId, productId, buttonCount, IsVirtualDevice(devicePath, displayName), physicalDeviceKey);
    }

    private static string ReadDevicePath(nint deviceHandle)
    {
        uint devicePathCharacterCount = 0u;
        NativeMethods.GetRawInputDeviceInfo(deviceHandle, NativeMethods.RIDI_DEVICENAME, IntPtr.Zero, ref devicePathCharacterCount);
        if (devicePathCharacterCount == 0 || devicePathCharacterCount > int.MaxValue)
        {
            return string.Empty;
        }

        StringBuilder devicePath = new StringBuilder((int)devicePathCharacterCount);
        if (NativeMethods.GetRawInputDeviceInfo(deviceHandle, NativeMethods.RIDI_DEVICENAME, devicePath, ref devicePathCharacterCount) == NativeMethods.InvalidRawInputResult)
        {
            return string.Empty;
        }
        return devicePath.ToString();
    }

    private static int ReadButtonCount(nint deviceHandle)
    {
        int deviceInfoSize = Marshal.SizeOf<NativeMethods.RID_DEVICE_INFO>();
        NativeMethods.RID_DEVICE_INFO deviceInfo = new NativeMethods.RID_DEVICE_INFO
        {
            cbSize = (uint)deviceInfoSize
        };
        uint size = deviceInfo.cbSize;
        nint deviceInfoBuffer = Marshal.AllocHGlobal(deviceInfoSize);
        try
        {
            Marshal.StructureToPtr(deviceInfo, deviceInfoBuffer, fDeleteOld: false);
            if (NativeMethods.GetRawInputDeviceInfo(deviceHandle, NativeMethods.RIDI_DEVICEINFO, deviceInfoBuffer, ref size) == NativeMethods.InvalidRawInputResult)
            {
                return 0;
            }

            NativeMethods.RID_DEVICE_INFO resolvedDeviceInfo = Marshal.PtrToStructure<NativeMethods.RID_DEVICE_INFO>(deviceInfoBuffer);
            if (resolvedDeviceInfo.dwType != NativeMethods.RIM_TYPEMOUSE)
            {
                return 0;
            }
            return (int)resolvedDeviceInfo.mouse.dwNumberOfButtons;
        }
        finally
        {
            Marshal.FreeHGlobal(deviceInfoBuffer);
        }
    }

    private static int? ReadHexToken(string value, string token)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        string pattern = string.Format(CultureInfo.InvariantCulture, "(?:^|[#\\\\]){0}_([0-9A-F]{{4}})", Regex.Escape(token));
        Match match = Regex.Match(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hexValue))
        {
            return null;
        }

        return hexValue;
    }

    private static bool IsVirtualDevice(string devicePath, string displayName)
    {
        if (!string.IsNullOrWhiteSpace(devicePath)
            && (devicePath.IndexOf("ROOT", StringComparison.OrdinalIgnoreCase) >= 0
                || devicePath.IndexOf("VIRTUAL", StringComparison.OrdinalIgnoreCase) >= 0
                || devicePath.IndexOf("RDP_MOU", StringComparison.OrdinalIgnoreCase) >= 0
                || devicePath.IndexOf("VMWARE", StringComparison.OrdinalIgnoreCase) >= 0
                || devicePath.IndexOf("VMBUS", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        return displayName.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0
            || displayName.IndexOf("Remote Desktop", StringComparison.OrdinalIgnoreCase) >= 0
            || displayName.IndexOf("RDP", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
