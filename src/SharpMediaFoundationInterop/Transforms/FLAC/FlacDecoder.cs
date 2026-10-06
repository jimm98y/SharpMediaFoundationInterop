using System;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Transforms.FLAC
{
    /// <summary>
    /// FLAC to PCM, of the stream's own bits: each input sample a frame, as MP4 carries it. Windows' FLAC decoder takes the
    /// stream's metadata blocks in an attribute the SDK does not name - as Windows' own MP4 source gives them, seen with
    /// mftrace - and, given none, never decodes: it waits on a decoding thread it did not start.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public class FlacDecoder : AudioTransformBase
    {
        /// <summary>The stream's metadata blocks, each with its header, STREAMINFO first: what the 'dfLa' box holds.</summary>
        public static readonly Guid MF_MT_AUDIO_FLAC_METADATA = new Guid("3eadb782-73df-4c6c-bf3a-e31977954435");

        public override Guid InputFormat => PInvoke.MFAudioFormat_FLAC;
        public override Guid OutputFormat => PInvoke.MFAudioFormat_PCM;

        /// <summary>The stream's metadata blocks, each with its header, STREAMINFO first.</summary>
        public byte[] MetadataBlocks { get; }

        /// <summary>The samples of each channel a frame holds, at most.</summary>
        public uint MaxBlockSize { get; }

        protected override uint DefaultOutputSize => MaxBlockSize * Channels * 4;

        public FlacDecoder(uint channels, uint sampleRate, uint bitsPerSample, byte[] metadataBlocks, uint maxBlockSize)
          : base(maxBlockSize, channels, sampleRate, bitsPerSample)
        {
            MetadataBlocks = metadataBlocks ?? throw new ArgumentNullException(nameof(metadataBlocks));
            MaxBlockSize = maxBlockSize;
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
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_BLOCK_ALIGNMENT, Channels * ((BitsPerSample + 7) / 8));
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, SampleRate * Channels * ((BitsPerSample + 7) / 8));
            mediaInput.SetUINT32(PInvoke.MF_MT_AUDIO_FLAC_MAX_BLOCK_SIZE, MaxBlockSize);
            mediaInput.SetBlob(MF_MT_AUDIO_FLAC_METADATA, MetadataBlocks);
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
