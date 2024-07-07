using System.Collections.Generic;
using System.Linq;

namespace SharpMediaFoundation
{
    public static class AnnexBParser
    {
        public static readonly byte[] AnnexB = [0, 0, 0, 1];

        public static IEnumerable<byte[]> ParseNalu(byte[] bytes, uint length)
        {
            long index = 0;

            while (index < length)
            {
                long next = IndexOf(bytes, AnnexB, index + AnnexB.Length, length);
                if (next == -1)
                    next = length;

                yield return bytes.Skip((int)index + AnnexB.Length).Take((int)(next - index - AnnexB.Length)).ToArray();

                index = next;
            }
        }

        private static unsafe long IndexOf(byte[] haystack, byte[] needle, long startOffset, long endOffset)
        {
            fixed (byte* h = haystack)
            fixed (byte* n = needle)
            {
                for (byte* hNext = h + startOffset, hEnd = h + endOffset + 1 - needle.LongLength, nEnd = n + needle.LongLength; hNext < hEnd; hNext++)
                    for (byte* hInc = hNext, nInc = n; *nInc == *hInc; hInc++)
                        if (++nInc == nEnd)
                            return hNext - h;
                return -1;
            }
        }
    }
}
