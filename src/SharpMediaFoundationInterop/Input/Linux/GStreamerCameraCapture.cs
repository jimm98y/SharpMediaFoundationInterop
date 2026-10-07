using System;
using System.Globalization;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Codecs;
using SharpMediaFoundationInterop.Utils;

namespace SharpMediaFoundationInterop.Input
{
    /// <summary>
    /// Captures a camera on Linux, of GStreamer: the device's source - PipeWire's or V4L2's, of the device monitor - in its
    /// widest format of raw pictures, as Windows' camera capture chooses, or of JPEG where it has no raw one that wide, its
    /// frames handed out as NV12. The first frame is waited for as it is initialized, which tells the size.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public sealed class GStreamerCameraCapture : IMediaVideoSource
    {
        /// <summary>How long <see cref="ReadSample"/> waits for a frame before it gives up and returns false.</summary>
        public int ReadTimeoutInMilliseconds { get; set; } = 2000;

        private readonly string _deviceId;
        private GstAppPipeline _pipeline;
        private IntPtr _first;
        private long _firstTime = -1;
        private bool _disposed;

        public uint Width { get; private set; }
        public uint Height { get; private set; }
        public uint OriginalWidth => Width;
        public uint OriginalHeight => Height;
        public Guid OutputFormat => MediaFormats.NV12;
        public uint OutputSize => Width * Height * 3 / 2;

        /// <param name="deviceId">The camera's id, as <see cref="MediaDevices.GetCameras"/> gives it; null for the first.</param>
        public GStreamerCameraCapture(string deviceId = null)
        {
            _deviceId = deviceId;
        }

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipeline != null)
                return;

            IntPtr source = GstDevices.CreateElement("Video/Source", _deviceId, out IntPtr caps)
                ?? throw new InvalidOperationException(_deviceId == null ? "There is no camera" : $"There is no camera {_deviceId}");
            string format = caps == IntPtr.Zero ? null : BestFormat(caps);
            if (caps != IntPtr.Zero)
                Gst.gst_mini_object_unref(caps);

            // raw pictures straight to the converter, JPEG through its decoder: decodebin cannot typefind a live raw stream,
            // PipeWire's above all, so it is only for a camera whose formats are not known
            string decode = format == null ? "decodebin" :
                format.StartsWith("image/jpeg", StringComparison.Ordinal) ? $"capsfilter caps=\"{format}\" ! jpegdec" :
                $"capsfilter caps=\"{format}\"";
            _pipeline = new GstAppPipeline(source,
                $"{decode} ! videoconvert ! video/x-raw,format=NV12 ! appsink name=sink drop=true max-buffers=2 sync=false",
                IntPtr.Zero);
            _pipeline.Play();

            // the first frame tells the size the camera gives
            _first = _pipeline.Pull(TimeSpan.FromSeconds(10));
            if (_first == IntPtr.Zero)
                throw new InvalidOperationException("The camera gave no frame");
            var (width, height) = GstFrames.Size(_first);
            Width = (uint)width;
            Height = (uint)height;
        }

        /// <summary>The widest of the camera's raw formats, of those the tallest; of JPEG where no raw one is as wide.</summary>
        private static string BestFormat(IntPtr caps)
        {
            string best = null;
            long bestKey = -1;
            for (uint i = 0; i < Gst.CapsSize(caps); i++)
            {
                var (name, width, height) = Gst.CapsStructure(caps, i);
                if (width == null || height == null || (name != "video/x-raw" && name != "image/jpeg"))
                    continue;
                // raw before JPEG, of a size alike
                long key = ((long)width.Value << 32) | ((long)height.Value << 1) | (name == "video/x-raw" ? 1L : 0L);
                if (key > bestKey)
                {
                    bestKey = key;
                    best = string.Create(CultureInfo.InvariantCulture, $"{name},width={width.Value},height={height.Value}");
                }
            }
            return best;
        }

        /// <summary>
        /// The next frame, NV12, of <see cref="Width"/> by <see cref="Height"/>, and its time, from the first frame's: waiting
        /// for one up to <see cref="ReadTimeoutInMilliseconds"/>. Frames not read in time are let go of, the newest kept.
        /// </summary>
        public bool ReadSample(byte[] sampleBytes, out long timestamp)
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
                return GstFrames.CopyNV12(sample, sampleBytes, Width, Height, out _, out _);
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
        }
    }
}
