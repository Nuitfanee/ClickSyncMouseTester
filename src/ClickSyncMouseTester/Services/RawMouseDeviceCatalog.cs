using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClickSyncMouseTester.Services;

internal sealed class RawMouseDeviceCatalog
{
    private static readonly Regex EndpointTokenRegex = new Regex(@"(?:^|[#\\&])(?<token>MI_[0-9A-F]{2}|COL[0-9A-F]{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly RawMouseEndpointClassifier _endpointClassifier;
    private readonly RawMouseDeviceLabeler _deviceLabeler;

    public RawMouseDeviceCatalog()
    {
        RawMousePhysicalEndpointGrouper endpointGrouper = new RawMousePhysicalEndpointGrouper();
        _endpointClassifier = new RawMouseEndpointClassifier(endpointGrouper);
        _deviceLabeler = new RawMouseDeviceLabeler(endpointGrouper);
    }

    public IReadOnlyList<RawMouseDeviceInfo> CreateSelectableDevices(IEnumerable<RawMouseDeviceInfo> devices, IReadOnlyDictionary<string, RawMouseEndpointActivitySnapshot> activitySnapshots)
    {
        if (devices == null)
        {
            return Array.Empty<RawMouseDeviceInfo>();
        }

        List<RawMouseDeviceCatalogEntry> catalogEntries = CreateCatalogEntries(devices, activitySnapshots);
        _endpointClassifier.ClassifyNonMotionSiblings(catalogEntries);

        List<RawMouseDeviceInfo> enrichedDevices = catalogEntries
            .Select(entry => CreateEnrichedDevice(entry.SourceDevice, entry.Activity, entry.ForcedEndpointKind))
            .ToList();

        _deviceLabeler.ApplyDuplicateLabels(enrichedDevices);

        bool hasMotionCapableDevice = enrichedDevices.Any(device => device != null && device.IsVisibleByDefault && device.EndpointKind == RawMouseEndpointKind.MotionCapable);
        return enrichedDevices
            .Where(device => IsSelectableByDefault(device, hasMotionCapableDevice))
            .OrderBy(device => IsGenericHidMouseName(device) ? 1 : 0)
            .ThenBy(ResolveSelectionPriority)
            .ThenBy(device => device.SelectionDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool ContainsSelectableDevice(IEnumerable<RawMouseDeviceInfo> devices, IReadOnlyDictionary<string, RawMouseEndpointActivitySnapshot> activitySnapshots, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        IReadOnlyList<RawMouseDeviceInfo> selectableDevices = CreateSelectableDevices(devices, activitySnapshots);
        return selectableDevices.Any(device => string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSelectableByDefault(RawMouseDeviceInfo device, bool hasMotionCapableDevice)
    {
        if (device == null || !device.IsVisibleByDefault)
        {
            return false;
        }

        return !hasMotionCapableDevice || device.EndpointKind == RawMouseEndpointKind.MotionCapable;
    }

    private static List<RawMouseDeviceCatalogEntry> CreateCatalogEntries(IEnumerable<RawMouseDeviceInfo> devices, IReadOnlyDictionary<string, RawMouseEndpointActivitySnapshot> activitySnapshots)
    {
        List<RawMouseDeviceCatalogEntry> catalogEntries = new List<RawMouseDeviceCatalogEntry>();
        foreach (RawMouseDeviceInfo device in devices)
        {
            if (device == null)
            {
                continue;
            }

            RawMouseEndpointActivitySnapshot activitySnapshot = null;
            if (!string.IsNullOrWhiteSpace(device.DeviceId) && activitySnapshots != null)
            {
                activitySnapshots.TryGetValue(device.DeviceId, out activitySnapshot);
            }

            catalogEntries.Add(new RawMouseDeviceCatalogEntry(device, activitySnapshot, CreateEnrichedDevice(device, activitySnapshot, null)));
        }

        return catalogEntries;
    }

    private static RawMouseDeviceInfo CreateEnrichedDevice(RawMouseDeviceInfo device, RawMouseEndpointActivitySnapshot activitySnapshot, RawMouseEndpointKind? forcedEndpointKind)
    {
        RawMouseEndpointKind endpointKind = forcedEndpointKind ?? ResolveEndpointKind(device, activitySnapshot);
        string endpointToken = ResolveEndpointToken(device.DeviceId);
        bool isVisibleByDefault = ResolveVisibility(device, endpointKind);
        bool isRecommended = endpointKind == RawMouseEndpointKind.MotionCapable;
        return device.WithEndpointMetadata(endpointKind, isVisibleByDefault, isRecommended, endpointToken);
    }

    private static RawMouseEndpointKind ResolveEndpointKind(RawMouseDeviceInfo device, RawMouseEndpointActivitySnapshot activitySnapshot)
    {
        if (device == null || device.IsVirtual)
        {
            return RawMouseEndpointKind.Virtual;
        }

        if (activitySnapshot?.EndpointKind == RawMouseEndpointKind.MotionCapable)
        {
            return RawMouseEndpointKind.MotionCapable;
        }

        return RawMouseEndpointKind.Unknown;
    }

    private static bool ResolveVisibility(RawMouseDeviceInfo device, RawMouseEndpointKind endpointKind)
    {
        if (device == null || device.IsVirtual)
        {
            return false;
        }

        if (IsKeyboardNamedDevice(device))
        {
            return false;
        }

        if (endpointKind == RawMouseEndpointKind.MotionCapable)
        {
            return true;
        }

        return endpointKind == RawMouseEndpointKind.Unknown && IsLikelyMouseCandidate(device);
    }

    private static int ResolveSelectionPriority(RawMouseDeviceInfo device)
    {
        if (device != null && device.EndpointKind == RawMouseEndpointKind.MotionCapable)
        {
            return 0;
        }

        return 1;
    }

    private static bool IsLikelyMouseCandidate(RawMouseDeviceInfo device)
    {
        return device != null && device.ButtonCount > 0;
    }

    private static bool IsKeyboardNamedDevice(RawMouseDeviceInfo device)
    {
        if (device == null)
        {
            return false;
        }

        return IsKeyboardNameText(device.SelectionDisplayName) || IsKeyboardNameText(device.DisplayName);
    }

    private static bool IsKeyboardNameText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalizedValue = value.Trim();
        return normalizedValue.IndexOf("Keyboard", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsGenericHidMouseName(RawMouseDeviceInfo device)
    {
        if (device == null)
        {
            return false;
        }

        return IsGenericHidMouseText(device.SelectionDisplayName) || IsGenericHidMouseText(device.DisplayName);
    }

    private static bool IsGenericHidMouseText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.TrimStart().StartsWith("HID Mouse", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveEndpointToken(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return string.Empty;
        }

        MatchCollection matches = EndpointTokenRegex.Matches(deviceId);
        if (matches.Count == 0)
        {
            return string.Empty;
        }

        List<string> tokens = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            string token = match.Groups["token"].Value;
            if (!string.IsNullOrWhiteSpace(token))
            {
                tokens.Add(token.ToUpperInvariant());
            }
        }

        return string.Join(" / ", tokens.Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
