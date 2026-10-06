using System;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Transforms.AAC;
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

        public byte[] Config { get; private set; }

        public MediaFoundationAudioEncoder(AudioCodec codec, AudioEncoderOptions options)
            : base(codec, Create(codec, options ?? throw new ArgumentNullException(nameof(options))))
        { }

        /// <summary>Whether there is an encoder of the codec here; whether it is installed shows as it is initialized.</summary>
        public static bool Supports(AudioCodec codec) => codec switch
        {
            AudioCodec.AAC or AudioCodec.Opus => true,
            _ => false
        };

        public override void Initialize()
        {
            base.Initialize();

            if (Transform is AACEncoder aac && aac.UserData != null && aac.UserData.Length > AacUserDataPrefix)
                Config = aac.UserData.AsSpan(AacUserDataPrefix).ToArray();
        }

        private static AudioTransformBase Create(AudioCodec codec, AudioEncoderOptions o) => codec switch
        {
            AudioCodec.AAC => new AACEncoder(o.Channels, o.SampleRate),
            AudioCodec.Opus => new OpusEncoder(960, o.Channels, o.SampleRate, 32),
            _ => throw new NotSupportedException($"No Media Foundation encoder of {codec}")
        };
    }
}
