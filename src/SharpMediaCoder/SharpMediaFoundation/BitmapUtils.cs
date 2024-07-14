using System;
using System.Runtime.InteropServices;

namespace SharpMediaFoundation
{
    public static class BitmapUtils
    {
        public static void CopyBitmap(byte[] source, int sourceWidth, int sourceHeight, nint target, int targetWidth, int targetHeight, int bytesPerPixel = 3, bool flip = true)
        {
            CopyPixels(source, 0, sourceWidth, sourceHeight, target, 0, targetWidth, targetHeight, bytesPerPixel, flip, true);
        }

        public static void CopyBitmap(byte[] source, int sourceWidth, int sourceHeight, byte[] target, int targetWidth, int targetHeight, int bytesPerPixel = 3, bool flip = false)
        {
            CopyPixels(source, 0, sourceWidth, sourceHeight, target, 0, targetWidth, targetHeight, bytesPerPixel, flip, true);
        }

        // https://learn.microsoft.com/en-us/answers/questions/1134688/media-foundation-wrong-size-for-video
        public static void CopyNV12Bitmap(byte[] source, int sourceWidth, int sourceHeight, byte[] target, int targetWidth, int targetHeight, bool flip = false)
        {
            // NV12 layout:
            /*
            Y0 Y1 Y2 Y3
            ...
            U0 V0 U1 V1
            ...
            */
            // copy luma (y)
            CopyPixels(
                source,
                0, 
                sourceWidth, 
                sourceHeight,
                target, 
                0, 
                targetWidth,
                targetHeight,
                1, 
                flip,
                false);
            // copy chroma (u, v)
            CopyPixels(
                source, 
                sourceWidth * sourceHeight, 
                sourceWidth / 2, 
                sourceHeight / 2, 
                target, 
                targetWidth * targetHeight, 
                targetWidth / 2, 
                targetHeight / 2, 
                2, 
                flip,
                false);
        }

        private static void CopyPixels(
            byte[] source,
            int sourceOffset,
            int sourceWidth,
            int sourceHeight,
            nint target,
            int targetOffset,
            int targetWidth,
            int targetHeight,
            int bytesPerPixel = 1,
            bool flip = false,
            bool skipTop = true)
        {
            int sourceStride = sourceWidth * bytesPerPixel;
            int sourceStartIndex = skipTop ? (sourceHeight - targetHeight) * sourceStride : 0;
            int targetStride = targetWidth * bytesPerPixel;
            int targetStartIndex = flip ? targetStride * (targetHeight - 1) : 0;
            int targetFlip = flip ? -1 : 1;
            for (int i = 0; i < targetHeight; i++)
            {
                Marshal.Copy(
                   source,
                   sourceOffset + sourceStartIndex + i * sourceStride,
                   target + targetOffset + targetStartIndex + i * targetFlip * targetStride,
                   targetStride
                );
            }
        }

        private static void CopyPixels(
            byte[] source,
            int sourceOffset,
            int sourceWidth,
            int sourceHeight,
            byte[] target,
            int targetOffset,
            int targetWidth,
            int targetHeight,
            int bytesPerPixel = 1,
            bool flip = false,
            bool skipTop = true)
        {
            int sourceStride = sourceWidth * bytesPerPixel;
            int sourceStartIndex = skipTop ? (sourceHeight - targetHeight) * sourceStride : 0;
            int targetStride = targetWidth * bytesPerPixel;
            int targetStartIndex = flip ? targetStride * (targetHeight - 1) : 0;
            int targetFlip = flip ? -1 : 1;
            for (int i = 0; i < targetHeight; i++)
            {
                Buffer.BlockCopy(
                   source,
                   sourceOffset + sourceStartIndex + i * sourceStride,
                   target,
                   targetOffset + targetStartIndex + i * targetFlip * targetStride,
                   targetStride
                );
            }
        }
    }
}
