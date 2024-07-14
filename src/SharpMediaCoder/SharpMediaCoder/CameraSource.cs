using SharpMediaFoundation.Colors;
using SharpMediaFoundation.Input;
using System.Buffers;
using Windows.Win32;

namespace SharpMediaFoundation.WPF
{
    public class CameraSource : IVideoSource
    {
        private MFDeviceSource _device;
        private bool _disposedValue;

        private byte[] _yuy2Buffer;

        private ColorConverter _converter;

        public VideoInfo Info { get; private set; }

        public CameraSource()
        { }

        public async Task InitializeAsync()
        {
            Info = await OpenAsync();
        }

        public Task<byte[]> GetSampleAsync()
        {
            if (_device.ReadSample(_yuy2Buffer, out _))
            {
                _converter.ProcessInput(_yuy2Buffer, 0);
                var sampleBytes = ArrayPool<byte>.Shared.Rent((int)_converter.OutputSize);
                _converter.ProcessOutput(ref sampleBytes, out _);
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
                _device = new MFDeviceSource();
                _device.Initialize();
                _yuy2Buffer = new byte[_device.OutputSize];

                _converter = new ColorConverter(_device.OutputFormat, PInvoke.MFVideoFormat_RGB24, _device.Width, _device.Height);
                _converter.Initialize();
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
