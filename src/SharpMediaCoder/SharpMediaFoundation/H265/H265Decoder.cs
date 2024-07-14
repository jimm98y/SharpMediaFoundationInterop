using System;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation.H265
{
    public class H265Decoder : VideoTransformBase
    {
        public const uint H265_RES_MULTIPLE = 8;

        public override Guid InputFormat => PInvoke.MFVideoFormat_HEVC;
        public override Guid OutputFormat => PInvoke.MFVideoFormat_NV12;

        private bool _isLowLatency = false;

        public H265Decoder(uint width, uint height, uint fpsNom, uint fpsDenom, bool isLowLatency = false)
            : base(H265_RES_MULTIPLE, width, height, fpsNom, fpsDenom)
        {
            _isLowLatency = isLowLatency;
        }

        protected override IMFTransform Create()
        {
            const int streamId = 0;

            var inputSubtype = PInvoke.MFVideoFormat_HEVC;
            var outputSubtype = PInvoke.MFVideoFormat_NV12;
            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = inputSubtype };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = outputSubtype };

            IMFTransform transform = MFTUtils.CreateTransform(PInvoke.MFT_CATEGORY_VIDEO_DECODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE, input, output);
            if (transform == null) transform = MFTUtils.CreateTransform(PInvoke.MFT_CATEGORY_VIDEO_DECODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT, input, output);
            if (transform == null) throw new NotSupportedException($"Unsupported transform! Input: {inputSubtype}, Output: {outputSubtype}");

            IMFMediaType mediaInput;
            MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
            mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, inputSubtype);
            mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MFTUtils.EncodeAttributeValue(Width, Height));
            mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MFTUtils.EncodeAttributeValue(FpsNom, FpsDenom));
            if (_isLowLatency)
            {
                transform.GetAttributes(out IMFAttributes attributes);
                attributes.SetUINT32(PInvoke.MF_LOW_LATENCY, 1);
            }
            MFTUtils.Check(transform.SetInputType(streamId, mediaInput, 0));

            IMFMediaType mediaOutput;
            MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
            mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, outputSubtype);
            mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MFTUtils.EncodeAttributeValue(Width, Height));
            MFTUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));

            return transform;
        }

        public override bool ProcessInput(byte[] data, long timestamp)
        {
            return base.ProcessInput(AnnexBUtils.PrefixNalu(data), timestamp);
        }
    }
}
