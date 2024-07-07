using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation.NV12
{
    /// <summary>
    /// Converts RGB to NV12 (YUV).
    /// </summary>
    public class RGBtoNV12 : VideoTransformBase
    {
        public RGBtoNV12(uint width, uint height) : base(width, height)
        { }

        protected override IMFTransform Create()
        {
            const int streamId = 0;

            IMFTransform transform =
                MFTUtils.CreateTransform(
                    PInvoke.MFT_CATEGORY_VIDEO_PROCESSOR,
                    MFT_ENUM_FLAG.MFT_ENUM_FLAG_ALL,
                    new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_RGB24 },
                    new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_NV12 });

            if (transform != null)
            {
                IMFMediaType mediaInput;
                MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
                mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_RGB24);
                mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MFTUtils.EncodeAttributeValue(Width, Height));
                MFTUtils.Check(transform.SetInputType(streamId, mediaInput, 0));

                IMFMediaType mediaOutput;
                MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
                mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
                mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MFTUtils.EncodeAttributeValue(Width, Height));
                MFTUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));
            }

            return transform;
        }
    }
}
