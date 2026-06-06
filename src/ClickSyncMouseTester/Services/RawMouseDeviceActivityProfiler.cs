using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ClickSyncMouseTester.Services;

internal sealed class RawMouseDeviceActivityProfiler
{
    private readonly object _syncRoot;
    private readonly Dictionary<string, EndpointActivity> _activitiesByDeviceId;

    public RawMouseDeviceActivityProfiler()
    {
        _syncRoot = new object();
        _activitiesByDeviceId = new Dictionary<string, EndpointActivity>(StringComparer.OrdinalIgnoreCase);
    }

    public EndpointActivity GetOrCreateActivity(string deviceId)
    {
        string key = deviceId ?? string.Empty;
        lock (_syncRoot)
        {
            if (!_activitiesByDeviceId.TryGetValue(key, out EndpointActivity activity))
            {
                activity = new EndpointActivity();
                _activitiesByDeviceId[key] = activity;
            }

            return activity;
        }
    }

    public IReadOnlyDictionary<string, RawMouseEndpointActivitySnapshot> CreateSnapshot()
    {
        lock (_syncRoot)
        {
            Dictionary<string, RawMouseEndpointActivitySnapshot> snapshots = new Dictionary<string, RawMouseEndpointActivitySnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, EndpointActivity> activity in _activitiesByDeviceId)
            {
                snapshots[activity.Key] = activity.Value.CreateSnapshot();
            }
            return snapshots;
        }
    }

    public sealed class EndpointActivity
    {
        private long _totalPacketCount;
        private long _motionPacketCount;
        private long _emptyPacketCount;
        private long _buttonPacketCount;
        private long _wheelPacketCount;
        private long _firstPacketTimestampMicroseconds = -1L;
        private long _lastPacketTimestampMicroseconds = -1L;
        private long _lastMotionTimestampMicroseconds = -1L;
        private int _endpointKind = (int)RawMouseEndpointKind.Inactive;

        public bool RecordPacket(double timestampMs, int deltaX, int deltaY, ushort buttonFlags)
        {
            if (!IsFinite(timestampMs))
            {
                return false;
            }

            long timestampMicroseconds = ToTimestampMicroseconds(timestampMs);
            RawMouseEndpointKind previousEndpointKind = (RawMouseEndpointKind)Volatile.Read(ref _endpointKind);
            Interlocked.CompareExchange(ref _firstPacketTimestampMicroseconds, timestampMicroseconds, -1L);
            Volatile.Write(ref _lastPacketTimestampMicroseconds, timestampMicroseconds);
            Interlocked.Increment(ref _totalPacketCount);

            bool hasMotion = deltaX != 0 || deltaY != 0;
            bool hasButtons = buttonFlags != 0;
            bool hasWheel = hasButtons
                && (buttonFlags & NativeMethods.RI_MOUSE_WHEEL) == NativeMethods.RI_MOUSE_WHEEL;

            if (hasMotion)
            {
                Interlocked.Increment(ref _motionPacketCount);
                Volatile.Write(ref _lastMotionTimestampMicroseconds, timestampMicroseconds);
            }
            else
            {
                Interlocked.Increment(ref _emptyPacketCount);
            }

            if (hasButtons)
            {
                Interlocked.Increment(ref _buttonPacketCount);
            }

            if (hasWheel)
            {
                Interlocked.Increment(ref _wheelPacketCount);
            }

            RawMouseEndpointKind currentEndpointKind = ResolveEndpointKind(hasMotion);
            Volatile.Write(ref _endpointKind, (int)currentEndpointKind);
            return currentEndpointKind != previousEndpointKind;
        }

        public RawMouseEndpointActivitySnapshot CreateSnapshot()
        {
            long totalPacketCount = Volatile.Read(ref _totalPacketCount);
            long motionPacketCount = Volatile.Read(ref _motionPacketCount);
            long emptyPacketCount = Volatile.Read(ref _emptyPacketCount);
            long buttonPacketCount = Volatile.Read(ref _buttonPacketCount);
            long wheelPacketCount = Volatile.Read(ref _wheelPacketCount);
            RawMouseEndpointKind endpointKind = (RawMouseEndpointKind)Volatile.Read(ref _endpointKind);
            double firstPacketTimestampMs = FromTimestampMicroseconds(Volatile.Read(ref _firstPacketTimestampMicroseconds));
            double lastPacketTimestampMs = FromTimestampMicroseconds(Volatile.Read(ref _lastPacketTimestampMicroseconds));
            double lastMotionTimestampMs = FromTimestampMicroseconds(Volatile.Read(ref _lastMotionTimestampMicroseconds));
            return new RawMouseEndpointActivitySnapshot(
                endpointKind,
                totalPacketCount,
                motionPacketCount,
                emptyPacketCount,
                buttonPacketCount,
                wheelPacketCount,
                firstPacketTimestampMs,
                lastPacketTimestampMs,
                lastMotionTimestampMs);
        }

        private RawMouseEndpointKind ResolveEndpointKind(bool hasMotion)
        {
            if (hasMotion || Volatile.Read(ref _motionPacketCount) > 0)
            {
                return RawMouseEndpointKind.MotionCapable;
            }

            return RawMouseEndpointKind.Unknown;
        }
    }

    private static long ToTimestampMicroseconds(double timestampMs)
    {
        if (!IsFinite(timestampMs) || timestampMs < 0.0)
        {
            return -1L;
        }

        double timestampMicroseconds = timestampMs * 1000.0;
        if (timestampMicroseconds >= long.MaxValue)
        {
            return long.MaxValue;
        }

        return (long)Math.Round(timestampMicroseconds);
    }

    private static double FromTimestampMicroseconds(long timestampMicroseconds)
    {
        return timestampMicroseconds >= 0L
            ? timestampMicroseconds / 1000.0
            : double.NaN;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
