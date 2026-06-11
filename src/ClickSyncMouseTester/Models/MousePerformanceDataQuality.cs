using System;

namespace ClickSyncMouseTester.Models;

public class MousePerformanceDataQuality
{
    private readonly int _droppedPacketCount;

    private readonly int _controlReportCount;

    private readonly int _wheelOnlyReportCount;

    private readonly int _emptyReportCount;

    private readonly int _zeroMotionReportCount;

    private readonly int _outOfOrderTimestampCount;

    private readonly int _zeroIntervalCount;

    private readonly int _queueOverflowCount;

    private readonly int _queueHighWatermarkCount;

    private readonly int _queueCapacity;

    private readonly int _sequenceGapCount;

    private readonly int _reorderedSequenceCount;

    private readonly int _sameTickCount;

    private readonly bool _isStrictTimingFaithful;

    private readonly MousePerformanceDataQualityLevel _qualityLevel;

    public int DroppedPacketCount => _droppedPacketCount;

    public int ControlReportCount => _controlReportCount;

    public int WheelOnlyReportCount => _wheelOnlyReportCount;

    public int EmptyReportCount => _emptyReportCount;

    public int ZeroMotionReportCount => _zeroMotionReportCount;

    public int OutOfOrderTimestampCount => _outOfOrderTimestampCount;

    public int ZeroIntervalCount => _zeroIntervalCount;

    public int QueueOverflowCount => _queueOverflowCount;

    public int QueueHighWatermarkCount => _queueHighWatermarkCount;

    public int QueueCapacity => _queueCapacity;

    public int SequenceGapCount => _sequenceGapCount;

    public int ReorderedSequenceCount => _reorderedSequenceCount;

    public int SameTickCount => _sameTickCount;

    public bool IsStrictTimingFaithful => _isStrictTimingFaithful;

    public MousePerformanceDataQualityLevel QualityLevel => _qualityLevel;

    public MousePerformanceDataQuality(int droppedPacketCount, int controlReportCount, int wheelOnlyReportCount, int emptyReportCount, int zeroMotionReportCount, int outOfOrderTimestampCount, int zeroIntervalCount, int queueOverflowCount, int queueHighWatermarkCount, int queueCapacity, int sequenceGapCount, int reorderedSequenceCount, int sameTickCount, bool isStrictTimingFaithful, MousePerformanceDataQualityLevel qualityLevel)
    {
        _droppedPacketCount = Math.Max(0, droppedPacketCount);
        _controlReportCount = Math.Max(0, controlReportCount);
        _wheelOnlyReportCount = Math.Max(0, wheelOnlyReportCount);
        _emptyReportCount = Math.Max(0, emptyReportCount);
        _zeroMotionReportCount = Math.Max(0, zeroMotionReportCount);
        _outOfOrderTimestampCount = Math.Max(0, outOfOrderTimestampCount);
        _zeroIntervalCount = Math.Max(0, zeroIntervalCount);
        _queueOverflowCount = Math.Max(0, queueOverflowCount);
        _queueHighWatermarkCount = Math.Max(0, queueHighWatermarkCount);
        _queueCapacity = Math.Max(0, queueCapacity);
        _sequenceGapCount = Math.Max(0, sequenceGapCount);
        _reorderedSequenceCount = Math.Max(0, reorderedSequenceCount);
        _sameTickCount = Math.Max(0, sameTickCount);
        _isStrictTimingFaithful = isStrictTimingFaithful;
        _qualityLevel = qualityLevel;
    }
}
