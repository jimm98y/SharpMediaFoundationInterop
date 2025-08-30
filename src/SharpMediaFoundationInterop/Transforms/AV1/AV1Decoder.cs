using SharpMediaFoundationInterop.Utils;
using System;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Transforms.AV1
{
    public class AV1Decoder : VideoTransformBase
    {
        public const uint AV1_RES_MULTIPLE = 1;

        public override Guid InputFormat => PInvoke.MFVideoFormat_AV1;
        public override Guid OutputFormat => PInvoke.MFVideoFormat_NV12;

        private bool _isLowLatency = false;

        public AV1Decoder(uint width, uint height, uint fpsNom, uint fpsDenom, bool isLowLatency = false)
          : base(AV1_RES_MULTIPLE, width, height, fpsNom, fpsDenom)
        {
            _isLowLatency = isLowLatency;
        }

        protected override IMFTransform Create()
        {
            const uint streamId = 0;

            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = InputFormat };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = OutputFormat };

            IMFTransform transform = CreateTransform(PInvoke.MFT_CATEGORY_VIDEO_DECODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE, input, output);
            if (transform == null) transform = CreateTransform(PInvoke.MFT_CATEGORY_VIDEO_DECODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT, input, output);
            if (transform == null) throw new NotSupportedException($"Unsupported transform! Input: {InputFormat}, Output: {OutputFormat}");

            IMFMediaType mediaInput;
            MediaUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
            mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, InputFormat);
            mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MediaUtils.EncodeAttributeValue(Width, Height));
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
            mediaOutput.SetUINT32(PInvoke.MF_MT_DEFAULT_STRIDE, Width);
            mediaOutput.SetUINT32(PInvoke.MF_MT_FIXED_SIZE_SAMPLES, 1);
            mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MediaUtils.EncodeAttributeValue(FpsNom, FpsDenom));
            mediaOutput.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, MediaUtils.EncodeAttributeValue(1, 1));
            mediaOutput.SetUINT32(PInvoke.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            mediaOutput.SetUINT32(PInvoke.MF_MT_SAMPLE_SIZE, Width * Height * 3 / 2);
            mediaOutput.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_MixedInterlaceOrProgressive);
            MediaUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));

            return transform;
        }

        public override bool ProcessInput(byte[] data, long timestamp)
        {
            return base.ProcessInput(data, timestamp);
        }
    }
}
