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
    /// way through Direct3D. Of a stereo video it shows both eyes as the frame has them, or one eye's picture alone - see
    /// <see cref="VideoControlBase.EyeView"/>; a spherical video it shows flat, as <see cref="VideoControlD3D"/> does not.
    /// </summary>
    public class VideoControl : VideoControlBase
    {
        private volatile WriteableBitmap _canvas;
        private Image _image;

        // what of the frame the bitmap is of: the whole picture, or one eye's
        private Int32Rect _sourceRect;

        protected override bool HasSurface => _canvas != null;

        protected override bool CreateSurface(Image image, VideoInfo videoInfo)
        {
            _image = image;
            CreateCanvas(videoInfo, new Int32Rect(0, 0, (int)videoInfo.OriginalWidth, (int)videoInfo.OriginalHeight));
            return true;
        }

        private void CreateCanvas(VideoInfo videoInfo, Int32Rect sourceRect)
        {
            var canvas = new WriteableBitmap(
                sourceRect.Width,
                sourceRect.Height,
                96,
                96,
                videoInfo.PixelFormat == PixelFormat.BGRA32 ? PixelFormats.Bgra32 : PixelFormats.Bgr24,
                null);
            _image.Source = canvas;
            _sourceRect = sourceRect;
            _canvas = canvas;
        }

        protected override void ReleaseSurface()
        {
            _canvas = null;
        }

        protected override void Present(object frame, VideoInfo videoInfo)
        {
            if (_canvas == null || frame is not byte[] bytes)
                return;

            // the whole picture, or one eye's: the bitmap made again of that size, where it was of the other
            var view = GetView(videoInfo);
            var rect = new Int32Rect(0, 0, (int)videoInfo.OriginalWidth, (int)videoInfo.OriginalHeight);
            if (view.IsOneEye)
            {
                var (x, y, width, height) = view.EyeRect(videoInfo, view.Eye == EyeView.Right);
                rect = new Int32Rect((int)x, (int)y, (int)width, (int)height);
            }
            if (!rect.Equals(_sourceRect))
                CreateCanvas(videoInfo, rect);

            var canvas = _canvas;
            int bytesPerPixel = videoInfo.PixelFormat == PixelFormat.BGRA32 ? 4 : 3;
            int frameStride = (int)videoInfo.OriginalWidth * bytesPerPixel;
            int rowBytes = rect.Width * bytesPerPixel;

            canvas.Lock();
            if (rowBytes == frameStride && canvas.BackBufferStride == frameStride)
            {
                // the whole width: the rows at once
                Marshal.Copy(bytes, rect.Y * frameStride, canvas.BackBuffer, frameStride * rect.Height);
            }
            else
            {
                for (int row = 0; row < rect.Height; row++)
                {
                    Marshal.Copy(bytes, (rect.Y + row) * frameStride + rect.X * bytesPerPixel,
                        canvas.BackBuffer + row * canvas.BackBufferStride, rowBytes);
                }
            }
            canvas.AddDirtyRect(new Int32Rect(0, 0, rect.Width, rect.Height));
            canvas.Unlock();
        }
    }
}
