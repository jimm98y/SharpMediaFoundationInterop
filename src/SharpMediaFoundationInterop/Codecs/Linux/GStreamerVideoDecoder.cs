using System;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// A decoder of GStreamer's, Linux's, of any <see cref="VideoCodec"/>, made for the <see cref="VideoDecoderOptions"/>: an
    /// appsrc of the codec's caps, decodebin - which picks the best decoder installed, a hardware one where there is one -
    /// and NV12 out of an appsink, padded to <see cref="Width"/> by <see cref="Height"/>. It takes what Media Foundation's
    /// decoders take, a NAL unit, an OBU or a picture each time, the parsers before the decoder making access units of them,
    /// and hands frames out in the order they are shown, each with the time of its input.
    /// <para>
    /// GStreamer decodes on threads of its own, so a frame comes out a while after its input went in: what ProcessOutput
    /// does not have yet, it has on a later call, and draining waits for all of it.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("linux")]
    public sealed class GStreamerVideoDecoder : IMediaVideoTransform
    {
        /// <summary>The multiple the frames' width and height are rounded up to: 2, for NV12's chroma, whatever the codec.</summary>
        public const uint ResMultiple = 2;

        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

        private readonly VideoCodec _codec;
        private GstAppPipeline _pipeline;
        private bool _draining;
        private bool _disposed;
        private bool _warnedOfSize;

        public GStreamerVideoDecoder(VideoCodec codec, VideoDecoderOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            _codec = codec;
            OriginalWidth = options.Width;
            OriginalHeight = options.Height;
            Width = MediaUtils.RoundToMultipleOf(Math.Max(options.Width, 2), ResMultiple);
            Height = MediaUtils.RoundToMultipleOf(Math.Max(options.Height, 2), ResMultiple);
            OutputSize = Width * Height * 3 / 2;
        }

        public VideoCodec Codec => _codec;
        public uint OriginalWidth { get; }
        public uint OriginalHeight { get; }
        public uint Width { get; }
        public uint Height { get; }
        public uint OutputSize { get; }
        public Guid InputFormat => MediaFormats.Of(_codec);
        public Guid OutputFormat => MediaFormats.NV12;

        /// <summary>The caps of the codec's elementary stream, as an appsrc says them.</summary>
        internal static string Caps(VideoCodec codec) => codec switch
        {
            VideoCodec.H264 => "video/x-h264,stream-format=byte-stream,alignment=nal",
            VideoCodec.H265 => "video/x-h265,stream-format=byte-stream,alignment=nal",
            VideoCodec.VP9 => "video/x-vp9",
            VideoCodec.AV1 => "video/x-av1,stream-format=obu-stream,alignment=obu",
            // MPEG-1 as MPEG-2: the parser tells them apart by the sequence extension
            VideoCodec.H262 => "video/mpeg,mpegversion=2,systemstream=false",
            VideoCodec.H263 => "video/x-h263,variant=itu",
            VideoCodec.Mpeg4 => "video/mpeg,mpegversion=4,systemstream=false",
            VideoCodec.ProRes => "video/x-prores",
            _ => throw new NotSupportedException($"No GStreamer caps of {codec}")
        };

        /// <summary>The caps the parser hands the decoder: whole access units, parsed.</summary>
        private static string ParsedCaps(VideoCodec codec) => codec switch
        {
            VideoCodec.H264 => "video/x-h264,stream-format=byte-stream,alignment=au",
            VideoCodec.H265 => "video/x-h265,stream-format=byte-stream,alignment=au",
            VideoCodec.AV1 => "video/x-av1,stream-format=obu-stream,alignment=tu",
            VideoCodec.H262 => "video/mpeg,mpegversion=2,systemstream=false,parsed=true",
            VideoCodec.Mpeg4 => "video/mpeg,mpegversion=4,systemstream=false,parsed=true",
            _ => Caps(codec)
        };

        /// <summary>Whether there is a decoder of the codec here: of the plugins installed, one decodebin would pick.</summary>
        public static bool Supports(VideoCodec codec) => Enum.IsDefined(codec) && Gst.HasDecoderFor(ParsedCaps(codec));

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipeline != null)
                return;
            if (!Supports(_codec))
                throw new NotSupportedException($"No GStreamer decoder of {_codec} is installed: of gstreamer1.0-libav, -plugins-good, -bad or -ugly");

            _pipeline = new GstAppPipeline(
                $"appsrc name=src format=time is-live=false block=true max-bytes=16000000 caps=\"{Caps(_codec)}\" ! " +
                "decodebin ! videoconvert ! video/x-raw,format=NV12 ! appsink name=sink sync=false");
            _pipeline.Play();
        }

        public bool ProcessInput(byte[] data, long timestamp) => ProcessInput(new ReadOnlySpan<byte>(data), timestamp);

        /// <summary>One sample in - a NAL unit, with its start code or not, an OBU, a picture - of the time given.</summary>
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            ThrowIfNotInitialized();
            if (data.IsEmpty)
                return false;
            // a NAL unit as a file holds it gets its start code, as Media Foundation's decoders give it one
            bool prefix = _codec is VideoCodec.H264 or VideoCodec.H265 && !AnnexBUtils.HasStartCode(data);
            if (_codec is VideoCodec.H262 or VideoCodec.Mpeg4)
                data = data.Slice(RepeatedHeadersEnd(data));
            return _pipeline.Push(prefix ? AnnexBUtils.AnnexB : ReadOnlySpan<byte>.Empty, data, timestamp);
        }

        /// <summary>
        /// Of a picture whose headers come twice before it - those of the sample entry put in front of those it has in band,
        /// as a player puts them - where the second copy starts: the parser takes the first copy for a picture of its own,
        /// and the picture's time is lost with it. 0 where the headers come once.
        /// </summary>
        private int RepeatedHeadersEnd(ReadOnlySpan<byte> data)
        {
            // MPEG-2's sequence header, before its first picture; MPEG-4's visual object sequence, or VOL, before its first VOP
            byte header = _codec == VideoCodec.H262 ? (byte)0xB3 : (byte)0xB0;
            int first = -1, last = -1;
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1)
                    continue;
                byte code = data[i + 3];
                if (_codec == VideoCodec.H262 ? code == 0x00 : code == 0xB6)
                    break; // the picture
                bool isHeader = code == header || (_codec == VideoCodec.Mpeg4 && first < 0 && code >= 0x20 && code <= 0x2F);
                if (isHeader)
                {
                    if (first < 0)
                        first = i;
                    else if (code == data[first + 3])
                        last = i;
                }
            }
            return first == 0 && last > 0 ? last : 0;
        }

        public bool ProcessOutput(ref byte[] buffer, out uint length) => ProcessOutput(ref buffer, out length, out _);

        /// <summary>
        /// The next frame decoded, of the order they are shown, and its time. While draining it waits for one, up to the
        /// stream's end; otherwise it takes only what is there.
        /// </summary>
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
                if (buffer == null || buffer.Length < OutputSize)
                    buffer = new byte[OutputSize];
                if (!GstFrames.CopyNV12(sample, buffer, Width, Height, out int frameWidth, out int frameHeight))
                    return false;
                if ((frameWidth > Width || frameHeight > Height) && !_warnedOfSize && Log.WarnEnabled)
                {
                    Log.Warn($"The frames are of {frameWidth}x{frameHeight}, larger than the {Width}x{Height} the decoder was made for: they are cut");
                    _warnedOfSize = true;
                }
                length = OutputSize;
                timestamp = GstFrames.Time(sample);
                return true;
            }
            finally
            {
                Gst.gst_mini_object_unref(sample);
            }
        }

        public bool Drain()
        {
            BeginDrain();
            EndDrain();
            return true;
        }

        /// <summary>Ends the stream, so that everything the decoder holds comes out; read it out before <see cref="EndDrain"/>.</summary>
        public void BeginDrain()
        {
            ThrowIfNotInitialized();
            _pipeline.EndOfStream();
            _draining = true;
        }

        /// <summary>Takes input again: the end of the stream is flushed away, the decoder's configuration kept.</summary>
        public void EndDrain()
        {
            ThrowIfNotInitialized();
            _draining = false;
            _pipeline.Flush();
        }

        /// <summary>Lets go of everything held, for a seek: the input starts again at a key frame.</summary>
        public void Flush()
        {
            ThrowIfNotInitialized();
            _pipeline.Flush();
        }

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipeline == null)
                throw new InvalidOperationException("The decoder is to be initialized first.");
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
