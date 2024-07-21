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

        public byte[] AudioSpecificConfig { get; private set; }

        public AACDecoder(uint channels, uint sampleRate, byte[] audioSpecificConfig) 
          : base(1024, channels, sampleRate, 16) // PCM = 16 bit, Float = 32 bit
        {
            this.AudioSpecificConfig = audioSpecificConfig;
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
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, Channels * SampleRate * BitsPerSample / 8);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_BLOCK_ALIGNMENT, Channels * BitsPerSample / 8);
            mediaInput.SetUINT32(PInvoke.MF_MT_AAC_PAYLOAD_TYPE, 0); // 0 = Raw, 1 = ADTS
            mediaInput.SetBlob(PInvoke.MF_MT_USER_DATA, CreateUserData());
            MFUtils.Check(transform.SetInputType(streamId, mediaInput, 0));

            const uint outSamplingRate = 44100; // only 44100 and 48000 are supported
            IMFMediaType mediaOutput;
            MFUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
            mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, OutputFormat);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, Channels);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, outSamplingRate);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, BitsPerSample);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AUDIO_BLOCK_ALIGNMENT, Channels * BitsPerSample / 8);
            mediaOutput.SetUINT32(PInvoke.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, Channels * outSamplingRate * BitsPerSample / 8);
            MFUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));

            return transform;
        }

        private byte[] CreateUserData()
        {
            var b = new byte[] { 0x00, 0x00, 0xFE, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            return b.Concat(AudioSpecificConfig).ToArray();
        }
    }
}
