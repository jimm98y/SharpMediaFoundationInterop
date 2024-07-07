using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation
{
    public class H264Encoder : MFTBase, IVideoTransform
    {
        private int _originalWidth;
        private int _originalHeight;

        private uint _width;
        private uint _height;

        public int OriginalWidth => _originalWidth;
        public int OriginalHeight => _originalHeight;

        public int Width => (int)_width;
        public int Height => (int)_height;

        private ulong DefaultFrameSize { get { return ((ulong)_width << 32) + _height; } }

        private IMFTransform decoder;
        private MFT_OUTPUT_DATA_BUFFER[] dataBuffer;

        public H264Encoder(int width, int height, uint fpsNom, uint fpsDenom) : base(fpsNom, fpsDenom)
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

            decoder = Create();

            decoder.GetOutputStreamInfo(0, out var streamInfo); // without MF_MT_AVG_BITRATE the cbSize will be 0
            dataBuffer = MFTUtils.CreateOutputDataBuffer(streamInfo.cbSize);
        }

        public bool ProcessInput(byte[] data, long ticks)
        {
            return ProcessInput(decoder, data, ticks);
        }

        public bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            return ProcessOutput(decoder, dataBuffer, ref buffer, out length);
        }

        private IMFTransform Create()
        {
            IMFTransform encoder = default;
            HRESULT result;

            /*
            The input media type must have one of the following subtypes:
                MFVideoFormat_I420
                MFVideoFormat_IYUV
                MFVideoFormat_NV12
                MFVideoFormat_YUY2
                MFVideoFormat_YV12
            */
            foreach (IMFActivate activate in MFTUtils.FindTransforms(PInvoke.MFT_CATEGORY_VIDEO_ENCODER,
                MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER,
                new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_NV12 },
                new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_H264 }))
            {
                try
                {
                    activate.GetAllocatedString(PInvoke.MFT_FRIENDLY_NAME_Attribute, out PWSTR deviceName, out _);
                    Debug.WriteLine($"Found video decoder MFT: {deviceName}");
                    encoder = activate.ActivateObject(IID_IMFTransform) as IMFTransform;
                    break;
                }
                finally
                {
                    Marshal.ReleaseComObject(activate);
                }
            }

            if (encoder != null)
            {
                try
                {
                    IMFMediaType mediaOutput;
                    MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
                    mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                    mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
                    mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, DefaultFrameSize);
                    mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, DefaultFPS);
                    mediaOutput.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, 2);
                    mediaOutput.SetUINT32(PInvoke.MF_MT_AVG_BITRATE, CalculateBitrate(_width, _height, FPS));
                    result = encoder.SetOutputType(0, mediaOutput, 0);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error while creating video encoder output media {ex}");
                }

                try
                {
                    IMFMediaType mediaInput;
                    MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
                    mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                    mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
                    mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, DefaultFrameSize);
                    mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, DefaultFPS);
                    result = encoder.SetInputType(0, mediaInput, 0);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error while creating video encoder input media {ex}");
                }
            }

            return encoder;
        }

        private static uint CalculateBitrate(uint width, uint height, double fps, double bpp = 0.12)
        {
            // https://stackoverflow.com/questions/8931200/video-bitrate-and-file-size-calculation
            return (uint)Math.Ceiling(width * height * fps * bpp * 0.001d);
        }
    }
}
