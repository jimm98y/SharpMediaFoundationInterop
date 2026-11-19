using System;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpMediaFoundationInterop.Utils;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// A video of the frames pushed into it - BGRA, top-down, all of one size - for a control to show: the latest each time
    /// it asks, as soon as it comes. Of frames an app makes or decodes itself, of any thread.
    /// </summary>
    public sealed class PushVideoSource : IVideoSource
    {
        private readonly TaskCompletionSource<bool> _first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new object();
        private readonly byte[] _latest;
        private bool _fresh;

        public PushVideoSource(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height));
            Width = width;
            Height = height;
            _latest = new byte[width * height * 4];
            VideoInfo = new VideoInfo { Width = (uint)width, Height = (uint)height, OriginalWidth = (uint)width, OriginalHeight = (uint)height, PixelFormat = PixelFormat.BGRA32 };
        }

        public int Width { get; }
        public int Height { get; }
        public VideoInfo VideoInfo { get; }
        public bool IsStill => false;

        /// <summary>A frame of rows of <paramref name="stride"/> bytes, <see cref="Width"/> by <see cref="Height"/> of it: copied, shown next.</summary>
        public void Push(ReadOnlySpan<byte> bgra, int stride)
        {
            if (stride < Width * 4 || bgra.Length < (Height - 1) * stride + Width * 4)
                throw new ArgumentException("The frame is smaller than its size and stride say.", nameof(bgra));
            lock (_lock)
            {
                for (int y = 0; y < Height; y++)
                    bgra.Slice(y * stride, Width * 4).CopyTo(_latest.AsSpan(y * Width * 4));
                _fresh = true;
            }
            _first.TrySetResult(true);
        }

        /// <summary>The latest frame, as a bitmap.</summary>
        public BitmapSource Snapshot()
        {
            lock (_lock)
                return BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, _latest, Width * 4);
        }

        /// <summary>Done once the first frame is pushed.</summary>
        public Task InitializeAsync() => _first.Task;

        public byte[] GetVideoSample(out long timestamp)
        {
            timestamp = -1;
            lock (_lock)
            {
                if (!_fresh)
                    return Array.Empty<byte>();
                _fresh = false;
                var frame = FramePool.Shared.Rent(_latest.Length);
                Buffer.BlockCopy(_latest, 0, frame, 0, _latest.Length);
                return frame;
            }
        }

        public void ReturnVideoSample(byte[] sample) => FramePool.Shared.Return(sample);
        public object GetVideoFrame(out long timestamp) => GetVideoSample(out timestamp);
        public void ReturnVideoFrame(object frame) => ReturnVideoSample((byte[])frame);
        public bool TrySetOutputFormat(PixelFormat format) => false;
        public bool TryUseDirect3D(Direct3DDevice device) => false;
        public void SetSuspended(bool suspended) { }
        public void Dispose() { }
    }
}
