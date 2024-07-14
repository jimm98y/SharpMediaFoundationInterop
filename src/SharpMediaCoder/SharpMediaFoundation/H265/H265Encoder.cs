using System;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation.H265
{
    public class H265Encoder : VideoTransformBase
    {
        public const uint H265_RES_MULTIPLE = 8;

        public override Guid InputFormat => PInvoke.MFVideoFormat_NV12;
        public override Guid OutputFormat => PInvoke.MFVideoFormat_HEVC;

        public H265Encoder(uint width, uint height, uint fpsNom, uint fpsDenom)
            : base(H265_RES_MULTIPLE, width, height, fpsNom, fpsDenom)
        { }

        protected override IMFTransform Create()
        {
            const uint streamId = 0;

            var inputSubtype = PInvoke.MFVideoFormat_NV12;
            var outputSubtype = PInvoke.MFVideoFormat_HEVC;
            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = inputSubtype };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = outputSubtype };

            IMFTransform transform = MFTUtils.CreateTransform(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER | MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE, input, output);
            if (transform == null) transform = MFTUtils.CreateTransform(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER, input, output);
            if (transform == null) throw new NotSupportedException($"Unsupported transform! Input: {inputSubtype}, Output: {outputSubtype}");

            IMFMediaType mediaOutput;
            MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
            mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, outputSubtype);
            mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MFTUtils.EncodeAttributeValue(Width, Height));
            mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MFTUtils.EncodeAttributeValue(FpsNom, FpsDenom));
            mediaOutput.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, 2);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AVG_BITRATE, MathUtils.CalculateBitrate(Width, Height, FpsNom, FpsDenom));
            MFTUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));

            IMFMediaType mediaInput;
            MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
            mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, inputSubtype);
            mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MFTUtils.EncodeAttributeValue(Width, Height));
            mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MFTUtils.EncodeAttributeValue(FpsNom, FpsDenom));
            MFTUtils.Check(transform.SetInputType(streamId, mediaInput, 0));

            return transform;
        }
    }
}
