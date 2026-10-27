using System;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>The matrix of a YUV picture's luma and chroma, of the RGB they are made of.</summary>
    public enum ColorMatrix
    {
        /// <summary>BT.709's of 720 lines and more, else BT.601's: what a video that says nothing is taken to be.</summary>
        Default,

        /// <summary>ITU-R BT.601: of standard definition.</summary>
        Bt601,

        /// <summary>ITU-R BT.709: of high definition.</summary>
        Bt709,

        /// <summary>ITU-R BT.2020, of non-constant luminance: of UHD and HDR.</summary>
        Bt2020,
    }

    /// <summary>What a color converter of <see cref="MediaCodecs.CreateColorConverter"/> is made for.</summary>
    public sealed class ColorConverterOptions
    {
        /// <summary>The picture's width: of the frames in and out alike, of rows of no padding.</summary>
        public uint Width { get; set; }

        /// <summary>The picture's height.</summary>
        public uint Height { get; set; }

        /// <summary>The matrix of the YUV side, in or out.</summary>
        public ColorMatrix Matrix { get; set; }

        /// <summary>Whether the YUV side is of the full range, 0 to 255, not the limited, 16 to 235 and 16 to 240.</summary>
        public bool FullRange { get; set; }

        /// <summary>
        /// Whether the RGB side's rows are top-down, as a bitmap's are; else bottom-up - its first row the picture's bottom
        /// one - as Media Foundation's RGB formats are, and as the screen captures hand them out by default.
        /// </summary>
        public bool TopDown { get; set; }
    }

    /// <summary>The formats a color converter takes and hands out, and their sizes.</summary>
    internal static class ColorFormats
    {
        /// <summary>Whether the format is one a color converter knows: NV12, YUY2, UYVY, RGB24, RGB32 or ARGB32.</summary>
        public static bool IsKnown(Guid format) => IsYuv(format) || IsRgb(format);

        public static bool IsYuv(Guid format) => format == MediaFormats.NV12 || format == MediaFormats.YUY2 || format == MediaFormats.UYVY;

        public static bool IsRgb(Guid format) => format == MediaFormats.RGB24 || format == MediaFormats.RGB32 || format == MediaFormats.ARGB32;

        /// <summary>The bytes of a pixel of an RGB format: 3 of RGB24, B, G and R; 4 of RGB32 and ARGB32, B, G, R and alpha.</summary>
        public static int BytesPerPixel(Guid format) => format == MediaFormats.RGB24 ? 3 : 4;

        /// <summary>The bytes of a frame of the format, of rows of no padding: of NV12 its chroma plane after its luma.</summary>
        public static uint FrameSize(Guid format, uint width, uint height) =>
            format == MediaFormats.NV12 ? width * height * 3 / 2 :
            format == MediaFormats.YUY2 || format == MediaFormats.UYVY ? width * height * 2 :
            width * height * (uint)BytesPerPixel(format);

        /// <summary>The matrix the options say, <see cref="ColorMatrix.Default"/> made BT.601's or BT.709's of the height.</summary>
        public static ColorMatrix Matrix(ColorConverterOptions options) =>
            options.Matrix != ColorMatrix.Default ? options.Matrix : options.Height >= 720 ? ColorMatrix.Bt709 : ColorMatrix.Bt601;

        /// <summary>Throws where the options or the formats are not of a conversion: a size of YUV's chroma is even.</summary>
        public static void Validate(Guid inputFormat, Guid outputFormat, ColorConverterOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (options.Width == 0 || options.Height == 0)
                throw new ArgumentException("The picture's size is to be given", nameof(options));
            if (!IsKnown(inputFormat))
                throw new NotSupportedException($"No color conversion of {inputFormat}");
            if (!IsKnown(outputFormat))
                throw new NotSupportedException($"No color conversion to {outputFormat}");
            if ((IsYuv(inputFormat) || IsYuv(outputFormat)) && (options.Width % 2 != 0 || (options.Height % 2 != 0 && (inputFormat == MediaFormats.NV12 || outputFormat == MediaFormats.NV12))))
                throw new ArgumentException($"A YUV picture's chroma is of pairs of pixels: {options.Width}x{options.Height} is not of them", nameof(options));
        }
    }
}
