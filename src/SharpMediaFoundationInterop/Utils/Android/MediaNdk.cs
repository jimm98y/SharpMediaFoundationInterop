using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>
    /// The NDK's media library, libmediandk: AMediaCodec, the device's codecs - hardware ones where it has them - and
    /// AImageReader, which a decoder renders into so that each frame comes with the layout of its planes, as the WPF fork's
    /// Android head decodes. No JNI: every call is a C entry point of the NDK's, of API 26 or later.
    /// </summary>
    [SupportedOSPlatform("android26.0")]
    internal static unsafe class MediaNdk
    {
        private const string Lib = "libmediandk.so";

        public const int AMEDIA_OK = 0;
        public const int CONFIGURE_FLAG_ENCODE = 1;
        public const uint BUFFER_FLAG_KEY_FRAME = 1;
        public const uint BUFFER_FLAG_CODEC_CONFIG = 2;
        public const uint BUFFER_FLAG_END_OF_STREAM = 4;
        public const int INFO_TRY_AGAIN_LATER = -1;
        public const int INFO_OUTPUT_FORMAT_CHANGED = -2;
        public const int INFO_OUTPUT_BUFFERS_CHANGED = -3;
        public const int AIMAGE_FORMAT_YUV_420_888 = 0x23;

        /// <summary>MediaCodecInfo.CodecCapabilities' COLOR_FormatYUV420SemiPlanar: NV12, which encoders take.</summary>
        public const int COLOR_FormatYUV420SemiPlanar = 21;

        /// <summary>AudioFormat's encodings, of "pcm-encoding".</summary>
        public const int ENCODING_PCM_16BIT = 2;
        public const int ENCODING_PCM_FLOAT = 4;
        public const int ENCODING_PCM_24BIT_PACKED = 21;

        [StructLayout(LayoutKind.Sequential)]
        public struct AMediaCodecBufferInfo
        {
            public int Offset;
            public int Size;
            public long PresentationTimeUs;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AImageCropRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private static bool? _available;

        /// <summary>Whether the NDK's media library is here: of Android 8 or later, as AAudio is.</summary>
        public static bool IsAvailable => _available ??= NativeLibrary.TryLoad(Lib, out _);

        #region AMediaFormat

        [DllImport(Lib)] public static extern IntPtr AMediaFormat_new();
        [DllImport(Lib)] public static extern int AMediaFormat_delete(IntPtr format);
        [DllImport(Lib)] public static extern void AMediaFormat_setString(IntPtr format, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(Lib)] public static extern void AMediaFormat_setInt32(IntPtr format, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int value);
        [DllImport(Lib)] public static extern void AMediaFormat_setInt64(IntPtr format, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, long value);
        [DllImport(Lib)] public static extern void AMediaFormat_setFloat(IntPtr format, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, float value);
        [DllImport(Lib)] public static extern void AMediaFormat_setBuffer(IntPtr format, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, void* data, nuint size);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool AMediaFormat_getInt32(IntPtr format, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, out int value);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool AMediaFormat_getBuffer(IntPtr format, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, out void* data, out nuint size);

        public static void SetBuffer(IntPtr format, string name, ReadOnlySpan<byte> data)
        {
            fixed (byte* p = data)
                AMediaFormat_setBuffer(format, name, p, (nuint)data.Length);
        }

        #endregion

        #region AMediaCodec

        [DllImport(Lib)] public static extern IntPtr AMediaCodec_createDecoderByType([MarshalAs(UnmanagedType.LPUTF8Str)] string mimeType);
        [DllImport(Lib)] public static extern IntPtr AMediaCodec_createEncoderByType([MarshalAs(UnmanagedType.LPUTF8Str)] string mimeType);
        [DllImport(Lib)] public static extern int AMediaCodec_configure(IntPtr codec, IntPtr format, IntPtr surface, IntPtr crypto, uint flags);
        [DllImport(Lib)] public static extern int AMediaCodec_start(IntPtr codec);
        [DllImport(Lib)] public static extern int AMediaCodec_stop(IntPtr codec);
        [DllImport(Lib)] public static extern int AMediaCodec_flush(IntPtr codec);
        [DllImport(Lib)] public static extern int AMediaCodec_delete(IntPtr codec);
        [DllImport(Lib)] public static extern nint AMediaCodec_dequeueInputBuffer(IntPtr codec, long timeoutUs);
        [DllImport(Lib)] public static extern byte* AMediaCodec_getInputBuffer(IntPtr codec, nuint index, out nuint size);
        [DllImport(Lib)] public static extern int AMediaCodec_queueInputBuffer(IntPtr codec, nuint index, nint offset, nuint size, ulong timeUs, uint flags);
        [DllImport(Lib)] public static extern nint AMediaCodec_dequeueOutputBuffer(IntPtr codec, AMediaCodecBufferInfo* info, long timeoutUs);
        [DllImport(Lib)] public static extern byte* AMediaCodec_getOutputBuffer(IntPtr codec, nuint index, out nuint size);
        [DllImport(Lib)] public static extern IntPtr AMediaCodec_getOutputFormat(IntPtr codec);
        [DllImport(Lib)] public static extern int AMediaCodec_releaseOutputBuffer(IntPtr codec, nuint index, [MarshalAs(UnmanagedType.I1)] bool render);

        #endregion

        #region AImageReader

        [DllImport(Lib)] public static extern int AImageReader_new(int width, int height, int format, int maxImages, out IntPtr reader);
        [DllImport(Lib)] public static extern void AImageReader_delete(IntPtr reader);
        [DllImport(Lib)] public static extern int AImageReader_getWindow(IntPtr reader, out IntPtr window);
        [DllImport(Lib)] public static extern int AImageReader_acquireNextImage(IntPtr reader, out IntPtr image);
        [DllImport(Lib)] public static extern int AImageReader_acquireLatestImage(IntPtr reader, out IntPtr image);
        [DllImport(Lib)] public static extern void AImage_delete(IntPtr image);
        [DllImport(Lib)] public static extern int AImage_getTimestamp(IntPtr image, out long timestampNs);
        [DllImport(Lib)] public static extern int AImage_getWidth(IntPtr image, out int width);
        [DllImport(Lib)] public static extern int AImage_getHeight(IntPtr image, out int height);
        [DllImport(Lib)] public static extern int AImage_getCropRect(IntPtr image, out AImageCropRect rect);
        [DllImport(Lib)] public static extern int AImage_getPlaneData(IntPtr image, int planeIndex, out byte* data, out int length);
        [DllImport(Lib)] public static extern int AImage_getPlaneRowStride(IntPtr image, int planeIndex, out int rowStride);
        [DllImport(Lib)] public static extern int AImage_getPlanePixelStride(IntPtr image, int planeIndex, out int pixelStride);

        #endregion

        /// <summary>
        /// Copies an AImage of YUV_420_888 - three planes, each of its own row stride, the chroma ones of a pixel stride of 1,
        /// planar, or 2, interleaved - into NV12 of <paramref name="width"/> by <paramref name="height"/>, its crop rectangle's
        /// picture in the top left. Its size, out.
        /// </summary>
        public static bool CopyNV12(IntPtr image, Span<byte> destination, uint width, uint height, out int frameWidth, out int frameHeight)
        {
            frameWidth = frameHeight = 0;
            if (AImage_getCropRect(image, out var crop) != AMEDIA_OK)
            {
                AImage_getWidth(image, out int w);
                AImage_getHeight(image, out int h);
                crop = new AImageCropRect { Right = w, Bottom = h };
            }
            frameWidth = crop.Right - crop.Left;
            frameHeight = crop.Bottom - crop.Top;
            if (frameWidth <= 0 || frameHeight <= 0)
                return false;

            if (AImage_getPlaneData(image, 0, out byte* y, out _) != AMEDIA_OK ||
                AImage_getPlaneData(image, 1, out byte* u, out _) != AMEDIA_OK ||
                AImage_getPlaneData(image, 2, out byte* v, out _) != AMEDIA_OK)
                return false;
            AImage_getPlaneRowStride(image, 0, out int yStride);
            AImage_getPlaneRowStride(image, 1, out int uvStride);
            AImage_getPlanePixelStride(image, 1, out int uvPixel);

            int rows = Math.Min(frameHeight, (int)height);
            int columns = Math.Min(frameWidth, (int)width);
            for (int row = 0; row < rows; row++)
            {
                byte* from = y + (long)(crop.Top + row) * yStride + crop.Left;
                new ReadOnlySpan<byte>(from, columns).CopyTo(destination.Slice(row * (int)width, columns));
            }

            var chroma = destination.Slice((int)(width * height));
            int chromaRows = Math.Min((frameHeight + 1) / 2, (int)height / 2);
            int chromaColumns = Math.Min((frameWidth + 1) / 2, (int)width / 2);
            for (int row = 0; row < chromaRows; row++)
            {
                long offset = (long)(crop.Top / 2 + row) * uvStride + (long)(crop.Left / 2) * uvPixel;
                var target = chroma.Slice(row * (int)width, chromaColumns * 2);
                if (uvPixel == 2 && v == u + 1)
                {
                    // already interleaved, NV12's own order
                    new ReadOnlySpan<byte>(u + offset, chromaColumns * 2).CopyTo(target);
                }
                else
                {
                    for (int column = 0; column < chromaColumns; column++)
                    {
                        target[column * 2] = u[offset + column * uvPixel];
                        target[column * 2 + 1] = v[offset + column * uvPixel];
                    }
                }
            }
            return true;
        }
    }
}
