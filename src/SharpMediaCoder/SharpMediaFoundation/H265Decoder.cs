using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation
{
    public class H265Decoder : VideoTransformBase
    {
        const uint H265_RES_MULTIPLE = 8;

        private bool _isLowLatency = false;

        public H265Decoder(uint width, uint height, uint fpsNom, uint fpsDenom, bool isLowLatency = false)
            : base(H265_RES_MULTIPLE, width, height, fpsNom, fpsDenom)
        {
            this._isLowLatency = isLowLatency;
        }

        protected override IMFTransform Create()
        {
            const int streamId = 0;

            IMFTransform transform =
                MFTUtils.CreateTransform(
                    PInvoke.MFT_CATEGORY_VIDEO_DECODER,
                    MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT,
                    new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_HEVC },
                    new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_NV12 });

            if (transform != null)
            {
                IMFMediaType mediaInput;
                MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
                mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_HEVC);
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
                mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
                mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MFTUtils.EncodeAttributeValue(Width, Height));
                MFTUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));
            }

            return transform;
        }

        public override bool ProcessInput(byte[] data, long ticks)
        {
            return base.ProcessInput(AnnexBUtils.PrefixNalu(data), ticks);
        }
    }
}
