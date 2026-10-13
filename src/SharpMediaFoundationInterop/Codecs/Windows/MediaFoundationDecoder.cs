using System;
using System.Runtime.Versioning;
using Windows.Win32;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// A decoder of Media Foundation's, of any <see cref="VideoCodec"/>: the H264Decoder, H265Decoder and so on of the
    /// codec, made for the <see cref="VideoDecoderOptions"/>. NV12 out, or of HEVC and AV1 any <see cref="VideoFrameFormat"/>. Given a <see cref="IMediaFoundationVideoTransform.DeviceManager"/>,
    /// it decodes on the GPU as the codec's own decoder does. Derived from by a decoder that hands out only some of the
    /// frames, overriding ProcessOutput.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public class MediaFoundationDecoder : MediaFoundationVideoCodec
    {
        public MediaFoundationDecoder(VideoCodec codec, VideoDecoderOptions options)
            : base(codec, Create(codec, options ?? throw new ArgumentNullException(nameof(options))))
        {
            if (options.Threads > 0)
                Transform.CodecProperties[CodecApiProperties.DecoderWorkerThreads] = options.Threads;
        }

        /// <summary>
        /// Whether there is a decoder of the codec here. Of VP9 and AV1, Windows' own come with extensions from the Store,
        /// so whether one is installed shows only as one is initialized: it throws <see cref="NotSupportedException"/>.
        /// </summary>
        public static bool Supports(VideoCodec codec) => codec switch
        {
            VideoCodec.H262 or VideoCodec.H263 or VideoCodec.Mpeg4 or VideoCodec.H264 or VideoCodec.H265 or VideoCodec.VP9 or VideoCodec.AV1 => true,
            _ => false
        };

        /// <summary>
        /// The multiple the codec's decoder rounds a picture's width and height up to: the size of the frames it hands out.
        /// </summary>
        public static uint Alignment(VideoCodec codec) => codec switch
        {
            VideoCodec.H262 => H262Decoder.H262_RES_MULTIPLE,
            VideoCodec.H263 => H263Decoder.H263_RES_MULTIPLE,
            VideoCodec.Mpeg4 => Mpeg4Decoder.MPEG4_RES_MULTIPLE,
            VideoCodec.H264 => H264Decoder.H264_RES_MULTIPLE,
            VideoCodec.H265 => H265Decoder.H265_RES_MULTIPLE,
            VideoCodec.VP9 => VP9Decoder.VP9_RES_MULTIPLE,
            VideoCodec.AV1 => AV1Decoder.AV1_RES_MULTIPLE,
            _ => 1
        };

        /// <summary>The Media Foundation subtype of a <see cref="VideoFrameFormat"/>.</summary>
        public static Guid SubtypeOf(VideoFrameFormat format) => format switch
        {
            VideoFrameFormat.NV12 => PInvoke.MFVideoFormat_NV12,
            VideoFrameFormat.P010 => PInvoke.MFVideoFormat_P010,
            VideoFrameFormat.P016 => PInvoke.MFVideoFormat_P016,
            VideoFrameFormat.YUY2 => PInvoke.MFVideoFormat_YUY2,
            VideoFrameFormat.Y210 => PInvoke.MFVideoFormat_Y210,
            VideoFrameFormat.Y216 => PInvoke.MFVideoFormat_Y216,
            VideoFrameFormat.AYUV => PInvoke.MFVideoFormat_AYUV,
            VideoFrameFormat.Y410 => PInvoke.MFVideoFormat_Y410,
            VideoFrameFormat.Y416 => PInvoke.MFVideoFormat_Y416,
            VideoFrameFormat.L8 => PInvoke.MFVideoFormat_L8,
            VideoFrameFormat.L16 => PInvoke.MFVideoFormat_L16,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        private static VideoTransformBase Create(VideoCodec codec, VideoDecoderOptions o) => codec switch
        {
            // other than NV12 of HEVC's and AV1's alone: the others' decoders hand out no more than 8 bits of 4:2:0
            _ when o.OutputFormat != VideoFrameFormat.NV12 && codec != VideoCodec.H265 && codec != VideoCodec.AV1
                => throw new NotSupportedException($"No Media Foundation decoder of {codec} in {o.OutputFormat}"),
            VideoCodec.H262 => new H262Decoder(o.Width, o.Height, o.FpsNom, o.FpsDenom, o.LowLatency),
            VideoCodec.H263 => new H263Decoder(o.Width, o.Height, o.FpsNom, o.FpsDenom, o.LowLatency),
            VideoCodec.Mpeg4 => new Mpeg4Decoder(o.Width, o.Height, o.FpsNom, o.FpsDenom, o.LowLatency),
            VideoCodec.H264 => new H264Decoder(o.Width, o.Height, o.FpsNom, o.FpsDenom, o.LowLatency),
            VideoCodec.H265 => new H265Decoder(o.Width, o.Height, o.FpsNom, o.FpsDenom, o.LowLatency, o.OutputFormat),
            VideoCodec.VP9 => new VP9Decoder(o.Width, o.Height, o.FpsNom, o.FpsDenom, o.LowLatency),
            VideoCodec.AV1 => new AV1Decoder(o.Width, o.Height, o.FpsNom, o.FpsDenom, o.LowLatency, o.OutputFormat),
            _ => throw new NotSupportedException($"No Media Foundation decoder of {codec}")
        };
    }
}
