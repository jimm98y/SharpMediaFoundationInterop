using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// An encoder of Media Foundation's, of H.264, H.265, VP9 or AV1: the H264Encoder, H265Encoder and so on of the codec,
    /// made for the <see cref="VideoEncoderOptions"/>, which go to it as ICodecAPI properties. NV12 in.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public sealed class MediaFoundationEncoder : MediaFoundationVideoCodec, IMediaVideoEncoder
    {
        private readonly Dictionary<Guid, string> _settings = new Dictionary<Guid, string>();
        private readonly List<string> _unapplied = new List<string>();

        public IReadOnlyList<string> UnappliedSettings => _unapplied;

        public MediaFoundationEncoder(VideoCodec codec, VideoEncoderOptions options)
            : base(codec, Create(codec, options ?? throw new ArgumentNullException(nameof(options))))
        {
            switch (options.RateControl)
            {
                case RateControlMode.Default:
                    break;

                case RateControlMode.ConstantBitrate:
                    Set(CodecApiProperties.RateControlMode, CodecApiProperties.RateControlModes.Cbr, nameof(options.RateControl));
                    break;

                case RateControlMode.VariableBitrate:
                    Set(CodecApiProperties.RateControlMode, CodecApiProperties.RateControlModes.UnconstrainedVbr, nameof(options.RateControl));
                    break;

                case RateControlMode.Quality:
                    Set(CodecApiProperties.RateControlMode, CodecApiProperties.RateControlModes.Quality, nameof(options.RateControl));
                    Set(CodecApiProperties.Quality, options.Quality, nameof(options.Quality));
                    break;

                case RateControlMode.ConstantQp:
                    // the quality mode, with the quantiser fixed: of every picture type alike, or the key frames get more
                    ulong qp = options.Qp;
                    Set(CodecApiProperties.RateControlMode, CodecApiProperties.RateControlModes.Quality, nameof(options.RateControl));
                    Set(CodecApiProperties.EncodeQp, qp, nameof(options.Qp));
                    Set(CodecApiProperties.EncodeFrameTypeQp, qp | (qp << 16) | (qp << 32), nameof(options.Qp) + " of each picture type");
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(options), options.RateControl, "unknown rate control mode");
            }

            if (options.KeyFrameInterval > 0)
                Set(CodecApiProperties.GopSize, options.KeyFrameInterval, nameof(options.KeyFrameInterval));
            if (options.Threads > 0)
                Set(CodecApiProperties.NumWorkerThreads, options.Threads, nameof(options.Threads));
        }

        /// <summary>Whether there is an encoder of the codec here; whether it is installed shows as it is initialized.</summary>
        public static bool Supports(VideoCodec codec) => codec switch
        {
            VideoCodec.H264 or VideoCodec.H265 or VideoCodec.VP9 or VideoCodec.AV1 => true,
            _ => false
        };

        public override void Initialize()
        {
            base.Initialize();

            _unapplied.Clear();
            foreach (var result in Transform.CodecPropertyResults)
                if (!result.Applied)
                    _unapplied.Add($"{_settings[result.Property]}: {(result.Supported ? "rejected" : "not supported")} by the encoder");
        }

        private void Set(Guid property, object value, string setting)
        {
            Transform.CodecProperties[property] = value;
            _settings[property] = setting;
        }

        private static VideoTransformBase Create(VideoCodec codec, VideoEncoderOptions o) => codec switch
        {
            VideoCodec.H264 => new H264Encoder(o.Width, o.Height, o.FpsNom, o.FpsDenom, o.Bitrate),
            VideoCodec.H265 => new H265Encoder(o.Width, o.Height, o.FpsNom, o.FpsDenom, o.Bitrate),
            VideoCodec.VP9 => new VP9Encoder(o.Width, o.Height, o.FpsNom, o.FpsDenom, o.Bitrate),
            VideoCodec.AV1 => new AV1Encoder(o.Width, o.Height, o.FpsNom, o.FpsDenom, o.Bitrate),
            _ => throw new NotSupportedException($"No Media Foundation encoder of {codec}")
        };
    }
}
