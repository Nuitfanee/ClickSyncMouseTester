using ClickSyncMouseTester.Models;
using System;
using System.Text.RegularExpressions;

namespace ClickSyncMouseTester.Services;

internal sealed class RawMousePhysicalEndpointGrouper
{
    private static readonly Regex MouseInterfaceSuffixRegex = new Regex(@"#\{378DE44C-56EF-11D1-BC8C-00A0C91405DD\}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex EndpointPathTokenRegex = new Regex(@"(?<separator>[#&])(?<token>MI_[0-9A-F]{2}|COL[0-9A-F]{2})(?=([#&\\]|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HidCollectionInstanceSuffixRegex = new Regex(@"&[0-9A-F]{4}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RepeatedPathSeparatorRegex = new Regex(@"([#&]){2,}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public string CreateTrustedGroupKey(RawMouseDeviceInfo device)
    {
        if (device == null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(device.PhysicalDeviceKey))
        {
            return device.PhysicalDeviceKey;
        }

        return CreateNormalizedEndpointPathKey(device.DeviceId);
    }

    public string CreateDuplicateFallbackKey(RawMouseDeviceInfo device)
    {
        if (device == null)
        {
            return string.Empty;
        }

        int vendorId = device.VendorId ?? -1;
        int productId = device.ProductId ?? -1;
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}|{1:X4}|{2:X4}", device.DisplayName ?? string.Empty, vendorId, productId);
    }

    public bool HaveHighlySimilarNormalizedPaths(RawMouseDeviceInfo left, RawMouseDeviceInfo right)
    {
        if (left == null || right == null)
        {
            return false;
        }

        string leftPathKey = CreateNormalizedEndpointPathKey(left.DeviceId);
        string rightPathKey = CreateNormalizedEndpointPathKey(right.DeviceId);
        return !string.IsNullOrWhiteSpace(leftPathKey)
            && string.Equals(leftPathKey, rightPathKey, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateNormalizedEndpointPathKey(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return string.Empty;
        }

        string normalizedPath = NormalizeDevicePath(deviceId);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return string.Empty;
        }

        normalizedPath = MouseInterfaceSuffixRegex.Replace(normalizedPath, string.Empty);
        normalizedPath = EndpointPathTokenRegex.Replace(normalizedPath, match => match.Groups["separator"].Value);
        normalizedPath = HidCollectionInstanceSuffixRegex.Replace(normalizedPath, string.Empty);
        normalizedPath = RepeatedPathSeparatorRegex.Replace(normalizedPath, match => match.Value[0].ToString());
        normalizedPath = normalizedPath.TrimEnd('#', '&', '\\');
        return normalizedPath;
    }

    private static string NormalizeDevicePath(string deviceId)
    {
        return deviceId
            .Trim()
            .TrimEnd('\0')
            .Replace("/", "\\")
            .ToUpperInvariant();
    }
}
