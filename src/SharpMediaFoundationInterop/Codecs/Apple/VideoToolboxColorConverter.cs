using System;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;
using static SharpMediaFoundationInterop.Utils.AppleNative;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// A color converter of VideoToolbox's pixel transfer, macOS's and iOS's: NV12, YUY2 or UYVY to RGB24, RGB32 or ARGB32,
    /// and back, of Core Video's pixel buffers - 420v or 420f, yuvs or yuvf, 2vuy, BGRA. The YUV side's buffer carries the
    /// options' matrix as its attachment, and its range is of its format: UYVY of the full range is none of Core Video's.
    /// RGB24 is of a BGRA buffer, its pixels packed into three bytes, or unpacked, as they are copied.
    /// </summary>
    [SupportedOSPlatform("macos11.0")]
    [SupportedOSPlatform("ios16.0")]
    public sealed unsafe class VideoToolboxColorConverter : IMediaVideoTransform
    {
        private readonly ColorConverterOptions _options;
        private IntPtr _session;
        private IntPtr _source;
        private IntPtr _destination;
        private bool _hasOutput;
        private long _outputTime;
        private bool _disposed;

        public VideoToolboxColorConverter(Guid inputFormat, Guid outputFormat, ColorConverterOptions options)
        {
            ColorFormats.Validate(inputFormat, outputFormat, options);
            if (!Supports(inputFormat, outputFormat, options))
                throw new NotSupportedException($"No VideoToolbox conversion of {inputFormat} to {outputFormat}{(options.FullRange ? " of the full range" : "")}");
            _options = options;
            InputFormat = inputFormat;
            OutputFormat = outputFormat;
            OutputSize = ColorFormats.FrameSize(outputFormat, options.Width, options.Height);
        }

        /// <summary>Whether VideoToolbox converts the one format to the other: YUV to RGB, or back, of a range Core Video has.</summary>
        public static bool Supports(Guid inputFormat, Guid outputFormat, ColorConverterOptions options) =>
            (ColorFormats.IsYuv(inputFormat) && ColorFormats.IsRgb(outputFormat) || ColorFormats.IsRgb(inputFormat) && ColorFormats.IsYuv(outputFormat)) &&
            !(options.FullRange && (inputFormat == MediaFormats.UYVY || outputFormat == MediaFormats.UYVY));

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
            if (_session != IntPtr.Zero)
                return;

            if (VTPixelTransferSessionCreate(IntPtr.Zero, out _session) != 0)
                throw new InvalidOperationException("No pixel transfer session");
            _source = CreateBuffer(InputFormat);
            _destination = CreateBuffer(OutputFormat);

            // of RGB to YUV, the matrix of the destination: the session's, as the source's attachment says it of YUV to RGB
            if (ColorFormats.IsYuv(OutputFormat))
                VTSessionSetProperty(_session, VideoToolboxConstant("kVTPixelTransferPropertyKey_DestinationYCbCrMatrix"), MatrixValue());
        }

        /// <summary>A pixel buffer of the format, of the picture's size; of YUV, of the options' matrix.</summary>
        private IntPtr CreateBuffer(Guid format)
        {
            if (CVPixelBufferCreate(IntPtr.Zero, Width, Height, PixelFormat(format), IntPtr.Zero, out IntPtr buffer) != 0)
                throw new InvalidOperationException($"No pixel buffer of {Width}x{Height} of {format}");
            if (ColorFormats.IsYuv(format))
                CVBufferSetAttachment(buffer, CoreVideoConstant("kCVImageBufferYCbCrMatrixKey"), MatrixValue(), kCVAttachmentMode_ShouldPropagate);
            return buffer;
        }

        private uint PixelFormat(Guid format) =>
            format == MediaFormats.NV12 ? (_options.FullRange ? kCVPixelFormatType_420YpCbCr8BiPlanarFullRange : kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange) :
            format == MediaFormats.YUY2 ? (_options.FullRange ? kCVPixelFormatType_422YpCbCr8FullRange : kCVPixelFormatType_422YpCbCr8_yuvs) :
            format == MediaFormats.UYVY ? kCVPixelFormatType_422YpCbCr8 :
            kCVPixelFormatType_32BGRA;

        private IntPtr MatrixValue() => CoreVideoConstant(ColorFormats.Matrix(_options) switch
        {
            ColorMatrix.Bt709 => "kCVImageBufferYCbCrMatrix_ITU_R_709_2",
            ColorMatrix.Bt2020 => "kCVImageBufferYCbCrMatrix_ITU_R_2020",
            _ => "kCVImageBufferYCbCrMatrix_ITU_R_601_4",
        });

        public bool ProcessInput(byte[] data, long timestamp) => ProcessInput(new ReadOnlySpan<byte>(data), timestamp);

        /// <summary>A frame in, converted now: read it out with ProcessOutput before the next goes in, which takes its place.</summary>
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            ThrowIfNotInitialized();
            uint size = ColorFormats.FrameSize(InputFormat, Width, Height);
            if (data.Length < size)
                throw new ArgumentException($"A frame of {Width}x{Height} is of {size} bytes", nameof(data));

            if (InputFormat == MediaFormats.NV12)
                PixelBuffers.FillNV12(_source, data, Width, Height);
            else
                Copy(_source, data, InputFormat, toBuffer: true, Span<byte>.Empty);

            int status = VTPixelTransferSessionTransferImage(_session, _source, _destination);
            if (status != 0)
                throw new InvalidOperationException($"The frame could not be converted: {FourCCString(status)}");
            _outputTime = timestamp;
            _hasOutput = true;
            return true;
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

            if (OutputFormat == MediaFormats.NV12)
                PixelBuffers.CopyNV12(_destination, buffer, Width, Height, out _, out _);
            else
                Copy(_destination, ReadOnlySpan<byte>.Empty, OutputFormat, toBuffer: false, buffer);
            length = OutputSize;
            timestamp = _outputTime;
            _hasOutput = false;
            return true;
        }

        /// <summary>
        /// A packed frame - 4:2:2, or RGB - copied into the pixel buffer, or out of it, row by row to its stride: RGB's rows
        /// flipped where they are bottom-up, and RGB24's pixels made of BGRA's four bytes, alpha opaque.
        /// </summary>
        private void Copy(IntPtr pixelBuffer, ReadOnlySpan<byte> from, Guid format, bool toBuffer, Span<byte> to)
        {
            if (CVPixelBufferLockBaseAddress(pixelBuffer, toBuffer ? 0 : kCVPixelBufferLock_ReadOnly) != 0)
                throw new InvalidOperationException("The pixel buffer cannot be locked");
            try
            {
                byte* baseAddress = (byte*)CVPixelBufferGetBaseAddress(pixelBuffer);
                int stride = (int)CVPixelBufferGetBytesPerRow(pixelBuffer);
                int height = (int)Height, width = (int)Width;
                bool rgb = ColorFormats.IsRgb(format);
                int bytes = rgb ? ColorFormats.BytesPerPixel(format) : 2;
                int frameStride = width * bytes;
                for (int row = 0; row < height; row++)
                {
                    int frameRow = rgb && !_options.TopDown ? height - 1 - row : row;
                    var line = new Span<byte>(baseAddress + (long)row * stride, rgb ? width * 4 : frameStride);
                    if (bytes != 3)
                    {
                        if (toBuffer)
                            from.Slice(frameRow * frameStride, frameStride).CopyTo(line);
                        else
                            line.CopyTo(to.Slice(frameRow * frameStride, frameStride));
                        continue;
                    }
                    for (int x = 0; x < width; x++)
                    {
                        if (toBuffer)
                        {
                            var pixel = from.Slice(frameRow * frameStride + x * 3, 3);
                            (line[x * 4], line[x * 4 + 1], line[x * 4 + 2], line[x * 4 + 3]) = (pixel[0], pixel[1], pixel[2], 255);
                        }
                        else
                        {
                            var pixel = to.Slice(frameRow * frameStride + x * 3, 3);
                            (pixel[0], pixel[1], pixel[2]) = (line[x * 4], line[x * 4 + 1], line[x * 4 + 2]);
                        }
                    }
                }
            }
            finally
            {
                CVPixelBufferUnlockBaseAddress(pixelBuffer, toBuffer ? 0 : kCVPixelBufferLock_ReadOnly);
            }
        }

        /// <summary>Nothing is held but the frame not read out, which stays to be read.</summary>
        public bool Drain() => true;

        public void BeginDrain() { }

        public void EndDrain() { }

        public void Flush() => _hasOutput = false;

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session == IntPtr.Zero)
                throw new InvalidOperationException("The converter is to be initialized first.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_session != IntPtr.Zero)
            {
                VTPixelTransferSessionInvalidate(_session);
                CFRelease(_session);
                _session = IntPtr.Zero;
            }
            if (_source != IntPtr.Zero)
                CFRelease(_source);
            if (_destination != IntPtr.Zero)
                CFRelease(_destination);
            _source = _destination = IntPtr.Zero;
        }
    }
}
