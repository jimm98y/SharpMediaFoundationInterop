using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation
{
    public class H264Decoder : MFTBase, IVideoTransform
    {
        private int _originalWidth;
        private int _originalHeight;

        private uint _width;
        private uint _height;
        private bool _isLowLatency = false;

        public int OriginalWidth => _originalWidth;
        public int OriginalHeight => _originalHeight;

        public int Width => (int)_width;
        public int Height => (int)_height;

        private ulong DefaultFrameSize { get { return ((ulong)_width << 32) + _height; } }

        private IMFTransform decoder;
        private MFT_OUTPUT_DATA_BUFFER[] dataBuffer;

        public H264Decoder(int width, int height, uint fpsNom, uint fpsDenom, bool isLowLatency = false) : base(fpsNom, fpsDenom)
        {
            this._originalWidth = width;
            this._originalHeight = height;

            // make sure the width and height are divisible by 16:
            /*
            ec. ITU-T H.264 (04/2017) page 21
             */
            int nwidth = ((width + 16 - 1) / 16) * 16;
            int nheight = ((height + 16 - 1) / 16) * 16;

            this._width = (uint)nwidth;
            this._height = (uint)nheight;
            this._isLowLatency = isLowLatency;

            decoder = Create();
            dataBuffer = MFTUtils.CreateOutputDataBuffer(_width * _height * 3 / 2);
        }

        public bool ProcessInput(byte[] data, long ticks)
        {
            if (data[0] != 0 || data[1] != 0 || data[2] != 0 || data[3] != 1)
            {
                // this little maneuver will cost us new allocation
                data = AnnexBParser.AnnexB.Concat(data).ToArray();
            }

            return ProcessInput(decoder, data, ticks);
        }

        public bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            return ProcessOutput(decoder, dataBuffer, ref buffer, out length);
        }

        private IMFTransform Create()
        {
            IMFTransform decoder = default;

            foreach (IMFActivate activate in MFTUtils.FindTransforms(PInvoke.MFT_CATEGORY_VIDEO_DECODER,
                MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT,
                new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_H264 },
                new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_NV12 }))
            {
                try
                {
                    activate.GetAllocatedString(PInvoke.MFT_FRIENDLY_NAME_Attribute, out PWSTR deviceName, out _);
                    Debug.WriteLine($"Found video decoder MFT: {deviceName}");
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
                    mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
                    mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, DefaultFrameSize);
                    mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, DefaultFPS);

                    if (_isLowLatency)
                    {
                        decoder.GetAttributes(out IMFAttributes attributes);
                        attributes.SetUINT32(PInvoke.MF_LOW_LATENCY, 1);
                    }

                    decoder.SetInputType(0, mediaInput, 0);
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
                    mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
                    mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, DefaultFrameSize);
                    decoder.SetOutputType(0, mediaOutput, 0);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error while creating video decoder output media {ex}");
                }
            }

            return decoder;
        }      
    }
}
