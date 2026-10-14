using System;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// Motion JPEG decoded by Windows' MJPEG decoder: each sample a JPEG picture, coded on its own, and handed out as it
    /// goes in. NV12 out, or YUY2 - of a picture of 4:2:2, as cameras' are, its chroma as it is.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public class MjpegDecoder : VideoTransformBase
    {
        /// <summary>Its frames are of the pictures' own size.</summary>
        public const uint MJPEG_RES_MULTIPLE = 1;

        public override Guid InputFormat => MediaFormats.MJPG;
        public override Guid OutputFormat => MediaFoundationDecoder.SubtypeOf(_format);

        private readonly VideoFrameFormat _format;

        protected override uint MinimumOutputSize => VideoFrameFormats.FrameSize(_format, Width, Height);

        public MjpegDecoder(uint width, uint height, uint fpsNom, uint fpsDenom, VideoFrameFormat format = VideoFrameFormat.NV12)
          : base(MJPEG_RES_MULTIPLE, width, height, fpsNom, fpsDenom)
        {
            if (format != VideoFrameFormat.NV12 && format != VideoFrameFormat.YUY2)
                throw new NotSupportedException($"Windows' MJPEG decoder hands out NV12 and YUY2, not {format}");
            _format = format;
        }

        protected override IMFTransform Create()
        {
            const uint streamId = 0;

            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = InputFormat };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = OutputFormat };

            IMFTransform transform = CreateTransform(PInvoke.MFT_CATEGORY_VIDEO_DECODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT, input, output);
            if (transform == null) throw new NotSupportedException($"Unsupported transform! Input: {InputFormat}, Output: {OutputFormat}");

            IMFMediaType mediaInput;
            MediaUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
            mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, InputFormat);
            mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MediaUtils.EncodeAttributeValue(Width, Height));
            if (HasFrameRate) // a hint, given only where the stream says it
                mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MediaUtils.EncodeAttributeValue(FpsNom, FpsDenom));
            mediaInput.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
            mediaInput.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, MediaUtils.EncodeAttributeValue(1, 1));
            mediaInput.SetUINT32(PInvoke.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            MediaUtils.Check(transform.SetInputType(streamId, mediaInput, 0));

            IMFMediaType mediaOutput;
            MediaUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
            mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, OutputFormat);
            mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MediaUtils.EncodeAttributeValue(Width, Height));
            mediaOutput.SetUINT32(PInvoke.MF_MT_DEFAULT_STRIDE, VideoFrameFormats.Stride(_format, Width));
            mediaOutput.SetUINT32(PInvoke.MF_MT_FIXED_SIZE_SAMPLES, 1);
            if (HasFrameRate)
                mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MediaUtils.EncodeAttributeValue(FpsNom, FpsDenom));
            mediaOutput.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, MediaUtils.EncodeAttributeValue(1, 1));
            mediaOutput.SetUINT32(PInvoke.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            mediaOutput.SetUINT32(PInvoke.MF_MT_SAMPLE_SIZE, VideoFrameFormats.FrameSize(_format, Width, Height));
            mediaOutput.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
            MediaUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));

            return transform;
        }
    }
}
