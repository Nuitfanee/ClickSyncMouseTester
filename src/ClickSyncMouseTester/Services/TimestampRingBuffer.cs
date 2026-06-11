using System;

namespace ClickSyncMouseTester.Services;

internal sealed class TimestampRingBuffer
{
    private readonly double[] _items;
    private int _startIndex;
    private int _count;

    public int Count => _count;

    public TimestampRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _items = new double[capacity];
    }

    public void Clear()
    {
        _startIndex = 0;
        _count = 0;
    }

    public void Add(double timestampMs)
    {
        if (_count < _items.Length)
        {
            _items[(_startIndex + _count) % _items.Length] = timestampMs;
            _count++;
            return;
        }

        _items[_startIndex] = timestampMs;
        _startIndex = (_startIndex + 1) % _items.Length;
    }

    public double PeekFront()
    {
        ThrowIfEmpty();
        return _items[_startIndex];
    }

    public double PeekBack()
    {
        ThrowIfEmpty();
        return _items[(_startIndex + _count - 1) % _items.Length];
    }

    public double GetAt(int index)
    {
        if (index < 0 || index >= _count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return _items[(_startIndex + index) % _items.Length];
    }

    public double PopFront()
    {
        double timestampMs = PeekFront();
        _startIndex = (_startIndex + 1) % _items.Length;
        _count--;

        if (_count == 0)
        {
            _startIndex = 0;
        }

        return timestampMs;
    }

    private void ThrowIfEmpty()
    {
        if (_count <= 0)
        {
            throw new InvalidOperationException("The timestamp buffer is empty.");
        }
    }
}
