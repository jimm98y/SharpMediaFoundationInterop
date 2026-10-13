using System;
using SharpMediaFoundationInterop.Codecs;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// The two eyes of a stereo video, each of a source of its own, as one source of side by side frames: the left eye's
    /// picture in the left half, the right eye's in the right, those of the same time together. Each eye is decoded on a
    /// thread of its own - the left on the thread frames are asked for on, the right ahead of it - and the pair is played,
    /// sought in and shown as any other source's frames: see <see cref="VideoControlBase.EyeView"/> to show one eye.
    /// </summary>
    /// <remarks>
    /// The sound and the subtitles are the left source's, where it has any. Frames are paired by their times, within half a
    /// frame of each other; a left frame with no right one of its time is shown with the right frame shown last, and where
    /// the right eye runs out first, its last frame stays.
    /// </remarks>
    public sealed class StereoVideoSource : ISeekableVideoSource, IAudioSource, ISubtitleSource
    {
        private readonly bool _ownsSources;
        private readonly object _initLock = new object();
        private bool _initialized;

        /// <summary>The left eye's source: its sound is the pair's.</summary>
        public IVideoSource Left { get; }

        /// <summary>The right eye's source.</summary>
        public IVideoSource Right { get; }

        private ISeekableVideoSource LeftSeekable => Left as ISeekableVideoSource;
        private ISeekableVideoSource RightSeekable => Right as ISeekableVideoSource;

        public VideoInfo VideoInfo { get; private set; }

        public AudioInfo AudioInfo => (Left as IAudioSource)?.AudioInfo;

        /// <param name="ownsSources">Whether the sources are disposed with this one.</param>
        public StereoVideoSource(IVideoSource left, IVideoSource right, bool ownsSources = true)
        {
            Left = left ?? throw new ArgumentNullException(nameof(left));
            Right = right ?? throw new ArgumentNullException(nameof(right));
            _ownsSources = ownsSources;
        }

        public bool TrySetOutputFormat(PixelFormat format)
        {
            // both, or the halves would not be of one format
            return Left.TrySetOutputFormat(format) & Right.TrySetOutputFormat(format);
        }

        /// <summary>Not on the GPU: the eyes are put side by side on the CPU.</summary>
        public bool TryUseDirect3D(Direct3DDevice device) => false;

        /// <summary>Initializes both sources, once: a player's sound and video threads both ask.</summary>
        public Task InitializeAsync()
        {
            lock (_initLock)
            {
                if (_initialized)
                    return Task.CompletedTask;

                Left.InitializeAsync().GetAwaiter().GetResult();
                Right.InitializeAsync().GetAwaiter().GetResult();
                var left = Left.VideoInfo;
                var right = Right.VideoInfo;
                if (left == null || right == null)
                    return Task.CompletedTask; // not yet: a live source that has not had its first frame

                // Eyes turned to be shown are composed as shown - of frames converted on the CPU, turned already: those as
                // decoded are not yet, and side by side as decoded would be one over the other once turned.
                if (IsDecoded(left.PixelFormat) && (!left.Orientation.IsIdentity || !right.Orientation.IsIdentity)
                    && Left.TrySetOutputFormat(PixelFormat.BGRA32) && Right.TrySetOutputFormat(PixelFormat.BGRA32))
                {
                    left = Left.VideoInfo;
                    right = Right.VideoInfo;
                }
                if (left.PixelFormat != right.PixelFormat)
                    throw new NotSupportedException($"The eyes' frames are of different formats: {left.PixelFormat} and {right.PixelFormat}.");

                VideoInfo = Compose(left, right);
                _frameTolerance = left.FpsNom > 0 && left.FpsDenom > 0
                    ? TimeSpan.TicksPerSecond * left.FpsDenom / left.FpsNom / 2
                    : DefaultFrameTolerance;
                _initialized = true;
            }
            return Task.CompletedTask;
        }

        #region Side by side

        // Where the right eye's picture starts across, and each eye's picture and frame: of the coded size where as decoded,
        // the picture at its top left; of the picture's alone where BGR24 or BGRA32.
        private int _rightX;
        private int _leftWidth, _leftHeight, _leftStride, _leftRows;
        private int _rightWidth, _rightHeight, _rightStride, _rightRows;

        /// <summary>
        /// A plane of a frame: its bytes a pixel across, as a fraction - of 4:2:0's chroma, a pair of samples of two pixels -
        /// its rows of the frame's, as a fraction, and the bytes of black, repeated across a row it has no eye's picture for.
        /// </summary>
        private readonly struct Plane
        {
            public Plane(int bytes, int perPixels, int rowsDivisor, byte[] blank)
            {
                Bytes = bytes;
                PerPixels = perPixels;
                RowsDivisor = rowsDivisor;
                Blank = blank;
            }

            public int Bytes { get; }
            public int PerPixels { get; }
            public int RowsDivisor { get; }
            public byte[] Blank { get; }

            /// <summary>The bytes of a row of so many pixels: of pixels in pairs, of the pairs they are of.</summary>
            public int RowBytes(int pixels) => (pixels + PerPixels - 1) / PerPixels * Bytes;
            public int Rows(int frameRows) => (frameRows + RowsDivisor - 1) / RowsDivisor;
        }

        private Plane[] _planes;

        /// <summary>The planes of frames of a format, and the black of each: of the frame's colour, of its range and bits.</summary>
        private static Plane[] PlanesOf(VideoInfo info)
        {
            switch (info.PixelFormat)
            {
                case PixelFormat.BGR24:
                    return new[] { new Plane(3, 1, 1, new byte[] { 0, 0, 0 }) };
                case PixelFormat.BGRA32:
                    return new[] { new Plane(4, 1, 1, new byte[] { 0, 0, 0, 255 }) };
            }

            // as decoded: of the stream's bits, its black luma - 16 of 8 bits of the limited range, scaled up - and its
            // neutral chroma, each of a byte, or of the high bits of 16, little-endian
            var colour = info.ColourOrDefault;
            int bits = colour.BitDepth;
            int black = colour.FullRange ? 0 : 16 << (bits - 8);
            int neutral = 1 << (bits - 1);
            byte[] Of8(params int[] values) => Array.ConvertAll(values, v => (byte)v);
            byte[] Of16(params int[] values)
            {
                var bytes = new byte[values.Length * 2];
                for (int i = 0; i < values.Length; i++)
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (ushort)(values[i] << (16 - bits)));
                return bytes;
            }

            return info.FrameFormat switch
            {
                VideoFrameFormat.NV12 => new[] { new Plane(1, 1, 1, Of8(black)), new Plane(2, 2, 2, Of8(neutral, neutral)) },
                VideoFrameFormat.P010 or VideoFrameFormat.P016 => new[] { new Plane(2, 1, 1, Of16(black)), new Plane(4, 2, 2, Of16(neutral, neutral)) },
                VideoFrameFormat.YUY2 => new[] { new Plane(4, 2, 1, Of8(black, neutral, black, neutral)) },
                VideoFrameFormat.Y210 or VideoFrameFormat.Y216 => new[] { new Plane(8, 2, 1, Of16(black, neutral, black, neutral)) },
                VideoFrameFormat.AYUV => new[] { new Plane(4, 1, 1, Of8(neutral, neutral, black, 255)) },
                VideoFrameFormat.Y410 => new[] { new Plane(4, 1, 1, BitConverter.GetBytes((uint)(neutral | black << 10 | neutral << 20 | 3 << 30))) },
                VideoFrameFormat.Y416 => new[] { new Plane(8, 1, 1, Of16(neutral, black, neutral, (1 << bits) - 1)) },
                VideoFrameFormat.L8 => new[] { new Plane(1, 1, 1, Of8(black)) },
                VideoFrameFormat.L16 => new[] { new Plane(2, 1, 1, Of16(black)) },
                _ => throw new NotSupportedException($"Frames of {info.FrameFormat} are not supported."),
            };
        }

        /// <summary>Whether frames are as decoded, of the coded size: NV12, or of another frame format.</summary>
        private static bool IsDecoded(PixelFormat format) => format == PixelFormat.NV12 || format == PixelFormat.Yuv;

        private VideoInfo Compose(VideoInfo left, VideoInfo right)
        {
            var format = left.PixelFormat;
            bool decoded = IsDecoded(format);
            if (decoded && left.FrameFormat != right.FrameFormat)
                throw new NotSupportedException($"The eyes' frames are of different formats: {left.FrameFormat} and {right.FrameFormat}.");
            _planes = PlanesOf(left);
            var first = _planes[0];

            // of the pictures as shown: frames converted are turned already, and frames as decoded are of eyes that are not
            _leftWidth = (int)left.ShownWidth;
            _leftHeight = (int)left.ShownHeight;
            _rightWidth = (int)right.ShownWidth;
            _rightHeight = (int)right.ShownHeight;
            _leftStride = decoded ? (int)VideoFrameFormats.Stride(left.FrameFormat, left.Width) : first.RowBytes(_leftWidth);
            _leftRows = decoded ? (int)left.Height : _leftHeight;
            _rightStride = decoded ? (int)VideoFrameFormats.Stride(right.FrameFormat, right.Width) : first.RowBytes(_rightWidth);
            _rightRows = decoded ? (int)right.Height : _rightHeight;

            // chroma of two columns at a time: the right picture starts at an even column
            _rightX = decoded ? (_leftWidth + 1) & ~1 : _leftWidth;
            uint width = (uint)(_rightX + _rightWidth);
            uint height = (uint)Math.Max(_leftHeight, _rightHeight);
            return new VideoInfo
            {
                VideoCodec = left.VideoCodec,
                OriginalWidth = width,
                OriginalHeight = height,
                Width = decoded ? (width + 1) & ~1u : width,
                Height = decoded ? (height + 1) & ~1u : height,
                FpsNom = left.FpsNom,
                FpsDenom = left.FpsDenom,
                PixelFormat = format,
                FrameFormat = left.FrameFormat,
                Colour = left.Colour,
                StereoLayout = StereoLayout.SideBySide,
                Projection = left.Projection,
                ProjectionBounds = left.ProjectionBounds,
            };
        }

        /// <summary>The two eyes' frames side by side, in an array of the pool; a missing right eye black.</summary>
        private byte[] Compose(byte[] left, byte[] right)
        {
            var info = VideoInfo;
            bool decoded = IsDecoded(info.PixelFormat);
            int stride = decoded ? (int)VideoFrameFormats.Stride(info.FrameFormat, info.Width) : _planes[0].RowBytes((int)info.OriginalWidth);
            int rows = decoded ? (int)info.Height : (int)info.OriginalHeight;
            int size = decoded ? (int)VideoFrameFormats.FrameSize(info.FrameFormat, info.Width, info.Height) : stride * rows;
            byte[] frame = ArrayPool<byte>.Shared.Rent(size);

            // each plane, one after another: of 4:2:0, the chroma's after the luma's
            int leftOffset = 0, rightOffset = 0, offset = 0;
            foreach (var plane in _planes)
            {
                int frameRows = plane.Rows(rows);
                CopyPlane(left, leftOffset, _leftStride, plane.RowBytes(_leftWidth), plane.Rows(_leftHeight),
                    frame, offset, stride, 0, plane.RowBytes(_rightX), frameRows, plane.Blank);
                CopyPlane(right, rightOffset, _rightStride, plane.RowBytes(_rightWidth), plane.Rows(_rightHeight),
                    frame, offset, stride, plane.RowBytes(_rightX), stride - plane.RowBytes(_rightX), frameRows, plane.Blank);
                leftOffset += _leftStride * plane.Rows(_leftRows);
                rightOffset += _rightStride * plane.Rows(_rightRows);
                offset += stride * frameRows;
            }
            return frame;
        }

        /// <summary>
        /// One eye's plane into its place in the frame's, row by row, of its span of each row - <paramref name="span"/> bytes
        /// from <paramref name="x"/>; what of the span it has no picture for, or all where it is missing, is
        /// <paramref name="blank"/>, repeated: black, of that plane.
        /// </summary>
        private static void CopyPlane(byte[] source, int sourceOffset, int sourceStride, int rowBytes, int sourceRows,
            byte[] target, int targetOffset, int targetStride, int x, int span, int targetRows, byte[] blank)
        {
            for (int y = 0; y < targetRows; y++)
            {
                var row = target.AsSpan(targetOffset + y * targetStride + x, span);
                int copied = 0;
                if (source != null && y < sourceRows)
                {
                    copied = Math.Min(rowBytes, span);
                    source.AsSpan(sourceOffset + y * sourceStride, copied).CopyTo(row);
                }
                for (int at = copied; at < span; at++)
                    row[at] = blank[(at - copied) % blank.Length];
            }
        }

        #endregion

        #region Frames

        /// <summary>How far apart a pair's times may be where the frame rate is not known: 20 ms.</summary>
        private static readonly long DefaultFrameTolerance = TimeSpan.FromMilliseconds(20).Ticks;
        private long _frameTolerance = DefaultFrameTolerance;

        // The left frame taken and waiting for its right one, and the seek it is of.
        private object _left;
        private long _leftTime;
        private long _leftRequest;

        // The right frame shown last, and the seek it is of: kept for a left frame with none of its own.
        private object _lastRight;
        private long _lastRightRequest;

        // The right eye's frames, taken ahead on a thread of their own, each with its time and the seek it is of.
        private readonly ConcurrentQueue<(object Frame, long Time, long Request)> _rightFrames = new ConcurrentQueue<(object Frame, long Time, long Request)>();
        private const int RightAhead = 3;
        private Thread _rightThread;
        private readonly AutoResetEvent _rightWake = new AutoResetEvent(false);
        private volatile bool _disposed;

        // The seek of the right source its frames ran out at; -1 while there are frames.
        private long _rightEndedRequest = -1;

        public byte[] GetVideoSample(out long timestamp)
        {
            var frame = GetVideoFrame(out timestamp);
            return frame as byte[];
        }

        public object GetVideoFrame(out long timestamp)
        {
            timestamp = -1;
            if (!_initialized)
                return Array.Empty<byte>();
            StartRightThread();

            // the left eye's, unless one is waiting for its right one - and not one of before a seek
            long leftTarget = Interlocked.Read(ref _leftTarget);
            if (_left != null && _leftRequest < leftTarget)
            {
                Left.ReturnVideoFrame(_left);
                _left = null;
            }
            if (_left == null)
            {
                var left = Left.GetVideoFrame(out long time);
                if (left == null)
                    return null; // the end: the left eye's is the pair's
                if (left is byte[] bytes && bytes.Length == 0)
                    return left; // none ready yet
                _left = left;
                _leftTime = time;
                _leftRequest = LeftSeekable?.Request ?? 0;
            }

            // its right one: those before its time let go of, one of after it left for the next - and the last one shown let
            // go of where it is of before a seek
            long rightTarget = Interlocked.Read(ref _rightTarget);
            if (_lastRight != null && _lastRightRequest < rightTarget)
            {
                Right.ReturnVideoFrame(_lastRight);
                _lastRight = null;
            }
            int direction = Rate < 0 ? -1 : 1;
            while (true)
            {
                if (!_rightFrames.TryPeek(out var next))
                {
                    bool ended = Interlocked.Read(ref _rightEndedRequest) >= rightTarget && rightTarget >= 0;
                    if (!ended && _lastRight == null)
                        return Array.Empty<byte>(); // wait for the right eye's first
                    if (!ended && _leftTime >= 0)
                        return Array.Empty<byte>(); // wait for its time to come
                    break; // ran out: the last right frame stays
                }
                if (next.Request < rightTarget)
                {
                    // of before a seek
                    if (_rightFrames.TryDequeue(out var stale))
                        Right.ReturnVideoFrame(stale.Frame);
                    _rightWake.Set();
                    continue;
                }
                if (_leftTime < 0 || next.Time < 0)
                {
                    // live, of no times: in the order they come
                    _rightFrames.TryDequeue(out _);
                    KeepRight(next.Frame, next.Request);
                    break;
                }
                long ahead = (next.Time - _leftTime) * direction;
                if (ahead < -_frameTolerance)
                {
                    // before the left frame's time: shown with none, kept as the last
                    _rightFrames.TryDequeue(out _);
                    KeepRight(next.Frame, next.Request);
                    _rightWake.Set();
                    continue;
                }
                if (ahead <= _frameTolerance)
                {
                    _rightFrames.TryDequeue(out _);
                    KeepRight(next.Frame, next.Request);
                }
                break; // after it: the left frame goes with the last right frame
            }
            _rightWake.Set();

            var pair = Compose(_left as byte[], _lastRight as byte[]);
            timestamp = _leftTime;
            if (_leftRequest >= leftTarget)
                Interlocked.Exchange(ref _request, Interlocked.Read(ref _requested));
            Left.ReturnVideoFrame(_left);
            _left = null;
            return pair;
        }

        private void KeepRight(object frame, long request)
        {
            if (_lastRight != null)
                Right.ReturnVideoFrame(_lastRight);
            _lastRight = frame;
            _lastRightRequest = request;
        }

        private void StartRightThread()
        {
            if (_rightThread != null)
                return;
            _rightThread = new Thread(RightLoop) { IsBackground = true, Name = "StereoVideoSource right eye" };
            _rightThread.Start();
        }

        /// <summary>The right eye's frames taken ahead, a few at a time, on a thread of their own: decoded beside the left's.</summary>
        private void RightLoop()
        {
            while (!_disposed)
            {
                try
                {
                    bool ended = Interlocked.Read(ref _rightEndedRequest) >= Interlocked.Read(ref _rightTarget);
                    if (_rightFrames.Count >= RightAhead || ended)
                    {
                        _rightWake.WaitOne(5);
                        continue;
                    }

                    var frame = Right.GetVideoFrame(out long time);
                    long request = RightSeekable?.Request ?? 0;
                    if (frame == null)
                    {
                        Interlocked.Exchange(ref _rightEndedRequest, request);
                        continue;
                    }
                    if (frame is byte[] bytes && bytes.Length == 0)
                    {
                        _rightWake.WaitOne(2);
                        continue;
                    }
                    _rightFrames.Enqueue((frame, time, request));
                }
                catch (Exception ex)
                {
                    if (Log.ErrorEnabled) Log.Error(ex.Message, ex);
                    _rightWake.WaitOne(5);
                }
            }
        }

        public void ReturnVideoFrame(object frame)
        {
            if (frame is byte[] bytes && bytes.Length > 0)
                ArrayPool<byte>.Shared.Return(bytes);
        }

        public void ReturnVideoSample(byte[] sample) => ReturnVideoFrame(sample);

        #endregion

        #region Seeking

        public bool CanSeek => LeftSeekable?.CanSeek == true && RightSeekable?.CanSeek == true;

        public long Duration => LeftSeekable?.Duration ?? -1;

        public long StartTime => LeftSeekable?.StartTime ?? 0;

        public int Rate { get; private set; } = 1;

        // The seek asked for last, and each eye's of it; and the seek of the pair handed out last.
        private long _requested;
        private long _leftTarget;
        private long _rightTarget;
        private long _request;
        private readonly object _seekLock = new object();

        public long Request => Interlocked.Read(ref _request);

        /// <summary>Seeks both eyes to the time, at the rate: the frames of each from before are let go of as they come.</summary>
        public long Seek(long time, int rate)
        {
            if (!CanSeek)
                return Request;
            lock (_seekLock)
            {
                long left = LeftSeekable.Seek(time, rate);
                long right = RightSeekable.Seek(time, rate);
                Rate = rate;
                Interlocked.Exchange(ref _leftTarget, left);
                Interlocked.Exchange(ref _rightTarget, right);
                long requested = Interlocked.Increment(ref _requested);
                _rightWake.Set();
                return requested;
            }
        }

        #endregion

        #region Subtitles

        /// <summary>The left source's subtitles.</summary>
        public IReadOnlyList<SubtitleTrackInfo> SubtitleTracks =>
            (Left as ISubtitleSource)?.SubtitleTracks ?? Array.Empty<SubtitleTrackInfo>();

        public IReadOnlyList<Subtitle> GetSubtitles(int track) =>
            (Left as ISubtitleSource)?.GetSubtitles(track) ?? Array.Empty<Subtitle>();

        #endregion

        #region Sound

        /// <summary>The left source's sound.</summary>
        public byte[] GetAudioSample() => (Left as IAudioSource)?.GetAudioSample();

        public byte[] GetAudioSample(out long timestamp)
        {
            timestamp = -1;
            return Left is IAudioSource audio ? audio.GetAudioSample(out timestamp) : null;
        }

        public void ReturnAudioSample(byte[] sample) => (Left as IAudioSource)?.ReturnAudioSample(sample);

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _rightWake.Set();
            _rightThread?.Join(1000);
            if (_ownsSources)
            {
                Left.Dispose();
                Right.Dispose();
            }
        }
    }
}
