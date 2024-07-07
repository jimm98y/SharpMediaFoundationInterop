using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation.H265
{
    public class H265Encoder : VideoTransformBase
    {
        const uint H265_RES_MULTIPLE = 8;

        public H265Encoder(uint width, uint height, uint fpsNom, uint fpsDenom)
            : base(H265_RES_MULTIPLE, width, height, fpsNom, fpsDenom)
        { }

        protected override IMFTransform Create()
        {
            const uint streamId = 0;
            IMFTransform transform =
                MFTUtils.CreateTransform(
                    PInvoke.MFT_CATEGORY_VIDEO_ENCODER,
                    MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER | MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE,
                    new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_NV12 },
                    new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_HEVC });

            if(transform == null)
            {
                transform =
                    MFTUtils.CreateTransform(
                        PInvoke.MFT_CATEGORY_VIDEO_ENCODER,
                        MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER,
                        new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_NV12 },
                        new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_HEVC });
            }

            if (transform != null)
            {
                IMFMediaType mediaOutput;
                MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
                mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_HEVC);
                mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MFTUtils.EncodeAttributeValue(Width, Height));
                mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MFTUtils.EncodeAttributeValue(FpsNom, FpsDenom));
                mediaOutput.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, 2);
                mediaOutput.SetUINT32(PInvoke.MF_MT_AVG_BITRATE, MathUtils.CalculateBitrate(Width, Height, FpsNom, FpsDenom));
                MFTUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));

                IMFMediaType mediaInput;
                MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
                mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
                mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MFTUtils.EncodeAttributeValue(Width, Height));
                mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MFTUtils.EncodeAttributeValue(FpsNom, FpsDenom));
                MFTUtils.Check(transform.SetInputType(streamId, mediaInput, 0));
            }

            return transform;
        }
    }
}
