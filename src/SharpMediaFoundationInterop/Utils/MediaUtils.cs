using System;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>Arithmetic of sizes and times, of every platform; what is of Media Foundation's alone is in its Windows part.</summary>
    public static partial class MediaUtils
    {
        public static uint RoundToMultipleOf(uint value, uint multiple)
        {
            return (value + multiple - 1) / multiple * multiple;
        }

        /// <summary>Media Foundation's unit of time, 100 ns: the ticks of a second.</summary>
        public const long TicksPerSecond = 10_000_000;

        /// <summary>
        /// A time of a clock of <paramref name="clockRate"/> ticks a second in 100 ns units, worked out wide so that no
        /// length of stream overflows it on the way.
        /// </summary>
        public static long ToTicks(long time, long clockRate)
        {
            return (long)((Int128)time * TicksPerSecond / clockRate);
        }

        public static ulong EncodeAttributeValue(uint highValue, uint lowValue)
        {
            return ((ulong)highValue << 32) + lowValue;
        }
    }
}
