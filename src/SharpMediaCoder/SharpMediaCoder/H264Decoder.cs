using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaCoder
{
    public class H264Decoder : MFTBase, IDecoder
    {
        private uint _width;
        private uint _height;
        private uint _fps;
        private bool _isLowLatency = false;

        private ulong DefaultFrameSize { get { return ((ulong)_width << 32) + _height; } }

        private IMFTransform decoder;
        private MFT_OUTPUT_DATA_BUFFER[] dataBuffer;

        private byte[] _annexB = [0, 0, 0, 1];

        public H264Decoder(int width, int height, int fps, bool isLowLatency = false) : base(fps)
        {
            this._width = (uint)width;
            this._height = (uint)height;
            this._fps = (uint)fps;
            this._isLowLatency = isLowLatency;

            decoder = Create();
            dataBuffer = MFTUtils.CreateOutputDataBuffer((int)(_width * _height * 3 / 2));
        }

        public bool ProcessInput(byte[] data, long ticks)
        {
            return ProcessInput(decoder, _annexB.Concat(data).ToArray(), ticks);
        }

        public bool ProcessOutput(ref byte[] buffer)
        {
            return ProcessOutput(decoder, dataBuffer, ref buffer);
        }

        private IMFTransform Create()
        {
            IMFTransform result = default;
            var subtype = PInvoke.MFVideoFormat_NV12;

            foreach (IMFActivate activate in MFTUtils.FindTransforms(PInvoke.MFT_CATEGORY_VIDEO_DECODER,
                MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER | MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE,
                new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_H264 },
                new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = subtype }))
            {
                try
                {
                    activate.GetAllocatedString(PInvoke.MFT_FRIENDLY_NAME_Attribute, out PWSTR deviceName, out _);
                    Debug.WriteLine($"Found video decoder MFT: {deviceName}");
                    result = activate.ActivateObject(new Guid("BF94C121-5B05-4E6F-8000-BA598961414D")) as IMFTransform;
                    break;
                }
                finally
                {
                    Marshal.ReleaseComObject(activate);
                }
            }

            if (result != null)
            {
                try
                {
                    IMFMediaType mediaInput;
                    MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
                    mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                    mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
                    mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, DefaultFrameSize);
                    mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, _fps);

                    if (_isLowLatency)
                    {
                        result.GetAttributes(out IMFAttributes attributes);
                        attributes.SetUINT32(PInvoke.MF_LOW_LATENCY, 1);
                    }

                    result.SetInputType(0, mediaInput, 0);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error while creating video decoder input media {ex}");
                }

                try
                {
                    IMFMediaType mediaOutput;
                    MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
                    mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                    mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, subtype);
                    mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, DefaultFrameSize);
                    mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, _fps);
                    result.SetOutputType(0, mediaOutput, 0);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error while creating video decoder output media {ex}");
                }
            }

            return result;
        }      
    }
}
