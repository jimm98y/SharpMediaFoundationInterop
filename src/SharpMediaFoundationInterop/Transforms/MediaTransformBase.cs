using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com.StructuredStorage;

namespace SharpMediaFoundationInterop.Transforms
{
    /// <summary>
    /// Base media transform.
    /// </summary>
    /// <remarks>
    /// To trace Media Foundation, run: mftrace -v SharpMediaPlayer_x86.exe
    /// </remarks>
    public abstract class MediaTransformBase
    {
        public abstract Guid InputFormat { get; }
        public abstract Guid OutputFormat { get; }

        static MediaTransformBase()
        {
            MediaUtils.Check(PInvoke.MFStartup(PInvoke.MF_API_VERSION, 0));
        }

        protected bool ProcessInput(IMFTransform transform, byte[] data, long sampleDuration, long timestamp)
        {
            bool ret = false;
            IMFSample sample = MediaUtils.CreateSample(data, sampleDuration, timestamp);

            // samples are large, so to keep the memory usage low we have to tell GC about large amounts of unmanaged memory being allocated
            GC.AddMemoryPressure(data.Length); // approximate size

            try
            {
                ret = Input(0, transform, sample);
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
                GC.RemoveMemoryPressure(data.Length);
            }

            return ret;
        }

        protected bool ProcessOutput(IMFTransform transform, MFT_OUTPUT_DATA_BUFFER[] dataBuffer, ref byte[] buffer, out uint length)
        {
            return Output(0, transform, dataBuffer, ref buffer, out length);
        }

        private unsafe bool Input(uint streamID, IMFTransform transform, IMFSample sample)
        {
            bool ret = false;
            const uint MF_E_NOTACCEPTING = 0xC00D36B5;

            try
            {
                MediaUtils.Check(transform.GetInputStatus(streamID, out uint decoderInputFlags));
                HRESULT inputResult = transform.ProcessInput(streamID, sample, 0);
                if(inputResult == MF_E_NOTACCEPTING) // after stream change, we have to flush sometimes
                {
                    transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, default);
                    inputResult = transform.ProcessInput(streamID, sample, 0); // try again
                }
                MediaUtils.Check(inputResult);
                ret = true;
            }
            catch (Exception ex)
            {
                if (Log.ErrorEnabled) Log.Error(ex.Message);
            }

            return ret;
        }

