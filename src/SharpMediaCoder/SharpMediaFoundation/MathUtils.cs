using System;

namespace SharpMediaFoundation
{
    public class MathUtils
    {
        public static uint CalculateBitrate(uint width, uint height, uint fpsNom, uint fpsDenom, double bpp = 0.12)
        {
            double fps = (double)fpsNom / fpsDenom;

            // https://stackoverflow.com/questions/8931200/video-bitrate-and-file-size-calculation
            return (uint)Math.Ceiling(width * height * fps * bpp * 0.001d);
        }

        public static uint RoundToMultipleOf(uint value, uint multiple)
        {
            return ((value + multiple - 1) / multiple) * multiple;
        }
    }
}
