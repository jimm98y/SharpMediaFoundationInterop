using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>Media Foundation's samples, buffers and results.</summary>
    public static partial class MediaUtils
    {
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
            // an object of its own, let go of as soon as it is done with
            return ComObjects.Take<IMFSample>(CreateRawSample(prefix, data, sampleDuration, timestamp));
        }

        /// <summary>
        /// The sample of <see cref="CreateSample(ReadOnlySpan{byte}, ReadOnlySpan{byte}, long, long)"/> as its pointer, of a
        /// reference the caller's to let go of: of no object - of each frame, nothing on the managed heap.
        /// </summary>
        [SupportedOSPlatform("windows10.0.17763.0")]
        internal static unsafe IMFSample_unmanaged* CreateRawSample(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> data, long sampleDuration, long timestamp)
        {
            uint length = (uint)(prefix.Length + data.Length);
            IMFMediaBuffer_unmanaged* buffer;
            Check(MediaObjects.CreateMemoryBufferRaw(length, &buffer));
            IMFSample_unmanaged* sample = null;
            try
            {
                byte* target = default;
                buffer->Lock(&target, null, null);
                try
                {
                    prefix.CopyTo(new Span<byte>(target, prefix.Length));
                    data.CopyTo(new Span<byte>(target + prefix.Length, data.Length));
                }
                finally
                {
                    buffer->SetCurrentLength(length);
                    buffer->Unlock();
                }

                Check(MediaObjects.CreateSampleRaw(&sample));
                sample->AddBuffer(buffer);
                if (sampleDuration > 0) // not known where the frame rate is not
                    sample->SetSampleDuration(sampleDuration);
                sample->SetSampleTime(timestamp); // timestamp is required
                return sample;
            }
            catch
            {
                if (sample != null)
                    sample->Release();
                throw;
            }
            finally
            {
                // the sample's to hold now
                buffer->Release();
            }
        }

        /// <summary>
        /// The buffer a transform's output is read into: of a size, a sample of ours of a buffer that large - its pointer's
        /// reference the buffer's, let go of with <see cref="ReleaseOutputDataBuffer"/>; of none, a transform's own samples.
        /// </summary>
        [SupportedOSPlatform("windows10.0.17763.0")]
        public static unsafe MFT_OUTPUT_DATA_BUFFER[] CreateOutputDataBuffer(uint size = 0)
        {
            MFT_OUTPUT_DATA_BUFFER[] result = new MFT_OUTPUT_DATA_BUFFER[1];

            if (size > 0)
            {
                IMFMediaBuffer buffer = MediaObjects.CreateMemoryBuffer(size);
                IMFSample sample = MediaObjects.CreateSample();
                sample.AddBuffer(buffer);
                result[0].pSample = (IMFSample_unmanaged*)ComObjects.PointerOf(sample);
                ComObjects.Release(buffer);
                ComObjects.Release(sample);
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

        /// <summary>Lets go of the sample of ours an output buffer holds, made with <see cref="CreateOutputDataBuffer"/>.</summary>
        [SupportedOSPlatform("windows10.0.17763.0")]
        public static unsafe void ReleaseOutputDataBuffer(MFT_OUTPUT_DATA_BUFFER[] dataBuffer)
        {
            if (dataBuffer == null)
                return;
            for (int i = 0; i < dataBuffer.Length; i++)
            {
                if (dataBuffer[i].pSample != null)
                    Marshal.Release((nint)dataBuffer[i].pSample);
                dataBuffer[i].pSample = null;
            }
        }

        /// <summary>
        /// The bytes of a buffer into the caller's array, grown to hold them: of a frame of padded rows - a decoder's of
        /// an odd width - what the buffer locks is more than it says its length is.
        /// </summary>
        [SupportedOSPlatform("windows10.0.17763.0")]
        public static unsafe bool CopyBuffer(IMFMediaBuffer buffer, ref byte[] sampleBytes, out uint sampleSize)
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
                    if (sampleBytes.Length < currentLength)
                        sampleBytes = new byte[currentLength];
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

        /// <summary>
        /// <see cref="CopyBuffer(IMFMediaBuffer, ref byte[], out uint)"/> of a buffer's pointer: of no object - of each frame,
        /// nothing on the managed heap.
        /// </summary>
        [SupportedOSPlatform("windows10.0.17763.0")]
        internal static unsafe bool CopyBuffer(IMFMediaBuffer_unmanaged* buffer, ref byte[] sampleBytes, out uint sampleSize)
        {
            bool ret = false;
            try
            {
                uint maxLength = default;
                uint currentLength = default;
                byte* data = default;
                buffer->Lock(&data, &maxLength, &currentLength);
                sampleSize = currentLength;
                if (sampleBytes != null)
                {
                    if (sampleBytes.Length < currentLength)
                        sampleBytes = new byte[currentLength];
                    new ReadOnlySpan<byte>(data, (int)currentLength).CopyTo(sampleBytes);
                }
                ret = true;
            }
            finally
            {
                buffer->SetCurrentLength(0);
                buffer->Unlock();
            }
            return ret;
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
    }
}
