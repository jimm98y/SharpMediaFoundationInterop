using System;

namespace SharpMediaFoundation
{
    public class MathUtils
    {
        public static uint CalculateBitrate(uint width, uint height, double fps, double bpp = 0.12)
        {
            // https://stackoverflow.com/questions/8931200/video-bitrate-and-file-size-calculation
            return (uint)Math.Ceiling(width * height * fps * bpp * 0.001d);
        }

        public static uint RoundToMultipleOf(uint value, uint multiple)
        {
            return ((value + multiple - 1) / multiple) * multiple;
        }

        public static ulong EncodeAttributeValue(uint highValue, uint lowValue)
        {
            return ((ulong)highValue << 32) + lowValue;
        }

        public static uint CalculateNV12BufferSize(uint width, uint height)
        {
            return width * height * 3 / 2;
        }

        public static uint CalculateRGB24BufferSize(uint width, uint height)
        {
            return width * height * 3;
        }
    }
}
