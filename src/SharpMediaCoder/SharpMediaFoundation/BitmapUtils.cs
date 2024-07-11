using System;
using System.Runtime.InteropServices;

namespace SharpMediaFoundation
{
    public static class BitmapUtils
    {
        public static void CopyBitmap(byte[] source, nint dest, int originalWidth, int originalHeight, int decodedWidth, int decodedHeight, bool flip = true)
        {
            int decodedStride = decodedWidth * 3;
            int startIndex = (decodedHeight - originalHeight) * decodedStride;
            int wbStride = originalWidth * 3;
            int wbIndex = flip ? wbStride * (originalHeight - 1) : 0;
            int wbFlip = flip ? -1 : 1;
            for (int i = 0; i < originalHeight; i++)
            {
                Marshal.Copy(
                   source,
                   startIndex + i * decodedStride,
                   dest + wbIndex + i * wbFlip * wbStride,
                   wbStride
                );
            }
        }

        public static void CopyBitmap(byte[] source, byte[] dest, int originalWidth, int originalHeight, int decodedWidth, int decodedHeight, bool flip = true)
        {
            int decodedStride = decodedWidth * 3;
            int startIndex = (decodedHeight - originalHeight) * decodedStride;
            int wbStride = originalWidth * 3;
            int wbIndex = flip ? wbStride * (originalHeight - 1) : 0;
            int wbFlip = flip ? -1 : 1;
            for (int i = 0; i < originalHeight; i++)
            {
                Buffer.BlockCopy(
                   source,
                   startIndex + i * decodedStride,
                   dest,
                   wbIndex + i * wbFlip * wbStride,
                   wbStride
                );
            }
        }
    }
}
