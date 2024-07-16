using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation
{
    public abstract class MFTBase
    {
        public static readonly Guid IID_IMFTransform = new Guid("BF94C121-5B05-4E6F-8000-BA598961414D");

        [Flags]
        public enum MFT_OUTPUT_DATA_BUFFER_FLAGS : uint
        {
            None = 0x00,
            FormatChange = 0x100,
            Incomplete = 0x1000000,
        }

        static MFTBase()
        {
            MFUtils.Check(PInvoke.MFStartup(PInvoke.MF_API_VERSION, 0));
        }

        protected bool ProcessInput(IMFTransform decoder, byte[] data, long sampleDuration, long timestamp)
        {
            IMFSample sample = MFUtils.CreateSample(data, sampleDuration, timestamp);
            return Input(0, decoder, sample);
        }

        protected bool ProcessOutput(IMFTransform decoder, MFT_OUTPUT_DATA_BUFFER[] dataBuffer, ref byte[] buffer, out uint length)
        {
            return Output(0, decoder, dataBuffer, ref buffer, out length);
        }

        private unsafe bool Input(uint streamID, IMFTransform decoder, IMFSample sample)
        {
            try
            {
                MFUtils.Check(decoder.GetInputStatus(streamID, out uint decoderInputFlags));
                HRESULT result = decoder.ProcessInput(streamID, sample, 0);
                return result.Value == 0;
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }
        }

        private unsafe bool Output(uint streamID, IMFTransform decoder, MFT_OUTPUT_DATA_BUFFER[] dataBuffer, ref byte[] bytes, out uint length)
        {
            const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xc00d6d72);
            uint decoderOutputStatus;
            HRESULT outputResult = decoder.ProcessOutput(0, dataBuffer, out decoderOutputStatus);
            IMFSample sample = dataBuffer[0].pSample;

            if (dataBuffer[0].dwStatus == (uint)MFT_OUTPUT_DATA_BUFFER_FLAGS.FormatChange)
            {
                decoder.GetOutputAvailableType(streamID, 0, out IMFMediaType mediaType);
                decoder.SetOutputType(streamID, mediaType, 0);
                decoder.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, default);
                dataBuffer[0].dwStatus = (uint)MFT_OUTPUT_DATA_BUFFER_FLAGS.None;
            }
            else if (outputResult.Value == MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                length = 0;
                return false;
            }
            else if (outputResult.Value == 0 && decoderOutputStatus == 0)
            {
                
                sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
                return MFUtils.CopyBuffer(buffer, bytes, out length);
            }

            length = 0;
            return false;
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
                    transform = activate.ActivateObject(IID_IMFTransform) as IMFTransform;
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
            MFUtils.Check(PInvoke.MFTEnumEx(category, flags, input, output, out IMFActivate[] activates, out uint activateCount));

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
