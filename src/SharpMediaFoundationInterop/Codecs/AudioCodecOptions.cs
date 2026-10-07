namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>The audio formats a codec of <see cref="MediaCodecs"/> can be asked for.</summary>
    public enum AudioCodec
    {
        /// <summary>AAC, raw - not ADTS: as MP4 carries it. 16 bit PCM on the other side.</summary>
        AAC,

        /// <summary>Opus, of 20 ms frames. 32 bit float on the other side.</summary>
        Opus,

        /// <summary>MP3: MPEG-1 and MPEG-2 Layer III. 16 bit PCM on the other side.</summary>
        Mp3,

        /// <summary>FLAC, of the stream's bits: 16 or 24 bit PCM on the other side.</summary>
        Flac,

        /// <summary>Apple Lossless, of the stream's bits: 16 or 24 bit PCM on the other side.</summary>
        Alac
    }

    /// <summary>What a decoder of <see cref="MediaCodecs.CreateAudioDecoder"/> is made for.</summary>
    public sealed class AudioDecoderOptions
    {
        public uint Channels { get; set; } = 2;

        public uint SampleRate { get; set; } = 48000;

        /// <summary>
        /// The codec's configuration, as the container carries it, which the channels, rate and bits of a lossless stream
        /// are read of: of AAC the AudioSpecificConfig; of FLAC the metadata blocks, each with its header, STREAMINFO first -
        /// what the 'dfLa' box holds; of ALAC the ALACSpecificConfig. Opus and MP3 need none.
        /// </summary>
        public byte[] Config { get; set; }

        /// <summary>
        /// Samples, of each channel, at the start of what the decoder hands out that are not of the sound: Opus's pre-skip,
        /// which the decoder takes to settle in. Dropped again after a flush - a seek - as the decoder settles in anew.
        /// </summary>
        public uint SkipSamples { get; set; }
    }

    /// <summary>What an encoder of <see cref="MediaCodecs.CreateAudioEncoder"/> is made for.</summary>
    public sealed class AudioEncoderOptions
    {
        public uint Channels { get; set; } = 2;

        /// <summary>
        /// Of AAC, 44100 or 48000 alone: what Windows' AAC encoder takes; of MP3, 32000, 44100, 48000 or MPEG-2's lower rates.
        /// </summary>
        public uint SampleRate { get; set; } = 48000;

        /// <summary>The bits of the PCM in: 16, or of FLAC and ALAC 24 as well. Opus takes 32 bit float.</summary>
        public uint BitsPerSample { get; set; } = 16;

        /// <summary>Of MP3, the bit rate in bits a second: the nearest the encoder offers not above it.</summary>
        public uint Bitrate { get; set; } = 192000;
    }
}
