using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ClickSyncMouseTester.Services;

internal sealed class SerialEventDispatcher<T> : IDisposable
{
    private readonly object _syncRoot;
    private readonly Queue<T> _items;
    private readonly AutoResetEvent _itemAvailable;
    private readonly Action<T> _dispatch;
    private readonly Thread _thread;
    private readonly int _capacity;

    private int _completionRequested;
    private int _disposed;
    private long _droppedCount;

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public SerialEventDispatcher(string threadName, Action<T> dispatch, int capacity = 65536)
    {
        _syncRoot = new object();
        _items = new Queue<T>();
        _itemAvailable = new AutoResetEvent(initialState: false);
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _capacity = Math.Max(0, capacity);
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = string.IsNullOrWhiteSpace(threadName) ? "SerialEventDispatcher" : threadName
        };
        _thread.Start();
    }

    public bool Enqueue(T item)
    {
        if (Volatile.Read(ref _completionRequested) != 0)
        {
            return false;
        }

        lock (_syncRoot)
        {
            if (Volatile.Read(ref _completionRequested) != 0)
            {
                return false;
            }

            _items.Enqueue(item);
            if (_capacity > 0 && _items.Count > _capacity)
            {
                _items.Dequeue();
                Interlocked.Increment(ref _droppedCount);
            }
            _itemAvailable.Set();
            return true;
        }
    }

    private void ThreadMain()
    {
        while (Volatile.Read(ref _completionRequested) == 0)
        {
            if (!TryDequeue(out T item))
            {
                try
                {
                    _itemAvailable.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                continue;
            }

            try
            {
                _dispatch(item);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Serial event dispatcher failed while dispatching {typeof(T).Name}: {ex}");
            }
        }

        while (TryDequeue(out T item))
        {
            try
            {
                _dispatch(item);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Serial event dispatcher failed while draining {typeof(T).Name}: {ex}");
            }
        }
    }

    private bool TryDequeue(out T item)
    {
        lock (_syncRoot)
        {
            if (_items.Count == 0)
            {
                item = default;
                return false;
            }

            item = _items.Dequeue();
            return true;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _completionRequested, 1);
        _itemAvailable.Set();
        if (_thread.IsAlive)
        {
            _thread.Join(1000);
        }
        _itemAvailable.Dispose();
    }
}
