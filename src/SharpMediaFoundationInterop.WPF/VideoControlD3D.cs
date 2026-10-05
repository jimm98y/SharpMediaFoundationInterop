using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// A video control that draws each frame with Direct3D. The source is given the control's device to decode on: where
    /// the GPU decodes the format, frames never leave it - each is converted to BGRA by the GPU's video processor from where
    /// it was decoded. Where it does not, frames come in NV12, as the CPU decoded them, and are uploaded to be converted the
    /// same way; a screen's BGRA goes up as it is. The BGRA texture is shared with Direct3D 9, which WPF composes through a
    /// D3DImage: no frame is converted or copied into a bitmap on the CPU, and WPF's render thread uploads none.
    /// </summary>
    public class VideoControlD3D : VideoControlBase
    {
        private volatile bool _hasSurface;
        private Direct3DDevice _device;
        private D3DImage _d3dImage;
        private D3D11FrameRenderer _renderer;
        private D3D9SharedSurface _surface;
        private Int32Rect _dirty;

        protected override bool HasSurface => _hasSurface;

        /// <summary>
        /// Frames decoded on this control's device where the GPU can, as the decoder made them where it cannot: the GPU
        /// converts them either way. On the video's thread, before the source is initialized.
        /// </summary>
        protected override void OnSourceInitializing(IVideoSource source)
        {
            _device ??= new Direct3DDevice();
            source.TrySetOutputFormat(PixelFormat.NV12);
            source.TryUseDirect3D(_device);
        }

        protected override bool CreateSurface(Image image, VideoInfo videoInfo)
        {
            ReleaseResources();

            _device ??= new Direct3DDevice();
            _renderer = new D3D11FrameRenderer(_device, videoInfo);
            _surface = new D3D9SharedSurface(_renderer.SharedHandle, _renderer.Width, _renderer.Height);
            _dirty = new Int32Rect(0, 0, (int)_renderer.Width, (int)_renderer.Height);

            _d3dImage = new D3DImage();
            _d3dImage.IsFrontBufferAvailableChanged += D3DImage_IsFrontBufferAvailableChanged;
            SetBackBuffer();
            image.Source = _d3dImage;

            _hasSurface = true;
            return true;
        }

        private void SetBackBuffer()
        {
            _d3dImage.Lock();
            _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface.Surface);
            _d3dImage.Unlock();
        }

        /// <summary>
        /// WPF lets go of the surface as its device is lost - the screen locked, the display changed - and says when it can
        /// have it again: it is given it again.
        /// </summary>
        private void D3DImage_IsFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_d3dImage.IsFrontBufferAvailable && _surface != null)
                SetBackBuffer();
        }

        protected override void ReleaseSurface()
        {
            _hasSurface = false;
            Dispatcher.BeginInvoke(new Action(ReleaseResources));
        }

        protected override void Present(object frame, VideoInfo videoInfo)
        {
            var d3dImage = _d3dImage;
            if (d3dImage == null || _renderer == null || !d3dImage.IsFrontBufferAvailable)
                return;

            d3dImage.Lock();
            try
            {
                _renderer.Draw(frame);
                d3dImage.AddDirtyRect(_dirty);
            }
            finally
            {
                d3dImage.Unlock();
            }
        }

        private void ReleaseResources()
        {
            if (_d3dImage != null)
            {
                _d3dImage.IsFrontBufferAvailableChanged -= D3DImage_IsFrontBufferAvailableChanged;
                _d3dImage.Lock();
                _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
                _d3dImage.Unlock();
                _d3dImage = null;
            }
            _surface?.Dispose();
            _surface = null;
            _renderer?.Dispose();
            _renderer = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hasSurface = false;
                Action release = () =>
                {
                    ReleaseResources();
                    _device?.Dispose();
                    _device = null;
                };
                if (Dispatcher.CheckAccess())
                    release();
                else
                    Dispatcher.BeginInvoke(release);
            }
            base.Dispose(disposing);
        }
    }
}
