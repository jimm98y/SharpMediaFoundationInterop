using System;
using System.Linq;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation.AAC
{
    public class AACDecoder : AudioTransformBase
    {
        public override Guid InputFormat => PInvoke.MFAudioFormat_AAC;
        public override Guid OutputFormat => PInvoke.MFAudioFormat_PCM;

        public uint SampleRateOut { get; private set; }
        public byte[] UserData { get; private set; }

        public AACDecoder(uint channels, uint sampleRate, byte[] userData, uint sampleRateOut = 0) 
          : base(1024, channels, sampleRate, 16) // PCM = 16 bit, Float = 32 bit
        {
            if(sampleRateOut != 0 && sampleRateOut != 44100 && sampleRateOut != 48000)
            {
                throw new ArgumentException(
                    $"MediaFoundation AAC decoder does not support sample rate {sampleRate} Hz on the output. " +
                    $"The only supported output sample rates are 44100 and 48000 Hz.");
            }

            if (sampleRateOut == 0)
            {
                if (sampleRateOut == 44100 || sampleRateOut == 48000)
                    sampleRateOut = sampleRate;
                else
                    sampleRateOut = 44100; // default
            }

            if (userData == null)
            {
                throw new ArgumentNullException(nameof(userData));
            }

            this.SampleRateOut = sampleRateOut;
            this.UserData = userData;
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
            MFUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
            mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, InputFormat);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, BitsPerSample);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, SampleRate);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, Channels);
            //mediaInput.SetUINT32(PInvoke.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x2A);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 16000 * Channels);
            mediaInput.SetUINT32(PInvoke.MF_MT_AAC_PAYLOAD_TYPE, 0); // 0 = Raw, 1 = ADTS
            mediaInput.SetBlob(PInvoke.MF_MT_USER_DATA, UserData);
            MFUtils.Check(transform.SetInputType(streamId, mediaInput, 0));

            IMFMediaType mediaOutput;
            MFUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
            mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, OutputFormat);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, Channels);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, SampleRateOut);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, BitsPerSample);
            MFUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));

            return transform;
        }

        public static byte[] CreateUserData(byte[] audioSpecificConfig)
        {
            var b = new byte[] { 0x00, 0x00, 0xFE, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            return b.Concat(audioSpecificConfig).ToArray();
        }
    }
}
