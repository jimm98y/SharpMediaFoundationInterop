﻿using SharpMediaFoundation.Input;
using SharpMediaFoundation.Utils;
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
        private int _bytesPerPixel;

        public VideoInfo Info { get; private set; }

        public async Task InitializeAsync()
        {
            Info = await OpenAsync();
        }

        public Task<byte[]> GetSampleAsync()
        {
            if (_device.ReadSample(_rgbaBuffer, out _))
            {
               var decoded = ArrayPool<byte>.Shared.Rent((int)_device.OutputSize);

                BitmapUtils.CopyBitmap(
                    _rgbaBuffer,
                    (int)Info.Width,
                    (int)Info.Height,
                    decoded,
                    (int)Info.OriginalWidth,
                    (int)Info.OriginalHeight,
                    _bytesPerPixel,
                    true);

                return Task.FromResult(decoded);
            }

            return Task.FromResult<byte[]>(null);
        }

        private Task<VideoInfo> OpenAsync()
        {
            if (_device == null)
            {
                var screens = ScreenCapture.Enumerate();
                _device = new ScreenCapture();
                _device.Initialize(screens.First());

                _bytesPerPixel = 4;

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
