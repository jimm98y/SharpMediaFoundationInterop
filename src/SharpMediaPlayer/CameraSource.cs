using SharpMediaFoundation.Input;
using SharpMediaFoundation.Transforms.Colors;
using SharpMediaFoundation.Utils;
using System.Buffers;
using Windows.Win32;

namespace SharpMediaFoundation.WPF
{
    public class CameraSource : IVideoSource
    {
        private DeviceCapture _device;
        private bool _disposedValue;

        private byte[] _yuy2Buffer;
        protected byte[] _rgbBuffer;
        private int _bytesPerPixel;
        private int _imageBufferLen;

        private ColorConverter _converter;

        public VideoInfo VideoInfo { get; private set; }

        public async Task InitializeAsync()
        {
            VideoInfo = await OpenAsync();
        }

        public bool GetVideoSample(out byte[] sample)
        {
            if (_device.ReadSample(_yuy2Buffer, out _))
            {
                if (_converter.ProcessInput(_yuy2Buffer, 0))
                {
                    if (_converter.ProcessOutput(ref _rgbBuffer, out _))
                    {
                        var decoded = ArrayPool<byte>.Shared.Rent(_imageBufferLen);

                        BitmapUtils.CopyBitmap(
                            _rgbBuffer,
                            (int)VideoInfo.Width,
                            (int)VideoInfo.Height,
                            decoded,
                            (int)VideoInfo.OriginalWidth,
                            (int)VideoInfo.OriginalHeight,
                            _bytesPerPixel,
                            true);

                        sample = decoded;
                        return true;
                    }
                }
            }

            sample = null;
            return false;
        }

        private Task<VideoInfo> OpenAsync()
        {
            if (_device == null)
            {
                var devices = DeviceCapture.Enumerate();
                _device = new DeviceCapture();
                _device.Initialize(devices.First());
                _yuy2Buffer = new byte[_device.OutputSize];

                _converter = new ColorConverter(_device.OutputFormat, PInvoke.MFVideoFormat_RGB24, _device.Width, _device.Height);
                _converter.Initialize();

                _bytesPerPixel = 3;

                _rgbBuffer = new byte[_converter.OutputSize];
                _imageBufferLen = (int)_converter.OutputSize;
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

        public void ReturnVideoSample(byte[] decoded)
        {
            ArrayPool<byte>.Shared.Return(decoded);
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
