using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using SharpMediaFoundationInterop.Utils;
using static SharpMediaFoundationInterop.Utils.MediaNdk;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// An encoder of Android's, of MediaCodec through the NDK, of H.264, H.265, VP9 or AV1 - where the device has one -
    /// made for the <see cref="VideoEncoderOptions"/>: NV12 in, of <see cref="Width"/> by <see cref="Height"/>, and out as
    /// Media Foundation's encoders hand it: of H.264 and H.265 an access unit at a time, Annex B, the parameter sets - the
    /// codec configuration MediaCodec hands out first - before each key frame; of VP9 a frame; of AV1 a temporal unit. It
    /// encodes no B-frames, so the units come out in the order the frames went in, each with the time of its frame.
    /// </summary>
    [SupportedOSPlatform("android26.0")]
    public sealed unsafe class AndroidVideoEncoder : IMediaVideoEncoder
    {
        // MediaCodecInfo.EncoderCapabilities' bitrate modes
        private const int BITRATE_MODE_CQ = 0;
        private const int BITRATE_MODE_VBR = 1;
        private const int BITRATE_MODE_CBR = 2;

        private static readonly Dictionary<VideoCodec, bool> Supported = new Dictionary<VideoCodec, bool>();

        private readonly VideoCodec _codec;
        private readonly VideoEncoderOptions _options;
        private readonly List<string> _unapplied = new List<string>();
        private readonly Queue<(byte[] Unit, long Time)> _units = new Queue<(byte[], long)>();
        private IntPtr _codecHandle;
        private byte[] _config;
        private int _stride, _sliceHeight;
        private bool _draining;
        private bool _ended;
        private bool _disposed;

        public AndroidVideoEncoder(VideoCodec codec, VideoEncoderOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (codec is not (VideoCodec.H264 or VideoCodec.H265 or VideoCodec.VP9 or VideoCodec.AV1))
                throw new NotSupportedException($"No MediaCodec encoder of {codec}");
            if (options.Width == 0 || options.Height == 0)
                throw new ArgumentException("The picture's size is to be given", nameof(options));
            _codec = codec;
        }

        public VideoCodec Codec => _codec;
        public uint OriginalWidth => _options.Width;
        public uint OriginalHeight => _options.Height;
        public uint Width => _options.Width;
        public uint Height => _options.Height;
        public uint OutputSize => Width * Height * 3 / 2;
        public Guid InputFormat => MediaFormats.NV12;
        public Guid OutputFormat => MediaFormats.Of(_codec);
        public IReadOnlyList<string> UnappliedSettings => _unapplied;

        /// <summary>Whether the device has an encoder of the codec: one MediaCodec makes of its type.</summary>
        public static bool Supports(VideoCodec codec)
        {
            if (codec is not (VideoCodec.H264 or VideoCodec.H265 or VideoCodec.VP9 or VideoCodec.AV1) || !MediaNdk.IsAvailable)
                return false;
            lock (Supported)
            {
                if (!Supported.TryGetValue(codec, out bool supported))
                {
                    IntPtr probe = AMediaCodec_createEncoderByType(AndroidVideoDecoder.Mime(codec));
                    supported = probe != IntPtr.Zero;
                    if (supported)
                        AMediaCodec_delete(probe);
                    Supported[codec] = supported;
                }
                return supported;
            }
        }

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_codecHandle != IntPtr.Zero)
                return;
            if (!Supports(_codec))
                throw new NotSupportedException($"This device has no {_codec} encoder");

            _unapplied.Clear();
            var o = _options;
            int mode = o.RateControl switch
            {
                RateControlMode.ConstantBitrate => BITRATE_MODE_CBR,
                RateControlMode.Quality or RateControlMode.ConstantQp => BITRATE_MODE_CQ,
                _ => BITRATE_MODE_VBR
            };
            // an encoder that has no constant quality takes the bit rate instead, and says so
            if (!TryConfigure(mode) && (mode == BITRATE_MODE_CQ || mode == BITRATE_MODE_CBR))
            {
                _unapplied.Add($"{nameof(o.RateControl)}: {o.RateControl} not supported by the encoder; of variable bit rate instead");
                if (o.RateControl == RateControlMode.Quality)
                    _unapplied.Add($"{nameof(o.Quality)}: not supported by the encoder");
                if (o.RateControl == RateControlMode.ConstantQp)
                    _unapplied.Add($"{nameof(o.Qp)}: not supported by the encoder");
                if (!TryConfigure(BITRATE_MODE_VBR))
                    throw new NotSupportedException($"The {_codec} encoder takes no {Width}x{Height} NV12");
            }
            else if (_codecHandle == IntPtr.Zero)
            {
                throw new NotSupportedException($"The {_codec} encoder takes no {Width}x{Height} NV12");
            }
            if (o.Threads > 0)
                _unapplied.Add($"{nameof(o.Threads)}: not supported by the encoder");
            if (o.RateControl == RateControlMode.ConstantQp && !OperatingSystem.IsAndroidVersionAtLeast(31))
                _unapplied.Add($"{nameof(o.Qp)}: of Android 12 or later alone");
        }

        private bool TryConfigure(int mode)
        {
            var o = _options;
            float fps = o.FpsNom > 0 && o.FpsDenom > 0 ? (float)o.FpsNom / o.FpsDenom : 30;
            IntPtr format = AMediaFormat_new();
            IntPtr codec = AMediaCodec_createEncoderByType(AndroidVideoDecoder.Mime(_codec));
            try
            {
                AMediaFormat_setString(format, "mime", AndroidVideoDecoder.Mime(_codec));
                AMediaFormat_setInt32(format, "width", (int)Width);
                AMediaFormat_setInt32(format, "height", (int)Height);
                AMediaFormat_setInt32(format, "color-format", COLOR_FormatYUV420SemiPlanar);
                AMediaFormat_setFloat(format, "frame-rate", fps);
                // the key frame interval is MediaCodec's of seconds: of the frames asked, at the frame rate; 2 s, of none asked
                AMediaFormat_setFloat(format, "i-frame-interval", o.KeyFrameInterval > 0 ? o.KeyFrameInterval / fps : 2f);
                AMediaFormat_setInt32(format, "bitrate-mode", mode);
                AMediaFormat_setInt32(format, "bitrate", (int)o.Bitrate);
                AMediaFormat_setInt32(format, "max-bframes", 0);
                if (mode == BITRATE_MODE_CQ && o.RateControl == RateControlMode.Quality)
                    AMediaFormat_setInt32(format, "quality", (int)Math.Min(o.Quality, 100));
                if (o.RateControl == RateControlMode.ConstantQp)
                {
                    AMediaFormat_setInt32(format, "video-qp-min", (int)o.Qp);
                    AMediaFormat_setInt32(format, "video-qp-max", (int)o.Qp);
                }

                if (codec == IntPtr.Zero || AMediaCodec_configure(codec, format, IntPtr.Zero, IntPtr.Zero, CONFIGURE_FLAG_ENCODE) != AMEDIA_OK ||
                    AMediaCodec_start(codec) != AMEDIA_OK)
                {
                    if (codec != IntPtr.Zero)
                        AMediaCodec_delete(codec);
                    return false;
                }
                _codecHandle = codec;
                codec = IntPtr.Zero;

                // the layout of the input the encoder takes: rows of its stride, the chroma after its slice height
                _stride = (int)Width;
                _sliceHeight = (int)Height;
                if (OperatingSystem.IsAndroidVersionAtLeast(28))
                {
                    IntPtr input = AMediaCodec_getInputFormat(_codecHandle);
                    if (input != IntPtr.Zero)
                    {
                        if (AMediaFormat_getInt32(input, "stride", out int stride) && stride >= Width)
                            _stride = stride;
                        if (AMediaFormat_getInt32(input, "slice-height", out int sliceHeight) && sliceHeight >= Height)
                            _sliceHeight = sliceHeight;
                        AMediaFormat_delete(input);
                    }
                }
                return true;
            }
            finally
            {
                AMediaFormat_delete(format);
            }
        }

        [System.Runtime.InteropServices.DllImport("libmediandk.so")]
        [SupportedOSPlatform("android28.0")]
        private static extern IntPtr AMediaCodec_getInputFormat(IntPtr codec);

        public bool ProcessInput(byte[] data, long timestamp) => ProcessInput(new ReadOnlySpan<byte>(data), timestamp);

        /// <summary>One NV12 frame in, of <see cref="Width"/> by <see cref="Height"/>, and its time.</summary>
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            ThrowIfNotInitialized();
            if (data.Length < OutputSize)
                throw new ArgumentException($"An NV12 frame of {Width}x{Height} is of {OutputSize} bytes", nameof(data));

            var stopwatch = Stopwatch.StartNew();
            nint index;
            while ((index = AMediaCodec_dequeueInputBuffer(_codecHandle, 5000)) < 0)
            {
                Pump();
                if (stopwatch.ElapsedMilliseconds > 5000)
                    return false;
            }
            byte* buffer = AMediaCodec_getInputBuffer(_codecHandle, (nuint)index, out nuint capacity);
            int size = _stride * _sliceHeight + _stride * (int)(Height / 2);
            if ((nuint)size > capacity)
                size = (int)capacity;
            var target = new Span<byte>(buffer, size);
            int w = (int)Width, h = (int)Height;
            for (int row = 0; row < h; row++)
                data.Slice(row * w, w).CopyTo(target.Slice(row * _stride, w));
            var chroma = target.Slice(_stride * _sliceHeight);
            for (int row = 0; row < h / 2 && (row + 1) * _stride <= chroma.Length; row++)
                data.Slice(w * h + row * w, w).CopyTo(chroma.Slice(row * _stride, w));
            AMediaCodec_queueInputBuffer(_codecHandle, (nuint)index, 0, (nuint)size, (ulong)Math.Max(timestamp / 10, 0), 0);
            Pump();
            return true;
        }

        /// <summary>
        /// Takes every unit the encoder has made: of the codec configuration kept - the parameter sets of H.264 and H.265, the
        /// sequence header of AV1 - and put before each key frame after it.
        /// </summary>
        private void Pump()
        {
            AMediaCodecBufferInfo info;
            while (true)
            {
                nint index = AMediaCodec_dequeueOutputBuffer(_codecHandle, &info, 0);
                if (index == INFO_TRY_AGAIN_LATER)
                    break;
                if (index < 0)
                    continue;
                if ((info.Flags & BUFFER_FLAG_END_OF_STREAM) != 0)
                    _ended = true;
                if (info.Size > 0)
                {
                    byte* data = AMediaCodec_getOutputBuffer(_codecHandle, (nuint)index, out _);
                    var payload = new ReadOnlySpan<byte>(data + info.Offset, info.Size);
                    if ((info.Flags & BUFFER_FLAG_CODEC_CONFIG) != 0)
                    {
                        // of AV1 an 'av1C', whose configOBUs - the sequence header, where it has one - follow its 4 bytes: those
                        // alone go in band, and of none, nothing, as the key frames have the sequence header in band of themselves
                        _config = _codec == VideoCodec.AV1 && payload.Length >= 4 && payload[0] == 0x81
                            ? (payload.Length > 4 ? payload.Slice(4).ToArray() : null)
                            : payload.ToArray();
                    }
                    else
                    {
                        bool key = (info.Flags & BUFFER_FLAG_KEY_FRAME) != 0 && _config != null && _codec != VideoCodec.VP9 && !payload.StartsWith(_config);
                        var unit = new byte[(key ? _config.Length : 0) + payload.Length];
                        if (key)
                            _config.CopyTo(unit, 0);
                        payload.CopyTo(unit.AsSpan(key ? _config.Length : 0));
                        _units.Enqueue((unit, info.PresentationTimeUs * 10));
                    }
                }
                AMediaCodec_releaseOutputBuffer(_codecHandle, (nuint)index, false);
            }
        }

        public bool ProcessOutput(ref byte[] buffer, out uint length) => ProcessOutput(ref buffer, out length, out _);

        /// <summary>The next unit encoded, and the time of its frame. While draining it waits for one, up to the stream's end.</summary>
        public bool ProcessOutput(ref byte[] buffer, out uint length, out long timestamp)
        {
            ThrowIfNotInitialized();
            length = 0;
            timestamp = 0;
            Pump();
            if (_draining)
            {
                var stopwatch = Stopwatch.StartNew();
                while (_units.Count == 0 && !_ended && stopwatch.ElapsedMilliseconds < 10000)
                {
                    Thread.Sleep(2);
                    Pump();
                }
            }
            if (!_units.TryDequeue(out var unit))
                return false;
            if (buffer == null || buffer.Length < unit.Unit.Length)
                buffer = new byte[Math.Max(unit.Unit.Length, OutputSize)];
            Buffer.BlockCopy(unit.Unit, 0, buffer, 0, unit.Unit.Length);
            length = (uint)unit.Unit.Length;
            timestamp = unit.Time;
            return true;
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
            _draining = true;
            var stopwatch = Stopwatch.StartNew();
            nint index;
            while ((index = AMediaCodec_dequeueInputBuffer(_codecHandle, 5000)) < 0 && stopwatch.ElapsedMilliseconds < 5000)
                Pump();
            if (index >= 0)
                AMediaCodec_queueInputBuffer(_codecHandle, (nuint)index, 0, 0, 0, BUFFER_FLAG_END_OF_STREAM);
        }

        /// <summary>Takes input again: the encoder, ended, is flushed.</summary>
        public void EndDrain()
        {
            ThrowIfNotInitialized();
            _draining = false;
            _ended = false;
            AMediaCodec_flush(_codecHandle);
        }

        public void Flush()
        {
            ThrowIfNotInitialized();
            AMediaCodec_flush(_codecHandle);
            _units.Clear();
        }

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_codecHandle == IntPtr.Zero)
                throw new InvalidOperationException("The encoder is to be initialized first.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_codecHandle != IntPtr.Zero)
            {
                AMediaCodec_stop(_codecHandle);
                AMediaCodec_delete(_codecHandle);
                _codecHandle = IntPtr.Zero;
            }
        }
    }
}
