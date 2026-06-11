using ClickSyncMouseTester.Models;
using System;
using System.Threading;

namespace ClickSyncMouseTester.Services;

internal sealed class BufferedRawMousePacketInbox : IDisposable
{
    private readonly object _producerSyncRoot;
    private readonly object _consumerSyncRoot;
    private readonly QueuedRawMousePacket[] _items;
    private readonly AutoResetEvent _itemAvailable;

    private long _writeSequence;
    private long _readSequence;
    private int _highWatermark;
    private int _completionRequested;
    private int _disposed;

    public int Count
    {
        get
        {
            long queuedCount = Volatile.Read(in _writeSequence) - Volatile.Read(in _readSequence);
            if (queuedCount <= 0)
            {
                return 0;
            }

            return queuedCount >= _items.Length ? _items.Length : (int)queuedCount;
        }
    }

    public int Capacity => _items.Length;

    public int HighWatermark => Math.Max(0, Interlocked.CompareExchange(ref _highWatermark, 0, 0));

    public BufferedRawMousePacketInbox(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _producerSyncRoot = new object();
        _consumerSyncRoot = new object();
        _itemAvailable = new AutoResetEvent(initialState: false);
        _items = new QueuedRawMousePacket[capacity];
    }

    public bool Enqueue(RawMousePacket packet, int generation, ref int droppedCount)
    {
        droppedCount = 0;
        if (packet == null || Volatile.Read(in _completionRequested) != 0)
        {
            return false;
        }

        object producerSyncRoot = _producerSyncRoot;
        bool lockTaken = false;

        try
        {
            Monitor.Enter(producerSyncRoot, ref lockTaken);

            if (Volatile.Read(in _completionRequested) != 0)
            {
                return false;
            }

            long readSequence = Volatile.Read(in _readSequence);
            long writeSequence = Volatile.Read(in _writeSequence);
            if (writeSequence - readSequence >= _items.Length)
            {
                droppedCount = 1;
                return false;
            }

            int writeIndex = (int)(writeSequence % _items.Length);
            _items[writeIndex] = new QueuedRawMousePacket(packet, generation);

            int queuedCount = (int)(writeSequence - readSequence + 1);
            UpdateHighWatermark(queuedCount);
            Volatile.Write(ref _writeSequence, writeSequence + 1);
            _itemAvailable.Set();
            return true;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(producerSyncRoot);
            }
        }
    }

    public bool TryDequeue(ref QueuedRawMousePacket queuedPacket)
    {
        object consumerSyncRoot = _consumerSyncRoot;
        bool lockTaken = false;

        try
        {
            Monitor.Enter(consumerSyncRoot, ref lockTaken);

            long readSequence = _readSequence;
            long writeSequence = Volatile.Read(in _writeSequence);
            if (readSequence >= writeSequence)
            {
                queuedPacket = QueuedRawMousePacket.Empty;
                return false;
            }

            int readIndex = (int)(readSequence % _items.Length);
            queuedPacket = _items[readIndex];
            _items[readIndex] = QueuedRawMousePacket.Empty;
            Volatile.Write(ref _readSequence, readSequence + 1);
            return queuedPacket.HasPacket;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(consumerSyncRoot);
            }
        }
    }

    public bool WaitForData(int timeoutMilliseconds = -1)
    {
        int remainingMilliseconds = timeoutMilliseconds;
        long startedTick = timeoutMilliseconds == -1 ? 0 : Environment.TickCount64;

        while (true)
        {
            if (Volatile.Read(in _completionRequested) != 0)
            {
                return false;
            }

            if (Count > 0)
            {
                return true;
            }

            if (remainingMilliseconds == 0)
            {
                return Count > 0 && Volatile.Read(in _completionRequested) == 0;
            }

            try
            {
                if (!_itemAvailable.WaitOne(remainingMilliseconds))
                {
                    return Count > 0 && Volatile.Read(in _completionRequested) == 0;
                }
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            if (timeoutMilliseconds != -1)
            {
                long elapsedMilliseconds = Environment.TickCount64 - startedTick;
                remainingMilliseconds = elapsedMilliseconds < timeoutMilliseconds
                    ? (int)(timeoutMilliseconds - elapsedMilliseconds)
                    : 0;
            }
        }
    }

    public void Drain()
    {
        object consumerSyncRoot = _consumerSyncRoot;
        bool lockTaken = false;

        try
        {
            Monitor.Enter(consumerSyncRoot, ref lockTaken);

            while (true)
            {
                long readSequence = _readSequence;
                long writeSequence = Volatile.Read(in _writeSequence);
                if (readSequence >= writeSequence)
                {
                    break;
                }

                for (; readSequence < writeSequence; readSequence++)
                {
                    int readIndex = (int)(readSequence % _items.Length);
                    _items[readIndex] = QueuedRawMousePacket.Empty;
                }

                Volatile.Write(ref _readSequence, readSequence);
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(consumerSyncRoot);
            }
        }
    }

    public void ResetHighWatermark()
    {
        Volatile.Write(ref _highWatermark, Count);
    }

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completionRequested, 1) == 0)
        {
            _itemAvailable.Set();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Complete();
            Drain();
            _itemAvailable.Dispose();
        }
    }

    void IDisposable.Dispose()
    {
        Dispose();
    }

    private void UpdateHighWatermark(int queuedCount)
    {
        int targetHighWatermark = Math.Max(0, queuedCount);
        int observedHighWatermark = Interlocked.CompareExchange(ref _highWatermark, 0, 0);

        while (targetHighWatermark > observedHighWatermark)
        {
            int previousHighWatermark = Interlocked.CompareExchange(ref _highWatermark, targetHighWatermark, observedHighWatermark);
            if (previousHighWatermark == observedHighWatermark)
            {
                break;
            }

            observedHighWatermark = previousHighWatermark;
        }
    }
}
