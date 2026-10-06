using System;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Transforms.ALAC
{
    /// <summary>
    /// ALAC to PCM, of the stream's own bits: each input sample a frame, as MP4 carries it, the ALACSpecificConfig - the
    /// magic cookie - the decoder's user data. Windows' ALAC decoder says no output size, and given no buffer to fill
    /// waits: it is given one of a frame's PCM.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public class AlacDecoder : AudioTransformBase
    {
        public override Guid InputFormat => PInvoke.MFAudioFormat_ALAC;
        public override Guid OutputFormat => PInvoke.MFAudioFormat_PCM;

        /// <summary>The ALACSpecificConfig, of 24 bytes.</summary>
        public byte[] MagicCookie { get; }

        /// <summary>The samples of each channel a frame holds.</summary>
        public uint FrameLength { get; }

        protected override uint DefaultOutputSize => FrameLength * Channels * 4;

        public AlacDecoder(uint channels, uint sampleRate, uint bitsPerSample, byte[] magicCookie)
          : base(4096, channels, sampleRate, bitsPerSample)
        {
            if (magicCookie == null || magicCookie.Length < 24)
                throw new ArgumentException("ALAC needs its ALACSpecificConfig, of 24 bytes.", nameof(magicCookie));
            MagicCookie = magicCookie;
            FrameLength = (uint)((magicCookie[0] << 24) | (magicCookie[1] << 16) | (magicCookie[2] << 8) | magicCookie[3]);
            if (FrameLength == 0)
                FrameLength = 4096;
        }

        protected override IMFTransform Create()
        {
            const uint streamId = 0;

            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Audio, guidSubtype = InputFormat };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Audio, guidSubtype = OutputFormat };

            IMFTransform transform = CreateTransform(PInvoke.MFT_CATEGORY_AUDIO_DECODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE, input, output);
            if (transform == null) transform = CreateTransform(PInvoke.MFT_CATEGORY_AUDIO_DECODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT, input, output);
            if (transform == null) throw new NotSupportedException($"Unsupported transform! Input: {InputFormat}, Output: {OutputFormat}");

            IMFMediaType mediaInput;
            MediaUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
            mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, InputFormat);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, Channels);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, SampleRate);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, BitsPerSample);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, SampleRate * Channels * ((BitsPerSample + 7) / 8));
            mediaInput.SetBlob(PInvoke.MF_MT_USER_DATA, MagicCookie.AsSpan(0, 24).ToArray());
            MediaUtils.Check(transform.SetInputType(streamId, mediaInput, 0));

            // PCM of the stream's bits, the one type it offers
            MediaUtils.Check(transform.GetOutputAvailableType(streamId, 0, out IMFMediaType mediaOutput));
            MediaUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));

            transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, default);
            transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, default);
            transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, default);

            return transform;
        }
    }
}
