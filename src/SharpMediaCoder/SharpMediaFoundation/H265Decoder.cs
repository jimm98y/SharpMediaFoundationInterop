using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation
{
    public class H265Decoder : MFTBase, IVideoTransform
    {
        const uint H265_RES_MULTIPLE = 8;

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

        public H265Decoder(uint width, uint height, uint fpsNom, uint fpsDenom, bool isLowLatency = false)
        {
            this._fpsNom = fpsNom;
            this._fpsDenom = fpsDenom;
            ulong sampleDuration;
            MFTUtils.Check(PInvoke.MFFrameRateToAverageTimePerFrame(_fpsNom, _fpsDenom, out sampleDuration));
            _sampleDuration = (long)sampleDuration;

            this._originalWidth = width;
            this._originalHeight = height;

            // https://video.stackexchange.com/questions/24829/h-265-disadvantages-with-video-dimensions-not-multiple-of-8
            this._width = MathUtils.RoundToMultipleOf(width, H265_RES_MULTIPLE);
            this._height = MathUtils.RoundToMultipleOf(height, H265_RES_MULTIPLE);
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

            return ProcessInput(decoder, data, _sampleDuration, ticks);
        }

        public bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            return ProcessOutput(decoder, dataBuffer, ref buffer, out length);
        }

        private IMFTransform Create()
        {
            IMFTransform decoder = default;
            HRESULT result;

            foreach (IMFActivate activate in MFTUtils.FindTransforms(PInvoke.MFT_CATEGORY_VIDEO_DECODER,
                MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT,
                new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_HEVC },
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
                    mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_HEVC);
                    mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MathUtils.EncodeAttributeValue(_width, _height));
                    mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MathUtils.EncodeAttributeValue(_fpsNom, _fpsDenom));

                    if (_isLowLatency)
                    {
                        decoder.GetAttributes(out IMFAttributes attributes);
                        attributes.SetUINT32(PInvoke.MF_LOW_LATENCY, 1);
                    }

                    result = decoder.SetInputType(0, mediaInput, 0);
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
                    mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MathUtils.EncodeAttributeValue(_width, _height));
                    result = decoder.SetOutputType(0, mediaOutput, 0);
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
