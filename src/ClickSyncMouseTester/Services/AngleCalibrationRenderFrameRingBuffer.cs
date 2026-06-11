using ClickSyncMouseTester.Models;
using System;
using System.Threading;

namespace ClickSyncMouseTester.Services;

internal class AngleCalibrationRenderFrameRingBuffer
{
    private readonly AngleCalibrationRenderFrame[] _items;

    private int _writeSequence;

    private int _readSequence;

    public AngleCalibrationRenderFrameRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        _items = new AngleCalibrationRenderFrame[capacity];
    }

    public void Write(AngleCalibrationRenderFrame frame)
    {
        if (frame == null)
        {
            return;
        }

        int nextWriteSequence = _writeSequence + 1;
        _items[nextWriteSequence % _items.Length] = frame;
        Thread.MemoryBarrier();
        Volatile.Write(ref _writeSequence, nextWriteSequence);
    }

    public bool TryReadLatest(ref AngleCalibrationRenderFrame frame)
    {
        int latestWriteSequence = Volatile.Read(ref _writeSequence);
        if (latestWriteSequence == 0 || latestWriteSequence == _readSequence)
        {
            frame = null;
            return false;
        }

        Thread.MemoryBarrier();
        frame = _items[latestWriteSequence % _items.Length];
        _readSequence = latestWriteSequence;
        return frame != null;
    }

    public void Clear()
    {
        Array.Clear(_items, 0, _items.Length);
        Volatile.Write(ref _writeSequence, 0);
        _readSequence = 0;
    }
}
