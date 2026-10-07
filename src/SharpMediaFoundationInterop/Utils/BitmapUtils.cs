using System;
using System.Runtime.InteropServices;

namespace SharpMediaFoundationInterop.Utils
{
    public static class BitmapUtils
    {
        public static void CopyBitmap(byte[] source, int sourceWidth, int sourceHeight, nint target, int targetWidth, int targetHeight, int bytesPerPixel = 3, bool flip = true)
        {
            CopyPixels(source, 0, sourceWidth, sourceHeight, target, 0, targetWidth, targetHeight, bytesPerPixel, flip, true);
        }

        public static void CopyBitmap(nint source, int sourceWidth, int sourceHeight, byte[] target, int targetWidth, int targetHeight, int bytesPerPixel = 3, bool flip = true)
        {
            CopyPixels(source, 0, sourceWidth, sourceHeight, target, 0, targetWidth, targetHeight, bytesPerPixel, flip, true);
        }

        public static void CopyBitmap(byte[] source, int sourceWidth, int sourceHeight, byte[] target, int targetWidth, int targetHeight, int bytesPerPixel = 3, bool flip = false)
        {
            CopyPixels(source, 0, sourceWidth, sourceHeight, target, 0, targetWidth, targetHeight, bytesPerPixel, flip, true);
        }

        /// <summary>
        /// Converts 32 bit BGRA - as a screen capture hands it out - to NV12, as an encoder takes it, of BT.709's limited range:
        /// each pixel's luma, and the chroma of each 2 by 2 block, averaged. Of <paramref name="bottomUp"/> rows, the first
        /// row in is the bottom one. Where there is no color converter of the system's - Media Foundation's - to do it.
        /// </summary>
        public static void ConvertBgraToNV12(ReadOnlySpan<byte> bgra, int width, int height, bool bottomUp, Span<byte> nv12)
        {
            if (bgra.Length < width * height * 4)
                throw new ArgumentException($"A BGRA frame of {width}x{height} is of {width * height * 4} bytes", nameof(bgra));
            int chromaWidth = (width + 1) / 2, chromaHeight = (height + 1) / 2;
            if (nv12.Length < width * height + chromaWidth * chromaHeight * 2)
                throw new ArgumentException($"An NV12 frame of {width}x{height} is of {width * height + chromaWidth * chromaHeight * 2} bytes", nameof(nv12));

            // BT.709 in 8 bits, of 16 to 235 and 16 to 240, its coefficients scaled by 256
            for (int y = 0; y < height; y++)
            {
                var row = bgra.Slice((bottomUp ? height - 1 - y : y) * width * 4, width * 4);
                var luma = nv12.Slice(y * width, width);
                for (int x = 0; x < width; x++)
                {
                    int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                    luma[x] = (byte)(((47 * r + 157 * g + 16 * b + 128) >> 8) + 16);
                }
            }

            var chroma = nv12.Slice(width * height);
            for (int cy = 0; cy < chromaHeight; cy++)
            {
                for (int cx = 0; cx < chromaWidth; cx++)
                {
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int dy = 0; dy < 2; dy++)
                    {
                        int y = cy * 2 + dy;
                        if (y >= height)
                            continue;
                        int rowStart = (bottomUp ? height - 1 - y : y) * width * 4;
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int x = cx * 2 + dx;
                            if (x >= width)
                                continue;
                            int i = rowStart + x * 4;
                            b += bgra[i];
                            g += bgra[i + 1];
                            r += bgra[i + 2];
                            n++;
                        }
                    }
                    r /= n; g /= n; b /= n;
                    chroma[(cy * chromaWidth + cx) * 2] = (byte)(((-26 * r - 87 * g + 112 * b + 128) >> 8) + 128);
                    chroma[(cy * chromaWidth + cx) * 2 + 1] = (byte)(((112 * r - 102 * g - 10 * b + 128) >> 8) + 128);
                }
            }
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
            nint source,
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
                Marshal.Copy(
                   source + sourceOffset + sourceStartIndex + i * sourceStride,
                   target,
                   targetOffset + targetStartIndex + i * targetFlip * targetStride,
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
