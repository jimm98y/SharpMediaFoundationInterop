using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation.Utils
{
    public static class MediaUtils
    {
        public static uint RoundToMultipleOf(uint value, uint multiple)
        {
            return (value + multiple - 1) / multiple * multiple;
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
                uint maxLength = default;
                uint currentLength = default;
                byte* target = default;
                buffer.Lock(&target, &maxLength, &currentLength);
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
            sample.SetSampleTime(timestamp); // timestamp is required

            return sample;
        }

        public static MFT_OUTPUT_DATA_BUFFER[] CreateOutputDataBuffer(uint size = 0)
        {
            MFT_OUTPUT_DATA_BUFFER[] result = new MFT_OUTPUT_DATA_BUFFER[1];

            if (size > 0)
            {
                Check(PInvoke.MFCreateMemoryBuffer(size, out IMFMediaBuffer buffer));
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

        public static unsafe bool CopyBuffer(IMFMediaBuffer buffer, byte[] sampleBytes, out uint sampleSize)
        {
            bool ret = false;
            try
            {
                uint maxLength = default;
                uint currentLength = default;
                byte* data = default;
                buffer.Lock(&data, &maxLength, &currentLength);
                sampleSize = currentLength;
                if (sampleBytes != null)
                {
                    Marshal.Copy((nint)data, sampleBytes, 0, (int)currentLength);
                }
                ret = true;
            }
            finally
            {
                buffer.SetCurrentLength(0);
                buffer.Unlock();
            }

            return ret;
        }

        public static long CalculateSampleDuration(uint fpsNom, uint fpsDenom)
        {
            Check(PInvoke.MFFrameRateToAverageTimePerFrame(fpsNom, fpsDenom, out ulong sampleDuration));
            return (long)sampleDuration;
        }

        public static ulong EncodeAttributeValue(uint highValue, uint lowValue)
        {
            return ((ulong)highValue << 32) + lowValue;
        }
    }
}
