using System;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Codecs;
using SharpMediaFoundationInterop.Utils;

namespace SharpMediaFoundationInterop.Input
{
    /// <summary>
    /// Captures a screen on Linux under Wayland, where no client may read the screen of itself: xdg-desktop-portal's
    /// ScreenCast is asked for one - the user picks it in the desktop's dialog, which <see cref="Initialize"/> waits for -
    /// and pipewiresrc reads the PipeWire stream the portal hands out, converted to 32 bit BGRA, the cursor in it, bottom-up
    /// or top-down as <see cref="BottomUp"/> says. As DXGI's duplication does, it hands a frame out only where the screen
    /// changed. Throws <see cref="UnauthorizedAccessException"/> where the user cancels the dialog.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public sealed class PortalScreenCapture : IMediaVideoSource
    {
        /// <summary>How long <see cref="ReadSample(byte[], out long)"/> waits for the screen to change before it returns false.</summary>
        public int ReadTimeoutInMilliseconds { get; set; } = 40;

        /// <summary>
        /// Whether frames are handed out bottom-up, the first row the bottom one - as a video transform takes RGB - rather
        /// than top-down, as a bitmap is.
        /// </summary>
        public bool BottomUp { get; set; } = true;

        /// <summary>How long the user is given to choose a screen in the portal's dialog.</summary>
        public TimeSpan ChooseTimeout { get; set; } = TimeSpan.FromMinutes(5);

        private ScreenCastPortal _portal;
        private GstAppPipeline _pipeline;
        private IntPtr _first;
        private long _firstTime = -1;
        private bool _disposed;

        public uint Width { get; private set; }
        public uint Height { get; private set; }
        public uint OriginalWidth => Width;
        public uint OriginalHeight => Height;
        /// <summary>BGRA in memory: Media Foundation's ARGB32, as DXGI's duplication hands it out.</summary>
        public Guid OutputFormat => MediaFormats.ARGB32;
        public uint OutputSize => Width * Height * 4;

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipeline != null)
                return;
            if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") == null && Log.WarnEnabled)
                Log.Warn("No Wayland session: the screen capture portal may have no screen to offer");

            Gst.EnsureAvailable();
            _portal = new ScreenCastPortal();
            _portal.Start(ChooseTimeout);

            _pipeline = new GstAppPipeline(
                $"pipewiresrc fd={_portal.PipeWireFd} path={_portal.NodeId} always-copy=true do-timestamp=true ! " +
                "videoconvert ! video/x-raw,format=BGRA ! appsink name=sink drop=true max-buffers=2 sync=false");
            _pipeline.Play();

            // the first frame tells the size the stream is of
            _first = _pipeline.Pull(TimeSpan.FromSeconds(10));
            if (_first == IntPtr.Zero)
                throw new InvalidOperationException("The screen capture gave no frame");
            var (width, height) = GstFrames.Size(_first);
            Width = (uint)width;
            Height = (uint)height;
        }

        /// <summary>The next frame, as <see cref="BottomUp"/> says; false where the screen has not changed in <see cref="ReadTimeoutInMilliseconds"/>.</summary>
        public bool ReadSample(byte[] sampleBytes, out long timestamp) => ReadSample(sampleBytes, BottomUp, out timestamp);

        /// <summary>
        /// The next frame, 32 bit BGRA of <see cref="Width"/> by <see cref="Height"/>, bottom-up or top-down, and its time,
        /// from the first frame's; false where the screen has not changed in <see cref="ReadTimeoutInMilliseconds"/>.
        /// </summary>
        public bool ReadSample(byte[] sampleBytes, bool bottomUp, out long timestamp)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            timestamp = 0;
            if (_pipeline == null)
                throw new InvalidOperationException("The capture is to be initialized first.");

            IntPtr sample = _first != IntPtr.Zero ? _first : _pipeline.Pull(TimeSpan.FromMilliseconds(ReadTimeoutInMilliseconds));
            _first = IntPtr.Zero;
            if (sample == IntPtr.Zero)
                return false;
            try
            {
                long time = GstFrames.Time(sample);
                if (_firstTime < 0)
                    _firstTime = time;
                timestamp = time - _firstTime;
                return GstFrames.CopyBgra(sample, sampleBytes, Width, Height, bottomUp);
            }
            finally
            {
                Gst.gst_mini_object_unref(sample);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_first != IntPtr.Zero)
                Gst.gst_mini_object_unref(_first);
            _pipeline?.Dispose();
            _pipeline = null;
            _portal?.Dispose();
            _portal = null;
        }
    }
}
