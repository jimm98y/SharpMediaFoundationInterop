using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// A video control that draws each frame with Direct3D: uploaded to a texture as the source gives it - NV12, as decoded,
    /// where the source can - converted to BGRA by the GPU's video processor into a texture shared with Direct3D 9, which
    /// WPF composes through a D3DImage. No frame is converted or copied into a bitmap on the CPU, and WPF's render thread
    /// uploads none.
    /// </summary>
    public class VideoControlD3D : VideoControlBase
    {
        private volatile bool _hasSurface;
        private D3DImage _d3dImage;
        private D3D11FrameRenderer _renderer;
        private D3D9SharedSurface _surface;
        private Int32Rect _dirty;

        protected override bool HasSurface => _hasSurface;

        /// <summary>Decoded frames as the decoder makes them: the GPU converts them.</summary>
        protected override void OnSourceInitializing(IVideoSource source)
        {
            source.TrySetOutputFormat(PixelFormat.NV12);
        }

        protected override bool CreateSurface(Image image, VideoInfo videoInfo)
        {
            ReleaseResources();

            _renderer = new D3D11FrameRenderer(videoInfo);
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

        protected override void Present(byte[] frame, VideoInfo videoInfo)
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
                if (Dispatcher.CheckAccess())
                    ReleaseResources();
                else
                    Dispatcher.BeginInvoke(new Action(ReleaseResources));
            }
            base.Dispose(disposing);
        }
    }
}
