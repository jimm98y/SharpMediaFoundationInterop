using System;
using System.Runtime.Versioning;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// A decoder of Media Foundation's, of any <see cref="AudioCodec"/>: the AACDecoder or OpusDecoder of the codec, made
    /// for the <see cref="AudioDecoderOptions"/>.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public sealed class MediaFoundationAudioDecoder : MediaFoundationAudioCodec
    {
        private readonly uint _skipSamples;

        /// <summary>What is left to drop of the <see cref="AudioDecoderOptions.SkipSamples"/>, in bytes.</summary>
        private uint _skipBytes;

        public MediaFoundationAudioDecoder(AudioCodec codec, AudioDecoderOptions options)
            : base(codec, Create(codec, options ?? throw new ArgumentNullException(nameof(options))))
        {
            _skipSamples = options.SkipSamples;
        }

        public override void Initialize()
        {
            base.Initialize();
            _skipBytes = SkipBytes;
        }

        public override void Flush()
        {
            base.Flush();
            _skipBytes = SkipBytes;
        }

        /// <summary>
        /// The next sound out, past what is to be skipped: that is moved off the front of the buffer, and an output that
        /// is all of it is passed over for the next.
        /// </summary>
        public override bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            while (Transform.ProcessOutput(ref buffer, out length))
            {
                if (_skipBytes == 0 || length == 0)
                    return true;

                uint skip = Math.Min(_skipBytes, length);
                _skipBytes -= skip;
                length -= skip;
                if (length > 0)
                {
                    Buffer.BlockCopy(buffer, (int)skip, buffer, 0, (int)length);
                    return true;
                }
            }
            return false;
        }

        private uint SkipBytes => _skipSamples * Channels * (BitsPerSample / 8);

        /// <summary>Whether there is a decoder of the codec here; whether it is installed shows as it is initialized.</summary>
        public static bool Supports(AudioCodec codec) => codec switch
        {
            AudioCodec.AAC or AudioCodec.Opus or AudioCodec.Mp3 or AudioCodec.Flac or AudioCodec.Alac => true,
            _ => false
        };

        private static AudioTransformBase Create(AudioCodec codec, AudioDecoderOptions o) => codec switch
        {
            AudioCodec.AAC => new AACDecoder(o.Channels, o.SampleRate,
                AACDecoder.CreateUserData(o.Config ?? throw new ArgumentException("AAC needs its AudioSpecificConfig", nameof(o))),
                ChannelConfiguration(o.Config, o.Channels)),
            AudioCodec.Opus => new OpusDecoder(960, o.Channels, o.SampleRate, 32),
            AudioCodec.Mp3 => new Mp3Decoder(o.Channels, o.SampleRate),
            AudioCodec.Flac => CreateFlac(o.Config ?? throw new ArgumentException("FLAC needs its metadata blocks", nameof(o))),
            AudioCodec.Alac => CreateAlac(o.Config ?? throw new ArgumentException("ALAC needs its ALACSpecificConfig", nameof(o))),
            _ => throw new NotSupportedException($"No Media Foundation decoder of {codec}")
        };

        /// <summary>
        /// A FLAC decoder of the stream's STREAMINFO (RFC 9639 8.2), the first block, after its header: the max block size in
        /// its bytes 2 and 3, then past the frame sizes the rate in 20 bits, the channels less one in 3, the bits less one in 5.
        /// </summary>
        private static FlacDecoder CreateFlac(byte[] blocks)
        {
            if (blocks.Length < 4 + 18 || (blocks[0] & 0x7F) != 0)
                throw new ArgumentException("FLAC's metadata blocks begin with STREAMINFO.", nameof(blocks));
            var s = blocks.AsSpan(4);
            uint maxBlockSize = (uint)((s[2] << 8) | s[3]);
            uint rate = (uint)((s[10] << 12) | (s[11] << 4) | (s[12] >> 4));
            uint channels = (uint)(((s[12] >> 1) & 0x7) + 1);
            uint bits = (uint)((((s[12] & 0x1) << 4) | (s[13] >> 4)) + 1);
            return new FlacDecoder(channels, rate, bits, blocks, maxBlockSize);
        }

        /// <summary>An ALAC decoder of the stream's ALACSpecificConfig: the bits at byte 5, the channels at 9, the rate at 20.</summary>
        private static AlacDecoder CreateAlac(byte[] config)
        {
            if (config.Length < 24)
                throw new ArgumentException("An ALACSpecificConfig is of 24 bytes.", nameof(config));
            uint rate = (uint)((config[20] << 24) | (config[21] << 16) | (config[22] << 8) | config[23]);
            return new AlacDecoder(config[9], rate, config[5], config);
        }

        /// <summary>
        /// The channel configuration of an AudioSpecificConfig: 5 bits of object type, 4 of sampling frequency index, then 4
        /// of it. Past an escaped object type or a frequency given outright the fields move along, and the channel count
        /// stands in, as it does for a configuration of 0 - one the config describes in a table of its own.
        /// </summary>
        private static int ChannelConfiguration(byte[] config, uint channels)
        {
            if (config.Length >= 2)
            {
                int objectType = config[0] >> 3;
                int frequencyIndex = ((config[0] & 0x07) << 1) | (config[1] >> 7);
                int channelConfiguration = (config[1] >> 3) & 0x0F;
                if (objectType != 31 && frequencyIndex != 15 && channelConfiguration != 0)
                    return channelConfiguration;
            }
            return (int)channels;
        }
    }
}
