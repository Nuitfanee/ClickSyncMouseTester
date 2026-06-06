using System;

namespace ClickSyncMouseTester.Models;

public class MousePerformanceSessionSegment
{
    private readonly int _segmentId;

    private readonly long _startedAtRawCaptureTicks;

    public int SegmentId => _segmentId;

    public long StartedAtRawCaptureTicks => _startedAtRawCaptureTicks;

    public MousePerformanceSessionSegment(int segmentId, long startedAtRawCaptureTicks)
    {
        _segmentId = Math.Max(0, segmentId);
        _startedAtRawCaptureTicks = Math.Max(0L, startedAtRawCaptureTicks);
    }
}





