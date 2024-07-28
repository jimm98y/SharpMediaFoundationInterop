using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpMediaFoundation.Utils;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation.Transforms
{
    public abstract class MediaTransformBase
    {
        public abstract Guid InputFormat { get; }
        public abstract Guid OutputFormat { get; }


        [Flags]
        public enum MFT_OUTPUT_DATA_BUFFER_FLAGS : uint
        {
            None = 0x00,
            FormatChange = 0x100,
            Incomplete = 0x1000000,
        }

        static MediaTransformBase()
        {
            MediaUtils.Check(PInvoke.MFStartup(PInvoke.MF_API_VERSION, 0));
        }

        protected bool ProcessInput(IMFTransform transform, byte[] data, long sampleDuration, long timestamp)
        {
            bool ret = false;
            IMFSample sample = MediaUtils.CreateSample(data, sampleDuration, timestamp);

            try
            {
                ret = Input(0, transform, sample);
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
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

            try
            {
                MediaUtils.Check(transform.GetInputStatus(streamID, out uint decoderInputFlags));
                MediaUtils.Check(transform.ProcessInput(streamID, sample, 0));
                ret = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
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
                Debug.WriteLine("MFT stream change requested");
                length = 0;

                IMFMediaType mediaType;
                uint i = 0;
                while (true)
                {
                    transform.GetOutputAvailableType(streamID, i++, out IMFMediaType mType);
                    mType.GetGUID(PInvoke.MF_MT_SUBTYPE, out var mSubtype);
                    if (mSubtype == OutputFormat)
                    {
                        // TODO: log format change
                        mediaType = mType;
                        break;
                    }
                }

                transform.SetOutputType(streamID, mediaType, 0);
                transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, default);
                dataBuffer[0].dwStatus = (uint)MFT_OUTPUT_DATA_BUFFER_FLAGS.None;
            }
            else if (outputResult.Value == MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                Debug.WriteLine("MFT needs more input");
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
                    Debug.WriteLine($"Found MFT: {name}");
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
