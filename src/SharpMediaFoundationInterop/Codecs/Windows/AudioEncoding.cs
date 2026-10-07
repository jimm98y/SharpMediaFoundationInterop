using System;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>The media types of the PCM an audio encoder takes, and of what it gives out.</summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    internal static class AudioEncoding
    {
        /// <summary>Integer PCM of the format given.</summary>
        public static IMFMediaType Pcm(uint channels, uint sampleRate, uint bitsPerSample)
        {
            MediaUtils.Check(PInvoke.MFCreateMediaType(out IMFMediaType pcm));
            pcm.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            pcm.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFAudioFormat_PCM);
            pcm.SetUINT32(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, channels);
            pcm.SetUINT32(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, sampleRate);
            pcm.SetUINT32(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, bitsPerSample);
            pcm.SetUINT32(PInvoke.MF_MT_AUDIO_BLOCK_ALIGNMENT, channels * bitsPerSample / 8);
            pcm.SetUINT32(PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, sampleRate * channels * bitsPerSample / 8);
            pcm.SetUINT32(PInvoke.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            return pcm;
        }

        /// <summary>
        /// The output type the encoder offers of the subtype, channels and rate given; of the bits given, where it says bits;
        /// of the bytes a second given, where not 0 - the nearest it offers not above them, or the least.
        /// </summary>
        public static IMFMediaType OutputType(IMFTransform transform, Guid subtype, uint channels, uint sampleRate, uint bitsPerSample, uint bytesPerSecond)
        {
            IMFMediaType best = null;
            uint bestBytes = 0;
            for (uint i = 0; transform.GetOutputAvailableType(0, i, out IMFMediaType offered).Succeeded; i++)
            {
                offered.GetGUID(PInvoke.MF_MT_SUBTYPE, out Guid offeredSubtype);
                if (offeredSubtype != subtype || UInt32(offered, PInvoke.MF_MT_AUDIO_NUM_CHANNELS) != channels
                    || UInt32(offered, PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND) != sampleRate)
                    continue;
                uint bits = UInt32(offered, PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE);
                if (bits != 0 && bits != bitsPerSample)
                    continue;
                if (bytesPerSecond == 0)
                    return offered;

                uint bytes = UInt32(offered, PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND);
                bool better = best == null
                    || (bytes <= bytesPerSecond && (bestBytes > bytesPerSecond || bytes > bestBytes))
                    || (bytes > bytesPerSecond && bestBytes > bytesPerSecond && bytes < bestBytes);
                if (better)
                {
                    best = offered;
                    bestBytes = bytes;
                }
            }
            return best ?? throw new NotSupportedException(
                $"The encoder has no output of {channels} channels at {sampleRate} Hz{(bitsPerSample != 0 ? $", {bitsPerSample} bits" : "")}");
        }

        private static uint UInt32(IMFMediaType type, Guid key)
        {
            try
            {
                type.GetUINT32(key, out uint value);
                return value;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
