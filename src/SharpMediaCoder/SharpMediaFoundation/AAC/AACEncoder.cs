using System;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation.AAC
{
    public class AACEncoder : AudioTransformBase
    {
        public override Guid InputFormat => PInvoke.MFAudioFormat_PCM;
        public override Guid OutputFormat => PInvoke.MFAudioFormat_AAC;

        public byte[] UserData { get; private set; }

        public AACEncoder(uint channels, uint sampleRate) 
          : base(1024, channels, sampleRate, 16) // PCM = 16 bit, Float = 32 bit
        {  }

        protected override unsafe IMFTransform Create()
        {
            const uint streamId = 0;

            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Audio, guidSubtype = InputFormat };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Audio, guidSubtype = OutputFormat };

            IMFTransform transform = CreateTransform(PInvoke.MFT_CATEGORY_AUDIO_ENCODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE, input, output);
            if (transform == null) transform = CreateTransform(PInvoke.MFT_CATEGORY_AUDIO_ENCODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT, input, output);
            if (transform == null) throw new NotSupportedException($"Unsupported transform! Input: {InputFormat}, Output: {OutputFormat}");

            IMFMediaType mediaOutput;
            MFUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
            mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, OutputFormat);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, BitsPerSample);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, SampleRate);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, Channels);
            //mediaOutput.SetUINT32(PInvoke.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x2A);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 16000 * Channels);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AAC_PAYLOAD_TYPE, 0); // 0 = Raw, 1 = ADTS
            MFUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));

            mediaOutput.GetBlobSize(PInvoke.MF_MT_USER_DATA, out var userDataSize);
            byte[] userData = new byte[userDataSize];
            var userDataMF = PInvoke.MF_MT_USER_DATA;
            mediaOutput.GetBlob(&userDataMF, userData, userDataSize);
            this.UserData = userData;

            IMFMediaType mediaInput;
            MFUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
            mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, InputFormat);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, Channels);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, SampleRate);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, BitsPerSample);
            MFUtils.Check(transform.SetInputType(streamId, mediaInput, 0));

            return transform;
        }
    }
}
