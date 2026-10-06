using System;
using System.Buffers.Binary;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Transforms.ALAC
{
    /// <summary>
    /// PCM to ALAC, a frame of 4096 samples an output. Windows' ALAC encoder hands out no magic cookie: the
    /// ALACSpecificConfig of its frames is <see cref="MagicCookie"/>, of Apple's encoder's defaults - the parameters each
    /// frame says again for itself.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public class AlacEncoder : AudioTransformBase
    {
        public const uint ALAC_FRAME_LENGTH = 4096;

        public override Guid InputFormat => PInvoke.MFAudioFormat_PCM;
        public override Guid OutputFormat => PInvoke.MFAudioFormat_ALAC;

        /// <summary>
        /// The ALACSpecificConfig of the stream (ALACMagicCookieDescription.txt): frameLength 4096, compatibleVersion 0, the
        /// bits, pb 40, mb 10, kb 14, the channels, maxRun 255, maxFrameBytes and avgBitRate 0 - not known - and the rate.
        /// </summary>
        public byte[] MagicCookie { get; }

        public AlacEncoder(uint channels, uint sampleRate, uint bitsPerSample = 16)
          : base(ALAC_FRAME_LENGTH, channels, sampleRate, bitsPerSample)
        {
            var cookie = new byte[24];
            BinaryPrimitives.WriteUInt32BigEndian(cookie, ALAC_FRAME_LENGTH);
            cookie[5] = (byte)bitsPerSample;
            cookie[6] = 40; // pb
            cookie[7] = 10; // mb
            cookie[8] = 14; // kb
            cookie[9] = (byte)channels;
            cookie[11] = 255; // maxRun
            BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(20), sampleRate);
            MagicCookie = cookie;
        }

        protected override IMFTransform Create()
        {
            const uint streamId = 0;

            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Audio, guidSubtype = InputFormat };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Audio, guidSubtype = OutputFormat };

            IMFTransform transform = CreateTransform(PInvoke.MFT_CATEGORY_AUDIO_ENCODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT, input, output);
            if (transform == null) throw new NotSupportedException($"Unsupported transform! Input: {InputFormat}, Output: {OutputFormat}");

            // its PCM taken first where it takes it; else its output chosen first, of which it then takes the PCM
            var pcm = AudioEncoding.Pcm(Channels, SampleRate, BitsPerSample);
            if (transform.SetInputType(streamId, pcm, 0).Succeeded)
            {
                MediaUtils.Check(transform.SetOutputType(streamId, AudioEncoding.OutputType(transform, OutputFormat, Channels, SampleRate, BitsPerSample, 0), 0));
            }
            else
            {
                MediaUtils.Check(transform.SetOutputType(streamId, AudioEncoding.OutputType(transform, OutputFormat, Channels, SampleRate, BitsPerSample, 0), 0));
                MediaUtils.Check(transform.SetInputType(streamId, pcm, 0));
            }

            transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, default);
            transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, default);

            return transform;
        }
    }
}
