using SharpMediaFoundationInterop.Utils;
using System;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Transforms.Opus
{
    public class OpusEncoder : AudioTransformBase
    {
        public override Guid InputFormat => PInvoke.MFAudioFormat_Float;
        public override Guid OutputFormat => PInvoke.MFAudioFormat_Opus;

        public OpusEncoder(long sampleDuration = 960, uint channels = 2, uint sampleRate = 48000, uint bitsPerSample = 32) : base(sampleDuration, channels, sampleRate, bitsPerSample)
        {
        }

        protected override IMFTransform Create()
        {
            const uint streamId = 0;

            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Audio, guidSubtype = InputFormat };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Audio, guidSubtype = OutputFormat };

            IMFTransform transform = CreateTransform(PInvoke.MFT_CATEGORY_AUDIO_ENCODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE, input, output);
            if (transform == null) transform = CreateTransform(PInvoke.MFT_CATEGORY_AUDIO_ENCODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT, input, output);
            if (transform == null) throw new NotSupportedException($"Unsupported transform! Input: {InputFormat}, Output: {OutputFormat}");

            IMFMediaType mediaOutput;
            MediaUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
            mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, OutputFormat);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, Channels);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, SampleRate);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, BitsPerSample);
            mediaOutput.SetDouble(PInvoke.MF_MT_AUDIO_FLOAT_SAMPLES_PER_SECOND, SampleRate);
            MediaUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));

            IMFMediaType mediaInput;
            MediaUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
            mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, InputFormat);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 384000);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_CHANNEL_MASK, 3);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_BLOCK_ALIGNMENT, 8);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, Channels);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, SampleRate);
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, BitsPerSample);
            MediaUtils.Check(transform.SetInputType(streamId, mediaInput, 0));

            return transform;
        }
    }
}
