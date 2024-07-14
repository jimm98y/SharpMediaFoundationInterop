using SharpMediaFoundation.Input;
using System.Buffers;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation.WPF
{
    public class CameraSource : IVideoSource
    {
        private MFDeviceSource _device;
        private bool _disposedValue;

        private byte[] _buffer = new byte[1280 * 720 * 3];

        public VideoInfo Info { get; private set; }

        public CameraSource()
        {
        }

        public Task InitializeAsync()
        {
            Info = OpenAsync().Result;
            return Task.CompletedTask;
        }

        public async Task<byte[]> GetSampleAsync()
        {
            if(_device == null)
            {
                _device = new MFDeviceSource();
            }

            while(!_device.ReadSample(_buffer))
            {
                await Task.Delay(100);
            }

            var sampleBytes = ArrayPool<byte>.Shared.Rent(1280 * 720 * 3);
            BitmapUtils.CopyBitmap(_buffer, 1280, 720, sampleBytes, 1280, 720, true);

            return sampleBytes;
        }

        private Task<VideoInfo> OpenAsync()
        {
            var videoInfo = new VideoInfo();
            videoInfo.Width = 1280;
            videoInfo.Height = 720; 
            videoInfo.OriginalWidth = 1280;
            videoInfo.OriginalHeight = 720;
            videoInfo.FpsNom = 24000;
            videoInfo.FpsDenom = 1001;
            return Task.FromResult(videoInfo);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
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
