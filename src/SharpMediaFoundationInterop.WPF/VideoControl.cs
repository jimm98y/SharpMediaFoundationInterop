using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// A video control that draws each frame into a bitmap: copied in on the UI thread, then uploaded and converted for the
    /// GPU by WPF's render thread. It takes frames of any source as they are; see <see cref="VideoControlD3D"/> for the
    /// way through Direct3D.
    /// </summary>
    public class VideoControl : VideoControlBase
    {
        private volatile WriteableBitmap _canvas;
        private Int32Rect _videoRect;

        protected override bool HasSurface => _canvas != null;

        protected override bool CreateSurface(Image image, VideoInfo videoInfo)
        {
            var canvas = new WriteableBitmap(
                (int)videoInfo.OriginalWidth,
                (int)videoInfo.OriginalHeight,
                96,
                96,
                videoInfo.PixelFormat == PixelFormat.BGRA32 ? PixelFormats.Bgra32 : PixelFormats.Bgr24,
                null);
            image.Source = canvas;
            _videoRect = new Int32Rect(0, 0, (int)videoInfo.OriginalWidth, (int)videoInfo.OriginalHeight);
            _canvas = canvas;
            return true;
        }

        protected override void ReleaseSurface()
        {
            _canvas = null;
        }

        protected override void Present(byte[] frame, VideoInfo videoInfo)
        {
            var canvas = _canvas;
            if (canvas == null)
                return;

            canvas.Lock();

            // TODO: bitmap stride?
            Marshal.Copy(
                frame,
                0,
                canvas.BackBuffer,
                (int)(videoInfo.OriginalWidth * videoInfo.OriginalHeight * (videoInfo.PixelFormat == PixelFormat.BGRA32 ? 4 : 3))
            );

            canvas.AddDirtyRect(_videoRect);
            canvas.Unlock();
        }
    }
}
