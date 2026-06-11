using System;

namespace ClickSyncMouseTester.Services;

internal sealed class ReportRateWindow
{
    private const double WindowMilliseconds = 300.0;
    private const double WarmupMilliseconds = 310.0;
    private const double MinimumSpanMilliseconds = 200.0;
    private const double MaximumReportedRate = 20000.0;
    private const int TimestampBufferCapacity = 8192;

    private readonly TimestampRingBuffer _timestamps;
    private double _rateEligibleAtMs;

    public ReportRateWindow()
    {
        _timestamps = new TimestampRingBuffer(TimestampBufferCapacity);
        _rateEligibleAtMs = double.NaN;
    }

    public void Reset()
    {
        _timestamps.Clear();
        _rateEligibleAtMs = double.NaN;
    }

    public void BeginSegment()
    {
        Reset();
    }

    public void AddReportTimestamp(double timestampMs)
    {
        if (!IsFinite(timestampMs))
        {
            return;
        }

        if (!IsFinite(_rateEligibleAtMs))
        {
            _rateEligibleAtMs = timestampMs + WarmupMilliseconds;
        }

        _timestamps.Add(timestampMs);
        PruneByReference(timestampMs);
    }

    public void PruneByNow(double nowMs)
    {
        if (!IsFinite(nowMs) || _timestamps.Count <= 0)
        {
            return;
        }

        double cutoffMs = nowMs - WindowMilliseconds;
        if (_timestamps.PeekBack() < cutoffMs)
        {
            Reset();
            return;
        }

        while (_timestamps.Count > 0 && _timestamps.PeekFront() < cutoffMs)
        {
            _timestamps.PopFront();
        }

        if (_timestamps.Count == 0)
        {
            _rateEligibleAtMs = double.NaN;
        }
    }

    public double? ComputeRate()
    {
        if (_timestamps.Count < 2 || !IsFinite(_rateEligibleAtMs) || _timestamps.PeekBack() < _rateEligibleAtMs)
        {
            return null;
        }

        double windowSpanMs = _timestamps.PeekBack() - _timestamps.PeekFront();
        if (windowSpanMs < MinimumSpanMilliseconds)
        {
            return null;
        }

        double rate = (_timestamps.Count - 1) * 1000.0 / windowSpanMs;
        return IsFinite(rate) && rate > 0.0 ? Math.Min(rate, MaximumReportedRate) : null;
    }

    public bool CanUseForPeak(double peakEligibleAtMs)
    {
        return IsFinite(peakEligibleAtMs)
            && _timestamps.Count >= 2
            && _timestamps.PeekFront() >= peakEligibleAtMs;
    }

    private void PruneByReference(double referenceMs)
    {
        if (_timestamps.Count < 2)
        {
            return;
        }

        double cutoffMs = referenceMs - WindowMilliseconds;
        while (_timestamps.Count > 2 && _timestamps.GetAt(1) < cutoffMs)
        {
            _timestamps.PopFront();
        }
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
