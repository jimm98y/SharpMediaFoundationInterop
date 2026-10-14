using System.Runtime.Versioning;
using System;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Codecs
{
    [SupportedOSPlatform("windows10.0.17763.0")]
    public class AACEncoder : AudioTransformBase
    {
        public override Guid InputFormat => PInvoke.MFAudioFormat_PCM;
        public override Guid OutputFormat => PInvoke.MFAudioFormat_AAC;

        public byte[] UserData { get; private set; }

        public AACEncoder(uint channels, uint sampleRate)
          : base(1024, channels, sampleRate, 16) // PCM = 16 bit, Float = 32 bit
        {
            if (sampleRate != 44100 && sampleRate != 48000)
            {
                throw new ArgumentException(
                    $"MediaFoundation AAC encoder does not support sample rate {sampleRate} Hz. " +
                    $"The only supported sample rates are 44100 and 48000 Hz.");
            }
        }

        protected override unsafe IMFTransform Create()
        {
            const uint streamId = 0;

            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Audio, guidSubtype = InputFormat };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Audio, guidSubtype = OutputFormat };

            IMFTransform transform = CreateTransform(PInvoke.MFT_CATEGORY_AUDIO_ENCODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE, input, output);
            if (transform == null) transform = CreateTransform(PInvoke.MFT_CATEGORY_AUDIO_ENCODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT, input, output);
            if (transform == null) throw new NotSupportedException($"Unsupported transform! Input: {InputFormat}, Output: {OutputFormat}");

            // Of the types the encoder offers, of the channels and rate, the bit rate offered nearest 128 kbit/s a channel,
            // not above - AAC-LC's, whose rates are above HE-AAC's: its rates are its own of each count of channels. Of 3, 4
            // or 5 channels it offers none, nor takes a type of one's own making. Of 8 it encodes channel configuration 7,
            // ISO's 7.1 of front centre left and right, not of side ones: what players play as 7.1 has its front pair on the
            // sides, and Windows' decoder decodes no more than 6 - so not 8 either.
            if (Channels is not (1 or 2 or 6))
                throw new NotSupportedException($"Windows' AAC encoder encodes 1, 2 or 6 (5.1) channels, not {Channels}");
            IMFMediaType mediaOutput = AudioEncoding.OutputType(transform, OutputFormat, Channels, SampleRate, BitsPerSample, 16000 * Channels);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AAC_PAYLOAD_TYPE, 0); // 0 = Raw, 1 = ADTS
            MediaUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));

            IMFMediaType mediaInput;
            MediaUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
            mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, InputFormat);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, Channels);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, SampleRate);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, BitsPerSample);
            MediaUtils.Check(transform.SetInputType(streamId, mediaInput, 0));

            mediaOutput.GetBlobSize(PInvoke.MF_MT_USER_DATA, out var userDataSize);
            byte[] userData = new byte[userDataSize];
            var userDataMF = PInvoke.MF_MT_USER_DATA;
            mediaOutput.GetBlob(&userDataMF, userData, userDataSize);
            UserData = userData;

            return transform;
        }
    }
}
