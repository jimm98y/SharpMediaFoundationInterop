using SharpMediaFoundationInterop.Devices;
using SharpMediaFoundationInterop.Transforms;
using SharpMediaFoundationInterop.Transforms.Colors;
using SharpMediaFoundationInterop.Utils;
using System.Buffers;
using Windows.Win32;

namespace SharpMediaFoundationInterop.WPF
{
    public class CameraSource : IVideoSource
    {
        private IMediaVideoSource _device;
        private bool _disposedValue;

        private byte[] _yuy2Buffer;
        protected byte[] _rgbBuffer;
        private int _bytesPerPixel;
        private int _imageBufferLen;

        public byte[] Empty { get; private set; } = new byte[0];

        private ColorConverter _converter;

        public VideoInfo VideoInfo { get; private set; }

        public async Task InitializeAsync()
        {
            VideoInfo = await OpenAsync();
        }

        public byte[] GetVideoSample(out long timestamp)
        {
            // the capture time the device gives the frame, in 100 ns units
            if (_device.ReadSample(_yuy2Buffer, out timestamp))
            {
                if (_converter.ProcessInput(_yuy2Buffer, timestamp))
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

                        return decoded;
                    }
                }
            }

            return Empty; // indicates whether the stream has ended
        }

        private Task<VideoInfo> OpenAsync()
        {
            if (_device == null)
            {
                _device = MediaDevices.CreateCameraCapture(MediaDevices.GetCameras().First());
                _device.Initialize();
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
            // not known: each frame carries the time it was captured at, which is what it is shown by
            videoInfo.FpsNom = 0;
            videoInfo.FpsDenom = 0;
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
