using System;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Codecs
{
    [SupportedOSPlatform("windows10.0.17763.0")]
    public class H265Decoder : VideoTransformBase
    {
        public const uint H265_RES_MULTIPLE = 8;

        public override Guid InputFormat => PInvoke.MFVideoFormat_HEVC;
        /// <summary>NV12, or what the stream's chroma and bits need, as it was made for: see <see cref="VideoFrameFormat"/>.</summary>
        public override Guid OutputFormat => MediaFoundationDecoder.SubtypeOf(_format);

        private bool _isLowLatency = false;
        private readonly VideoFrameFormat _format;

        protected override uint MinimumOutputSize => VideoFrameFormats.FrameSize(_format, Width, Height);

        public H265Decoder(uint width, uint height, uint fpsNom, uint fpsDenom, bool isLowLatency = false, VideoFrameFormat format = VideoFrameFormat.NV12)
            : base(H265_RES_MULTIPLE, width, height, fpsNom, fpsDenom)
        {
            _isLowLatency = isLowLatency;
            _format = format;
        }

        protected override IMFTransform Create()
        {
            const int streamId = 0;

            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = InputFormat };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = OutputFormat };

            IMFTransform transform = CreateTransform(PInvoke.MFT_CATEGORY_VIDEO_DECODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE, input, output);
            if (transform == null) transform = CreateTransform(PInvoke.MFT_CATEGORY_VIDEO_DECODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT, input, output);
            if (transform == null && _format != VideoFrameFormat.NV12)
            {
                // a format the decoder does not say it hands out, but offers at the stream change - Windows' of AV1, of
                // monochrome: found by NV12, which it does
                var nv12 = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_NV12 };
                transform = CreateTransform(PInvoke.MFT_CATEGORY_VIDEO_DECODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT, input, nv12);
            }
            if (transform == null) throw new NotSupportedException($"Unsupported transform! Input: {InputFormat}, Output: {OutputFormat}");

            // on the GPU, where a device was given and the transform can use one
            AttachDeviceManager(transform);

            ApplyCodecProperties(transform);

            IMFMediaType mediaInput;
            MediaUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
            mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, InputFormat);
            mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MediaUtils.EncodeAttributeValue(Width, Height));
            if (HasFrameRate) // a hint, given only where the stream says it
                mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MediaUtils.EncodeAttributeValue(FpsNom, FpsDenom));
            mediaInput.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_MixedInterlaceOrProgressive);
            mediaInput.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, MediaUtils.EncodeAttributeValue(1, 1));
            if (_isLowLatency)
            {
                transform.GetAttributes(out IMFAttributes attributes);
                attributes.SetUINT32(PInvoke.MF_LOW_LATENCY, 1);
            }
            MediaUtils.Check(transform.SetInputType(streamId, mediaInput, 0));

            IMFMediaType mediaOutput = null;
            MediaUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
            mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, OutputFormat);
            mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MediaUtils.EncodeAttributeValue(Width, Height));
            mediaOutput.SetUINT32(PInvoke.MF_MT_DEFAULT_STRIDE, VideoFrameFormats.Stride(_format, Width));
            mediaOutput.SetUINT32(PInvoke.MF_MT_FIXED_SIZE_SAMPLES, 1);
            if (HasFrameRate) // a hint, given only where the stream says it
                mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MediaUtils.EncodeAttributeValue(FpsNom, FpsDenom));
            mediaOutput.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, MediaUtils.EncodeAttributeValue(1, 1));
            mediaOutput.SetUINT32(PInvoke.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            mediaOutput.SetUINT32(PInvoke.MF_MT_SAMPLE_SIZE, VideoFrameFormats.FrameSize(_format, Width, Height));
            mediaOutput.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_MixedInterlaceOrProgressive);
            // Monochrome is set at the stream change alone: HEVC's decoder takes L8 before it, then writes frames larger
            // than the type says.
            bool monochrome = _format == VideoFrameFormat.L8 || _format == VideoFrameFormat.L16;
            var result = monochrome ? new Windows.Win32.Foundation.HRESULT(unchecked((int)0x80004005)) : transform.SetOutputType(streamId, mediaOutput, 0);
            if (result.Failed && _format != VideoFrameFormat.NV12)
            {
                // Not taken before the decoder knows the stream - Windows' decoder of AV1, of 4:2:2, 4:4:4 and monochrome:
                // NV12 until then, and the format asked for chosen at the stream change, of those it offers then.
                mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
                mediaOutput.SetUINT32(PInvoke.MF_MT_DEFAULT_STRIDE, VideoFrameFormats.Stride(VideoFrameFormat.NV12, Width));
                mediaOutput.SetUINT32(PInvoke.MF_MT_SAMPLE_SIZE, VideoFrameFormats.FrameSize(VideoFrameFormat.NV12, Width, Height));
                result = transform.SetOutputType(streamId, mediaOutput, 0);
            }
            MediaUtils.Check(result);

            return transform;
        }

        public override bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            // a NAL unit as a file holds it gets its start code in front of it in the media buffer, not in a new array
            return AnnexBUtils.HasStartCode(data)
                ? base.ProcessInput(data, timestamp)
                : ProcessInput(AnnexBUtils.AnnexB, data, timestamp);
        }
    }
}
