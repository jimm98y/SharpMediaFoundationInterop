using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaCoder
{
    public class H264Decoder
    {
        [Flags] 
        public enum MftInputStatusFlags
        {
            AcceptData = 1 
        }
        
        [Flags]
        public enum MFT_OUTPUT_DATA_BUFFERFlags : uint
        {
            None = 0x00,
            FormatChange = 0x100,
            Incomplete = 0x1000000,
        }

        private uint _width;
        private uint _height;
        private uint _fps;

        private ulong DefaultFrameSize { get { return ((ulong)_width << 32) + _height; } }

        private ulong _sampleDuration;
        private IMFTransform colorConverter;
        private IMFTransform decoder;
        private MFT_OUTPUT_DATA_BUFFER[] videoData;
        private MFT_OUTPUT_DATA_BUFFER[] colorData;

        private byte[] _annexB = [0, 0, 0, 1];
        private byte[] _decodedBytes;

        public H264Decoder(int width, int height, int fps)
        {
            this._width = (uint)width;
            this._height = (uint)height;
            this._fps = (uint)fps;

            this._decodedBytes = new byte[width * height * 3 / 2];

            Check(PInvoke.MFFrameRateToAverageTimePerFrame(_fps, 1, out _sampleDuration));
            Check(PInvoke.MFStartup(PInvoke.MF_API_VERSION, 0));

            decoder = CreateVideoDecoder();
            colorConverter = CreateColorConverter();
            videoData = CreateOutputDataBuffer((int)(_width * _height * 3 / 2));
            colorData = CreateOutputDataBuffer((int)(_width * _height * 3));
        }

        public bool Process(byte[] nalu, long ticks, ref byte[] sampleBytes)
        {
            IMFSample sampleToProcess = CreateSample(_annexB.Concat(nalu).ToArray(), ticks);
            if (ProcessVideoSample(0, decoder, sampleToProcess, videoData, ref _decodedBytes))
            {
                var sample = CreateSample(_decodedBytes, ticks);
                return ProcessVideoSample(0, colorConverter, sample, colorData, ref sampleBytes);
            }

            return false;
        }

        private static void Check(HRESULT result)
        {
            if (result.Failed)
                Marshal.ThrowExceptionForHR(result.Value);
        }

        private static IEnumerable<IMFActivate> FindTransforms(Guid category, MFT_ENUM_FLAG flags, MFT_REGISTER_TYPE_INFO? input, MFT_REGISTER_TYPE_INFO? output)
        {
            Check(PInvoke.MFTEnumEx(category, flags, input, output, out IMFActivate[] activates, out uint activateCount));

            if (activateCount > 0)
            {
                foreach (IMFActivate activate in activates)
                {
                    yield return activate;
                }
            }
        }

        private IMFTransform CreateColorConverter()
        {
            IMFTransform result = default;

            foreach (IMFActivate activate in FindTransforms(PInvoke.MFT_CATEGORY_VIDEO_PROCESSOR,
                MFT_ENUM_FLAG.MFT_ENUM_FLAG_ALL,
                new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_NV12 },
                new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_RGB24 }))
            {
                try
                {
                    activate.GetAllocatedString(PInvoke.MFT_FRIENDLY_NAME_Attribute, out PWSTR deviceName, out _);
                    Debug.WriteLine($"Found color converter MFT: {deviceName}");
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
                    Check(PInvoke.MFCreateMediaType(out mediaInput));
                    mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                    mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
                    mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, DefaultFrameSize);
                    result.SetInputType(0, mediaInput, 0);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error while creating color converter input media {ex}");
                }

                try
                {
                    IMFMediaType mediaOutput;
                    Check(PInvoke.MFCreateMediaType(out mediaOutput));
                    mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                    mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_RGB24);
                    mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, DefaultFrameSize);
                    result.SetOutputType(0, mediaOutput, 0);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error while creating color converter output media {ex}");
                }
            }

            return result;
        }

        private IMFTransform CreateVideoDecoder()
        {
            IMFTransform result = default;
            var subtype = PInvoke.MFVideoFormat_NV12;

            foreach (IMFActivate activate in FindTransforms(PInvoke.MFT_CATEGORY_VIDEO_DECODER,
                MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER,
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

            if (result is not null)
            {
                try
                {
                    IMFMediaType mediaInput;
                    Check(PInvoke.MFCreateMediaType(out mediaInput));
                    mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                    mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
                    mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, DefaultFrameSize);
                    mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, _fps);
                    result.SetInputType(0, mediaInput, 0);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error while creating video decoder input media {ex}");
                }

                try
                {
                    IMFMediaType mediaOutput;
                    Check(PInvoke.MFCreateMediaType(out mediaOutput));
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

        private unsafe IMFSample CreateSample(byte[] data, long timestamp)
        {
            Check(PInvoke.MFCreateMemoryBuffer((uint)data.Length, out IMFMediaBuffer buffer));

            try
            {
                uint* maxLength = default;
                uint* currentLength = default;
                buffer.Lock(out byte* target, maxLength, currentLength);
                fixed (byte* source = data)
                {
                    Unsafe.CopyBlock(target, source, (uint)data.Length);
                }
            }
            finally
            {
                buffer.SetCurrentLength((uint)data.Length);
                buffer.Unlock();
            }

            Check(PInvoke.MFCreateSample(out IMFSample sample));

            sample.AddBuffer(buffer);
            sample.SetSampleDuration((long)_sampleDuration);
            sample.SetSampleTime(timestamp);
            return sample;
        }

        private static MFT_OUTPUT_DATA_BUFFER[] CreateOutputDataBuffer(int size = 0)
        {
            MFT_OUTPUT_DATA_BUFFER[] result = new MFT_OUTPUT_DATA_BUFFER[1];

            if (size > 0)
            {
                Check(PInvoke.MFCreateMemoryBuffer((uint)size, out IMFMediaBuffer buffer));
                Check(PInvoke.MFCreateSample(out IMFSample sample));

                sample.AddBuffer(buffer);
                result[0].pSample = sample;
            }
            else
            {
                result[0].pSample = default;
            }

            result[0].dwStreamID = 0;
            result[0].dwStatus = 0;
            result[0].pEvents = default;

            return result;
        }

        private unsafe bool ProcessVideoSample(uint inputStreamID, IMFTransform decoder, IMFSample sample, MFT_OUTPUT_DATA_BUFFER[] videoData, ref byte[] bytes)
        {
            try
            {
                bool isProcessed = false;
                HRESULT inputResult;
                HRESULT inputStatusResult = decoder.GetInputStatus(inputStreamID, out uint decoderInputFlags);

                if (inputStatusResult.Value == 0)
                {
                    inputResult = decoder.ProcessInput(inputStreamID, sample, 0);

                    if (inputResult.Value == 0)
                    {
                        while (true)
                        {
                            uint decoderOutputStatus;
                            HRESULT outputResult = decoder.ProcessOutput(0, videoData, out decoderOutputStatus);

                            if (videoData[0].dwStatus == (uint)MFT_OUTPUT_DATA_BUFFERFlags.FormatChange)
                            {
                                decoder.GetOutputAvailableType(inputStreamID, 0, out IMFMediaType mediaType);
                                decoder.SetOutputType(inputStreamID, mediaType, 0);
                                decoder.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, default);
                                videoData[0].dwStatus = (uint)MFT_OUTPUT_DATA_BUFFERFlags.None;
                            }
                            else if (outputResult.Value == unchecked((int)0xc00d6d72))
                            {
                                // needs more input
                                break;
                            }
                            else if (outputResult.Value == 0 && decoderOutputStatus == 0)
                            {
                                isProcessed = true;
                            }
                            else if (outputResult.Value == unchecked((int)0xc00d36b1))
                            {
                                isProcessed = true;
                            }
                        }
                    }
                }

                if (isProcessed)
                {
                    videoData[0].pSample.GetBufferByIndex(0, out IMFMediaBuffer buffer);
                    try
                    {
                        uint maxLength = default;
                        uint currentLength = default;
                        buffer.Lock(out byte* data, &maxLength, &currentLength);
                        Marshal.Copy((IntPtr)data, bytes, 0, (int)currentLength);
                        return true;
                    }
                    finally
                    {
                        buffer.SetCurrentLength(0);
                        buffer.Unlock();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error while processing input {ex}");
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }

            return false;
        }
    }
}
