using System;
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

        // Where the right eye's picture starts across, and the eyes' pictures and frames: of the coded size where NV12, the
        // picture at its top left; of the picture's alone where BGR24 or BGRA32.
        private int _rightX;
        private int _leftWidth, _leftHeight, _leftStride, _leftRows;
        private int _rightWidth, _rightHeight, _rightStride, _rightRows;
        private int _bytesPerPixel;

        private VideoInfo Compose(VideoInfo left, VideoInfo right)
        {
            var format = left.PixelFormat;
            bool nv12 = format == PixelFormat.NV12;
            _bytesPerPixel = nv12 ? 1 : format == PixelFormat.BGRA32 ? 4 : 3;

            _leftWidth = (int)left.OriginalWidth;
            _leftHeight = (int)left.OriginalHeight;
            _rightWidth = (int)right.OriginalWidth;
            _rightHeight = (int)right.OriginalHeight;
            _leftStride = nv12 ? (int)left.Width : _leftWidth * _bytesPerPixel;
            _leftRows = nv12 ? (int)left.Height : _leftHeight;
            _rightStride = nv12 ? (int)right.Width : _rightWidth * _bytesPerPixel;
            _rightRows = nv12 ? (int)right.Height : _rightHeight;

            // NV12's chroma is of two columns at a time: the right picture starts at an even column
            _rightX = nv12 ? (_leftWidth + 1) & ~1 : _leftWidth;
            uint width = (uint)(_rightX + _rightWidth);
            uint height = (uint)Math.Max(_leftHeight, _rightHeight);
            return new VideoInfo
            {
                VideoCodec = left.VideoCodec,
                OriginalWidth = width,
                OriginalHeight = height,
                Width = nv12 ? (width + 1) & ~1u : width,
                Height = nv12 ? (height + 1) & ~1u : height,
                FpsNom = left.FpsNom,
                FpsDenom = left.FpsDenom,
                PixelFormat = format,
                StereoLayout = StereoLayout.SideBySide,
                Projection = left.Projection,
                ProjectionBounds = left.ProjectionBounds,
            };
        }

        /// <summary>The two eyes' frames side by side, in an array of the pool; a missing right eye black.</summary>
        private byte[] Compose(byte[] left, byte[] right)
        {
            var info = VideoInfo;
            bool nv12 = info.PixelFormat == PixelFormat.NV12;
            int stride = nv12 ? (int)info.Width : (int)info.OriginalWidth * _bytesPerPixel;
            int rows = nv12 ? (int)info.Height : (int)info.OriginalHeight;
            int size = nv12 ? stride * rows * 3 / 2 : stride * rows;
            byte[] frame = ArrayPool<byte>.Shared.Rent(size);

            // the picture: of the luma, black at 16, or of the pixels
            byte black = nv12 ? (byte)16 : (byte)0;
            CopyPlane(left, 0, _leftStride, _leftWidth * _bytesPerPixel, _leftHeight, frame, 0, stride, 0, rows, black);
            CopyPlane(right, 0, _rightStride, _rightWidth * _bytesPerPixel, _rightHeight, frame, 0, stride, _rightX * _bytesPerPixel, rows, black);
            if (nv12)
            {
                // the chroma, at half the height, of pairs of bytes at half the width: as many bytes a row as the luma
                int chroma = stride * rows;
                CopyPlane(left, _leftStride * _leftRows, _leftStride, _leftWidth, (_leftHeight + 1) / 2, frame, chroma, stride, 0, rows / 2, 128);
                CopyPlane(right, _rightStride * _rightRows, _rightStride, _rightWidth, (_rightHeight + 1) / 2, frame, chroma, stride, _rightX, rows / 2, 128);
            }
            return frame;
        }

        /// <summary>
        /// One eye's plane into its place in the frame's, row by row; the rows of the frame's plane it has none for, or all
        /// where it is missing, are <paramref name="blank"/>: black, of that plane.
        /// </summary>
        private static void CopyPlane(byte[] source, int sourceOffset, int sourceStride, int rowBytes, int sourceRows,
            byte[] target, int targetOffset, int targetStride, int x, int targetRows, byte blank)
        {
            for (int y = 0; y < targetRows; y++)
            {
                var row = target.AsSpan(targetOffset + y * targetStride + x, rowBytes);
                if (source != null && y < sourceRows)
                    source.AsSpan(sourceOffset + y * sourceStride, rowBytes).CopyTo(row);
                else
                    row.Fill(blank);
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
