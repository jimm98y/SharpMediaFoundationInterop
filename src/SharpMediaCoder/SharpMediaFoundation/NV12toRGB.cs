using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation
{
    public class NV12toRGB : VideoTransformBase
    {
        public NV12toRGB(uint width, uint height) : base(1, width, height)
        {  }

        protected override IMFTransform Create()
        {
            IMFTransform decoder = default;
            const int streamId = 0;

            foreach (IMFActivate activate in MFTUtils.FindTransforms(
                PInvoke.MFT_CATEGORY_VIDEO_PROCESSOR,
                MFT_ENUM_FLAG.MFT_ENUM_FLAG_ALL,
                new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_NV12 },
                new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_RGB24 }))
            {
                try
                {
                    activate.GetAllocatedString(PInvoke.MFT_FRIENDLY_NAME_Attribute, out PWSTR deviceName, out _);
                    Debug.WriteLine($"Found color converter MFT: {deviceName}");
                    decoder = activate.ActivateObject(MFTUtils.IID_IMFTransform) as IMFTransform;
                    break;
                }
                finally
                {
                    Marshal.ReleaseComObject(activate);
                }
            }

            if (decoder != null)
            {
                IMFMediaType mediaInput;
                MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
                mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
                mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MathUtils.EncodeAttributeValue(Width, Height));
                MFTUtils.Check(decoder.SetInputType(streamId, mediaInput, 0));

                IMFMediaType mediaOutput;
                MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
                mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_RGB24);
                mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MathUtils.EncodeAttributeValue(Width, Height));
                MFTUtils.Check(decoder.SetOutputType(streamId, mediaOutput, 0));
            }

            return decoder;
        }
    }
}
