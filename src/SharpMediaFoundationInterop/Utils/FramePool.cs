using System;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>
    /// Arrays of the exact size asked for, kept for reuse: frames of video, decoded sound. A frame's array is as long as
    /// its picture or its PCM, which a consumer may take its length from, and is never let go of under memory pressure,
    /// as <see cref="System.Buffers.ArrayPool{T}.Shared"/> lets go of what it holds at each gen2 collection. The frames of
    /// a video are megabytes each, of the large object heap; allocated again, each brings the next gen2 collection on.
    /// Safe to rent from and return to on any thread: one thread decodes, another shows and gives back.
    /// </summary>
    public sealed class FramePool
    {
        /// <summary>One pool for every source and consumer: a frame may be given back by whatever showed it.</summary>
        public static FramePool Shared { get; } = new FramePool(32);

        private readonly object _lock = new object();
        private readonly byte[][] _arrays;
        // the order each was given back in, the oldest first let go of
        private readonly long[] _returned;
        private int _count;
        private long _clock;

        /// <param name="capacity">How many arrays are kept at most, of whatever sizes.</param>
        public FramePool(int capacity = 16)
        {
            if (capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            _arrays = new byte[capacity][];
            _returned = new long[capacity];
        }

        /// <summary>
        /// An array of exactly <paramref name="size"/> bytes, of what was given back or new; its contents are whatever they
        /// were. Where none is of the size, the oldest of another size is let go of: what a stream no longer decodes to.
        /// </summary>
        public byte[] Rent(int size)
        {
            if (size <= 0)
                return Array.Empty<byte>();

            lock (_lock)
            {
                int oldestOther = -1;
                for (int i = _count - 1; i >= 0; i--)
                {
                    var array = _arrays[i];
                    if (array.Length == size)
                    {
                        RemoveAt(i);
                        return array;
                    }
                    if (oldestOther < 0 || _returned[i] < _returned[oldestOther])
                        oldestOther = i;
                }
                if (oldestOther >= 0)
                    RemoveAt(oldestOther);
            }
            return new byte[size];
        }

        /// <summary>Gives an array back, to be rented again; of a full pool, the oldest given back is let go of for it.</summary>
        public void Return(byte[] array)
        {
            if (array == null || array.Length == 0)
                return;

            lock (_lock)
            {
                int at = _count;
                if (_count == _arrays.Length)
                {
                    at = 0;
                    for (int i = 1; i < _count; i++)
                        if (_returned[i] < _returned[at])
                            at = i;
                }
                else
                {
                    _count++;
                }
                _arrays[at] = array;
                _returned[at] = ++_clock;
            }
        }

        /// <summary>Lets go of every array kept.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_arrays, 0, _count);
                _count = 0;
            }
        }

        private void RemoveAt(int i)
        {
            _count--;
            _arrays[i] = _arrays[_count];
            _returned[i] = _returned[_count];
            _arrays[_count] = null;
        }
    }
}
