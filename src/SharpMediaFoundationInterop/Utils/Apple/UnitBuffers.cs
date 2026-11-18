using System;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>
    /// Arrays of at least the size asked for, kept for reuse: of what an encoder hands out, an access unit or a picture of a
    /// size of its own each time, queued with its length - where <see cref="FramePool"/>'s exact sizes would never be
    /// found again. Each is made of a power of two, so that a stream's units come to fit the few there are. Safe to rent
    /// from and return to on any thread: an encoder's callback fills them, the caller reads them out.
    /// </summary>
    internal sealed class UnitBuffers
    {
        private const int Smallest = 4096;

        private readonly object _lock = new object();
        private readonly byte[][] _free;
        private int _count;

        /// <param name="capacity">How many arrays are kept at most.</param>
        public UnitBuffers(int capacity = 8)
        {
            _free = new byte[Math.Max(1, capacity)][];
        }

        /// <summary>An array of <paramref name="size"/> bytes at least: the smallest kept that holds it, or a new one.</summary>
        public byte[] Rent(int size)
        {
            lock (_lock)
            {
                int best = -1;
                for (int i = 0; i < _count; i++)
                {
                    if (_free[i].Length >= size && (best < 0 || _free[i].Length < _free[best].Length))
                        best = i;
                }
                if (best >= 0)
                {
                    var array = _free[best];
                    _free[best] = _free[--_count];
                    _free[_count] = null;
                    return array;
                }
            }
            int length = Smallest;
            while (length < size && length < 0x40000000)
                length <<= 1;
            return new byte[Math.Max(length, size)];
        }

        /// <summary>Gives an array back; of a full pool, the smallest kept is let go of for it, where it is the larger.</summary>
        public void Return(byte[] array)
        {
            if (array == null || array.Length == 0)
                return;
            lock (_lock)
            {
                if (_count < _free.Length)
                {
                    _free[_count++] = array;
                    return;
                }
                int smallest = 0;
                for (int i = 1; i < _count; i++)
                {
                    if (_free[i].Length < _free[smallest].Length)
                        smallest = i;
                }
                if (_free[smallest].Length < array.Length)
                    _free[smallest] = array;
            }
        }
    }
}
