using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation
{
    public abstract class MFTBase
    {
        [Flags]
        public enum MFT_OUTPUT_DATA_BUFFER_FLAGS : uint
        {
            None = 0x00,
            FormatChange = 0x100,
            Incomplete = 0x1000000,
        }

        static MFTBase()
        {
            MFTUtils.Check(PInvoke.MFStartup(PInvoke.MF_API_VERSION, 0));
        }

        protected bool ProcessInput(IMFTransform decoder, byte[] data, long sampleDuration, long timestamp)
        {
            IMFSample sample = MFTUtils.CreateSample(data, sampleDuration, timestamp);
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
                MFTUtils.Check(decoder.GetInputStatus(streamID, out uint decoderInputFlags));
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

            if (dataBuffer[0].pEvents != null)
            {
                Marshal.ReleaseComObject(dataBuffer[0].pEvents);
                dataBuffer[0].pEvents = null;
            }

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
                dataBuffer[0].pSample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
                try
                {
                    uint maxLength = default;
                    uint currentLength = default;
                    byte* data = default;
                    buffer.Lock(&data, &maxLength, &currentLength);
                    Marshal.Copy((IntPtr)data, bytes, 0, (int)currentLength);
                    length = currentLength;
                    return true;
                }
                finally
                {
                    buffer.SetCurrentLength(0);
                    buffer.Unlock();
                }
            }

            length = 0;
            return false;
        }
    }
}
