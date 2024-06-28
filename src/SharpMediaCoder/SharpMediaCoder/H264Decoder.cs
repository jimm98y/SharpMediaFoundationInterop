﻿using System.Diagnostics;
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

        private UInt32 _width;
        private UInt32 _height;
        private UInt32 _fps;

        private UInt64 DefaultFrameSize { get { return ((UInt64)_width << 32) + _height; } }
        const Int32 DefaultInterlaceMode = 2;
        const Int64 DefaultPixelAspectRatio = (1L << 32) + 1;

        UInt64 sampleDuration;
        private IMFTransform? colorConverter;
        IMFTransform? decoder;
        MFT_OUTPUT_DATA_BUFFER[] videoData;
        MFT_OUTPUT_DATA_BUFFER[] colorData;

        private byte[] annexB = new byte[] { 0, 0, 0, 1 };

        public H264Decoder(int width, int height, int fps)
        {
            this._width = (uint)width;
            this._height = (uint)height;
            this._fps = (uint)fps;

            Check(PInvoke.MFFrameRateToAverageTimePerFrame(_fps, 1, out sampleDuration));
            Startup();

            decoder = CreateVideoDecoder();
            colorConverter = CreateColorConverter();
            videoData = CreateOutputDataBuffer((int)Math.Ceiling(_width * _height * 3 / 2d));
            colorData = CreateOutputDataBuffer((int)(_width * _height * 3));
        }

        public byte[] Process(byte[] nalu, bool keyframe)
        {
            var ticks = DateTime.Now.Ticks;
            IMFSample sampleToProcess = CreateSample(annexB.Concat(nalu).ToArray(), ticks, keyframe);
            var sampleToDecode = ProcessVideoSample(0, decoder, sampleToProcess, videoData);
            if (sampleToDecode != null)
            {
                var sample = CreateSample(sampleToDecode, ticks, true);
                return ProcessVideoSample(0, colorConverter, sample, colorData);
            }
            else
            {
                return null;
            }
        }
        
        static void Check(HRESULT result)
        {
            if (result.Failed) 
                Marshal.ThrowExceptionForHR(result.Value);
        }

        static void Startup()
        {
            Check(PInvoke.MFStartup(PInvoke.MF_API_VERSION, 0));
        }

        static IMFMediaType CreateMediaType()
        {
            Check(PInvoke.MFCreateMediaType(out IMFMediaType mediaType));
            return mediaType;
        }

        static IEnumerable<IMFActivate> FindTransforms(Guid category, MFT_ENUM_FLAG flags, MFT_REGISTER_TYPE_INFO? input, MFT_REGISTER_TYPE_INFO? output)
        {
            Check(PInvoke.MFTEnumEx(category, flags, input, output, out IMFActivate[] activates, out UInt32 activateCount));

            if (activateCount > 0)
            {
                foreach (IMFActivate activate in activates)
                {
                    yield return activate;
                }
            }
        }

        IMFTransform CreateColorConverter()
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
                    IMFMediaType mediaInput = CreateMediaType();

                    mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                    mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
                    mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, DefaultFrameSize);
                    mediaInput.SetUINT64(PInvoke.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
                    mediaInput.SetUINT64(PInvoke.MF_MT_DEFAULT_STRIDE, 3 * _width);
                    mediaInput.SetUINT64(PInvoke.MF_MT_FIXED_SIZE_SAMPLES, 1);

                    result.GetAttributes(out IMFAttributes attributes);
                    attributes.SetUINT32(PInvoke.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
                    result.SetInputType(0, mediaInput, 0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error while creating color converter input media {ex}");
                }

                try
                {
                    IMFMediaType mediaOutput = CreateMediaType();

                    mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                    mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_RGB24);
                    mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, DefaultFrameSize);

                    result.SetOutputType(0, mediaOutput, 0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error while creating color converter output media {ex}");
                }
            }

            return result;
        }

        IMFTransform? CreateVideoDecoder()
        {
            IMFTransform? result = default;
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
                    IMFMediaType mediaInput = CreateMediaType();

                    mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                    mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
                    mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, DefaultFrameSize);
                    mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, _fps);
                    mediaInput.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, DefaultInterlaceMode);
                    mediaInput.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, DefaultPixelAspectRatio);

                    result.GetAttributes(out IMFAttributes attributes);
                    attributes.SetUINT32(PInvoke.MF_LOW_LATENCY, 1);
                    attributes.SetUINT32(PInvoke.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
                    result.SetInputType(0, mediaInput, 0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error while creating video decoder input media {ex}");
                }

                try
                {
                    IMFMediaType mediaOutput = CreateMediaType();

                    mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                    mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, subtype);
                    mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, DefaultFrameSize);
                    mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, _fps);
                    
                    result.SetOutputType(0, mediaOutput, 0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error while creating video decoder output media {ex}");
                }
            }

            return result;
        }

        unsafe IMFSample CreateSample(Byte[] data, Int64 timestamp, bool isFirstFrame)
        {
            Check(PInvoke.MFCreateMemoryBuffer((UInt32)data.Length, out IMFMediaBuffer buffer));

            try
            {
                UInt32* maxLength = default;
                UInt32* currentLength = default;

                buffer.Lock(out Byte* target, maxLength, currentLength);

                fixed (Byte* source = data)
                {
                    Unsafe.CopyBlock(target, source, (UInt32)data.Length);
                }
            }
            finally
            {
                buffer.SetCurrentLength((UInt32)data.Length);
                buffer.Unlock();
            }

            Check(PInvoke.MFCreateSample(out IMFSample sample));

            if (isFirstFrame)
            {
                sample.SetUINT32(PInvoke.MFSampleExtension_CleanPoint, 1);
                sample.SetUINT32(PInvoke.MFSampleExtension_Discontinuity, 1);
            }

            sample.AddBuffer(buffer);
            sample.SetSampleDuration((Int64)sampleDuration);
            sample.SetSampleTime(timestamp);

            return sample;
        }

        static MFT_OUTPUT_DATA_BUFFER[] CreateOutputDataBuffer(Int32 size = 0)
        {
            MFT_OUTPUT_DATA_BUFFER[] result = new MFT_OUTPUT_DATA_BUFFER[1];

            if (size > 0)
            {
                Check(PInvoke.MFCreateMemoryBuffer((UInt32)size, out IMFMediaBuffer buffer));
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

        unsafe byte[] ProcessVideoSample(uint inputStreamID, IMFTransform decoder, IMFSample sample, MFT_OUTPUT_DATA_BUFFER[] videoData)
        {
            try
            {
                Boolean isProcessed = false;

                HRESULT inputStatusResult = decoder.GetInputStatus(inputStreamID, out uint decoderInputFlags);

                if (inputStatusResult.Value == 0 )
                {
                    HRESULT inputResult = decoder.ProcessInput(inputStreamID, sample, 0);

                    if (inputResult.Value == 0)
                    {
                        do
                        {
                            HRESULT outputResult;
                            uint decoderOutputStatus;
                            if (decoder == this.decoder)
                                outputResult = decoder.ProcessOutput(0, videoData, out decoderOutputStatus);
                            else
                                outputResult = decoder.ProcessOutput(0, 1, videoData, &decoderOutputStatus);

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
                            }
                            else if (outputResult.Value == 0 && decoderOutputStatus == 0)
                            {
                                isProcessed = true;
                            }
                            else if(outputResult.Value == unchecked((int)0xc00d36b1))
                            {
                                isProcessed = true;
                            }
                        }
                        while (videoData[0].dwStatus == (uint)MFT_OUTPUT_DATA_BUFFERFlags.Incomplete);
                    }
                }

                if (isProcessed)
                {
                    videoData[0].pSample.GetBufferByIndex(0, out IMFMediaBuffer buffer);
                    try
                    {
                        UInt32 maxLength = default;
                        UInt32 currentLength = default;

                        buffer.Lock(out Byte* data, &maxLength, &currentLength);

                        var safearray = new byte[currentLength];
                        Marshal.Copy((IntPtr)data, safearray, 0, (int)currentLength);

                        return safearray;
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

            return null;
        }
    }
}
