using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Utils
{
    public static class MediaUtils
    {
        public static uint RoundToMultipleOf(uint value, uint multiple)
        {
            return (value + multiple - 1) / multiple * multiple;
        }

        /// <summary>Media Foundation's unit of time, 100 ns: the ticks of a second.</summary>
        public const long TicksPerSecond = 10_000_000;

        /// <summary>
        /// A time of a clock of <paramref name="clockRate"/> ticks a second in 100 ns units, worked out wide so that no
        /// length of stream overflows it on the way.
        /// </summary>
        public static long ToTicks(long time, long clockRate)
        {
            return (long)((Int128)time * TicksPerSecond / clockRate);
        }

        [SupportedOSPlatform("windows10.0.17763.0")]
        public static void Check(HRESULT result)
        {
            if (result.Failed)
                Marshal.ThrowExceptionForHR(result.Value);
        }

        [SupportedOSPlatform("windows10.0.17763.0")]
        public static IMFSample CreateSample(byte[] data, long sampleDuration, long timestamp)
        {
            return CreateSample(ReadOnlySpan<byte>.Empty, data, sampleDuration, timestamp);
        }

        [SupportedOSPlatform("windows10.0.17763.0")]
        public static IMFSample CreateSample(ReadOnlySpan<byte> data, long sampleDuration, long timestamp)
        {
            return CreateSample(ReadOnlySpan<byte>.Empty, data, sampleDuration, timestamp);
        }

        /// <summary>
        /// A sample of <paramref name="prefix"/> and then <paramref name="data"/>, copied straight into the media buffer:
        /// a start code goes in front of a NAL unit without the two being put together in an array first.
        /// </summary>
        [SupportedOSPlatform("windows10.0.17763.0")]
        public static unsafe IMFSample CreateSample(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> data, long sampleDuration, long timestamp)
        {
            uint length = (uint)(prefix.Length + data.Length);
            Check(PInvoke.MFCreateMemoryBuffer(length, out IMFMediaBuffer buffer));

            try
            {
                uint maxLength = default;
                uint currentLength = default;
                byte* target = default;
                buffer.Lock(&target, &maxLength, &currentLength);
                prefix.CopyTo(new Span<byte>(target, prefix.Length));
                data.CopyTo(new Span<byte>(target + prefix.Length, data.Length));
            }
            finally
            {
                buffer.SetCurrentLength(length);
                buffer.Unlock();
            }

            Check(PInvoke.MFCreateSample(out IMFSample sample));
            sample.AddBuffer(buffer);
            if (sampleDuration > 0) // not known where the frame rate is not
                sample.SetSampleDuration(sampleDuration);
            sample.SetSampleTime(timestamp); // timestamp is required

            return sample;
        }

        [SupportedOSPlatform("windows10.0.17763.0")]
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

        [SupportedOSPlatform("windows10.0.17763.0")]
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

        [SupportedOSPlatform("windows10.0.17763.0")]
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
