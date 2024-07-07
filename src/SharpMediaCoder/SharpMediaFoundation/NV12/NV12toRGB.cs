using System;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation.NV12
{
    /// <summary>
    /// Converts NV12 (YUV) to RGB.
    /// </summary>
    public class NV12toRGB : VideoTransformBase
    {
        public NV12toRGB(uint width, uint height) : base(width, height)
        { }

        protected override IMFTransform Create()
        {
            const int streamId = 0;

            var inputSubtype = PInvoke.MFVideoFormat_NV12;
            var outputSubtype = PInvoke.MFVideoFormat_RGB24;
            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = inputSubtype };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = outputSubtype };

            IMFTransform transform = MFTUtils.CreateTransform(PInvoke.MFT_CATEGORY_VIDEO_PROCESSOR, MFT_ENUM_FLAG.MFT_ENUM_FLAG_ALL, input, output);
            if (transform == null) throw new NotSupportedException($"Unsupported transform! Input: {inputSubtype}, Output: {outputSubtype}");

            IMFMediaType mediaInput;
            MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
            mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, inputSubtype);
            mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MFTUtils.EncodeAttributeValue(Width, Height));
            MFTUtils.Check(transform.SetInputType(streamId, mediaInput, 0));

            IMFMediaType mediaOutput;
            MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
            mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, outputSubtype);
            mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MFTUtils.EncodeAttributeValue(Width, Height));
            MFTUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));

            return transform;
        }
    }
}
