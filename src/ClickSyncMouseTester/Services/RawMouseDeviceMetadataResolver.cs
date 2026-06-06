using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ClickSyncMouseTester.Services;

internal sealed class RawMouseDeviceMetadataResolver
{
    private const int HidStringBufferLength = 512;
    private const int DeviceInterfaceDetailDataOffset = 4;
    private const int DeviceInterfaceDetailDataSizeX86 = 6;
    private const int DeviceInterfaceDetailDataSizeX64 = 8;
    private const int DevPropertyTypeGuid = 13;

    private static readonly HashSet<string> GenericLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HID-compliant mouse", "USB Input Device", "HID-compliant device", "HID Mouse", "Mouse", "Mouse Device" };

    private static readonly NativeMethods.DEVPROPKEY DeviceContainerIdPropertyKey = new NativeMethods.DEVPROPKEY
    {
        fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"),
        pid = 2
    };

    private readonly object _syncRoot;
    private readonly Dictionary<string, RawMouseDeviceMetadata> _metadataByNormalizedPath;

    public RawMouseDeviceMetadataResolver()
    {
        _syncRoot = new object();
        _metadataByNormalizedPath = new Dictionary<string, RawMouseDeviceMetadata>(StringComparer.OrdinalIgnoreCase);
    }

    public RawMouseDeviceMetadata ResolveMetadata(string devicePath)
    {
        string normalizedPath = NormalizeDevicePath(devicePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return RawMouseDeviceMetadata.Empty;
        }

        lock (_syncRoot)
        {
            if (_metadataByNormalizedPath.TryGetValue(normalizedPath, out RawMouseDeviceMetadata cachedMetadata))
            {
                return cachedMetadata;
            }
        }

        RawMouseDeviceMetadata resolvedMetadata = ResolveMetadataUncached(normalizedPath);
        lock (_syncRoot)
        {
            if (!_metadataByNormalizedPath.ContainsKey(normalizedPath))
            {
                _metadataByNormalizedPath[normalizedPath] = resolvedMetadata;
            }
            return _metadataByNormalizedPath[normalizedPath];
        }
    }

    private RawMouseDeviceMetadata ResolveMetadataUncached(string normalizedPath)
    {
        SetupApiMouseInterfaceMetadata setupApiMetadata = ReadSetupApiMetadata(normalizedPath);
        string displayName = SanitizeDisplayName(ReadHidDisplayName(normalizedPath));
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = SanitizeDisplayName(setupApiMetadata.DisplayName);
        }

        string physicalDeviceKey = string.IsNullOrWhiteSpace(setupApiMetadata.ContainerId)
            ? string.Empty
            : "container:" + setupApiMetadata.ContainerId.ToUpperInvariant();

