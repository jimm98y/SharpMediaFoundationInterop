using System;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Transforms.MP3
{
    /// <summary>
    /// MP3 to 16 bit PCM: each input sample a frame, or several. Windows' MP3 decoder offers 32 bit float first: the 16 bit
    /// integer type is chosen.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public class Mp3Decoder : AudioTransformBase
    {
        public override Guid InputFormat => PInvoke.MFAudioFormat_MP3;
        public override Guid OutputFormat => PInvoke.MFAudioFormat_PCM;

        public Mp3Decoder(uint channels, uint sampleRate)
          : base(1152, channels, sampleRate, 16)
        { }

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
            mediaInput.SetUINT32(PInvoke.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            MediaUtils.Check(transform.SetInputType(streamId, mediaInput, 0));

            IMFMediaType mediaOutput = null;
            for (uint i = 0; transform.GetOutputAvailableType(streamId, i, out IMFMediaType offered).Succeeded; i++)
            {
                offered.GetGUID(PInvoke.MF_MT_SUBTYPE, out Guid subtype);
                offered.GetUINT32(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, out uint bits);
                if (subtype == OutputFormat && bits == BitsPerSample)
                {
                    mediaOutput = offered;
                    break;
                }
            }
            if (mediaOutput == null) throw new NotSupportedException("The MP3 decoder offers no 16 bit PCM");
            MediaUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));

            transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, default);
            transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, default);
            transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, default);

            return transform;
        }
    }
}
