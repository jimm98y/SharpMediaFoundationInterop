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
    /// A decoder of Android's, of MediaCodec through the NDK, of any <see cref="VideoCodec"/> the device has a decoder of,
    /// made for the <see cref="VideoDecoderOptions"/>: it takes what Media Foundation's decoders take - NAL units, OBUs or
    /// pictures, each with its time - gathers NAL units and OBUs into the access units MediaCodec takes one at a time, and
    /// hands NV12 out, in the order the frames are shown, padded to <see cref="Width"/> by <see cref="Height"/>. Frames are
    /// rendered into an AImageReader, whose images say the layout of their planes, which a decoder's own buffers do not.
    /// The decoder is made as the configuration it needs comes in band - the parameter sets of H.264 and H.265, the headers
    /// before the first MPEG-4 picture; what comes before that is dropped.
    /// </summary>
    [SupportedOSPlatform("android26.0")]
    public sealed unsafe class AndroidVideoDecoder : IMediaVideoTransform
    {
        public const uint ResMultiple = 2;

        private static readonly byte[] StartCode = [0, 0, 0, 1];
        private static readonly Dictionary<VideoCodec, bool> Supported = new Dictionary<VideoCodec, bool>();

        private readonly VideoCodec _codec;
        private readonly VideoDecoderOptions _options;
        private readonly AccessUnitAssembler _units;
        /// <summary>The frames decoded, in order of their times; of frames of one time, as they were decoded.</summary>
        private readonly List<(byte[] Frame, long Time)> _frames = new List<(byte[], long)>();
        /// <summary>
        /// The frames held back before the earliest is handed out: of MPEG-2 and MPEG-4 Part 2, one - the anchor a B-picture
        /// comes before - as a decoder of them may hand frames out in the order they are decoded; of the rest, none.
        /// </summary>
        private readonly int _reorderDepth;
        private readonly Stack<byte[]> _pool = new Stack<byte[]>();

        private IntPtr _reader;
        private IntPtr _window;
        private IntPtr _codecHandle;
        private bool _initialized;
        private bool _draining;
        private bool _ended;
        private int _rendered;
        private bool _disposed;

        public AndroidVideoDecoder(VideoCodec codec, VideoDecoderOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _codec = codec;
            Mime(codec);
            OriginalWidth = options.Width;
            OriginalHeight = options.Height;
            Width = MediaUtils.RoundToMultipleOf(Math.Max(options.Width, 2), ResMultiple);
            Height = MediaUtils.RoundToMultipleOf(Math.Max(options.Height, 2), ResMultiple);
            OutputSize = Width * Height * 3 / 2;
            _reorderDepth = codec is VideoCodec.H262 or VideoCodec.Mpeg4 ? 1 : 0;
            if (codec is VideoCodec.H264 or VideoCodec.H265 or VideoCodec.AV1)
            {
                _units = new AccessUnitAssembler(codec, lengthPrefixed: false);
                _units.AccessUnit += OnAccessUnit;
            }
        }

        public VideoCodec Codec => _codec;
        public uint OriginalWidth { get; }
        public uint OriginalHeight { get; }
        public uint Width { get; }
        public uint Height { get; }
        public uint OutputSize { get; }
        public Guid InputFormat => MediaFormats.Of(_codec);
        public Guid OutputFormat => MediaFormats.NV12;

        internal static string Mime(VideoCodec codec) => codec switch
        {
            VideoCodec.H264 => "video/avc",
            VideoCodec.H265 => "video/hevc",
            VideoCodec.VP9 => "video/x-vnd.on2.vp9",
            VideoCodec.AV1 => "video/av01",
            VideoCodec.H262 => "video/mpeg2",
            VideoCodec.H263 => "video/3gpp",
            VideoCodec.Mpeg4 => "video/mp4v-es",
            _ => throw new NotSupportedException($"No MediaCodec type of {codec}")
        };

        /// <summary>Whether the device has a decoder of the codec: one MediaCodec makes of its type.</summary>
        public static bool Supports(VideoCodec codec)
        {
            if (!Enum.IsDefined(codec) || codec == VideoCodec.ProRes || !MediaNdk.IsAvailable)
                return false;
            lock (Supported)
            {
                if (!Supported.TryGetValue(codec, out bool supported))
                {
                    IntPtr probe = AMediaCodec_createDecoderByType(Mime(codec));
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
            if (_initialized)
                return;
            if (!Supports(_codec))
                throw new NotSupportedException($"This device has no {_codec} decoder");

            // a few images in flight: the decoder renders ahead of what is read
            if (AImageReader_new((int)Width, (int)Height, AIMAGE_FORMAT_YUV_420_888, 4, out _reader) != AMEDIA_OK ||
                AImageReader_getWindow(_reader, out _window) != AMEDIA_OK)
                throw new InvalidOperationException("No image reader for the decoder's frames");
            _initialized = true;

            // the codecs whose configuration is of their pictures alone are made now
            if (_codec is VideoCodec.VP9 or VideoCodec.AV1 or VideoCodec.H262 or VideoCodec.H263)
                Configure(null, null);
        }

        /// <summary>Makes and starts the decoder, of the configuration given: its csd-0 and csd-1.</summary>
        private void Configure(byte[] csd0, byte[] csd1)
        {
            IntPtr format = AMediaFormat_new();
            try
            {
                AMediaFormat_setString(format, "mime", Mime(_codec));
                AMediaFormat_setInt32(format, "width", (int)Width);
                AMediaFormat_setInt32(format, "height", (int)Height);
                AMediaFormat_setInt32(format, "max-input-size", (int)Math.Max(OutputSize, 1 << 20));
                if (_options.LowLatency)
                    AMediaFormat_setInt32(format, "low-latency", 1);
                if (csd0 != null)
                    SetBuffer(format, "csd-0", csd0);
                if (csd1 != null)
                    SetBuffer(format, "csd-1", csd1);

                _codecHandle = AMediaCodec_createDecoderByType(Mime(_codec));
                if (_codecHandle == IntPtr.Zero)
                    throw new NotSupportedException($"This device has no {_codec} decoder");
                int status = AMediaCodec_configure(_codecHandle, format, _window, IntPtr.Zero, 0);
                if (status == AMEDIA_OK)
                    status = AMediaCodec_start(_codecHandle);
                if (status != AMEDIA_OK)
                {
                    AMediaCodec_delete(_codecHandle);
                    _codecHandle = IntPtr.Zero;
                    throw new NotSupportedException($"The {_codec} decoder takes no stream of this configuration: {status}");
                }
            }
            finally
            {
                AMediaFormat_delete(format);
            }
        }

        public bool ProcessInput(byte[] data, long timestamp) => ProcessInput(new ReadOnlySpan<byte>(data), timestamp);

        /// <summary>One sample in - a NAL unit, an access unit, an OBU, a picture - and its time.</summary>
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            ThrowIfNotInitialized();
            if (data.IsEmpty)
                return false;
            if (_units != null)
            {
                _units.Push(data, timestamp);
                return true;
            }

            if (_codecHandle == IntPtr.Zero && _codec == VideoCodec.Mpeg4)
            {
                // MPEG-4 Part 2's decoder is told its headers, those before the first VOP, as csd-0
                byte[] headers = SampleDescriptions.ReadMpeg4Headers(data);
                if (headers == null)
                    return false;
                Configure(headers, null);
            }
            if (_codecHandle == IntPtr.Zero)
                return false;
            Queue(ReadOnlySpan<byte>.Empty, data, timestamp, 0);
            return true;
        }

        /// <summary>An access unit complete: decoded, the decoder made first, of the parameter sets, where it is not yet.</summary>
        private void OnAccessUnit(ReadOnlySpan<byte> unit, long time)
        {
            byte[] parameterSets = null;
            if (_codec != VideoCodec.AV1 && _units.ParameterSetsChanged && _units.HasParameterSets)
            {
                var sets = _units.TakeParameterSets();
                if (_codecHandle == IntPtr.Zero)
                {
                    // of H.264 the SPS as csd-0 and the PPS as csd-1; of H.265 all three as csd-0 - each with its start code
                    var spsEnd = _codec == VideoCodec.H264 ? sets.FindIndex(s => (s[0] & 0x1F) == 8) : sets.Count;
                    Configure(AnnexB(sets.GetRange(0, spsEnd)), spsEnd < sets.Count ? AnnexB(sets.GetRange(spsEnd, sets.Count - spsEnd)) : null);
                }
                else
                {
                    // parameter sets that changed go in band, before the picture they are of
                    parameterSets = AnnexB(sets);
                }
            }
            if (_codecHandle == IntPtr.Zero)
                return; // nothing to decode it with: its configuration is yet to come
            Queue(parameterSets, unit, time, 0);
        }

        private static byte[] AnnexB(List<byte[]> nalUnits)
        {
            var result = new List<byte>();
            foreach (var nal in nalUnits)
            {
                result.AddRange(StartCode);
                result.AddRange(nal);
            }
            return result.ToArray();
        }

        /// <summary>
        /// Queues a sample - a prefix, then the data - into the decoder's next input buffer, taking out what it has decoded
        /// while it has none free.
        /// </summary>
        private void Queue(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> data, long time, uint flags)
        {
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                nint index = AMediaCodec_dequeueInputBuffer(_codecHandle, 5000);
                if (index >= 0)
                {
                    byte* buffer = AMediaCodec_getInputBuffer(_codecHandle, (nuint)index, out nuint capacity);
                    int length = prefix.Length + data.Length;
                    if ((nuint)length > capacity)
                    {
                        if (Log.WarnEnabled)
                            Log.Warn($"A {_codec} sample of {length} bytes is larger than the decoder's buffer of {capacity}: it is left out");
                        AMediaCodec_queueInputBuffer(_codecHandle, (nuint)index, 0, 0, (ulong)(time / 10), flags);
                        return;
                    }
                    prefix.CopyTo(new Span<byte>(buffer, prefix.Length));
                    data.CopyTo(new Span<byte>(buffer + prefix.Length, data.Length));
                    AMediaCodec_queueInputBuffer(_codecHandle, (nuint)index, 0, (nuint)length, (ulong)Math.Max(time / 10, 0), flags);
                    return;
                }
                Pump();
                if (stopwatch.ElapsedMilliseconds > 5000)
                {
                    if (Log.WarnEnabled)
                        Log.Warn($"The {_codec} decoder took no input for 5 s: the sample is left out");
                    return;
                }
            }
        }

        /// <summary>
        /// Renders every frame the decoder has into the image reader, one at a time, each taken out of the reader as NV12
        /// before the next is rendered: a decoder's surface does not wait for its reader, and drops what is queued on it
        /// unread. An image follows its render a little after, through the window's queue.
        /// </summary>
        private void Pump()
        {
            if (_codecHandle == IntPtr.Zero)
                return;
            AMediaCodecBufferInfo info;
            while (true)
            {
                nint index = AMediaCodec_dequeueOutputBuffer(_codecHandle, &info, 0);
                if (index == INFO_TRY_AGAIN_LATER)
                    break;
                if (index < 0)
                    continue; // the format, or the buffers, changed
                bool end = (info.Flags & BUFFER_FLAG_END_OF_STREAM) != 0;
                bool frame = info.Size > 0 && (info.Flags & BUFFER_FLAG_CODEC_CONFIG) == 0;
                AMediaCodec_releaseOutputBuffer(_codecHandle, (nuint)index, frame);
                if (frame)
                {
                    _rendered++;
                    TakeImages(TimeSpan.FromMilliseconds(100));
                }
                if (end)
                    _ended = true;
            }
            TakeImages(TimeSpan.Zero);
        }

        /// <summary>Takes the images rendered and not yet read, waiting for each up to the time given.</summary>
        private void TakeImages(TimeSpan wait)
        {
            var stopwatch = Stopwatch.StartNew();
            while (_rendered > 0)
            {
                if (AImageReader_acquireNextImage(_reader, out IntPtr image) != AMEDIA_OK)
                {
                    if (stopwatch.Elapsed >= wait)
                        return;
                    Thread.Sleep(1);
                    continue;
                }
                _rendered--;
                try
                {
                    byte[] nv12 = _pool.Count > 0 ? _pool.Pop() : new byte[OutputSize];
                    AImage_getTimestamp(image, out long ns);
                    if (CopyNV12(image, nv12, Width, Height, out _, out _))
                    {
                        // after every frame of a time no later than its own
                        long time = ns / 100;
                        int at = _frames.Count;
                        while (at > 0 && _frames[at - 1].Time > time)
                            at--;
                        _frames.Insert(at, (nv12, time));
                    }
                    else
                    {
                        _pool.Push(nv12);
                    }
                }
                finally
                {
                    AImage_delete(image);
                }
            }
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
            Pump();
            if (_draining)
            {
                var stopwatch = Stopwatch.StartNew();
                while (_frames.Count == 0 && (!_ended || _rendered > 0) && stopwatch.ElapsedMilliseconds < 10000)
                {
                    Thread.Sleep(2);
                    Pump();
                }
            }
            if (_frames.Count == 0 || (_frames.Count <= _reorderDepth && !_draining))
                return false;
            var frame = _frames[0];
            _frames.RemoveAt(0);
            if (buffer == null || buffer.Length < OutputSize)
                buffer = new byte[OutputSize];
            Buffer.BlockCopy(frame.Frame, 0, buffer, 0, (int)OutputSize);
            _pool.Push(frame.Frame);
            length = OutputSize;
            timestamp = frame.Time;
            return true;
        }

        public bool Drain()
        {
            BeginDrain();
            EndDrain();
            return true;
        }

        /// <summary>Ends the stream, so that every frame the decoder holds comes out; read them out before <see cref="EndDrain"/>.</summary>
        public void BeginDrain()
        {
            ThrowIfNotInitialized();
            _units?.Flush();
            _draining = true;
            if (_codecHandle == IntPtr.Zero)
            {
                _ended = true;
                return;
            }
            Queue(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, 0, BUFFER_FLAG_END_OF_STREAM);
        }

        /// <summary>Takes input again: the decoder, ended, is flushed, its configuration kept.</summary>
        public void EndDrain()
        {
            ThrowIfNotInitialized();
            _draining = false;
            _ended = false;
            if (_codecHandle != IntPtr.Zero)
                AMediaCodec_flush(_codecHandle);
            DropImages();
        }

        /// <summary>Lets go of everything held, for a seek: the input starts again at a key frame.</summary>
        public void Flush()
        {
            ThrowIfNotInitialized();
            _units?.Clear();
            if (_codecHandle != IntPtr.Zero)
                AMediaCodec_flush(_codecHandle);
            DropImages();
            foreach (var frame in _frames)
                _pool.Push(frame.Frame);
            _frames.Clear();
        }

        private void DropImages()
        {
            Thread.Sleep(5);
            while (AImageReader_acquireNextImage(_reader, out IntPtr image) == AMEDIA_OK)
                AImage_delete(image);
            _rendered = 0;
        }

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_initialized)
                throw new InvalidOperationException("The decoder is to be initialized first.");
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
            if (_reader != IntPtr.Zero)
            {
                AImageReader_delete(_reader);
                _reader = IntPtr.Zero;
            }
        }
    }
}
