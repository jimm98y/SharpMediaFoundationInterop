using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// A video control that draws each frame with Direct3D. The source is given the control's device to decode on: where
    /// the GPU decodes the format, frames never leave it - each is converted to BGRA by the GPU's video processor from where
    /// it was decoded. Where it does not, frames come in NV12, as the CPU decoded them, and are uploaded to be converted the
    /// same way; a screen's BGRA goes up as it is. The BGRA texture is shared with Direct3D 9, which WPF composes through a
    /// D3DImage: no frame is converted or copied into a bitmap on the CPU, and WPF's render thread uploads none.
    /// </summary>
    /// <remarks>
    /// Of a stereo video it shows both eyes as the frame has them, or one eye's picture alone - see
    /// <see cref="MediaControlBase.EyeView"/>. A spherical video - a VR180 or 360 degree camera's - it shows as a view into
    /// the sphere, drawn by the GPU from the frame: looked around by dragging, zoomed with the wheel, of one eye, or of both
    /// side by side.
    /// </remarks>
    public class GpuMediaControl : MediaControlBase
    {
        private volatile bool _hasSurface;
        private Direct3DDevice _device;
        private D3DImage _d3dImage;
        private D3D11FrameRenderer _renderer;
        private D3D9SharedSurface _surface;
        // the screen the surface is of: shown on another, it is made again of that one's adapter
        private IntPtr _surfaceMonitor;
        private Int32Rect _dirty;
        private Image _image;
        private VideoInfo _videoInfo;

        /// <summary>The most pixels a spherical view is drawn at: one bigger on the screen is drawn at this, and stretched.</summary>
        public const int MaxViewPixels = 3840 * 2160;

        protected override bool HasSurface => _hasSurface;

        protected override bool CanShowSpherical => true;

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
            _image = image;
            _videoInfo = videoInfo;
            CreateRenderer(videoInfo, OutputSize(GetView(videoInfo), videoInfo));
            _hasSurface = true;
            return true;
        }

        /// <summary>
        /// The renderer, and the surface WPF shows, of the size what is shown is: the whole frame where it is null, else
        /// one eye's picture, or a spherical view.
        /// </summary>
        private void CreateRenderer(VideoInfo videoInfo, (uint Width, uint Height)? output)
        {
            ReleaseResources();

            _device ??= new Direct3DDevice();
            _renderer = output == null
                ? new D3D11FrameRenderer(_device, videoInfo)
                : new D3D11FrameRenderer(_device, videoInfo, output.Value.Width, output.Value.Height);
            _dirty = new Int32Rect(0, 0, (int)_renderer.Width, (int)_renderer.Height);
            CreateSharedSurface();
        }

        /// <summary>
        /// The surface WPF shows, of the renderer's texture, and the D3DImage of it: of the adapter of the screen the control
        /// is on now - WPF shows a surface of another adapter's as white.
        /// </summary>
        private void CreateSharedSurface()
        {
            ReleaseSharedSurface();
            _surfaceMonitor = CurrentMonitor();
            _surface = new D3D9SharedSurface(_renderer.SharedHandle, _renderer.Width, _renderer.Height, _surfaceMonitor);

            _d3dImage = new D3DImage();
            _d3dImage.IsFrontBufferAvailableChanged += D3DImage_IsFrontBufferAvailableChanged;
            SetBackBuffer();
            _image.Source = _d3dImage;
        }

        /// <summary>The screen the control is on, most of its window: zero where it is in no window yet.</summary>
        private IntPtr CurrentMonitor() =>
            PresentationSource.FromVisual(this) is HwndSource source ? MonitorFromWindow(source.Handle, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;

        /// <summary>Of a control moved to a screen of another adapter, its surface made again of that one's.</summary>
        private void FollowMonitor()
        {
            var monitor = CurrentMonitor();
            if (monitor != IntPtr.Zero && monitor != _surfaceMonitor && _renderer != null)
                CreateSharedSurface();
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        /// <summary>
        /// The size of what is shown: null for the whole frame; one eye's picture at its own size; a spherical view at the
        /// control's size on the screen, in pixels, no more than <see cref="MaxViewPixels"/>.
        /// </summary>
        private (uint Width, uint Height)? OutputSize(in VideoView view, VideoInfo videoInfo)
        {
            if (view.IsWholeFrame)
                return null;
            if (view.Projection == VideoProjection.Flat)
            {
                var (_, _, width, height) = view.EyeRect(videoInfo, view.Eye == EyeView.Right);
                return (width, height);
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            double w = Math.Max(2, ActualWidth * dpi.DpiScaleX), h = Math.Max(2, ActualHeight * dpi.DpiScaleY);
            double scale = Math.Min(1, Math.Sqrt(MaxViewPixels / (w * h)));
            return ((uint)(w * scale) & ~1u, (uint)(h * scale) & ~1u);
        }

        /// <summary>Whether the renderer draws what is shown at the size it is now.</summary>
        private bool RendererFits((uint Width, uint Height)? output)
        {
            if (_renderer == null)
                return false;
            if (output == null)
                return _renderer.IsWholeFrame;
            return !_renderer.IsWholeFrame && _renderer.Width == output.Value.Width && _renderer.Height == output.Value.Height;
        }

        /// <summary>
        /// The surface given to the D3DImage, with software fallback: where WPF has no device of its own to show it by -
        /// rendering in software, as over Remote Desktop or of <see cref="System.Windows.Media.RenderOptions.ProcessRenderMode"/>,
        /// or its device lost - WPF reads each frame back from it instead, and the control is drawn into as ever. Without
        /// it, WPF would show nothing new there.
        /// </summary>
        private void SetBackBuffer()
        {
            _d3dImage.Lock();
            _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface.Surface, true);
            _d3dImage.Unlock();
        }

        /// <summary>
        /// WPF says when its device is lost - the screen locked, the display changed - and when it has one again: the surface
        /// is given it again.
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
            if (_renderer == null || _image == null)
                return;

            // made again where what is shown is of another size: an eye chosen, the control resized
            _videoInfo = videoInfo;
            var view = GetView(videoInfo);
            var output = OutputSize(view, videoInfo);
            if (!RendererFits(output))
                CreateRenderer(videoInfo, output);
            else
                FollowMonitor();

            var d3dImage = _d3dImage;
            if (d3dImage == null)
                return;

            d3dImage.Lock();
            try
            {
                if (_renderer.IsWholeFrame)
                    _renderer.Draw(frame);
                else
                    _renderer.Draw(frame, view);
                d3dImage.AddDirtyRect(_dirty);
            }
            finally
            {
                d3dImage.Unlock();
            }
        }

        /// <summary>
        /// The frame shown last, drawn again of the view as it is now, from the converted frame the renderer keeps: what turns
        /// a paused spherical view. Not where what is shown is now of another size, which takes the frame itself.
        /// </summary>
        protected override bool RedrawView()
        {
            if (_renderer != null)
                FollowMonitor();
            var d3dImage = _d3dImage;
            if (_renderer == null || _videoInfo == null || d3dImage == null)
                return false;
            var view = GetView(_videoInfo);
            if (_renderer.IsWholeFrame || !RendererFits(OutputSize(view, _videoInfo)))
                return false;

            d3dImage.Lock();
            try
            {
                _renderer.Redraw(view);
                d3dImage.AddDirtyRect(_dirty);
            }
            finally
            {
                d3dImage.Unlock();
            }
            return true;
        }

        private void ReleaseResources()
        {
            ReleaseSharedSurface();
            _renderer?.Dispose();
            _renderer = null;
        }

        private void ReleaseSharedSurface()
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
            _surfaceMonitor = IntPtr.Zero;
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
