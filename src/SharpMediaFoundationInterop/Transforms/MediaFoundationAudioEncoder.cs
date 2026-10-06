using System;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Transforms.AAC;
using SharpMediaFoundationInterop.Transforms.ALAC;
using SharpMediaFoundationInterop.Transforms.FLAC;
using SharpMediaFoundationInterop.Transforms.MP3;
using SharpMediaFoundationInterop.Transforms.Opus;

namespace SharpMediaFoundationInterop.Transforms
{
    /// <summary>
    /// An encoder of Media Foundation's, of any <see cref="AudioCodec"/>: the AACEncoder or OpusEncoder of the codec, made
    /// for the <see cref="AudioEncoderOptions"/>.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public sealed class MediaFoundationAudioEncoder : MediaFoundationAudioCodec, IMediaAudioEncoder
    {
        /// <summary>
        /// Of AAC, the bytes Media Foundation's user data holds before the AudioSpecificConfig: what is left of a
        /// HEAACWAVEINFO past its WAVEFORMATEX.
        /// </summary>
        private const int AacUserDataPrefix = 12;

        private byte[] _aacConfig;

        /// <summary>
        /// Of AAC the AudioSpecificConfig, known once initialized; of FLAC the metadata blocks, each with its header - what
        /// the 'dfLa' box holds - known once the first frame is read out and complete once drained; of ALAC the
        /// ALACSpecificConfig. Opus and MP3 have none.
        /// </summary>
        public byte[] Config => Transform switch
        {
            AACEncoder => _aacConfig,
            FlacEncoder flac => flac.MetadataBlocks,
            AlacEncoder alac => alac.MagicCookie,
            _ => null,
        };

        public MediaFoundationAudioEncoder(AudioCodec codec, AudioEncoderOptions options)
            : base(codec, Create(codec, options ?? throw new ArgumentNullException(nameof(options))))
        { }

        /// <summary>Whether there is an encoder of the codec here; whether it is installed shows as it is initialized.</summary>
        public static bool Supports(AudioCodec codec) => codec switch
        {
            AudioCodec.AAC or AudioCodec.Opus or AudioCodec.Mp3 or AudioCodec.Flac or AudioCodec.Alac => true,
            _ => false
        };

        public override void Initialize()
        {
            base.Initialize();

            if (Transform is AACEncoder aac && aac.UserData != null && aac.UserData.Length > AacUserDataPrefix)
                _aacConfig = aac.UserData.AsSpan(AacUserDataPrefix).ToArray();
        }

        private static AudioTransformBase Create(AudioCodec codec, AudioEncoderOptions o) => codec switch
        {
            AudioCodec.AAC => new AACEncoder(o.Channels, o.SampleRate),
            AudioCodec.Opus => new OpusEncoder(960, o.Channels, o.SampleRate, 32),
            AudioCodec.Mp3 => new Mp3Encoder(o.Channels, o.SampleRate, o.Bitrate),
            AudioCodec.Flac => new FlacEncoder(o.Channels, o.SampleRate, o.BitsPerSample),
            AudioCodec.Alac => new AlacEncoder(o.Channels, o.SampleRate, o.BitsPerSample),
            _ => throw new NotSupportedException($"No Media Foundation encoder of {codec}")
        };
    }
}
