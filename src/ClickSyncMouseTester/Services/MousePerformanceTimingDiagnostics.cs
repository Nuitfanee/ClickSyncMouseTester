using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Services;

internal readonly struct MousePerformanceVelocityWindowSample
{
    public long StartTicks { get; }

    public long EndTicks { get; }

    public double DistanceCounts { get; }

    public MousePerformanceVelocityWindowSample(long startTicks, long endTicks, double distanceCounts)
    {
        StartTicks = Math.Max(0L, startTicks);
        EndTicks = Math.Max(StartTicks, endTicks);
        DistanceCounts = Math.Max(0.0, distanceCounts);
    }
}

internal sealed class MousePerformanceTimingDiagnostics
{
    private readonly Queue<MousePerformanceVelocityWindowSample> _recentVelocitySamples;

    private long _lastAcceptedRawTicks;
    private int _lastAcceptedSegmentId;
    private long _lastAcceptedTimingSequence;
    private bool _hasAcceptedRawTicks;
    private bool _hasAcceptedTimingSequence;
    private int _outOfOrderTimestampCount;
    private int _zeroIntervalCount;
    private int _sequenceGapCount;
    private int _reorderedSequenceCount;
    private int _sameTickCount;

    public int OutOfOrderTimestampCount => _outOfOrderTimestampCount;

    public int ZeroIntervalCount => _zeroIntervalCount;

    public int SequenceGapCount => _sequenceGapCount;

    public int ReorderedSequenceCount => _reorderedSequenceCount;

    public int SameTickCount => _sameTickCount;

    public MousePerformanceTimingDiagnostics()
    {
        _recentVelocitySamples = new Queue<MousePerformanceVelocityWindowSample>();
        Reset();
    }

    public void Reset()
    {
        _recentVelocitySamples.Clear();
        _lastAcceptedRawTicks = 0L;
        _lastAcceptedSegmentId = 0;
        _lastAcceptedTimingSequence = 0L;
        _hasAcceptedRawTicks = false;
        _hasAcceptedTimingSequence = false;
        _outOfOrderTimestampCount = 0;
        _zeroIntervalCount = 0;
        _sequenceGapCount = 0;
        _reorderedSequenceCount = 0;
        _sameTickCount = 0;
    }

    public void RecordReport(int currentSegmentId, long timingSequence, long rawCaptureTicks, int relativeDeltaX, int relativeDeltaY, double velocityWindowMs)
    {
        if (_hasAcceptedRawTicks)
        {
            RecordIntervalDiagnostics(currentSegmentId, timingSequence, rawCaptureTicks, relativeDeltaX, relativeDeltaY, velocityWindowMs);
        }

        _lastAcceptedRawTicks = rawCaptureTicks;
        _lastAcceptedSegmentId = currentSegmentId;
        _lastAcceptedTimingSequence = timingSequence;
        _hasAcceptedRawTicks = true;
        _hasAcceptedTimingSequence = true;
    }

    public double ResolveCurrentVelocityMetersPerSecond(long nowTicks, double cpi, double velocityWindowMs)
    {
        long windowEndTicks = Math.Max(0L, nowTicks);
        long windowStartTicks = Math.Max(0L, windowEndTicks - MillisecondsToTicks(velocityWindowMs));
        TrimVelocityWindowSamples(windowStartTicks);
        if (_recentVelocitySamples.Count == 0)
        {
            return 0.0;
        }

        double distanceCountsInWindow = 0.0;
        long coveredTicks = 0L;
        foreach (MousePerformanceVelocityWindowSample sample in _recentVelocitySamples)
        {
            if (sample.EndTicks <= windowStartTicks || sample.StartTicks >= windowEndTicks)
            {
                continue;
            }

            long overlapStartTicks = Math.Max(sample.StartTicks, windowStartTicks);
            long overlapEndTicks = Math.Min(sample.EndTicks, windowEndTicks);
            if (overlapEndTicks <= overlapStartTicks)
            {
                continue;
            }

            long sampleDurationTicks = sample.EndTicks - sample.StartTicks;
            if (sampleDurationTicks <= 0)
            {
                continue;
            }

            long overlapTicks = overlapEndTicks - overlapStartTicks;
            double overlapRatio = (double)overlapTicks / sampleDurationTicks;
            distanceCountsInWindow += sample.DistanceCounts * overlapRatio;
            coveredTicks += overlapTicks;
        }

        if (coveredTicks <= 0 || distanceCountsInWindow <= 0.0)
        {
            return 0.0;
        }

        double coveredMilliseconds = TicksToMilliseconds(coveredTicks);
        return coveredMilliseconds > 0.0 ? distanceCountsInWindow / coveredMilliseconds / cpi * 25.4 : 0.0;
    }

    private void RecordIntervalDiagnostics(int currentSegmentId, long timingSequence, long rawCaptureTicks, int relativeDeltaX, int relativeDeltaY, double velocityWindowMs)
    {
        bool isSameSegment = currentSegmentId == _lastAcceptedSegmentId;
        if (!isSameSegment)
        {
            return;
        }

        if (_hasAcceptedTimingSequence)
        {
            if (timingSequence < _lastAcceptedTimingSequence)
            {
                _reorderedSequenceCount++;
            }
            else if (timingSequence > _lastAcceptedTimingSequence + 1)
            {
                _sequenceGapCount = SaturatingAdd(_sequenceGapCount, timingSequence - _lastAcceptedTimingSequence - 1);
            }
        }

        if (rawCaptureTicks < _lastAcceptedRawTicks)
        {
            _outOfOrderTimestampCount++;
        }
        else if (rawCaptureTicks == _lastAcceptedRawTicks)
        {
            _zeroIntervalCount++;
            _sameTickCount++;
        }
        else if (!_hasAcceptedTimingSequence || timingSequence > _lastAcceptedTimingSequence)
        {
            double distanceCounts = Math.Sqrt((double)relativeDeltaX * relativeDeltaX + (double)relativeDeltaY * relativeDeltaY);
            RecordVelocityWindowSample(_lastAcceptedRawTicks, rawCaptureTicks, distanceCounts, velocityWindowMs);
        }
    }

    private void RecordVelocityWindowSample(long startTicks, long endTicks, double distanceCounts, double velocityWindowMs)
    {
        if (distanceCounts >= 0.0 && endTicks > startTicks)
        {
            _recentVelocitySamples.Enqueue(new MousePerformanceVelocityWindowSample(startTicks, endTicks, distanceCounts));
            TrimVelocityWindowSamples(endTicks - MillisecondsToTicks(velocityWindowMs));
        }
    }

    private void TrimVelocityWindowSamples(long minimumEndTicks)
    {
        while (_recentVelocitySamples.Count > 0 && _recentVelocitySamples.Peek().EndTicks <= minimumEndTicks)
        {
            _recentVelocitySamples.Dequeue();
        }
    }

    private static int SaturatingAdd(int currentValue, long delta)
    {
        if (delta <= 0)
        {
            return currentValue;
        }
        if (currentValue >= int.MaxValue)
        {
            return int.MaxValue;
        }
        long boundedDelta = Math.Min(delta, int.MaxValue - currentValue);
        return currentValue + (int)boundedDelta;
    }

    private static double TicksToMilliseconds(long ticks)
    {
        return (double)ticks * 1000.0 / (double)System.Diagnostics.Stopwatch.Frequency;
    }

    private static long MillisecondsToTicks(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds <= 0.0)
        {
            return 0L;
        }
        return (long)Math.Round(milliseconds * System.Diagnostics.Stopwatch.Frequency / 1000.0);
    }
}
