using ClickSyncMouseTester.Models;
using System;
using System.Collections;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Services;

internal sealed class MousePerformanceEventBuffer
{
    private sealed class MousePerformanceEventReadOnlyView : IReadOnlyList<MousePerformanceEvent>
    {
        private sealed class Enumerator : IEnumerator<MousePerformanceEvent>
        {
            private readonly MousePerformanceEvent[][] _chunks;

            private readonly int _count;

            private int _index;

            public MousePerformanceEvent Current
            {
                get
                {
                    if (_index < 0 || _index >= _count)
                    {
                        throw new InvalidOperationException();
                    }
                    int chunkIndex = _index / ChunkSize;
                    int offsetInChunk = _index % ChunkSize;
                    if (chunkIndex < 0 || chunkIndex >= _chunks.Length || _chunks[chunkIndex] == null)
                    {
                        throw new InvalidOperationException("The event buffer snapshot is not available.");
                    }
                    return _chunks[chunkIndex][offsetInChunk];
                }
            }

            object IEnumerator.Current => Current;

            public Enumerator(MousePerformanceEvent[][] chunks, int count)
            {
                _chunks = chunks ?? Array.Empty<MousePerformanceEvent[]>();
                _count = count;
                _index = -1;
            }

            public bool MoveNext()
            {
                if (_index >= _count)
                {
                    return false;
                }
                _index++;
                return _index < _count;
            }

            bool IEnumerator.MoveNext()
            {
                return this.MoveNext();
            }

            public void Reset()
            {
                _index = -1;
            }

            void IEnumerator.Reset()
            {
                this.Reset();
            }

            public void Dispose()
            {
            }

            void IDisposable.Dispose()
            {
                this.Dispose();
            }
        }

        private readonly MousePerformanceEvent[][] _chunks;

        private readonly int _count;

        public MousePerformanceEvent this[int index]
        {
            get
            {
                if (index < 0 || index >= _count)
                {
                    throw new ArgumentOutOfRangeException("index");
                }
                return ResolveItem(index);
            }
        }

        public int Count => _count;

        public MousePerformanceEventReadOnlyView(MousePerformanceEvent[][] chunks, int count)
        {
            _chunks = chunks ?? Array.Empty<MousePerformanceEvent[]>();
            _count = count;
        }

        public IEnumerator<MousePerformanceEvent> GetEnumerator()
        {
            return new Enumerator(_chunks, _count);
        }

        IEnumerator<MousePerformanceEvent> IEnumerable<MousePerformanceEvent>.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        private IEnumerator GetUntypedEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetUntypedEnumerator();
        }

        private MousePerformanceEvent ResolveItem(int index)
        {
            int chunkIndex = index / ChunkSize;
            int offsetInChunk = index % ChunkSize;
            if (chunkIndex < 0 || chunkIndex >= _chunks.Length || _chunks[chunkIndex] == null)
            {
                throw new InvalidOperationException("The event buffer snapshot is not available.");
            }
            return _chunks[chunkIndex][offsetInChunk];
        }
    }

    private const int ChunkSize = 2048;

    private readonly List<MousePerformanceEvent[]> _chunks;

    private int _count;

    public int Count => _count;

    public MousePerformanceEvent this[int index]
    {
        get
        {
            if (index < 0 || index >= _count)
            {
                throw new ArgumentOutOfRangeException("index");
            }
            return _chunks[index / ChunkSize][index % ChunkSize];
        }
    }

    public MousePerformanceEventBuffer()
    {
        _chunks = new List<MousePerformanceEvent[]>();
    }

    public void Clear()
    {
        _chunks.Clear();
        _count = 0;
    }

    public void Add(MousePerformanceEvent mouseEvent)
    {
        if (mouseEvent == null)
        {
            return;
        }

        int chunkIndex = _count / ChunkSize;
        int offsetInChunk = _count % ChunkSize;
        if (chunkIndex >= _chunks.Count)
        {
            _chunks.Add(new MousePerformanceEvent[ChunkSize]);
        }
        _chunks[chunkIndex][offsetInChunk] = mouseEvent;
        _count++;
    }

    public IReadOnlyList<MousePerformanceEvent> CreateReadOnlyView(int snapshotCount)
    {
        int count = Math.Max(0, Math.Min(snapshotCount, _count));
        return new MousePerformanceEventReadOnlyView(_chunks.ToArray(), count);
    }
}






