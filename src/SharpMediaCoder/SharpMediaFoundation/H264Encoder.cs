using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation
{
    public class H264Encoder : VideoTransformBase
    {
        const uint H264_RES_MULTIPLE = 16;

        public H264Encoder(uint width, uint height, uint fpsNom, uint fpsDenom) :
            base(H264_RES_MULTIPLE, width, height,fpsNom, fpsDenom)
        { }

        protected override IMFTransform Create()
        {
            IMFTransform encoder = default;
            const uint streamId = 0;

            foreach (IMFActivate activate in MFTUtils.FindTransforms(PInvoke.MFT_CATEGORY_VIDEO_ENCODER,
                MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER,
                new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_NV12 },
                new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_H264 }))
            {
                try
                {
                    activate.GetAllocatedString(PInvoke.MFT_FRIENDLY_NAME_Attribute, out PWSTR deviceName, out _);
                    Debug.WriteLine($"Found video decoder MFT: {deviceName}");
                    encoder = activate.ActivateObject(MFTUtils.IID_IMFTransform) as IMFTransform;
                    break;
                }
                finally
                {
                    Marshal.ReleaseComObject(activate);
                }
            }

            if (encoder != null)
            {
                IMFMediaType mediaOutput;
                MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
                mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
                mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MathUtils.EncodeAttributeValue(Width, Height));
                mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MathUtils.EncodeAttributeValue(FpsNom, FpsDenom));
                mediaOutput.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, 2);
                mediaOutput.SetUINT32(PInvoke.MF_MT_AVG_BITRATE, MathUtils.CalculateBitrate(Width, Height, (double)FpsNom / FpsDenom));
                MFTUtils.Check(encoder.SetOutputType(streamId, mediaOutput, 0));

                IMFMediaType mediaInput;
                MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
                mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
                mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MathUtils.EncodeAttributeValue(Width, Height));
                mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MathUtils.EncodeAttributeValue(FpsNom, FpsDenom));
                MFTUtils.Check(encoder.SetInputType(streamId, mediaInput, 0));
            }

            return encoder;
        }
    }
}
