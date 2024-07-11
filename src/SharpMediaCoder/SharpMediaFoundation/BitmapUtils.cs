using System.Runtime.InteropServices;

namespace SharpMediaFoundation
{
    public static class BitmapUtils
    {
        public static void CopyBitmap(byte[] decoded, nint backBuffer, int originalWidth, int originalHeight, int decodedWidth, int decodedHeight, bool flip = true)
        {
            int decodedStride = decodedWidth * 3;
            int startIndex = (decodedHeight - originalHeight) * decodedStride;
            int wbStride = originalWidth * 3;
            int wbIndex = flip ? wbStride * (originalHeight - 1) : 0;
            int wbDelta = flip ? -1 * wbStride : wbStride;
            for (int i = 0; i < originalHeight; i++)
            {
                Marshal.Copy(
                   decoded,
                   startIndex + i * decodedStride,
                   backBuffer + wbIndex + i * wbDelta,
                   wbStride
                );
            }
        }
    }
}