        return new RawMouseDeviceMetadata(displayName, physicalDeviceKey);
    }

    private string ReadHidDisplayName(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return string.Empty;
        }

        nint hidDeviceHandle = NativeMethods.CreateFile(
            normalizedPath,
            desiredAccess: 0u,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            flagsAndAttributes: 0u,
            IntPtr.Zero);
        if (hidDeviceHandle == IntPtr.Zero || hidDeviceHandle == NativeMethods.InvalidFileHandle)
        {
            return string.Empty;
        }

        try
        {
            string manufacturerName = SanitizeDisplayName(ReadHidWideString((buffer, size) => NativeMethods.HidD_GetManufacturerString(hidDeviceHandle, buffer, size)));
            string productName = SanitizeDisplayName(ReadHidWideString((buffer, size) => NativeMethods.HidD_GetProductString(hidDeviceHandle, buffer, size)));
            if (!string.IsNullOrWhiteSpace(productName))
            {
                if (!string.IsNullOrWhiteSpace(manufacturerName) && productName.IndexOf(manufacturerName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return manufacturerName + " " + productName;
                }
                return productName;
            }
            return manufacturerName;
        }
        finally
        {
            NativeMethods.CloseHandle(hidDeviceHandle);
        }
    }

    private static string ReadHidWideString(Func<byte[], int, bool> readFunc)
    {
        if (readFunc == null)
        {
            return string.Empty;
        }

        byte[] buffer = new byte[HidStringBufferLength];
        if (!readFunc(buffer, buffer.Length))
        {
            return string.Empty;
        }

        string decodedText = Encoding.Unicode.GetString(buffer);
        int terminatorIndex = decodedText.IndexOf('\0');
        if (terminatorIndex >= 0)
        {
            decodedText = decodedText.Substring(0, terminatorIndex);
        }
        return decodedText.Trim();
    }

    private static SetupApiMouseInterfaceMetadata ReadSetupApiMetadata(string normalizedTargetPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedTargetPath))
        {
            return SetupApiMouseInterfaceMetadata.Empty;
        }

        Guid classGuid = NativeMethods.GuidDevInterfaceMouse;
        nint deviceInfoSet = NativeMethods.SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == NativeMethods.InvalidDeviceInfoSet)
        {
            return SetupApiMouseInterfaceMetadata.Empty;
        }

        try
        {
            uint interfaceIndex = 0u;
            while (true)
            {
                NativeMethods.SP_DEVICE_INTERFACE_DATA interfaceData = new NativeMethods.SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf<NativeMethods.SP_DEVICE_INTERFACE_DATA>()
                };
                if (!NativeMethods.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref classGuid, interfaceIndex, ref interfaceData))
                {
                    break;
                }

                NativeMethods.SP_DEVINFO_DATA deviceInfoData = new NativeMethods.SP_DEVINFO_DATA
                {
                    cbSize = Marshal.SizeOf<NativeMethods.SP_DEVINFO_DATA>()
                };
                uint requiredDetailSize = 0u;
                NativeMethods.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0u, ref requiredDetailSize, ref deviceInfoData);
                if (requiredDetailSize == 0)
                {
                    interfaceIndex++;
                    continue;
                }

                SetupApiMouseInterfaceMetadata metadata = TryReadSetupApiMetadataForInterface(deviceInfoSet, ref interfaceData, ref deviceInfoData, requiredDetailSize, normalizedTargetPath);
                if (!metadata.IsEmpty)
                {
                    return metadata;
                }

                interfaceIndex++;
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return SetupApiMouseInterfaceMetadata.Empty;
    }

    private static SetupApiMouseInterfaceMetadata TryReadSetupApiMetadataForInterface(nint deviceInfoSet, ref NativeMethods.SP_DEVICE_INTERFACE_DATA interfaceData, ref NativeMethods.SP_DEVINFO_DATA deviceInfoData, uint requiredDetailSize, string normalizedTargetPath)
    {
        if (requiredDetailSize > int.MaxValue)
        {
            return SetupApiMouseInterfaceMetadata.Empty;
        }

        nint detailBuffer = Marshal.AllocHGlobal((int)requiredDetailSize);
        try
        {
            int detailDataSize = IntPtr.Size == 8 ? DeviceInterfaceDetailDataSizeX64 : DeviceInterfaceDetailDataSizeX86;
            Marshal.WriteInt32(detailBuffer, detailDataSize);
            deviceInfoData.cbSize = Marshal.SizeOf<NativeMethods.SP_DEVINFO_DATA>();
            uint actualDetailSize = 0u;
            if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailBuffer, requiredDetailSize, ref actualDetailSize, ref deviceInfoData))
            {
                return SetupApiMouseInterfaceMetadata.Empty;
            }

            string interfacePath = NormalizeDevicePath(Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, DeviceInterfaceDetailDataOffset)));
            if (!string.Equals(interfacePath, normalizedTargetPath, StringComparison.OrdinalIgnoreCase))
            {
                return SetupApiMouseInterfaceMetadata.Empty;
            }

            string friendlyName = ReadRegistryProperty(deviceInfoSet, deviceInfoData, NativeMethods.SPDRP_FRIENDLYNAME);
            if (string.IsNullOrWhiteSpace(friendlyName))
            {
                friendlyName = ReadRegistryProperty(deviceInfoSet, deviceInfoData, NativeMethods.SPDRP_DEVICEDESC);
            }

            return new SetupApiMouseInterfaceMetadata(friendlyName, ReadContainerIdProperty(deviceInfoSet, deviceInfoData));
        }
        finally
        {
            Marshal.FreeHGlobal(detailBuffer);
        }
    }

    private static string ReadContainerIdProperty(nint infoSet, NativeMethods.SP_DEVINFO_DATA deviceInfoData)
    {
        NativeMethods.DEVPROPKEY propertyKey = DeviceContainerIdPropertyKey;
        uint propertyType = 0u;
        uint requiredSize = 0u;
        NativeMethods.SetupDiGetDeviceProperty(infoSet, ref deviceInfoData, ref propertyKey, ref propertyType, IntPtr.Zero, 0u, ref requiredSize, 0u);
        if (requiredSize == 0 || requiredSize > int.MaxValue)
        {
            return string.Empty;
        }

        nint propertyBuffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            if (!NativeMethods.SetupDiGetDeviceProperty(infoSet, ref deviceInfoData, ref propertyKey, ref propertyType, propertyBuffer, requiredSize, ref requiredSize, 0u)
                || propertyType != DevPropertyTypeGuid
                || requiredSize != Marshal.SizeOf<Guid>())
            {
                return string.Empty;
            }

            Guid containerId = Marshal.PtrToStructure<Guid>(propertyBuffer);
            return containerId == Guid.Empty ? string.Empty : containerId.ToString("D");
        }
        finally
        {
            Marshal.FreeHGlobal(propertyBuffer);
        }
    }

    private static string ReadRegistryProperty(nint infoSet, NativeMethods.SP_DEVINFO_DATA deviceInfoData, uint propertyId)
    {
        uint requiredSize = 0u;
        NativeMethods.SetupDiGetDeviceRegistryProperty(infoSet, ref deviceInfoData, propertyId, IntPtr.Zero, IntPtr.Zero, 0u, ref requiredSize);
        if (requiredSize == 0 || requiredSize > int.MaxValue)
        {
            return string.Empty;
        }

        nint propertyBuffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            uint actualSize = 0u;
            if (!NativeMethods.SetupDiGetDeviceRegistryProperty(infoSet, ref deviceInfoData, propertyId, IntPtr.Zero, propertyBuffer, requiredSize, ref actualSize))
            {
                return string.Empty;
            }

            string propertyValue = Marshal.PtrToStringUni(propertyBuffer);
            return propertyValue == null ? string.Empty : propertyValue.TrimEnd('\0').Trim();
        }
        finally
        {
            Marshal.FreeHGlobal(propertyBuffer);
        }
    }

    private static string SanitizeDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        StringBuilder sanitized = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (!char.IsControl(ch))
            {
                sanitized.Append(ch);
            }
        }

        string displayName = CollapseWhitespace(sanitized.ToString().Trim().Replace("_", " "));
        if (string.IsNullOrWhiteSpace(displayName) || GenericLabels.Contains(displayName))
        {
            return string.Empty;
        }
        return displayName;
    }

    private static string CollapseWhitespace(string value)
    {
        StringBuilder collapsed = new StringBuilder(value.Length);
        bool previousWasWhitespace = false;
        foreach (char ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasWhitespace)
                {
                    collapsed.Append(' ');
                    previousWasWhitespace = true;
                }
            }
            else
            {
                collapsed.Append(ch);
                previousWasWhitespace = false;
            }
        }
        return collapsed.ToString().Trim();
    }

    private static string NormalizeDevicePath(string value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        string normalizedPath = value.Trim().TrimEnd('\0').Replace("/", "\\");
        if (normalizedPath.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = "\\\\?\\" + normalizedPath.Substring(4);
        }
        return normalizedPath;
    }

    private readonly struct SetupApiMouseInterfaceMetadata
    {
        public static SetupApiMouseInterfaceMetadata Empty => new SetupApiMouseInterfaceMetadata(string.Empty, string.Empty);

        public string DisplayName { get; }

        public string ContainerId { get; }

        public bool IsEmpty => string.IsNullOrWhiteSpace(DisplayName) && string.IsNullOrWhiteSpace(ContainerId);

        public SetupApiMouseInterfaceMetadata(string displayName, string containerId)
        {
            DisplayName = displayName ?? string.Empty;
            ContainerId = containerId ?? string.Empty;
        }
    }
}
