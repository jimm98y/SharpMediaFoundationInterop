using System;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// A color converter of managed code alone, of every platform: where the system has none of its own - Android's NDK has
    /// none, nor iOS before 16 - or none installed. NV12, YUY2 or UYVY to RGB24, RGB32 or ARGB32, of <see cref="VideoColor"/>; and
    /// RGB24, RGB32 or ARGB32 to NV12, the chroma of each 2 by 2 block averaged. The matrix and the range are the options'; no
    /// primaries or transfer are converted, as the systems' converters convert none.
    /// </summary>
    public sealed class ManagedColorConverter : IMediaVideoTransform
    {
        private readonly ColorConverterOptions _options;
        private readonly VideoColor _color;
        private readonly int _rgbBytes;
        private byte[] _input;
        private byte[] _output;
        private long _outputTime;
        private bool _hasOutput;
        private bool _initialized;
        private bool _disposed;

        public ManagedColorConverter(Guid inputFormat, Guid outputFormat, ColorConverterOptions options)
        {
            ColorFormats.Validate(inputFormat, outputFormat, options);
            if (!Supports(inputFormat, outputFormat))
                throw new NotSupportedException($"No managed conversion of {inputFormat} to {outputFormat}");
            _options = options;
            InputFormat = inputFormat;
            OutputFormat = outputFormat;
            OutputSize = ColorFormats.FrameSize(outputFormat, options.Width, options.Height);
            _rgbBytes = ColorFormats.BytesPerPixel(ColorFormats.IsRgb(inputFormat) ? inputFormat : outputFormat);

            // the matrix alone: of sRGB's primaries and curve, which make it no more than that
            int matrix = ColorFormats.Matrix(options) switch
            {
                ColorMatrix.Bt709 => 1,
                ColorMatrix.Bt2020 => 9,
                _ => 6,
            };
            _color = VideoColor.FromCodes(2, 2, matrix, options.FullRange, 8, sceneReferred: false);
        }

        /// <summary>Whether this converts the one format to the other: YUV to RGB, or RGB to NV12.</summary>
        public static bool Supports(Guid inputFormat, Guid outputFormat) =>
            ColorFormats.IsYuv(inputFormat) && ColorFormats.IsRgb(outputFormat) ||
            ColorFormats.IsRgb(inputFormat) && outputFormat == MediaFormats.NV12;

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
            _output ??= new byte[OutputSize];
            _initialized = true;
        }

        public bool ProcessInput(byte[] data, long timestamp) => ProcessInput(new ReadOnlySpan<byte>(data), timestamp);

        /// <summary>A frame in, converted now: read it out with ProcessOutput before the next goes in, which takes its place.</summary>
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            ThrowIfNotInitialized();
            uint size = ColorFormats.FrameSize(InputFormat, Width, Height);
            if (data.Length < size)
                throw new ArgumentException($"A frame of {Width}x{Height} is of {size} bytes", nameof(data));

            if (ColorFormats.IsRgb(OutputFormat))
                ToRgb(data.Slice(0, (int)size));
            else
                ToNV12(data);
            _outputTime = timestamp;
            _hasOutput = true;
            return true;
        }

        private void ToRgb(ReadOnlySpan<byte> frame)
        {
            var format = InputFormat == MediaFormats.NV12 ? VideoFrameFormat.NV12 : VideoFrameFormat.YUY2;
            int width = (int)Width, height = (int)Height;
            // bottom-up rows: the picture flipped, top to bottom
            var orientation = _options.TopDown ? VideoOrientation.Identity : new VideoOrientation(1, 0, 0, -1);
            // VideoColor reads an array: one of this converter's, made once; UYVY made YUY2, each Cb, Y0, Cr, Y1 Y0, Cb, Y1, Cr
            _input ??= new byte[frame.Length];
            frame.CopyTo(_input);
            if (InputFormat == MediaFormats.UYVY)
            {
                for (int i = 0; i + 1 < _input.Length; i += 2)
                    (_input[i], _input[i + 1]) = (_input[i + 1], _input[i]);
            }
            _color.Convert(_input, format, (int)VideoFrameFormats.Stride(format, Width), height, width, height,
                _output, width, _rgbBytes, FramePlacement.Whole(width, height, orientation));
        }

        private void ToNV12(ReadOnlySpan<byte> rgb)
        {
            int width = (int)Width, height = (int)Height, bytes = _rgbBytes;
            bool bottomUp = !_options.TopDown;
            var (kr, kb) = ColorFormats.Matrix(_options) switch
            {
                ColorMatrix.Bt709 => (0.2126, 0.0722),
                ColorMatrix.Bt2020 => (0.2627, 0.0593),
                _ => (0.299, 0.114),
            };
            double kg = 1 - kr - kb;
            bool full = _options.FullRange;
            double lumaScale = full ? 1 : 219 / 255.0, chromaScale = full ? 1 : 224 / 255.0;
            int lumaOffset = full ? 0 : 16;

            // of 8 bit R, G and B, scaled by 2^16: Y of its offset, Cb and Cr of 128
            const double one = 1 << 16;
            int yr = (int)Math.Round(kr * lumaScale * one), yg = (int)Math.Round(kg * lumaScale * one), yb = (int)Math.Round(kb * lumaScale * one);
            double cb = chromaScale / (2 * (1 - kb)), cr = chromaScale / (2 * (1 - kr));
            int ur = (int)Math.Round(-kr * cb * one), ug = (int)Math.Round(-kg * cb * one), ub = (int)Math.Round((1 - kb) * cb * one);
            int vr = (int)Math.Round((1 - kr) * cr * one), vg = (int)Math.Round(-kg * cr * one), vb = (int)Math.Round(-kb * cr * one);
            const int half = 1 << 15;

            int stride = width * bytes;
            var output = _output.AsSpan();
            for (int y = 0; y < height; y++)
            {
                var row = rgb.Slice((bottomUp ? height - 1 - y : y) * stride, stride);
                var luma = output.Slice(y * width, width);
                for (int x = 0; x < width; x++)
                {
                    int b = row[x * bytes], g = row[x * bytes + 1], r = row[x * bytes + 2];
                    luma[x] = (byte)Math.Clamp(((yr * r + yg * g + yb * b + half) >> 16) + lumaOffset, 0, 255);
                }
            }

            var chroma = output.Slice(width * height);
            for (int cy = 0; cy < height / 2; cy++)
            {
                var top = rgb.Slice((bottomUp ? height - 1 - cy * 2 : cy * 2) * stride, stride);
                var bottom = rgb.Slice((bottomUp ? height - 2 - cy * 2 : cy * 2 + 1) * stride, stride);
                for (int cx = 0; cx < width / 2; cx++)
                {
                    int left = cx * 2 * bytes, right = left + bytes;
                    int b = top[left] + top[right] + bottom[left] + bottom[right];
                    int g = top[left + 1] + top[right + 1] + bottom[left + 1] + bottom[right + 1];
                    int r = top[left + 2] + top[right + 2] + bottom[left + 2] + bottom[right + 2];
                    // of the sums of four: the average, of their rounding at once
                    chroma[cx * 2 + cy * width] = (byte)Math.Clamp(((ur * r + ug * g + ub * b + (half << 2)) >> 18) + 128, 0, 255);
                    chroma[cx * 2 + 1 + cy * width] = (byte)Math.Clamp(((vr * r + vg * g + vb * b + (half << 2)) >> 18) + 128, 0, 255);
                }
            }
        }

        public bool ProcessOutput(ref byte[] buffer, out uint length) => ProcessOutput(ref buffer, out length, out _);

        /// <summary>The frame converted of the last input, and its time; false where it has been read out already.</summary>
        public bool ProcessOutput(ref byte[] buffer, out uint length, out long timestamp)
        {
            ThrowIfNotInitialized();
            length = 0;
            timestamp = 0;
            if (!_hasOutput)
                return false;
            if (buffer == null || buffer.Length < OutputSize)
                buffer = new byte[OutputSize];
            Buffer.BlockCopy(_output, 0, buffer, 0, (int)OutputSize);
            length = OutputSize;
            timestamp = _outputTime;
            _hasOutput = false;
            return true;
        }

        /// <summary>Nothing is held but the frame not read out, which stays to be read.</summary>
        public bool Drain() => true;

        public void BeginDrain() { }

        public void EndDrain() { }

        public void Flush() => _hasOutput = false;

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_initialized)
                throw new InvalidOperationException("The converter is to be initialized first.");
        }

        public void Dispose() => _disposed = true;
    }
}
