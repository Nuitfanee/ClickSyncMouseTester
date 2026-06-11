using System;
using System.Globalization;

namespace ClickSyncMouseTester.Models;

internal sealed class MousePerformanceSessionIdentityResolver
{
    public static string ResolveSessionInstanceIdentity(MousePerformanceSessionArchive session)
    {
        if (session == null)
        {
            return string.Empty;
        }

        MousePerformanceSessionMetadata metadata = session.Metadata;
        MousePerformanceSnapshot snapshot = session.Snapshot;
        long sessionStartTicks = 0L;
        long firstCaptureSequence = 0L;
        if (snapshot != null)
        {
            if (snapshot.SessionSegments != null && snapshot.SessionSegments.Count > 0)
            {
                sessionStartTicks = Math.Max(0L, snapshot.SessionSegments[0].StartedAtRawCaptureTicks);
            }
            if (snapshot.Events != null && snapshot.Events.Count > 0)
            {
                MousePerformanceEvent firstEvent = snapshot.Events[0];
                if (firstEvent != null)
                {
                    firstCaptureSequence = Math.Max(0L, firstEvent.CaptureSequence);
                    if (sessionStartTicks <= 0)
                    {
                        sessionStartTicks = Math.Max(0L, firstEvent.RawCaptureTicks);
                    }
                }
            }
        }

        return string.Join("|", new string[6]
        {
            metadata?.DeviceId ?? string.Empty,
            FormatNullableInt(metadata?.VendorId),
            FormatNullableInt(metadata?.ProductId),
            metadata?.PathSummary ?? string.Empty,
            sessionStartTicks.ToString(CultureInfo.InvariantCulture),
            firstCaptureSequence.ToString(CultureInfo.InvariantCulture)
        });
    }

    public static string ResolveSessionContentIdentity(MousePerformanceSessionArchive session)
    {
        if (session == null)
        {
            return string.Empty;
        }

        MousePerformanceSnapshot snapshot = session.Snapshot;
        int eventCount = 0;
        long lastRawCaptureTicks = 0L;
        long lastCaptureSequence = 0L;
        if (snapshot != null)
        {
            eventCount = Math.Max(0, snapshot.EventCount);
            if (snapshot.Events != null && snapshot.Events.Count > 0)
            {
                MousePerformanceEvent lastEvent = snapshot.Events[snapshot.Events.Count - 1];
                if (lastEvent != null)
                {
                    lastRawCaptureTicks = Math.Max(0L, lastEvent.RawCaptureTicks);
                    lastCaptureSequence = Math.Max(0L, lastEvent.CaptureSequence);
                }
            }
        }

        return string.Join("|", new string[4]
        {
            ResolveSessionInstanceIdentity(session),
            eventCount.ToString(CultureInfo.InvariantCulture),
            lastRawCaptureTicks.ToString(CultureInfo.InvariantCulture),
            lastCaptureSequence.ToString(CultureInfo.InvariantCulture)
        });
    }

    public static bool AreEquivalentSessionContent(MousePerformanceSessionArchive left, MousePerformanceSessionArchive right)
    {
        return string.Equals(ResolveSessionContentIdentity(left), ResolveSessionContentIdentity(right), StringComparison.Ordinal);
    }

    private static string FormatNullableInt(int? value)
    {
        if (!value.HasValue)
        {
            return string.Empty;
        }
        return value.Value.ToString(CultureInfo.InvariantCulture);
    }
}
