using SharpMediaFoundation.Input;
using System.Buffers;
using System.Windows.Media;

// Together with the app.manifest where we disable dpiAware for the WPF application,
//  this is necessary for the SetProcessDpiAwarenessContext API to work. This API must be called before DuplicateOutput1...
[assembly: DisableDpiAwareness]

namespace SharpMediaFoundation.WPF
{
    public class ScreenSource : IVideoSource
    {
        private ScreenCapture _device;
        private bool _disposedValue;

        private byte[] _rgbaBuffer;

        public VideoInfo Info { get; private set; }

        public ScreenSource()
        { }

        public async Task InitializeAsync()
        {
            Info = await OpenAsync();
        }

        public Task<byte[]> GetSampleAsync()
        {
            if (_device.ReadSample(_rgbaBuffer, out _))
            {
               var sampleBytes = ArrayPool<byte>.Shared.Rent((int)_device.OutputSize);
               Buffer.BlockCopy(
                    _rgbaBuffer,
                    0,
                    sampleBytes,
                    0,
                    (int)Info.Width * (int)Info.Height * 4);
                return Task.FromResult(sampleBytes);
            }
            else
            {
                return Task.FromResult<byte[]>(null);
            }
        }

        private Task<VideoInfo> OpenAsync()
        {
            if (_device == null)
            {
                _device = new ScreenCapture();
                _device.Initialize();

                _rgbaBuffer = new byte[_device.OutputSize];
            }
        
            var videoInfo = new VideoInfo();
            videoInfo.Width = _device.Width;
            videoInfo.Height = _device.Height;
            videoInfo.OriginalWidth = _device.Width;
            videoInfo.OriginalHeight = _device.Height;
            videoInfo.FpsNom = 24000;
            videoInfo.FpsDenom = 1001;
            videoInfo.PixelFormat = PixelFormat.BGRA32;
            return Task.FromResult(videoInfo);
        }

        public void Return(byte[] decoded)
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
