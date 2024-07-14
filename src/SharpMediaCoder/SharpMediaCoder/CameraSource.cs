using SharpMediaFoundation.Input;
using System.Buffers;
using Windows.Win32;

namespace SharpMediaFoundation.WPF
{
    public class CameraSource : IVideoSource
    {
        private MFDeviceSource _device;
        private bool _disposedValue;

        private byte[] _buffer;

        public VideoInfo Info { get; private set; }

        public CameraSource()
        { }

        public async Task InitializeAsync()
        {
            Info = await OpenAsync();
        }

        public Task<byte[]> GetSampleAsync()
        {
            if (_device.ReadSample(_buffer))
            {
                var sampleBytes = ArrayPool<byte>.Shared.Rent(_buffer.Length);
                BitmapUtils.CopyBitmap(_buffer, (int)Info.Width, (int)Info.Height, sampleBytes, (int)Info.Width, (int)Info.Height, true);
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
                _device = new MFDeviceSource(PInvoke.MFVideoFormat_RGB24); // PInvoke.MFVideoFormat_YUY2
                _device.Initialize();
                _buffer = new byte[_device.OutputSize];
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
                if (disposing)
                {
                }

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
