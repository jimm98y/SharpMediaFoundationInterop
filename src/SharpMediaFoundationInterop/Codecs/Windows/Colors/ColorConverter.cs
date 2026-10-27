using System.Runtime.Versioning;
using System;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// Converts among different color formats, of Media Foundation's video processor. Made of a size alone, it takes the
    /// video processor's own color: BT.601's limited range below 720 lines, BT.709's from 720, and RGB bottom-up. Made of
    /// <see cref="ColorConverterOptions"/>, it is told the YUV side's range and the RGB side's row order, which it keeps to;
    /// the matrix it is not told, as the video processor does not keep to what it is told of it - of 720 lines and more it
    /// is BT.709's whatever it is told, and below them BT.601's told is BT.709's - so it is made only of the matrix it takes
    /// of itself: see <see cref="Supports"/>.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public class ColorConverter : VideoTransformBase
    {
        private Guid _inputFormat;
        private Guid _outputFormat;
        private readonly ColorConverterOptions _options;

        public override Guid InputFormat => _inputFormat;
        public override Guid OutputFormat => _outputFormat;

        public ColorConverter(Guid inputFormat, Guid outputFormat, uint width, uint height) : base(width, height)
        {
            _inputFormat = inputFormat;
            _outputFormat = outputFormat;
        }

        public ColorConverter(Guid inputFormat, Guid outputFormat, ColorConverterOptions options)
            : base(options?.Width ?? throw new ArgumentNullException(nameof(options)), options.Height)
        {
            ColorFormats.Validate(inputFormat, outputFormat, options);
            if (!Supports(inputFormat, outputFormat, options))
                throw new NotSupportedException($"Media Foundation's video processor does not convert {inputFormat} to {outputFormat} of {ColorFormats.Matrix(options)} at {options.Height} lines");
            _inputFormat = inputFormat;
            _outputFormat = outputFormat;
            _options = options;
        }

        /// <summary>
        /// Whether the video processor converts the one format to the other as the options say: of YUV and RGB, of the
        /// matrix it takes of itself - BT.601's below 720 lines, BT.709's from 720. Of UYVY none, whose type it refuses, and
        /// of YUY2 to RGB32 or ARGB32 none, as it hands out white.
        /// </summary>
        public static bool Supports(Guid inputFormat, Guid outputFormat, ColorConverterOptions options)
        {
            if (!ColorFormats.IsKnown(inputFormat) || !ColorFormats.IsKnown(outputFormat))
                return false;
            if (inputFormat == MediaFormats.UYVY || outputFormat == MediaFormats.UYVY)
                return false;
            if (inputFormat == MediaFormats.YUY2 && (outputFormat == MediaFormats.RGB32 || outputFormat == MediaFormats.ARGB32))
                return false;
            bool ofMatrix = ColorFormats.IsYuv(inputFormat) != ColorFormats.IsYuv(outputFormat);
            var own = options.Height >= 720 ? ColorMatrix.Bt709 : ColorMatrix.Bt601;
            return !ofMatrix || ColorFormats.Matrix(options) == own;
        }

        protected override IMFTransform Create()
        {
            const int streamId = 0;

            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = InputFormat };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = OutputFormat };

            IMFTransform transform = CreateTransform(PInvoke.MFT_CATEGORY_VIDEO_PROCESSOR, MFT_ENUM_FLAG.MFT_ENUM_FLAG_ALL, input, output);
            if (transform == null) throw new NotSupportedException($"Unsupported transform! Input: {InputFormat}, Output: {OutputFormat}");

            IMFMediaType mediaInput;
            MediaUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
            mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, InputFormat);
            mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MediaUtils.EncodeAttributeValue(Width, Height));
            SetColor(mediaInput, InputFormat);
            MediaUtils.Check(transform.SetInputType(streamId, mediaInput, 0));

            IMFMediaType mediaOutput;
            MediaUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
            mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, OutputFormat);
            mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MediaUtils.EncodeAttributeValue(Width, Height));
            SetColor(mediaOutput, OutputFormat);
            MediaUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));

            return transform;
        }

        /// <summary>
        /// Of the options: a YUV type's range; an RGB type's stride, of its row order - negative of bottom-up rows, the first
        /// the picture's bottom one.
        /// </summary>
        private void SetColor(IMFMediaType type, Guid format)
        {
            if (_options == null)
                return;
            if (ColorFormats.IsYuv(format))
            {
                type.SetUINT32(PInvoke.MF_MT_VIDEO_NOMINAL_RANGE, (uint)(_options.FullRange ? MFNominalRange.MFNominalRange_0_255 : MFNominalRange.MFNominalRange_16_235));
            }
            else if (ColorFormats.IsRgb(format))
            {
                int stride = (int)Width * ColorFormats.BytesPerPixel(format);
                type.SetUINT32(PInvoke.MF_MT_DEFAULT_STRIDE, (uint)(_options.TopDown ? stride : -stride));
            }
        }
    }
}
