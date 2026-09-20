using System.Collections.Generic;

namespace SharpSpatialVideo
{
    /// <summary>
    /// Conversion between a NAL unit as stored (EBSP, with emulation prevention bytes) and its
    /// raw payload (RBSP). SharpH26X's <c>ItuStream</c> reads and writes plain bytes, so headers
    /// must be parsed out of - and serialized back into - RBSP.
    /// </summary>
    public static class RbspUtils
    {
        /// <summary>
        /// Strips emulation prevention bytes: 0x03 is removed when it follows two zero bytes and
        /// precedes a byte of 0x03 or less.
        /// </summary>
        public static byte[] ToRbsp(byte[] ebsp)
        {
            var output = new List<byte>(ebsp.Length);
            int zeros = 0;

            for (int i = 0; i < ebsp.Length; i++)
            {
                byte b = ebsp[i];

                if (zeros >= 2 && b == 0x03 && i + 1 < ebsp.Length && ebsp[i + 1] <= 0x03)
                {
                    zeros = 0;
                    continue;
                }

                output.Add(b);
                zeros = b == 0x00 ? zeros + 1 : 0;
            }

            return output.ToArray();
        }

        /// <summary>
        /// Re-inserts emulation prevention bytes. The first <paramref name="headerBytes"/> bytes are
        /// copied through untouched (the NAL unit header is not escaped) but still seed the zero run.
        /// </summary>
        public static byte[] ToEbsp(byte[] rbsp, int headerBytes = 2)
        {
            var output = new List<byte>(rbsp.Length + rbsp.Length / 64 + 8);
            int zeros = 0;

            for (int i = 0; i < rbsp.Length; i++)
            {
                byte b = rbsp[i];

                if (i >= headerBytes && zeros >= 2 && b <= 0x03)
                {
                    output.Add(0x03);
                    zeros = 0;
                }

                output.Add(b);
                zeros = b == 0x00 ? zeros + 1 : 0;
            }

            return output.ToArray();
        }
    }
}
