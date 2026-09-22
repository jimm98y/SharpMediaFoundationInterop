using System;
using System.Collections.Generic;

namespace SharpSpatialVideo
{
    /// <summary>Splits an Annex B byte stream - what the Media Foundation encoder hands out - into NAL units.</summary>
    public static class AnnexB
    {
        /// <summary>
        /// The NAL units in a sample, without their start codes. Each one ends where the next one's
        /// start code begins, not where its payload does: ending it there instead carries the start
        /// code along, and a parameter set that ends 00 00 00 01 goes into hvcC with it.
        /// </summary>
        public static IEnumerable<byte[]> Nalus(byte[] data)
        {
            var starts = new List<int>();
            var codes = new List<int>();
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                {
                    codes.Add(i);
                    starts.Add(i + 3);
                    i += 2;
                }
            }

            for (int i = 0; i < starts.Count; i++)
            {
                int start = starts[i];
                int end = i + 1 < starts.Count ? codes[i + 1] : data.Length;

                // The leading zero of a four byte start code, and any trailing_zero_8bits.
                while (end > start && data[end - 1] == 0)
                    end--;
                if (end > start)
                    yield return data.AsSpan(start, end - start).ToArray();
            }
        }
    }
}
