using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// PCM to FLAC, a frame of 4096 samples an output. Windows' FLAC encoder hands out the stream's header first - 'fLaC',
    /// then each metadata block - which is taken in, not handed on, as <see cref="MetadataBlocks"/>; and, as it drains, the
    /// final STREAMINFO - its total samples, frame sizes and MD5 - which completes it.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public class FlacEncoder : AudioTransformBase
    {
        public const uint FLAC_BLOCK_SIZE = 4096;

        /// <summary>
        /// The final stream info, an attribute of the sample of no data the encoder hands out as it drains, little-endian:
        /// the min and max block size, the min and max frame size, the rate, the channels and the bits in 32 bits each, the
        /// total samples in 64, then the MD5 of the PCM.
        /// </summary>
        private static readonly Guid FlacFinalStreamInfo = new Guid("3f4d3615-a581-43a1-9aa4-d8ded4f70d3d");

        public override Guid InputFormat => PInvoke.MFAudioFormat_PCM;
        public override Guid OutputFormat => PInvoke.MFAudioFormat_FLAC;

        private readonly List<byte[]> _blocks = new List<byte[]>();
        private bool _headerRead;

        /// <summary>
        /// The stream's metadata blocks, each with its header, STREAMINFO first and the last marked so - what the 'dfLa' box
        /// holds: known once the first frame is read out, its STREAMINFO complete once the encoder is drained. Null before.
        /// </summary>
        public byte[] MetadataBlocks
        {
            get
            {
                if (_blocks.Count == 0)
                    return null;
                var all = new List<byte>();
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var block = (byte[])_blocks[i].Clone();
                    block[0] = (byte)((block[0] & 0x7F) | (i == _blocks.Count - 1 ? 0x80 : 0));
                    all.AddRange(block);
                }
                return all.ToArray();
            }
        }

        public FlacEncoder(uint channels, uint sampleRate, uint bitsPerSample = 16)
          : base(FLAC_BLOCK_SIZE, channels, sampleRate, bitsPerSample)
        { }

        /// <summary>The next frame out: the pieces of the stream's header before it taken in as the metadata blocks.</summary>
        public override bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            while (base.ProcessOutput(ref buffer, out length))
            {
                if (_headerRead || length == 0 || (buffer[0] == 0xFF && (buffer[1] & 0xFE) == 0xF8))
                {
                    _headerRead = true;
                    return true;
                }

                // 'fLaC', then each block with its header
                if (!(length == 4 && buffer[0] == 'f' && buffer[1] == 'L' && buffer[2] == 'a' && buffer[3] == 'C'))
                    _blocks.Add(buffer.AsSpan(0, (int)length).ToArray());
            }
            return false;
        }

        /// <summary>The final stream info, which completes STREAMINFO's frame sizes, total samples and MD5.</summary>
        protected override unsafe void OnOutputWithoutData(IMFSample sample)
        {
            Guid key = FlacFinalStreamInfo;
            byte[] info;
            try
            {
                sample.GetBlobSize(&key, out uint size);
                if (size < 52)
                    return;
                info = new byte[size];
                sample.GetBlob(&key, info, size);
            }
            catch (Exception)
            {
                return; // not of the final stream info
            }
            if (_blocks.Count == 0 || (_blocks[0][0] & 0x7F) != 0 || _blocks[0].Length < 4 + 34)
                return;

            uint minBlock = BitConverter.ToUInt32(info, 0), maxBlock = BitConverter.ToUInt32(info, 4);
            uint minFrame = BitConverter.ToUInt32(info, 8), maxFrame = BitConverter.ToUInt32(info, 12);
            uint rate = BitConverter.ToUInt32(info, 16), channels = BitConverter.ToUInt32(info, 20), bits = BitConverter.ToUInt32(info, 24);
            ulong total = BitConverter.ToUInt64(info, 28);

            // STREAMINFO (RFC 9639 8.2), after the block's header: block sizes in 16 bits each, frame sizes in 24 each, the
            // rate in 20, the channels less one in 3, the bits less one in 5, the total samples in 36, then the MD5
            byte[] s = _blocks[0];
            int p = 4;
            s[p++] = (byte)(minBlock >> 8); s[p++] = (byte)minBlock;
            s[p++] = (byte)(maxBlock >> 8); s[p++] = (byte)maxBlock;
            s[p++] = (byte)(minFrame >> 16); s[p++] = (byte)(minFrame >> 8); s[p++] = (byte)minFrame;
            s[p++] = (byte)(maxFrame >> 16); s[p++] = (byte)(maxFrame >> 8); s[p++] = (byte)maxFrame;
            s[p++] = (byte)(rate >> 12); s[p++] = (byte)(rate >> 4);
            s[p++] = (byte)(((rate & 0xF) << 4) | (((channels - 1) & 0x7) << 1) | (((bits - 1) >> 4) & 0x1));
            s[p++] = (byte)((((bits - 1) & 0xF) << 4) | (uint)((total >> 32) & 0xF));
            s[p++] = (byte)(total >> 24); s[p++] = (byte)(total >> 16); s[p++] = (byte)(total >> 8); s[p++] = (byte)total;
            Buffer.BlockCopy(info, 36, s, p, 16);
        }

        protected override IMFTransform Create()
        {
            const uint streamId = 0;

            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Audio, guidSubtype = InputFormat };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Audio, guidSubtype = OutputFormat };

            IMFTransform transform = CreateTransform(PInvoke.MFT_CATEGORY_AUDIO_ENCODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT, input, output);
            if (transform == null) throw new NotSupportedException($"Unsupported transform! Input: {InputFormat}, Output: {OutputFormat}");

            MediaUtils.Check(transform.SetInputType(streamId, AudioEncoding.Pcm(Channels, SampleRate, BitsPerSample), 0));
            MediaUtils.Check(transform.SetOutputType(streamId, AudioEncoding.OutputType(transform, OutputFormat, Channels, SampleRate, BitsPerSample, 0), 0));

            transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, default);
            transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, default);

            return transform;
        }
    }
}
