using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClickSyncMouseTester.Services;

internal sealed class RawMouseEndpointClassifier
{
    private readonly RawMousePhysicalEndpointGrouper _endpointGrouper;

    public RawMouseEndpointClassifier(RawMousePhysicalEndpointGrouper endpointGrouper)
    {
        _endpointGrouper = endpointGrouper ?? throw new ArgumentNullException(nameof(endpointGrouper));
    }

    public void ClassifyNonMotionSiblings(IList<RawMouseDeviceCatalogEntry> entries)
    {
        if (entries == null || entries.Count == 0)
        {
            return;
        }

        ApplyTrustedPhysicalGrouping(entries);
        ApplyDuplicateFallbackGrouping(entries);
    }

    private void ApplyTrustedPhysicalGrouping(IList<RawMouseDeviceCatalogEntry> entries)
    {
        ApplyGrouping(
            entries,
            entry => _endpointGrouper.CreateTrustedGroupKey(entry.SourceDevice),
            canHideEntry: entry => IsNonMotionSibling(entry, hideSilentEndpoints: true));
    }

    private void ApplyDuplicateFallbackGrouping(IList<RawMouseDeviceCatalogEntry> entries)
    {
        ApplyGrouping(
            entries,
            entry => _endpointGrouper.CreateDuplicateFallbackKey(entry.SourceDevice),
            canHideEntry: entry => IsNonMotionSibling(entry, hideSilentEndpoints: false) && HasHighlySimilarMotionSibling(entry, entries));
    }

    private void ApplyGrouping(IList<RawMouseDeviceCatalogEntry> entries, Func<RawMouseDeviceCatalogEntry, string> groupKeyFactory, Func<RawMouseDeviceCatalogEntry, bool> canHideEntry)
    {
        List<IGrouping<string, RawMouseDeviceCatalogEntry>> groups = entries
            .Where(entry => entry?.Device != null && !entry.Device.IsVirtual)
            .GroupBy(groupKeyFactory, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key)
                && group.Count() > 1
                && group.Any(entry => entry.Device.EndpointKind == RawMouseEndpointKind.MotionCapable))
            .ToList();

        foreach (IGrouping<string, RawMouseDeviceCatalogEntry> group in groups)
        {
            foreach (RawMouseDeviceCatalogEntry entry in group)
            {
                if (!canHideEntry(entry))
                {
                    continue;
                }

                entry.ForcedEndpointKind = ResolveNonMotionSiblingKind(entry.Activity);
            }
        }
    }

    private bool HasHighlySimilarMotionSibling(RawMouseDeviceCatalogEntry entry, IEnumerable<RawMouseDeviceCatalogEntry> entries)
    {
        return entries.Any(candidate => !ReferenceEquals(candidate, entry)
            && candidate?.Device != null
            && candidate.Device.EndpointKind == RawMouseEndpointKind.MotionCapable
            && string.Equals(_endpointGrouper.CreateDuplicateFallbackKey(candidate.SourceDevice), _endpointGrouper.CreateDuplicateFallbackKey(entry.SourceDevice), StringComparison.OrdinalIgnoreCase)
            && _endpointGrouper.HaveHighlySimilarNormalizedPaths(candidate.SourceDevice, entry.SourceDevice));
    }

    private static bool IsNonMotionSibling(RawMouseDeviceCatalogEntry entry, bool hideSilentEndpoints)
    {
        if (entry == null || entry.Device == null)
        {
            return false;
        }

        if (entry.Device.EndpointKind != RawMouseEndpointKind.Unknown && entry.Device.EndpointKind != RawMouseEndpointKind.Inactive)
        {
            return false;
        }

        if (entry.Activity == null)
        {
            return hideSilentEndpoints;
        }

        if (entry.Activity.MotionPacketCount > 0)
        {
            return false;
        }

        return hideSilentEndpoints || entry.Activity.TotalPacketCount > 0;
    }

    private static RawMouseEndpointKind ResolveNonMotionSiblingKind(RawMouseEndpointActivitySnapshot activitySnapshot)
    {
        return activitySnapshot == null || activitySnapshot.TotalPacketCount <= 0
            ? RawMouseEndpointKind.Inactive
            : RawMouseEndpointKind.ControlOnly;
    }
}
