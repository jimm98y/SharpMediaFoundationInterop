using SharpMediaFoundationInterop.Devices;
using SharpMediaFoundationInterop.Transforms;
using System.Buffers;
using System.Windows.Media;

// Together with the app.manifest where we disable dpiAware for the WPF application,
//  this is necessary for the SetProcessDpiAwarenessContext API to work. This API must be called before DuplicateOutput1...
[assembly: DisableDpiAwareness]

namespace SharpMediaFoundationInterop.WPF
{
    public class ScreenSource : IVideoSource
    {
        private IMediaVideoSource _device;
        private bool _disposedValue;


        public byte[] Empty { get; private set; } = new byte[0];

        public VideoInfo VideoInfo { get; private set; }

        public async Task InitializeAsync()
        {
            VideoInfo = await OpenAsync();
        }

        public byte[] GetVideoSample(out long timestamp)
        {
            // the frame top-down, as the bitmap it is shown in is, straight into the array handed out; and when the screen
            // was captured, in 100 ns units
            var frame = ArrayPool<byte>.Shared.Rent((int)_device.OutputSize);
            if (_device.ReadSample(frame, out timestamp))
                return frame;

            ArrayPool<byte>.Shared.Return(frame);
            return Empty; // the screen has not changed
        }

        private Task<VideoInfo> OpenAsync()
        {
            if (_device == null)
            {
                _device = MediaDevices.CreateScreenCapture(MediaDevices.GetScreens().First(), topDown: true);
                _device.Initialize();
            }
        
            var videoInfo = new VideoInfo();
            videoInfo.Width = _device.Width;
            videoInfo.Height = _device.Height;
            videoInfo.OriginalWidth = _device.Width;
            videoInfo.OriginalHeight = _device.Height;
            // not known: each frame carries the time it was captured at, which is what it is shown by
            videoInfo.FpsNom = 0;
            videoInfo.FpsDenom = 0;
            videoInfo.PixelFormat = PixelFormat.BGRA32;
            return Task.FromResult(videoInfo);
        }

        public void ReturnVideoSample(byte[] decoded)
        {
            ArrayPool<byte>.Shared.Return(decoded);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if(_device != null)
                {
                    _device.Dispose();
                }

                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
