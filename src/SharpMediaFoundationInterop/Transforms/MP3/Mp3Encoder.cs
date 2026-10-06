using System;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Transforms.MP3
{
    /// <summary>
    /// 16 bit PCM to MP3, at a bit rate the encoder offers: the nearest not above the one asked for. An output is a frame,
    /// or, as it drains, several. Windows' MP3 encoder takes its output type first, of its own list - 32000, 44100 and
    /// 48000 Hz, and MPEG-2's lower rates - and its PCM only then.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public class Mp3Encoder : AudioTransformBase
    {
        public override Guid InputFormat => PInvoke.MFAudioFormat_PCM;
        public override Guid OutputFormat => PInvoke.MFAudioFormat_MP3;

        /// <summary>The bit rate asked for, in bits a second; <see cref="AvgBitrate"/> the one encoded at.</summary>
        public uint Bitrate { get; }

        /// <summary>The bit rate encoded at, of the encoder's list, in bits a second: known once initialized.</summary>
        public uint AvgBitrate { get; private set; }

        public Mp3Encoder(uint channels, uint sampleRate, uint bitrate = 192000)
          : base(1152, channels, sampleRate, 16)
        {
            Bitrate = bitrate;
        }

        protected override IMFTransform Create()
        {
            const uint streamId = 0;

            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Audio, guidSubtype = InputFormat };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Audio, guidSubtype = OutputFormat };

            IMFTransform transform = CreateTransform(PInvoke.MFT_CATEGORY_AUDIO_ENCODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT, input, output);
            if (transform == null) throw new NotSupportedException($"Unsupported transform! Input: {InputFormat}, Output: {OutputFormat}");

            var mediaOutput = AudioEncoding.OutputType(transform, OutputFormat, Channels, SampleRate, 0, Bitrate / 8);
            mediaOutput.GetUINT32(PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, out uint bytesPerSecond);
            AvgBitrate = bytesPerSecond * 8;
            MediaUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));
            MediaUtils.Check(transform.SetInputType(streamId, AudioEncoding.Pcm(Channels, SampleRate, BitsPerSample), 0));

            transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, default);
            transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, default);

            return transform;
        }
    }
}
