namespace SharpMediaFoundationInterop.Transforms
{
    /// <summary>The audio formats a codec of <see cref="MediaCodecs"/> can be asked for.</summary>
    public enum AudioCodec
    {
        /// <summary>AAC, raw - not ADTS: as MP4 carries it. 16 bit PCM on the other side.</summary>
        AAC,

        /// <summary>Opus, of 20 ms frames. 32 bit float on the other side.</summary>
        Opus
    }

    /// <summary>What a decoder of <see cref="MediaCodecs.CreateAudioDecoder"/> is made for.</summary>
    public sealed class AudioDecoderOptions
    {
        public uint Channels { get; set; } = 2;

        public uint SampleRate { get; set; } = 48000;

        /// <summary>
        /// The codec's configuration, as the container carries it: of AAC the AudioSpecificConfig, which it needs; of Opus
        /// none is needed.
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

        /// <summary>Of AAC, 44100 or 48000 alone: what Windows' AAC encoder takes.</summary>
        public uint SampleRate { get; set; } = 48000;
    }
}
