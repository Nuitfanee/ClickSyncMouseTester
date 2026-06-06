using System;

namespace ClickSyncMouseTester.Services;

internal sealed class StabilityMetricsWindow
{
    private const double WindowMilliseconds = 1000.0;
    private const int PacketSampleCapacity = 32768;

    private readonly RollingEmptyPacketWindow _emptyPackets;

    private double _eligibleAtMs;
    private double _latestPacketTimestampMs;

    public bool HasStarted => IsFinite(_eligibleAtMs);

    public bool IsReady => HasStarted
        && IsFinite(_latestPacketTimestampMs)
        && _latestPacketTimestampMs >= _eligibleAtMs;

    public StabilityMetricsWindow()
    {
        _emptyPackets = new RollingEmptyPacketWindow(PacketSampleCapacity);
        _eligibleAtMs = double.NaN;
        _latestPacketTimestampMs = double.NaN;
    }

    public void Reset()
    {
        _emptyPackets.Clear();
        _eligibleAtMs = double.NaN;
        _latestPacketTimestampMs = double.NaN;
    }

    public void BeginSegment()
    {
        Reset();
    }

    public void PushPacket(double timestampMs, bool isEmptyPacket, bool isCountedReport)
    {
        if (!IsFinite(timestampMs))
        {
            return;
        }

        if (!HasStarted)
        {
            if (!isCountedReport)
            {
                return;
            }

            _eligibleAtMs = timestampMs + WindowMilliseconds;
        }

        _emptyPackets.Add(timestampMs, isEmptyPacket);
        _emptyPackets.Prune(timestampMs - WindowMilliseconds);
        _latestPacketTimestampMs = timestampMs;
    }

    public void PruneByNow(double nowMs)
    {
        if (!IsFinite(nowMs))
        {
            return;
        }

        double cutoffMs = nowMs - WindowMilliseconds;
        _emptyPackets.Prune(cutoffMs);
    }

    public double? ComputeEmptyPacketPercent()
    {
        return IsReady ? _emptyPackets.ComputeEmptyPacketPercent() : null;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private sealed class RollingEmptyPacketWindow
    {
        private readonly PacketSample[] _items;
        private int _startIndex;
        private int _count;
        private int _emptyCount;

        public RollingEmptyPacketWindow(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _items = new PacketSample[capacity];
        }

        public void Clear()
        {
            _startIndex = 0;
            _count = 0;
            _emptyCount = 0;
        }

        public void Add(double timestampMs, bool isEmptyPacket)
        {
            if (_count == _items.Length)
            {
                RemoveFront();
            }

            _items[(_startIndex + _count) % _items.Length] = new PacketSample(timestampMs, isEmptyPacket);
            _count++;
            if (isEmptyPacket)
            {
                _emptyCount++;
            }
        }

        public void Prune(double cutoffMs)
        {
            while (_count > 0 && PeekFront().TimestampMs < cutoffMs)
            {
                RemoveFront();
            }
        }

        public double? ComputeEmptyPacketPercent()
        {
            if (_count <= 0)
            {
                return null;
            }

            double percent = _emptyCount * 100.0 / _count;
            return IsFinite(percent) && percent >= 0.0 ? percent : null;
        }

        private PacketSample PeekFront()
        {
            return _items[_startIndex];
        }

        private void RemoveFront()
        {
            PacketSample sample = _items[_startIndex];
            if (sample.IsEmpty)
            {
                _emptyCount--;
            }

            _startIndex = (_startIndex + 1) % _items.Length;
            _count--;

            if (_count == 0)
            {
                _startIndex = 0;
            }
        }
    }

    private readonly struct PacketSample
    {
        public double TimestampMs { get; }
        public bool IsEmpty { get; }

        public PacketSample(double timestampMs, bool isEmpty)
        {
            TimestampMs = timestampMs;
            IsEmpty = isEmpty;
        }
    }
}
