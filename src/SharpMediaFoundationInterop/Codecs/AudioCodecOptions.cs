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

        /// <summary>MPEG-1 and MPEG-2 audio of Layers I and II - MP2. 16 bit PCM on the other side.</summary>
        Mp2,


        /// <summary>FLAC, of the stream's bits: 16 or 24 bit PCM on the other side.</summary>
        Flac,

        /// <summary>Apple Lossless, of the stream's bits: 16 or 24 bit PCM on the other side.</summary>
        Alac,

        /// <summary>
        /// Uncompressed PCM, of <see cref="AudioDecoderOptions.Pcm"/>'s format: 16 bit PCM on the other side, of 16 bits or
        /// fewer, else 32. Of <see cref="PcmDecoder"/>, of every platform.
        /// </summary>
        Pcm
    }

    /// <summary>How PCM's samples are coded: linearly, or companded as G.711 has them.</summary>
    public enum PcmCompanding
    {
        /// <summary>Linear: each sample its value.</summary>
        None,

        /// <summary>G.711 A-law: a byte of a 13 bit sample, QuickTime's 'alaw'.</summary>
        ALaw,

        /// <summary>G.711 mu-law: a byte of a 14 bit sample, QuickTime's 'ulaw'.</summary>
        MuLaw,
    }

    /// <summary>
    /// The samples of uncompressed PCM, as a container says them - QuickTime's 'lpcm', 'twos', 'sowt', ISO's 'ipcm' - and of
    /// G.711's 'alaw' and 'ulaw', of 8 bits companded.
    /// </summary>
    public sealed class PcmFormat
    {
        /// <summary>The bits of a sample of one channel: 8, 16, 24 or 32 of an integer, 32 or 64 of a float.</summary>
        public uint BitsPerSample { get; set; } = 16;

        public bool IsFloat { get; set; }

        /// <summary>Whether integers are signed: all but QuickTime's 8 bit 'raw ', of offset binary.</summary>
        public bool IsSigned { get; set; } = true;

        public bool IsLittleEndian { get; set; } = true;

        /// <summary>Of G.711, how the samples - of 8 bits - are companded: expanded to 16 bit PCM.</summary>
        public PcmCompanding Companding { get; set; }
    }

    /// <summary>What a decoder of <see cref="MediaCodecs.CreateAudioDecoder"/> is made for.</summary>
    public sealed class AudioDecoderOptions
    {
        public uint Channels { get; set; } = 2;

        public uint SampleRate { get; set; } = 48000;

        /// <summary>
        /// The codec's configuration, as the container carries it, which the channels, rate and bits of a lossless stream
        /// are read of: of AAC the AudioSpecificConfig; of FLAC the metadata blocks, each with its header, STREAMINFO first -
        /// what the 'dfLa' box holds; of ALAC the ALACSpecificConfig; of Opus the 'dOps' box's payload, of its channel mapping -
        /// needed of more than two channels, which <see cref="OpusMultistreamDecoder"/> decodes. MP3 needs none.
        /// </summary>
        public byte[] Config { get; set; }

        /// <summary>
        /// Samples, of each channel, at the start of what the decoder hands out that are not of the sound: Opus's pre-skip,
        /// which the decoder takes to settle in. Dropped again after a flush - a seek - as the decoder settles in anew.
        /// </summary>
        public uint SkipSamples { get; set; }

        /// <summary>Of <see cref="AudioCodec.Pcm"/>, the samples' format; 16 bit little-endian integers where null.</summary>
        public PcmFormat Pcm { get; set; }
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
