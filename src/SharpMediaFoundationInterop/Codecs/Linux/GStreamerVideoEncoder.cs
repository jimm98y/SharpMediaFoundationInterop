using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// An encoder of GStreamer's, Linux's, of H.264, H.265, VP9 or AV1, made for the <see cref="VideoEncoderOptions"/>: NV12
    /// in, of <see cref="Width"/> by <see cref="Height"/>, through the first encoder installed of the codec - a VA-API or V4L2
    /// one, of the GPU, before x264, x265, libvpx, SVT-AV1 or libaom - and out as Media Foundation's encoders hand it: of H.264
    /// and H.265 an access unit at a time, Annex B, the parameter sets before each key frame; of VP9 a frame; of AV1 a
    /// temporal unit of OBUs. It encodes no B-frames, so the units come out in the order the frames went in, each with the
    /// time of its frame. The options become the encoder's properties: what it has none of is in <see cref="UnappliedSettings"/>.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public sealed class GStreamerVideoEncoder : IMediaVideoEncoder
    {
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

        private readonly VideoCodec _codec;
        private readonly VideoEncoderOptions _options;
        private readonly List<string> _unapplied = new List<string>();
        private GstAppPipeline _pipeline;
        private bool _draining;
        private bool _disposed;

        public GStreamerVideoEncoder(VideoCodec codec, VideoEncoderOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (options.Width == 0 || options.Height == 0)
                throw new ArgumentException("The picture's size is to be given", nameof(options));
            _codec = codec;
            _options = options;
            Element = Encoders(codec) is { } names ? Gst.FirstElement(names) : null;
        }

        public VideoCodec Codec => _codec;

        /// <summary>The GStreamer element that encodes: of the encoders of the codec, the first installed; null of none.</summary>
        public string Element { get; }

        public uint OriginalWidth => _options.Width;
        public uint OriginalHeight => _options.Height;
        /// <summary>The picture's size, as given: the NV12 frames in are of it, of rows of no padding.</summary>
        public uint Width => _options.Width;
        public uint Height => _options.Height;
        public uint OutputSize => Width * Height * 3 / 2;
        public Guid InputFormat => MediaFormats.NV12;
        /// <summary>The codec's subtype; of ProRes, its profile's FOURCC.</summary>
        public Guid OutputFormat => _codec == VideoCodec.ProRes ? MediaFormats.Of(_options.ProResProfile) : MediaFormats.Of(_codec);
        public IReadOnlyList<string> UnappliedSettings => _unapplied;

        /// <summary>The encoders of the codec, of the GPU's first.</summary>
        private static string[] Encoders(VideoCodec codec) => codec switch
        {
            VideoCodec.H264 => ["vah264enc", "vah264lpenc", "v4l2h264enc", "x264enc", "openh264enc"],
            VideoCodec.H265 => ["vah265enc", "vah265lpenc", "v4l2h265enc", "x265enc"],
            VideoCodec.VP9 => ["vavp9enc", "vp9enc"],
            VideoCodec.AV1 => ["vaav1enc", "svtav1enc", "av1enc", "rav1enc"],
            VideoCodec.ProRes => ["avenc_prores_ks", "avenc_prores"],
            _ => null
        };

        /// <summary>What makes the encoder's output the stream Media Foundation's encoders hand out.</summary>
        private static string Output(VideoCodec codec) => codec switch
        {
            VideoCodec.H264 => "h264parse config-interval=-1 ! video/x-h264,stream-format=byte-stream,alignment=au",
            VideoCodec.H265 => "h265parse config-interval=-1 ! video/x-h265,stream-format=byte-stream,alignment=au",
            VideoCodec.AV1 => "av1parse ! video/x-av1,stream-format=obu-stream,alignment=tu",
            _ => "identity"
        };

        /// <summary>Whether there is an encoder of the codec here: of the plugins installed.</summary>
        public static bool Supports(VideoCodec codec) => Encoders(codec) is { } names && Gst.FirstElement(names) != null;

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipeline != null)
                return;
            if (Element == null)
                throw new NotSupportedException($"No GStreamer encoder of {_codec} is installed: of {string.Join(", ", Encoders(_codec) ?? [])}");

            string framerate = _options.FpsNom > 0 && _options.FpsDenom > 0 ? $"{_options.FpsNom}/{_options.FpsDenom}" : "0/1";
            _pipeline = new GstAppPipeline(
                $"appsrc name=src format=time is-live=false block=true max-bytes=64000000 caps=\"video/x-raw,format=NV12,width={Width},height={Height},framerate={framerate}\" ! " +
                $"videoconvert ! {Element} name=enc ! {Output(_codec)} ! appsink name=sink sync=false");

            IntPtr encoder = _pipeline.Get("enc");
            try
            {
                ApplySettings(encoder);
            }
            finally
            {
                Gst.gst_object_unref(encoder);
            }
            _pipeline.Play();
        }

        /// <summary>
        /// The options, as the properties of the encoder there is - each its own names and units for them. What it has no
        /// property of, or none of the mode asked for, is reported in <see cref="UnappliedSettings"/>.
        /// </summary>
        private void ApplySettings(IntPtr encoder)
        {
            _unapplied.Clear();
            var o = _options;
            uint kbps = Math.Max(o.Bitrate / 1000, 1);
            string Str(long value) => value.ToString(CultureInfo.InvariantCulture);
            void Set(string property, string value, string setting)
            {
                if (!Gst.SetProperty(encoder, property, value))
                    _unapplied.Add($"{setting}: not supported by {Element}");
            }
            void Quietly(string property, string value) => Gst.SetProperty(encoder, property, value);
            void Unsupported(string setting) => _unapplied.Add($"{setting}: not supported by {Element}");

            switch (Element)
            {
                case "x264enc":
                    // as Media Foundation's encoder: frames out as they go in, no B-frames, quick, and no access unit delimiters
                    Quietly("bframes", "0");
                    Quietly("aud", "false");
                    Quietly("tune", "zerolatency");
                    Quietly("speed-preset", "veryfast");
                    switch (o.RateControl)
                    {
                        case RateControlMode.Quality: Set("pass", "qual", nameof(o.RateControl)); Set("quantizer", Str(51 - Math.Min(o.Quality, 100) * 51 / 100), nameof(o.Quality)); break;
                        case RateControlMode.ConstantQp: Set("pass", "quant", nameof(o.RateControl)); Set("quantizer", Str(o.Qp), nameof(o.Qp)); break;
                        default: Set("pass", "cbr", nameof(o.RateControl)); Set("bitrate", Str(kbps), nameof(o.Bitrate)); break;
                    }
                    if (o.KeyFrameInterval > 0) Set("key-int-max", Str(o.KeyFrameInterval), nameof(o.KeyFrameInterval));
                    if (o.Threads > 0) Set("threads", Str(o.Threads), nameof(o.Threads));
                    break;

                case "x265enc":
                    Quietly("tune", "zerolatency");
                    Quietly("speed-preset", "veryfast");
                    string options = "bframes=0";
                    switch (o.RateControl)
                    {
                        case RateControlMode.Quality: options += $":crf={Str(51 - Math.Min(o.Quality, 100) * 51 / 100)}"; break;
                        case RateControlMode.ConstantQp: Set("qp", Str(o.Qp), nameof(o.Qp)); break;
                        case RateControlMode.ConstantBitrate: Set("bitrate", Str(kbps), nameof(o.Bitrate)); options += $":vbv-maxrate={kbps}:vbv-bufsize={kbps}"; break;
                        default: Set("bitrate", Str(kbps), nameof(o.Bitrate)); break;
                    }
                    if (o.KeyFrameInterval > 0) Set("key-int-max", Str(o.KeyFrameInterval), nameof(o.KeyFrameInterval));
                    if (o.Threads > 0) options += $":pools={Str(o.Threads)}";
                    Set("option-string", options, "x265 options");
                    break;

                case "openh264enc":
                    switch (o.RateControl)
                    {
                        case RateControlMode.Quality: Set("rate-control", "quality", nameof(o.RateControl)); Unsupported(nameof(o.Quality)); break;
                        case RateControlMode.ConstantQp: Set("rate-control", "off", nameof(o.RateControl)); Set("qp-min", Str(o.Qp), nameof(o.Qp)); Set("qp-max", Str(o.Qp), nameof(o.Qp)); break;
                        default: Set("rate-control", "bitrate", nameof(o.RateControl)); Set("bitrate", Str(o.Bitrate), nameof(o.Bitrate)); break;
                    }
                    if (o.KeyFrameInterval > 0) Set("gop-size", Str(o.KeyFrameInterval), nameof(o.KeyFrameInterval));
                    if (o.Threads > 0) Set("multi-thread", Str(o.Threads), nameof(o.Threads));
                    break;

                case "vp9enc":
                    // realtime, and no alternate reference frames: each frame out as it goes in, none hidden
                    Quietly("deadline", "1");
                    Quietly("cpu-used", "8");
                    Quietly("lag-in-frames", "0");
                    switch (o.RateControl)
                    {
                        case RateControlMode.Quality: Set("end-usage", "cq", nameof(o.RateControl)); Set("cq-level", Str(63 - Math.Min(o.Quality, 100) * 63 / 100), nameof(o.Quality)); break;
                        case RateControlMode.ConstantQp: Set("end-usage", "q", nameof(o.RateControl)); Set("min-quantizer", Str(Math.Min(o.Qp, 63)), nameof(o.Qp)); Set("max-quantizer", Str(Math.Min(o.Qp, 63)), nameof(o.Qp)); break;
                        case RateControlMode.ConstantBitrate: Set("end-usage", "cbr", nameof(o.RateControl)); Set("target-bitrate", Str(o.Bitrate), nameof(o.Bitrate)); break;
                        default: Set("end-usage", "vbr", nameof(o.RateControl)); Set("target-bitrate", Str(o.Bitrate), nameof(o.Bitrate)); break;
                    }
                    if (o.KeyFrameInterval > 0) Set("keyframe-max-dist", Str(o.KeyFrameInterval), nameof(o.KeyFrameInterval));
                    if (o.Threads > 0) Set("threads", Str(o.Threads), nameof(o.Threads));
                    break;

                case "av1enc":
                    Quietly("usage-profile", "realtime");
                    Quietly("cpu-used", "8");
                    Quietly("lag-in-frames", "0");
                    switch (o.RateControl)
                    {
                        case RateControlMode.Quality: Set("end-usage", "cq", nameof(o.RateControl)); Set("cq-level", Str(63 - Math.Min(o.Quality, 100) * 63 / 100), nameof(o.Quality)); break;
                        case RateControlMode.ConstantQp: Set("end-usage", "q", nameof(o.RateControl)); Set("min-quantizer", Str(Math.Min(o.Qp, 63)), nameof(o.Qp)); Set("max-quantizer", Str(Math.Min(o.Qp, 63)), nameof(o.Qp)); break;
                        case RateControlMode.ConstantBitrate: Set("end-usage", "cbr", nameof(o.RateControl)); Set("target-bitrate", Str(kbps), nameof(o.Bitrate)); break;
                        default: Set("end-usage", "vbr", nameof(o.RateControl)); Set("target-bitrate", Str(kbps), nameof(o.Bitrate)); break;
                    }
                    if (o.KeyFrameInterval > 0) Set("keyframe-max-dist", Str(o.KeyFrameInterval), nameof(o.KeyFrameInterval));
                    if (o.Threads > 0) Set("threads", Str(o.Threads), nameof(o.Threads));
                    break;

                case "svtav1enc":
                    Quietly("preset", "10");
                    switch (o.RateControl)
                    {
                        case RateControlMode.Quality: Set("crf", Str(63 - Math.Min(o.Quality, 100) * 63 / 100), nameof(o.Quality)); break;
                        case RateControlMode.ConstantQp: Set("cqp", Str(Math.Min(o.Qp, 63)), nameof(o.Qp)); break;
                        case RateControlMode.ConstantBitrate: Set("target-bitrate", Str(kbps), nameof(o.Bitrate)); Set("maximum-buffer-size", "1000", nameof(o.RateControl)); break;
                        default: Set("target-bitrate", Str(kbps), nameof(o.Bitrate)); break;
                    }
                    if (o.KeyFrameInterval > 0) Set("intra-period-length", Str(o.KeyFrameInterval), nameof(o.KeyFrameInterval));
                    if (o.Threads > 0) Set("logical-processors", Str(o.Threads), nameof(o.Threads));
                    break;

                case "avenc_prores_ks":
                case "avenc_prores":
                    // the profile, of its fixed quality: there is no bit rate or quantiser to set
                    Set("profile", o.ProResProfile switch
                    {
                        ProResProfile.Proxy => "proxy",
                        ProResProfile.LT => "lt",
                        ProResProfile.Standard => "standard",
                        ProResProfile.P4444 => "4444",
                        ProResProfile.P4444XQ => "4444xq",
                        _ => "hq"
                    }, nameof(o.ProResProfile));
                    if (o.RateControl != RateControlMode.Default)
                        Unsupported(nameof(o.RateControl));
                    if (o.Threads > 0) Set("threads", Str(o.Threads), nameof(o.Threads));
                    break;

                default:
                    // VA-API's and V4L2's, of the GPU: properties alike across the codecs of each
                    Quietly("b-frames", "0");
                    switch (o.RateControl)
                    {
                        case RateControlMode.Quality: Set("rate-control", "icq", nameof(o.RateControl)); Set("target-percentage", Str(Math.Max(o.Quality, 1)), nameof(o.Quality)); break;
                        case RateControlMode.ConstantQp: Set("rate-control", "cqp", nameof(o.RateControl)); Set("qpi", Str(o.Qp), nameof(o.Qp)); Set("qpp", Str(o.Qp), nameof(o.Qp)); break;
                        case RateControlMode.ConstantBitrate: Set("rate-control", "cbr", nameof(o.RateControl)); Set("bitrate", Str(kbps), nameof(o.Bitrate)); break;
                        case RateControlMode.VariableBitrate: Set("rate-control", "vbr", nameof(o.RateControl)); Set("bitrate", Str(kbps), nameof(o.Bitrate)); break;
                        default: Set("bitrate", Str(kbps), nameof(o.Bitrate)); break;
                    }
                    if (o.KeyFrameInterval > 0) Set("key-int-max", Str(o.KeyFrameInterval), nameof(o.KeyFrameInterval));
                    if (o.Threads > 0) Unsupported(nameof(o.Threads));
                    break;
            }
        }

        public bool ProcessInput(byte[] data, long timestamp) => ProcessInput(new ReadOnlySpan<byte>(data), timestamp);

        /// <summary>One NV12 frame in, of <see cref="Width"/> by <see cref="Height"/>, and its time; read what it makes with ProcessOutput.</summary>
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            ThrowIfNotInitialized();
            if (data.Length < OutputSize)
                throw new ArgumentException($"An NV12 frame of {Width}x{Height} is of {OutputSize} bytes", nameof(data));
            return _pipeline.Push(ReadOnlySpan<byte>.Empty, data.Slice(0, (int)OutputSize), timestamp, FrameDuration);
        }

        private long? FrameDuration => _options.FpsNom > 0 && _options.FpsDenom > 0 ? MediaUtils.TicksPerSecond * _options.FpsDenom / _options.FpsNom : null;

        public bool ProcessOutput(ref byte[] buffer, out uint length) => ProcessOutput(ref buffer, out length, out _);

        /// <summary>The next unit encoded, and the time of its frame. While draining it waits for one, up to the stream's end.</summary>
        public bool ProcessOutput(ref byte[] buffer, out uint length, out long timestamp)
        {
            ThrowIfNotInitialized();
            length = 0;
            timestamp = 0;
            IntPtr sample = _pipeline.Pull(_draining ? DrainTimeout : TimeSpan.Zero);
            if (sample == IntPtr.Zero)
                return false;
            try
            {
                byte[] unit = GstFrames.Bytes(sample);
                int start = SkipDelimiter(unit);
                int size = unit.Length - start;
                if (buffer == null || buffer.Length < size)
                    buffer = new byte[Math.Max(size, OutputSize)];
                Buffer.BlockCopy(unit, start, buffer, 0, size);
                length = (uint)size;
                timestamp = GstFrames.Time(sample);
                return true;
            }
            finally
            {
                Gst.gst_mini_object_unref(sample);
            }
        }

        /// <summary>
        /// Where an access unit starts past its access unit delimiter: h264parse puts one in front of each it makes whole, and
        /// Media Foundation's encoders hand out none - the parameter sets come first, of a key frame.
        /// </summary>
        private int SkipDelimiter(byte[] unit)
        {
            if (_codec is not (VideoCodec.H264 or VideoCodec.H265) || unit.Length < 6)
                return 0;
            int header = unit[2] == 1 ? 3 : unit[3] == 1 ? 4 : 0;
            if (header == 0)
                return 0;
            int type = _codec == VideoCodec.H264 ? unit[header] & 0x1F : (unit[header] >> 1) & 0x3F;
            if (type != (_codec == VideoCodec.H264 ? 9 : 35))
                return 0;
            for (int i = header + 1; i + 3 < unit.Length; i++)
            {
                if (unit[i] == 0 && unit[i + 1] == 0 && (unit[i + 2] == 1 || (unit[i + 2] == 0 && unit[i + 3] == 1)))
                    return i;
            }
            return 0;
        }

        public bool Drain()
        {
            BeginDrain();
            EndDrain();
            return true;
        }

        /// <summary>Ends the stream, so that every frame the encoder holds comes out; read them out before <see cref="EndDrain"/>.</summary>
        public void BeginDrain()
        {
            ThrowIfNotInitialized();
            _pipeline.EndOfStream();
            _draining = true;
        }

        public void EndDrain()
        {
            ThrowIfNotInitialized();
            _draining = false;
            _pipeline.Flush();
        }

        public void Flush()
        {
            ThrowIfNotInitialized();
            _pipeline.Flush();
        }

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipeline == null)
                throw new InvalidOperationException("The encoder is to be initialized first.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _pipeline?.Dispose();
            _pipeline = null;
        }
    }
}
