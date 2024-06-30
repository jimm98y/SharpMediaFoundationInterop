using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaCoder
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

    public interface IDecoder
    {
        bool ProcessInput(byte[] data, long ticks);
        bool ProcessOutput(ref byte[] buffer);
    }

    public abstract class MFTBase
    {
        private SemaphoreSlim _semaphore = new SemaphoreSlim(1);
        private ulong _sampleDuration = 1;
        private uint _fps;
        private bool _isFirst = true;

        protected MFTBase(int fps)
        {
            this._fps = (uint)fps;
            MFTUtils.Check(PInvoke.MFFrameRateToAverageTimePerFrame(_fps, 1, out _sampleDuration));
        }

        static MFTBase()
        {
            MFTUtils.Check(PInvoke.MFStartup(PInvoke.MF_API_VERSION, 0));
        }

        protected bool ProcessInput(IMFTransform decoder, byte[] data, long ticks)
        {
            try
            {
                _semaphore.Wait();
                var sample = MFTUtils.CreateSample(data, (long)_sampleDuration, ticks);
                return Input(0, decoder, sample);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        protected bool ProcessOutput(IMFTransform decoder, MFT_OUTPUT_DATA_BUFFER[] dataBuffer, ref byte[] buffer)
        {
            try
            {
                _semaphore.Wait();
                return Output(0, decoder, dataBuffer, ref buffer);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private unsafe bool Input(uint streamID, IMFTransform decoder, IMFSample sample)
        {
            try
            {
                HRESULT result = decoder.GetInputStatus(streamID, out uint decoderInputFlags);

                if (result.Value == 0)
                {
                    if (_isFirst)
                    {
                        this._isFirst = false;
                        decoder.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, default);
                        decoder.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, default);
                        decoder.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, default);
                    }

                    result = decoder.ProcessInput(streamID, sample, 0);
                    return result.Value == 0;
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

        private unsafe bool Output(uint streamID, IMFTransform decoder, MFT_OUTPUT_DATA_BUFFER[] dataBuffer, ref byte[] bytes)
        {
            uint decoderOutputStatus;
            HRESULT outputResult = decoder.ProcessOutput(0, dataBuffer, out decoderOutputStatus);

            if (dataBuffer[0].dwStatus == (uint)MFT_OUTPUT_DATA_BUFFERFlags.FormatChange)
            {
                decoder.GetOutputAvailableType(streamID, 0, out IMFMediaType mediaType);
                decoder.SetOutputType(streamID, mediaType, 0);
                decoder.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, default);
                dataBuffer[0].dwStatus = (uint)MFT_OUTPUT_DATA_BUFFERFlags.None;
            }
            else if (outputResult.Value == unchecked((int)0xc00d6d72))
            {
                // needs more input
                return false;
            }
            else if (outputResult.Value == 0 && decoderOutputStatus == 0)
            {
                dataBuffer[0].pSample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
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

            return false;
        }
    }

    public static class MFTUtils
    {
        public static IEnumerable<IMFActivate> FindTransforms(Guid category, MFT_ENUM_FLAG flags, MFT_REGISTER_TYPE_INFO? input, MFT_REGISTER_TYPE_INFO? output)
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

        public static void Check(HRESULT result)
        {
            if (result.Failed)
                Marshal.ThrowExceptionForHR(result.Value);
        }

        public static unsafe IMFSample CreateSample(byte[] data, long sampleDuration, long timestamp)
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
            sample.SetSampleDuration(sampleDuration);
            sample.SetSampleTime(timestamp);
            return sample;
        }

        public static MFT_OUTPUT_DATA_BUFFER[] CreateOutputDataBuffer(int size = 0)
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
    }
}
