using SharpMediaFoundation.Input;
using SharpMediaFoundation.Utils;
using System.Buffers;
using System.Windows.Media;

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
                BitmapUtils.CopyBitmap(
                    _rgbaBuffer,
                    (int)Info.Width,
                    (int)Info.Height,
                    sampleBytes,
                    (int)Info.Width,
                    (int)Info.Height,
                    4,
                    true);
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
            return Task.FromResult(videoInfo);
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
