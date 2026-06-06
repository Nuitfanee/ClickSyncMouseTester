using System;
using System.Threading;

namespace ClickSyncMouseTester.Services;

internal sealed class LatestValueRingBuffer<T>
    where T : class
{
    private readonly T[] _items;

    private int _writeSequence;

    private int _readSequence;

    public LatestValueRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _items = new T[capacity];
    }

    public void Write(T value)
    {
        if (value == null)
        {
            return;
        }

        int nextWriteSequence = _writeSequence + 1;
        _items[nextWriteSequence % _items.Length] = value;
        Thread.MemoryBarrier();
        Volatile.Write(ref _writeSequence, nextWriteSequence);
    }

    public bool TryReadLatest(ref T value)
    {
        int latestWriteSequence = Volatile.Read(ref _writeSequence);
        if (latestWriteSequence == 0 || latestWriteSequence == _readSequence)
        {
            value = null;
            return false;
        }

        Thread.MemoryBarrier();
        value = _items[latestWriteSequence % _items.Length];
        _readSequence = latestWriteSequence;
        return value != null;
    }

    public bool TryPeekLatest(ref T value)
    {
        int latestWriteSequence = Volatile.Read(ref _writeSequence);
        if (latestWriteSequence == 0)
        {
            value = null;
            return false;
        }

        Thread.MemoryBarrier();
        value = _items[latestWriteSequence % _items.Length];
        return value != null;
    }

    public void Clear()
    {
        Array.Clear(_items, 0, _items.Length);
        Volatile.Write(ref _writeSequence, 0);
        _readSequence = 0;
    }
}
