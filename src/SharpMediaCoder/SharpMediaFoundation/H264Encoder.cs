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
        const uint H264_RES_MULTIPLE = 16;

        private uint _originalWidth;
        private uint _originalHeight;
        private uint _fpsNom;
        private uint _fpsDenom;

        private uint _width;
        private uint _height;
        private long _sampleDuration = 1;
        private bool _isLowLatency = false;

        public uint OriginalWidth => _originalWidth;
        public uint OriginalHeight => _originalHeight;

        public uint Width => _width;
        public uint Height => _height;

        private IMFTransform decoder;
        private MFT_OUTPUT_DATA_BUFFER[] dataBuffer;

        public H264Encoder(uint width, uint height, uint fpsNom, uint fpsDenom)
        {
            this._fpsNom = fpsNom;
            this._fpsDenom = fpsDenom;
            ulong sampleDuration;
            MFTUtils.Check(PInvoke.MFFrameRateToAverageTimePerFrame(_fpsNom, _fpsDenom, out sampleDuration));
            _sampleDuration = (long)sampleDuration;

            this._originalWidth = width;
            this._originalHeight = height;

            // make sure the width and height are divisible by 16:
            /*
            ec. ITU-T H.264 (04/2017) page 21
             */
            const uint h264Multiple = 16;
            this._width = MathUtils.RoundToMultipleOf(width, h264Multiple);
            this._height = MathUtils.RoundToMultipleOf(height, h264Multiple);

            decoder = Create();
            decoder.GetOutputStreamInfo(0, out var streamInfo); // without MF_MT_AVG_BITRATE the cbSize will be 0
            dataBuffer = MFTUtils.CreateOutputDataBuffer(streamInfo.cbSize);
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
                    mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MathUtils.EncodeAttributeValue(_width, _height));
                    mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MathUtils.EncodeAttributeValue(_fpsNom, _fpsDenom));
                    mediaOutput.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, 2);
                    mediaOutput.SetUINT32(PInvoke.MF_MT_AVG_BITRATE, MathUtils.CalculateBitrate(_width, _height, (double)_fpsNom / _fpsDenom));
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
                    mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MathUtils.EncodeAttributeValue(_width, _height));
                    mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MathUtils.EncodeAttributeValue(_fpsNom, _fpsDenom));
                    result = encoder.SetInputType(0, mediaInput, 0);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error while creating video encoder input media {ex}");
                }
            }

            return encoder;
        }
    }
}
