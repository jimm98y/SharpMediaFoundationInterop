using System;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>
    /// A first-in first-out queue of a few items, safe to use from any thread, that allocates nothing once it has room
    /// for as many as it holds at once: a ring of one array, grown only past that. It can be peeked at as often as is
    /// wanted: ConcurrentQueue's TryPeek, of items that hold references, freezes the segment it read from, and the next
    /// item enqueued allocates a new one - a queue of frames peeked at as each is due allocates at every frame.
    /// </summary>
    public sealed class FrameQueue<T>
    {
        private readonly object _lock = new object();
        private T[] _items;
        private int _head;
        private int _count;

        public FrameQueue(int capacity = 8)
        {
            _items = new T[Math.Max(1, capacity)];
        }

        public int Count
        {
            get { lock (_lock) return _count; }
        }

        public bool IsEmpty => Count == 0;

        public void Enqueue(T item)
        {
            lock (_lock)
            {
                if (_count == _items.Length)
                {
                    var grown = new T[_items.Length * 2];
                    for (int i = 0; i < _count; i++)
                        grown[i] = _items[(_head + i) % _items.Length];
                    _items = grown;
                    _head = 0;
                }
                _items[(_head + _count) % _items.Length] = item;
                _count++;
            }
        }

        public bool TryDequeue(out T item)
        {
            lock (_lock)
            {
                if (_count == 0)
                {
                    item = default;
                    return false;
                }
                item = _items[_head];
                _items[_head] = default; // not kept alive by the queue
                _head = (_head + 1) % _items.Length;
                _count--;
                return true;
            }
        }

        public bool TryPeek(out T item)
        {
            lock (_lock)
            {
                if (_count == 0)
                {
                    item = default;
                    return false;
                }
                item = _items[_head];
                return true;
            }
        }
    }
}
