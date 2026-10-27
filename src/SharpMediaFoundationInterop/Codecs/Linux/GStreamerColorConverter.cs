using System;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// A color converter of GStreamer's videoconvert, Linux's: of any of NV12, YUY2, UYVY, RGB24, RGB32 and ARGB32 to any
    /// other. The YUV side's caps carry the options' matrix and range, of BT.709's primaries and transfer on both sides, so
    /// that no more than the matrix is converted. The frames go in and come out of rows of no padding, as the library's are:
    /// they are laid out as GStreamer lays a frame out - rows of a multiple of 4 bytes - and back, RGB's rows flipped where
    /// they are bottom-up.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public sealed class GStreamerColorConverter : IMediaVideoTransform
    {
        private static readonly TimeSpan ConvertTimeout = TimeSpan.FromSeconds(5);

        private readonly ColorConverterOptions _options;
        private GstAppPipeline _pipeline;
        private byte[] _input;
        private int _pending;
        private bool _disposed;

        public GStreamerColorConverter(Guid inputFormat, Guid outputFormat, ColorConverterOptions options)
        {
            ColorFormats.Validate(inputFormat, outputFormat, options);
            _options = options;
            InputFormat = inputFormat;
            OutputFormat = outputFormat;
            OutputSize = ColorFormats.FrameSize(outputFormat, options.Width, options.Height);
        }

        /// <summary>Whether GStreamer converts the one format to the other: of the plugins installed, videoconvert's.</summary>
        public static bool Supports(Guid inputFormat, Guid outputFormat) =>
            ColorFormats.IsKnown(inputFormat) && ColorFormats.IsKnown(outputFormat) && Gst.IsAvailable && Gst.HasElement("videoconvert");

        public Guid InputFormat { get; }
        public Guid OutputFormat { get; }
        public uint OutputSize { get; }
        public uint OriginalWidth => _options.Width;
        public uint OriginalHeight => _options.Height;
        public uint Width => _options.Width;
        public uint Height => _options.Height;

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipeline != null)
                return;
            if (!Supports(InputFormat, OutputFormat))
                throw new NotSupportedException($"No GStreamer conversion of {InputFormat} to {OutputFormat}: {Gst.UnavailableReason ?? "videoconvert is not installed"}");

            _input = new byte[FrameSize(InputFormat)];
            _pipeline = new GstAppPipeline(
                $"appsrc name=src format=time is-live=false block=true caps=\"{Caps(InputFormat, input: true)},framerate=0/1\" ! " +
                $"videoconvert ! {Caps(OutputFormat, input: false)} ! appsink name=sink sync=false");
            _pipeline.Play();
        }

        /// <summary>
        /// The caps of a frame of the format: RGB24 is GStreamer's BGR, RGB32 its BGRx in and BGRA out - so that alpha comes out
        /// opaque - and ARGB32 its BGRA. YUV's colorimetry is of the range, the matrix, BT.709's transfer and its primaries.
        /// </summary>
        private string Caps(Guid format, bool input)
        {
            string name =
                format == MediaFormats.NV12 ? "NV12" :
                format == MediaFormats.YUY2 ? "YUY2" :
                format == MediaFormats.UYVY ? "UYVY" :
                format == MediaFormats.RGB24 ? "BGR" :
                format == MediaFormats.RGB32 && input ? "BGRx" :
                "BGRA";
            string caps = $"video/x-raw,format={name},width={Width},height={Height}";
            if (ColorFormats.IsYuv(format))
            {
                // GstVideoColorRange: 1 of 0 to 255, 2 of 16 to 235; GstVideoColorMatrix: 3 BT.709, 4 BT.601, 6 BT.2020
                int range = _options.FullRange ? 1 : 2;
                int matrix = ColorFormats.Matrix(_options) switch
                {
                    ColorMatrix.Bt709 => 3,
                    ColorMatrix.Bt2020 => 6,
                    _ => 4,
                };
                caps += $",colorimetry={range}:{matrix}:5:1";
            }
            return caps;
        }

        /// <summary>The bytes of each row of a plane of the format, of the library's frame, and its rows.</summary>
        private (int RowBytes, int Rows)[] Planes(Guid format)
        {
            int width = (int)Width, height = (int)Height;
            if (format == MediaFormats.NV12)
                return [(width, height), (width, height / 2)];
            if (format == MediaFormats.YUY2 || format == MediaFormats.UYVY)
                return [(width * 2, height)];
            return [(width * ColorFormats.BytesPerPixel(format), height)];
        }

        /// <summary>GStreamer's stride of a row of so many bytes: a multiple of 4.</summary>
        private static int DefaultStride(int rowBytes) => (rowBytes + 3) & ~3;

        /// <summary>The bytes of a frame of the format, as GStreamer lays it out: of its default strides.</summary>
        private int FrameSize(Guid format)
        {
            int size = 0;
            foreach (var (rowBytes, rows) in Planes(format))
                size += DefaultStride(rowBytes) * rows;
            return size;
        }

        public bool ProcessInput(byte[] data, long timestamp) => ProcessInput(new ReadOnlySpan<byte>(data), timestamp);

        /// <summary>A frame in, and its time; read what it makes with ProcessOutput.</summary>
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            ThrowIfNotInitialized();
            uint size = ColorFormats.FrameSize(InputFormat, Width, Height);
            if (data.Length < size)
                throw new ArgumentException($"A frame of {Width}x{Height} is of {size} bytes", nameof(data));

            // of the library's rows to GStreamer's: each plane after the one before, of its default stride
            bool flip = ColorFormats.IsRgb(InputFormat) && !_options.TopDown;
            int from = 0, to = 0;
            foreach (var (rowBytes, rows) in Planes(InputFormat))
            {
                int stride = DefaultStride(rowBytes);
                for (int row = 0; row < rows; row++)
                    data.Slice(from + (flip ? rows - 1 - row : row) * rowBytes, rowBytes).CopyTo(_input.AsSpan(to + row * stride));
                from += rowBytes * rows;
                to += stride * rows;
            }

            if (!_pipeline.Push(ReadOnlySpan<byte>.Empty, _input, timestamp))
                return false;
            _pending++;
            return true;
        }

        public bool ProcessOutput(ref byte[] buffer, out uint length) => ProcessOutput(ref buffer, out length, out _);

        /// <summary>The next frame converted, and its time: waited for, of a frame that went in and has not come out.</summary>
        public unsafe bool ProcessOutput(ref byte[] buffer, out uint length, out long timestamp)
        {
            ThrowIfNotInitialized();
            length = 0;
            timestamp = 0;
            IntPtr sample = _pipeline.Pull(_pending > 0 ? ConvertTimeout : TimeSpan.Zero);
            if (sample == IntPtr.Zero)
            {
                _pipeline.ThrowIfError();
                return false;
            }
            _pending = Math.Max(0, _pending - 1);
            try
            {
                IntPtr frame = Gst.gst_sample_get_buffer(sample);
                if (frame == IntPtr.Zero)
                    return false;
                var planes = Planes(OutputFormat);
                Span<int> strides = stackalloc int[4];
                Span<long> offsets = stackalloc long[4];
                if (!Gst.GetVideoLayout(frame, strides, offsets))
                {
                    long offset = 0;
                    for (int i = 0; i < planes.Length; i++)
                    {
                        strides[i] = DefaultStride(planes[i].RowBytes);
                        offsets[i] = offset;
                        offset += (long)strides[i] * planes[i].Rows;
                    }
                }

                if (buffer == null || buffer.Length < OutputSize)
                    buffer = new byte[OutputSize];
                if (!Gst.Map(frame, out var info))
                    return false;
                try
                {
                    // of GStreamer's rows to the library's
                    bool flip = ColorFormats.IsRgb(OutputFormat) && !_options.TopDown;
                    var source = new ReadOnlySpan<byte>(info.Data, (int)info.Size);
                    int to = 0;
                    for (int i = 0; i < planes.Length; i++)
                    {
                        var (rowBytes, rows) = planes[i];
                        for (int row = 0; row < rows; row++)
                        {
                            long at = offsets[i] + (long)row * strides[i];
                            if (at + rowBytes > source.Length)
                                break;
                            source.Slice((int)at, rowBytes).CopyTo(buffer.AsSpan(to + (flip ? rows - 1 - row : row) * rowBytes));
                        }
                        to += rowBytes * rows;
                    }
                }
                finally
                {
                    Gst.Unmap(frame, ref info);
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

        /// <summary>Nothing is held but the frames not read out, which stay to be read.</summary>
        public bool Drain() => true;

        public void BeginDrain() { }

        public void EndDrain() { }

        public void Flush()
        {
            ThrowIfNotInitialized();
            _pipeline.Flush();
            _pending = 0;
        }

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipeline == null)
                throw new InvalidOperationException("The converter is to be initialized first.");
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
