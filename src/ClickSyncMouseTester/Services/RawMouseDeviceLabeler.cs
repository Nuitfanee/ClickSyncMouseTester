using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClickSyncMouseTester.Services;

internal sealed class RawMouseDeviceLabeler
{
    private readonly RawMousePhysicalEndpointGrouper _endpointGrouper;

    public RawMouseDeviceLabeler(RawMousePhysicalEndpointGrouper endpointGrouper)
    {
        _endpointGrouper = endpointGrouper ?? throw new ArgumentNullException(nameof(endpointGrouper));
    }

    public void ApplyDuplicateLabels(IList<RawMouseDeviceInfo> devices)
    {
        if (devices == null || devices.Count == 0)
        {
            return;
        }

        Dictionary<string, int> visibleCountsByDuplicateKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (RawMouseDeviceInfo device in devices)
        {
            if (device == null || device.IsVirtual || !device.IsVisibleByDefault)
            {
                continue;
            }

            string duplicateKey = _endpointGrouper.CreateDuplicateFallbackKey(device);
            visibleCountsByDuplicateKey.TryGetValue(duplicateKey, out int count);
            visibleCountsByDuplicateKey[duplicateKey] = count + 1;
        }

        Dictionary<string, int> duplicateIndicesByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < devices.Count; index++)
        {
            RawMouseDeviceInfo device = devices[index];
            if (device == null || device.IsVirtual || !device.IsVisibleByDefault)
            {
                continue;
            }

            string duplicateKey = _endpointGrouper.CreateDuplicateFallbackKey(device);
            if (!visibleCountsByDuplicateKey.TryGetValue(duplicateKey, out int visibleCount) || visibleCount <= 1)
            {
                continue;
            }

            duplicateIndicesByKey.TryGetValue(duplicateKey, out int duplicateIndex);
            duplicateIndex++;
            duplicateIndicesByKey[duplicateKey] = duplicateIndex;

            string duplicateLabel = string.Format(CultureInfo.InvariantCulture, "{0} #{1}", device.DisplayName, duplicateIndex);
            devices[index] = device.WithSelectionDisplayName(duplicateLabel);
        }
    }
}
