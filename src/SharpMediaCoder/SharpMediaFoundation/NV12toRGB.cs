using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation
{
    public class NV12toRGB : MFTBase, IVideoTransform
    {
        private uint _width;
        private uint _height;
        private long _sampleDuration = 1;

        public uint OriginalWidth => _width;
        public uint OriginalHeight => _height;

        public uint Width => _width;
        public uint Height => _height;

        private IMFTransform decoder;
        private MFT_OUTPUT_DATA_BUFFER[] dataBuffer;

        public NV12toRGB(uint width, uint height)
        {
            this._width = width;
            this._height = height;

            decoder = Create();
            dataBuffer = MFTUtils.CreateOutputDataBuffer(_width * _height * 3);
        }

        public bool ProcessInput(byte[] data, long ticks)
        {
            return ProcessInput(decoder, data, _sampleDuration, ticks);
        }

        public bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            return ProcessOutput(decoder, dataBuffer, ref buffer, out length);
        }

        private IMFTransform Create()
        {
            IMFTransform decoder = default;

            foreach (IMFActivate activate in MFTUtils.FindTransforms(PInvoke.MFT_CATEGORY_VIDEO_PROCESSOR,
                MFT_ENUM_FLAG.MFT_ENUM_FLAG_ALL,
                new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_NV12 },
                new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_RGB24 }))
            {
                try
                {
                    activate.GetAllocatedString(PInvoke.MFT_FRIENDLY_NAME_Attribute, out PWSTR deviceName, out _);
                    Debug.WriteLine($"Found color converter MFT: {deviceName}");
                    decoder = activate.ActivateObject(IID_IMFTransform) as IMFTransform;
                    break;
                }
                finally
                {
                    Marshal.ReleaseComObject(activate);
                }
            }

            if (decoder != null)
            {
                try
                {
                    IMFMediaType mediaInput;
                    MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
                    mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                    mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
                    mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MathUtils.EncodeAttributeValue(_width, _height));
                    decoder.SetInputType(0, mediaInput, 0);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error while creating color converter input media {ex}");
                }

                try
                {
                    IMFMediaType mediaOutput;
                    MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
                    mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                    mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_RGB24);
                    mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MathUtils.EncodeAttributeValue(_width, _height));
                    decoder.SetOutputType(0, mediaOutput, 0);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error while creating color converter output media {ex}");
                }
            }

            return decoder;
        }
    }
}
