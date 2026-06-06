using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ClickSyncMouseTester.Services;

internal sealed class MousePerformanceSessionAnalysisIndex
{
    private const int CancellationCheckMask = 1023;

    private readonly IReadOnlyList<MousePerformanceEvent> _events;
    private readonly long[] _rawRelativeTicks;
    private readonly long[] _logicalTicks;
    private readonly int[] _deltaX;
    private readonly int[] _deltaY;
    private readonly long[] _cumulativeX;
    private readonly long[] _cumulativeY;
    private readonly double[] _pathPrefixCounts;
    private readonly int[] _segmentIds;
    private readonly object _segmentBoundarySyncRoot;
    private int[] _segmentStarts;
    private int[] _segmentEnds;

    public IReadOnlyList<MousePerformanceEvent> Events => _events;

    public int Count => _events.Count;

    public MousePerformanceSessionAnalysisIndex(IReadOnlyList<MousePerformanceEvent> events, CancellationToken cancellationToken = default)
    {
        _events = events ?? Array.Empty<MousePerformanceEvent>();
        int count = _events.Count;
        _rawRelativeTicks = new long[count];
        _logicalTicks = new long[count];
        _deltaX = new int[count];
        _deltaY = new int[count];
        _cumulativeX = new long[count];
        _cumulativeY = new long[count];
        _pathPrefixCounts = new double[count + 1];
        _segmentIds = new int[count];
        _segmentBoundarySyncRoot = new object();

        for (int eventIndex = 0; eventIndex < count; eventIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken, eventIndex);
            MousePerformanceEvent mouseEvent = _events[eventIndex];
            if (mouseEvent == null)
            {
                continue;
            }
            _rawRelativeTicks[eventIndex] = mouseEvent.RawRelativeTicks;
            _logicalTicks[eventIndex] = mouseEvent.LogicalTicks;
            _deltaX[eventIndex] = mouseEvent.DeltaX;
            _deltaY[eventIndex] = mouseEvent.DeltaY;
            _cumulativeX[eventIndex] = mouseEvent.SessionCumulativeX;
            _cumulativeY[eventIndex] = mouseEvent.SessionCumulativeY;
            _pathPrefixCounts[eventIndex + 1] = _pathPrefixCounts[eventIndex] + Math.Sqrt((double)mouseEvent.DeltaX * mouseEvent.DeltaX + (double)mouseEvent.DeltaY * mouseEvent.DeltaY);
            _segmentIds[eventIndex] = mouseEvent.SessionSegmentId;
        }
    }

    public MousePerformanceEvent GetEvent(int index)
    {
        return index >= 0 && index < _events.Count ? _events[index] : null;
    }

    public long GetTimeTicks(int index, MousePerformanceTimeBasis timeBasis)
    {
        if (index < 0 || index >= Count)
        {
            return 0L;
        }
        return timeBasis == MousePerformanceTimeBasis.RawCapture ? _rawRelativeTicks[index] : _logicalTicks[index];
    }

    public double GetTimeMs(int index, MousePerformanceTimeBasis timeBasis)
    {
        return TicksToMilliseconds(GetTimeTicks(index, timeBasis));
    }

    public int GetDeltaX(int index)
    {
        return index >= 0 && index < Count ? _deltaX[index] : 0;
    }

    public int GetDeltaY(int index)
    {
        return index >= 0 && index < Count ? _deltaY[index] : 0;
    }

    public long GetCumulativeX(int index)
    {
        return index >= 0 && index < Count ? _cumulativeX[index] : 0L;
    }

    public long GetCumulativeY(int index)
    {
        return index >= 0 && index < Count ? _cumulativeY[index] : 0L;
    }

    public double GetPathCountsBetween(int startIndex, int endIndex)
    {
        if (Count == 0)
        {
            return 0.0;
        }
        int clampedStartIndex = Math.Max(0, Math.Min(startIndex, Count - 1));
        int clampedEndIndex = Math.Max(clampedStartIndex, Math.Min(endIndex, Count - 1));
        return Math.Max(0.0, _pathPrefixCounts[clampedEndIndex + 1] - _pathPrefixCounts[clampedStartIndex]);
    }

    public int GetSegmentId(int index)
    {
        return index >= 0 && index < Count ? _segmentIds[index] : 0;
    }

    public int GetSegmentStart(int index)
    {
        EnsureSegmentBoundaryMaps();
        return index >= 0 && index < Count ? _segmentStarts[index] : 0;
    }

    public int GetSegmentEnd(int index)
    {
        EnsureSegmentBoundaryMaps();
        return index >= 0 && index < Count ? _segmentEnds[index] : Math.Max(0, Count - 1);
    }

    public bool TryGetIntervalMs(int previousIndex, int currentIndex, MousePerformanceTimeBasis timeBasis, ref double intervalMs)
    {
        intervalMs = 0.0;
        if (previousIndex < 0 || currentIndex < 0 || previousIndex >= Count || currentIndex >= Count)
        {
            return false;
        }
        if (timeBasis == MousePerformanceTimeBasis.RawCapture && _segmentIds[previousIndex] != _segmentIds[currentIndex])
        {
            return false;
        }
        long intervalTicks = GetTimeTicks(currentIndex, timeBasis) - GetTimeTicks(previousIndex, timeBasis);
        if (intervalTicks <= 0L)
        {
            return false;
        }
        intervalMs = TicksToMilliseconds(intervalTicks);
        return intervalMs > 0.0;
    }

    public int ClampStartIndex(int startIndex)
    {
        if (Count == 0)
        {
            return 0;
        }
        return Math.Max(0, Math.Min(startIndex, Count - 1));
    }

    public int ClampEndIndex(int startIndex, int endIndex)
    {
        if (Count == 0)
        {
            return 0;
        }
        int clampedStartIndex = ClampStartIndex(startIndex);
        return Math.Max(clampedStartIndex, Math.Min(endIndex, Count - 1));
    }

    public static double TicksToMilliseconds(long ticks)
    {
        return (double)ticks * 1000.0 / Stopwatch.Frequency;
    }

    private void EnsureSegmentBoundaryMaps()
    {
        if (_segmentStarts != null && _segmentEnds != null)
        {
            return;
        }

        object segmentBoundarySyncRoot = _segmentBoundarySyncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(segmentBoundarySyncRoot, ref lockTaken);
            if (_segmentStarts == null || _segmentEnds == null)
            {
                BuildSegmentBoundaryMaps();
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(segmentBoundarySyncRoot);
            }
        }
    }

    private void BuildSegmentBoundaryMaps()
    {
        int[] segmentStarts = new int[Count];
        int[] segmentEnds = new int[Count];
        int segmentStartIndex = 0;
        while (segmentStartIndex < Count)
        {
            int segmentId = _segmentIds[segmentStartIndex];
            int segmentEndIndex = segmentStartIndex;
            while (segmentEndIndex + 1 < Count && _segmentIds[segmentEndIndex + 1] == segmentId)
            {
                segmentEndIndex++;
            }
            for (int eventIndex = segmentStartIndex; eventIndex <= segmentEndIndex; eventIndex++)
            {
                segmentStarts[eventIndex] = segmentStartIndex;
                segmentEnds[eventIndex] = segmentEndIndex;
            }
            segmentStartIndex = segmentEndIndex + 1;
        }
        _segmentStarts = segmentStarts;
        _segmentEnds = segmentEnds;
    }

    private static void ThrowIfCancellationRequested(CancellationToken cancellationToken, int iteration)
    {
        if (cancellationToken.CanBeCanceled && (Math.Max(0, iteration) & CancellationCheckMask) == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}

