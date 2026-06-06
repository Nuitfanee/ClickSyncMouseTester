using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Services;

internal sealed class PollingHistoryPointRingBuffer
{
    private readonly PollingHistoryPoint[] _items;

    private int _startIndex;

    private int _count;

    private int _version;

    public int Count => _count;

    public int Version => _version;

    public PollingHistoryPointRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        _items = new PollingHistoryPoint[capacity];
    }

    public void Clear()
    {
        if (_count <= 0)
        {
            return;
        }

        Array.Clear(_items, 0, _items.Length);
        _startIndex = 0;
        _count = 0;
        _version++;
    }

    public void Add(PollingHistoryPoint point)
    {
        if (point == null)
        {
            return;
        }

        if (_count < _items.Length)
        {
            int writeIndex = (_startIndex + _count) % _items.Length;
            _items[writeIndex] = point;
            _count++;
        }
        else
        {
            _items[_startIndex] = point;
            _startIndex = NextIndex(_startIndex);
        }
        _version++;
    }

    public void RemoveBefore(double cutoffTimestampMs)
    {
        if (_count <= 0)
        {
            return;
        }

        bool removedAny = false;
        while (_count > 0)
        {
            PollingHistoryPoint oldestPoint = _items[_startIndex];
            if (oldestPoint == null || oldestPoint.TimestampMs >= cutoffTimestampMs)
            {
                break;
            }

            _items[_startIndex] = null;
            _startIndex = NextIndex(_startIndex);
            _count--;
            removedAny = true;
        }

        if (!removedAny)
        {
            return;
        }

        if (_count == 0)
        {
            _startIndex = 0;
        }
        _version++;
    }

    public IReadOnlyList<PollingHistoryPoint> CreateView()
    {
        if (_count <= 0)
        {
            return Array.Empty<PollingHistoryPoint>();
        }

        PollingHistoryPoint[] snapshot = new PollingHistoryPoint[_count];
        for (int offset = 0; offset < _count; offset++)
        {
            snapshot[offset] = _items[(_startIndex + offset) % _items.Length];
        }
        return snapshot;
    }

    private int NextIndex(int currentIndex)
    {
        return (currentIndex + 1) % _items.Length;
    }
}
