using System;
using System.Runtime.Versioning;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>
    /// Copies the pictures of GStreamer's samples into the library's: rows of no padding, of the size it says. The strides
    /// and plane offsets are a buffer's GstVideoMeta's, where it has one - a hardware decoder's buffers often do - and
    /// GStreamer's defaults where not: rows of a multiple of 4 bytes, the chroma plane after the luma rounded to even rows.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal static unsafe class GstFrames
    {
        /// <summary>The size of the sample's picture, of its caps; 0 by 0 where they do not say.</summary>
        public static (int Width, int Height) Size(IntPtr sample)
        {
            IntPtr caps = Gst.gst_sample_get_caps(sample);
            if (caps == IntPtr.Zero)
                return (0, 0);
            return (Gst.GetCapsInt(caps, "width") ?? 0, Gst.GetCapsInt(caps, "height") ?? 0);
        }

        /// <summary>
        /// Copies the sample's NV12 picture into the top left of the buffer's <paramref name="width"/> by
        /// <paramref name="height"/>: the luma, then the interleaved chroma. A picture larger is cut at its edges. Its size, out.
        /// </summary>
        public static bool CopyNV12(IntPtr sample, Span<byte> destination, uint width, uint height, out int frameWidth, out int frameHeight)
        {
            (frameWidth, frameHeight) = Size(sample);
            IntPtr buffer = Gst.gst_sample_get_buffer(sample);
            if (buffer == IntPtr.Zero || frameWidth == 0 || frameHeight == 0)
                return false;

            Span<int> strides = stackalloc int[4];
            Span<long> offsets = stackalloc long[4];
            if (!Gst.GetVideoLayout(buffer, strides, offsets))
            {
                strides[0] = strides[1] = (frameWidth + 3) & ~3;
                offsets[0] = 0;
                offsets[1] = (long)strides[0] * ((frameHeight + 1) & ~1);
            }

            if (!Gst.Map(buffer, out var info))
                return false;
            try
            {
                for (int plane = 0; plane < 2; plane++)
                {
                    int rows = Math.Min(plane == 0 ? frameHeight : (frameHeight + 1) / 2, (int)(plane == 0 ? height : height / 2));
                    int rowBytes = Math.Min(plane == 0 ? frameWidth : (frameWidth + 1) / 2 * 2, (int)width);
                    var target = destination.Slice(plane == 0 ? 0 : (int)(width * height));
                    for (int row = 0; row < rows; row++)
                    {
                        long from = offsets[plane] + (long)row * strides[plane];
                        if (from + rowBytes > (long)info.Size)
                            break;
                        new ReadOnlySpan<byte>(info.Data + from, rowBytes).CopyTo(target.Slice(row * (int)width, rowBytes));
                    }
                }
                return true;
            }
            finally
            {
                Gst.Unmap(buffer, ref info);
            }
        }

        /// <summary>
        /// Copies the sample's BGRA picture into the buffer's <paramref name="width"/> by <paramref name="height"/>, top-down,
        /// or bottom-up - the first row the bottom one.
        /// </summary>
        public static bool CopyBgra(IntPtr sample, Span<byte> destination, uint width, uint height, bool bottomUp)
        {
            var (frameWidth, frameHeight) = Size(sample);
            IntPtr buffer = Gst.gst_sample_get_buffer(sample);
            if (buffer == IntPtr.Zero || frameWidth == 0 || frameHeight == 0)
                return false;

            Span<int> strides = stackalloc int[4];
            Span<long> offsets = stackalloc long[4];
            if (!Gst.GetVideoLayout(buffer, strides, offsets))
            {
                strides[0] = frameWidth * 4;
                offsets[0] = 0;
            }

            if (!Gst.Map(buffer, out var info))
                return false;
            try
            {
                int rows = Math.Min(frameHeight, (int)height);
                int rowBytes = Math.Min(frameWidth, (int)width) * 4;
                for (int row = 0; row < rows; row++)
                {
                    long from = offsets[0] + (long)row * strides[0];
                    if (from + rowBytes > (long)info.Size)
                        break;
                    int targetRow = bottomUp ? (int)height - 1 - row : row;
                    new ReadOnlySpan<byte>(info.Data + from, rowBytes).CopyTo(destination.Slice(targetRow * (int)width * 4, rowBytes));
                }
                return true;
            }
            finally
            {
                Gst.Unmap(buffer, ref info);
            }
        }

        /// <summary>The bytes of the sample's buffer, copied.</summary>
        public static byte[] Bytes(IntPtr sample)
        {
            IntPtr buffer = Gst.gst_sample_get_buffer(sample);
            return buffer == IntPtr.Zero ? Array.Empty<byte>() : Gst.ToArray(buffer);
        }

        /// <summary>The time of the sample's buffer, in 100 ns ticks; 0 where it has none.</summary>
        public static long Time(IntPtr sample)
        {
            IntPtr buffer = Gst.gst_sample_get_buffer(sample);
            return buffer == IntPtr.Zero ? 0 : Gst.GetTime(buffer) ?? 0;
        }
    }
}
