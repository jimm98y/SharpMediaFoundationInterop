using System;
using System.Runtime.Versioning;
using static SharpMediaFoundationInterop.Utils.AppleNative;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>Copies the pictures of Core Video's pixel buffers into the library's: rows of no padding, of the size it says.</summary>
    [SupportedOSPlatform("macos11.0")]
    [SupportedOSPlatform("ios14.0")]
    internal static unsafe class PixelBuffers
    {
        /// <summary>
        /// Copies an NV12 frame - of 420v or 420f - into the top left of the buffer's <paramref name="width"/> by
        /// <paramref name="height"/>: the luma, then the interleaved chroma. A frame larger is cut at its edges. False of a frame
        /// of any other format; its size, either way.
        /// </summary>
        public static bool CopyNV12(IntPtr pixelBuffer, Span<byte> buffer, uint width, uint height, out int frameWidth, out int frameHeight)
        {
            frameWidth = (int)CVPixelBufferGetWidth(pixelBuffer);
            frameHeight = (int)CVPixelBufferGetHeight(pixelBuffer);
            uint format = CVPixelBufferGetPixelFormatType(pixelBuffer);
            if (format != kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange && format != kCVPixelFormatType_420YpCbCr8BiPlanarFullRange)
                return false;
            if (buffer.Length < width * height * 3 / 2)
                throw new ArgumentException($"The buffer is smaller than an NV12 frame of {width}x{height}", nameof(buffer));

            if (CVPixelBufferLockBaseAddress(pixelBuffer, kCVPixelBufferLock_ReadOnly) != 0)
                return false;
            try
            {
                fixed (byte* destination = buffer)
                {
                    for (nuint plane = 0; plane < 2; plane++)
                    {
                        byte* source = (byte*)CVPixelBufferGetBaseAddressOfPlane(pixelBuffer, plane);
                        int stride = (int)CVPixelBufferGetBytesPerRowOfPlane(pixelBuffer, plane);
                        // of the chroma plane, the width is of its pairs of bytes
                        int rowBytes = (int)CVPixelBufferGetWidthOfPlane(pixelBuffer, plane) * (plane == 0 ? 1 : 2);
                        int rows = (int)CVPixelBufferGetHeightOfPlane(pixelBuffer, plane);
                        int destinationRows = (int)(plane == 0 ? height : height / 2);
                        byte* target = destination + (plane == 0 ? 0 : width * height);

                        int copyBytes = Math.Min(rowBytes, (int)width);
                        int copyRows = Math.Min(rows, destinationRows);
                        for (int row = 0; row < copyRows; row++)
                            Buffer.MemoryCopy(source + row * stride, target + row * width, width, copyBytes);
                    }
                }
                return true;
            }
            finally
            {
                CVPixelBufferUnlockBaseAddress(pixelBuffer, kCVPixelBufferLock_ReadOnly);
            }
        }

        /// <summary>
        /// Copies an NV12 frame of <paramref name="width"/> by <paramref name="height"/>, of rows of no padding, into a pixel
        /// buffer of 420v of that size: the luma, then the interleaved chroma, each row to the buffer's own stride.
        /// </summary>
        public static void FillNV12(IntPtr pixelBuffer, ReadOnlySpan<byte> frame, uint width, uint height)
        {
            if (CVPixelBufferLockBaseAddress(pixelBuffer, 0) != 0)
                throw new InvalidOperationException("The pixel buffer cannot be written");
            try
            {
                fixed (byte* source = frame)
                {
                    for (nuint plane = 0; plane < 2; plane++)
                    {
                        byte* target = (byte*)CVPixelBufferGetBaseAddressOfPlane(pixelBuffer, plane);
                        int stride = (int)CVPixelBufferGetBytesPerRowOfPlane(pixelBuffer, plane);
                        int rows = Math.Min((int)CVPixelBufferGetHeightOfPlane(pixelBuffer, plane), (int)(plane == 0 ? height : (height + 1) / 2));
                        int rowBytes = Math.Min(stride, (int)width);
                        byte* from = source + (plane == 0 ? 0 : width * height);
                        for (int row = 0; row < rows; row++)
                            Buffer.MemoryCopy(from + row * width, target + row * stride, stride, rowBytes);
                    }
                }
            }
            finally
            {
                CVPixelBufferUnlockBaseAddress(pixelBuffer, 0);
            }
        }

        /// <summary>
        /// Copies a 32 bit BGRA frame into the buffer's <paramref name="width"/> by <paramref name="height"/>, top-down, or
        /// bottom-up - its first row the bottom one. False of a frame of any other format.
        /// </summary>
        public static bool CopyBgra(IntPtr pixelBuffer, Span<byte> buffer, uint width, uint height, bool bottomUp)
        {
            if (CVPixelBufferGetPixelFormatType(pixelBuffer) != kCVPixelFormatType_32BGRA)
                return false;
            if (buffer.Length < width * height * 4)
                throw new ArgumentException($"The buffer is smaller than a BGRA frame of {width}x{height}", nameof(buffer));

            if (CVPixelBufferLockBaseAddress(pixelBuffer, kCVPixelBufferLock_ReadOnly) != 0)
                return false;
            try
            {
                byte* source = (byte*)CVPixelBufferGetBaseAddress(pixelBuffer);
                int stride = (int)CVPixelBufferGetBytesPerRow(pixelBuffer);
                int rows = Math.Min((int)CVPixelBufferGetHeight(pixelBuffer), (int)height);
                int rowBytes = Math.Min((int)CVPixelBufferGetWidth(pixelBuffer), (int)width) * 4;
                int targetStride = (int)width * 4;
                fixed (byte* destination = buffer)
                {
                    for (int row = 0; row < rows; row++)
                    {
                        int targetRow = bottomUp ? (int)height - 1 - row : row;
                        Buffer.MemoryCopy(source + row * stride, destination + targetRow * targetStride, targetStride, rowBytes);
                    }
                }
                return true;
            }
            finally
            {
                CVPixelBufferUnlockBaseAddress(pixelBuffer, kCVPixelBufferLock_ReadOnly);
            }
        }
    }
}