        private unsafe bool Output(uint streamID, IMFTransform transform, MFT_OUTPUT_DATA_BUFFER[] dataBuffer, ref byte[] bytes, out uint length)
        {
            bool ret = false;
            const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xc00d6d72);
            const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xc00d6d61);
            uint decoderOutputStatus;
            HRESULT outputResult = transform.ProcessOutput(0, dataBuffer, out decoderOutputStatus);
            IMFSample sample = dataBuffer[0].pSample;

            if (outputResult.Value == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                // the stream change happens every time with the H264/H265 decoder
                if (Log.WarnEnabled) Log.Warn("MFT stream change requested");
                length = 0;

                IMFMediaType mediaType = null;
                uint i = 0;
                try
                {
                    while (true)
                    {
                        transform.GetOutputAvailableType(streamID, i++, out IMFMediaType mType);
                        mType.GetGUID(PInvoke.MF_MT_SUBTYPE, out var mSubtype);
                        if (mSubtype == OutputFormat)
                        {
                            mediaType = mType;
                            break;
                        }
                    }
                }
                catch(Exception ex)
                {
                    if (Log.ErrorEnabled) Log.Error(ex.Message);
                    throw new Exception("Unsupported subtype!");
                }

                // Enumerate the new type and list all the changes
                i = 0; 
                StringBuilder log = new StringBuilder();
                try
                {
                    while (true)
                    {
                        Guid guid;
                        PROPVARIANT variant = default;
                        mediaType.GetItemByIndex(i++, &guid, ref variant);
                        
                        if(guid == PInvoke.MF_MT_GEOMETRIC_APERTURE ||
                            guid == PInvoke.MF_MT_PAN_SCAN_APERTURE ||
                            guid == PInvoke.MF_MT_MINIMUM_DISPLAY_APERTURE)
                        {
                            mediaType.GetBlobSize(&guid, out var blobSize);
                            byte[] blob = new byte[blobSize];
                            mediaType.GetBlob(guid, blob, &blobSize);
                            log.AppendLine($"Blob {guid}: {Convert.ToHexString(blob)}");
                        }
                        else
                        {
                            log.AppendLine($"{guid}: {variant.Anonymous.Anonymous.Anonymous.uhVal}");
                        }
                    }
                }
                catch (Exception ex)
                {
                }

                // 206b4fc8-fcf9-4c51-afe3-9764369e33a0 MF_SA_D3D11_AWARE
                // 2efd8eee-1150-4328-9cf5-66dce933fcf4 STATIC_CODECAPI_AVDecVideoThumbnailGenerationMode
                // 5ae557b8-77af-41f5-9fa6-4db2fe1d4bca STATIC_CODECAPI_AVDecVideoMaxCodedWidth
                // 7262a16a-d2dc-4e75-9ba8-65c0c6d32b13 STATIC_CODECAPI_AVDecVideoMaxCodedHeight
                // 8ea2e44a-dd86-4bba-a048-2b203f26f4a7 MF_VIDEO_DECODER_DROPPED_FRAME_COUNT
                // 9561c3e8-ea9e-4435-9b1e-a93e691894d8 STATIC_CODECAPI_AVDecNumWorkerThreads
                // 9c27891a-ed7a-40e1-88e8-b22727a024ee MF_LOW_LATENCY
                // a24e30d7-de25-4558-bbfb-71070a2d332e MFT_DECODER_QUALITY_MANAGEMENT_CUSTOM_CONTROL
                // c0bbf0ca-86cb-49b3-b36c-f2d02bea31da MF_VIDEO_DECODER_CORRUPTED_FRAME_COUNT
                // d8980deb-0a48-425f-8623-611db41d3810 MFT_DECODER_QUALITY_MANAGEMENT_RECOVERY_WITHOUT_ARTIFACTS
                // eaa35c29-775e-488e-9b61-b3283e49583b MF_SA_D3D_AWARE
                // ef80833f-f8fa-44d9-80d8-41ed6232670c MFT_DECODER_EXPOSE_OUTPUT_TYPES_IN_NATIVE_ORDER
                // f7db8a2f-4f48-4ee8-ae31-8b6ebe558ae2 STATIC_CODECAPI_AVDecVideoAcceleration_H264
                log.AppendLine("Attributes:");
                i = 0;
                transform.GetAttributes(out IMFAttributes attributes);
                try
                {
                    while (true)
                    {
                        Guid guid;
                        PROPVARIANT variant = default;
                        attributes.GetItemByIndex(i++, &guid, ref variant);
                        log.AppendLine($"{guid}: {variant.Anonymous.Anonymous.Anonymous.uhVal}");
                    }
                }
                catch (Exception ex)
                {
                }

                // https://www.magnumdb.com/search?q=f7e34c9a-42e8-4714-b74b-cb29d72c35e5
                // MF_MT_FRAME_SIZE 1652c33d-d6b2-4012-b834-72030849a37d
                // MF_MT_YUV_MATRIX 3e23d450-2c75-4d25-a00e-b91670d12327 = 2
                // MF_MT_MAJOR_TYPE 48eba18e-f8c9-4687-bf11-0a74c9f96a8f
                // MF_MT_TRANSFER_FUNCTION 5fb0fce9-be5c-4935-a811-ec838f8eed93 = 5
                // MF_MT_DEFAULT_STRIDE 644b4e48-1e02-4516-b0eb-c01ca9d49ac6 = 640
                // MF_MT_GEOMETRIC_APERTURE 66758743-7e5f-400d-980a-aa8596c85696 = 16
                // MF_MT_PAN_SCAN_APERTURE 79614dde-9187-48fb-b8c7-4d52689de649 = 16
                // MF_MT_FIXED_SIZE_SAMPLES b8ebefaf-b718-4e04-b0a9-116775e3321b = 1
                // MF_MT_VIDEO_NOMINAL_RANGE c21b8ee5-b956-4071-8daf-325edf5cab11 = 2
                // MF_MT_VIDEO_ROTATION c380465d-2271-428c-9b83-ecea3b4a85c1 = 0
                // MF_MT_FRAME_RATE c459a2e8-3d2c-4e44-b132-fee5156c7bb0
                // MF_MT_PIXEL_ASPECT_RATIO c6376a1e-8d0a-4027-be45-6d9a0ad39bb6
                // MF_MT_ALL_SAMPLES_INDEPENDENT c9173739-5e56-461c-b713-46fb995cb95f = 1
                // MF_MT_MINIMUM_DISPLAY_APERTURE d7388766-18fe-48c6-a177-ee894867c8c4 = 16
                // MF_MT_SAMPLE_SIZE dad3ab78-1990-408b-bce2-eba673dacc10 = 353280
                // MF_MT_VIDEO_PRIMARIES dbfbe4d7-0740-4ee0-8192-850ab0e21935 = 5
                // MF_MT_INTERLACE_MODE e2724bb8-e676-4806-b4b2-a8d6efb44ccd = 7
                // MF_MT_SUBTYPE f7e34c9a-42e8-4714-b74b-cb29d72c35e5
                if (Log.WarnEnabled) Log.Warn(log.ToString());

                transform.SetOutputType(streamID, mediaType, 0);

                // because the subtype has not changed, do not flush, otherwise we'd lose frames:
                //transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, default);
                dataBuffer[0].dwStatus = 0;
            }
            else if (outputResult.Value == MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                if (Log.DebugEnabled) Log.Debug("MFT needs more input");
                length = 0;
            }
            else if (outputResult.Value == 0 && decoderOutputStatus == 0)
            {
                sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
                ret = MediaUtils.CopyBuffer(buffer, bytes, out length);
            }
            else
            {
                length = 0;
                MediaUtils.Check(outputResult);
            }

            return ret;
        }

        public static IMFTransform CreateTransform(Guid category, MFT_ENUM_FLAG flags, MFT_REGISTER_TYPE_INFO? input, MFT_REGISTER_TYPE_INFO? output)
        {
            IMFTransform transform = default;

            foreach (IMFActivate activate in FindTransforms(category, flags, input, output))
            {
                try
                {
                    activate.GetAllocatedString(PInvoke.MFT_FRIENDLY_NAME_Attribute, out PWSTR name, out _);
                    if (Log.InfoEnabled) Log.Info($"Found MFT: {name}");
                    transform = activate.ActivateObject(typeof(IMFTransform).GUID) as IMFTransform;
                    break;
                }
                finally
                {
                    Marshal.ReleaseComObject(activate);
                }
            }

            return transform;
        }

        public static void DestroyTransform(IMFTransform transform)
        {
            Marshal.ReleaseComObject(transform);
        }

        public static IEnumerable<IMFActivate> FindTransforms(Guid category, MFT_ENUM_FLAG flags, MFT_REGISTER_TYPE_INFO? input, MFT_REGISTER_TYPE_INFO? output)
        {
            MediaUtils.Check(PInvoke.MFTEnumEx(category, flags, input, output, out IMFActivate[] activates, out uint activateCount));

            if (activateCount > 0)
            {
                foreach (IMFActivate activate in activates)
                {
                    yield return activate;
                }
            }
        }
    }
}
