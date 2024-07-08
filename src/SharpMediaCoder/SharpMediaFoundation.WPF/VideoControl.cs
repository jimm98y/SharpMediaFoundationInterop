using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SharpMediaFoundation.WPF
{
    public struct VideoInfo
    {
        public uint Width { get; set; }
        public uint OriginalWidth { get; set; }
        public uint Height { get; set; }
        public uint OriginalHeight { get; set; }
        public uint FpsNom { get; set; }
        public uint FpsDenom { get; set; }
    }

    [TemplatePart(Name = "PART_image", Type = typeof(Image))]
    public class VideoControl : Control
    {
        //public string Path
        //{
        //    get { return (string)GetValue(PathProperty); }
        //    set { SetValue(PathProperty, value); }
        //}

        //public static readonly DependencyProperty PathProperty =
        //    DependencyProperty.Register("Path", typeof(string), typeof(VideoControl), new PropertyMetadata("", OnPathPropertyChanged));

        //private static async void OnPathPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        //{
        //    var sender = d as VideoControl;
        //    if(sender != null)
        //    {
        //        await sender.StartPlaying((string)e.NewValue);
        //    }
        //}

        public VideoControlSource Source { get; set; } = new VideoControlSource("frag_bunny.mp4");

        private Image _image;
        private WriteableBitmap _wb;
        private System.Timers.Timer _timerDecoder;

        private ConcurrentQueue<byte[]> _renderQueue = new ConcurrentQueue<byte[]>();
        private SemaphoreSlim _semaphore = new SemaphoreSlim(1);
        private Int32Rect _rect;
        private long _lastTime = 0;
        private Stopwatch _stopwatch = new Stopwatch();

        static VideoControl()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(VideoControl), new FrameworkPropertyMetadata(typeof(VideoControl)));
        }

        public VideoControl()
        {
            Loaded += VideoControl_Loaded;
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }

        private async void VideoControl_Loaded(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.Closing += (s1, e1) =>
                {
                    CompositionTarget.Rendering -= CompositionTarget_Rendering;
                };
            }

            await Source.InitializeAsync();
            StartPlaying();
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            this._image = this.Template.FindName("PART_Image", this) as Image;
            this._image.RenderTransformOrigin = new Point(0.5, 0.5);

            // decoded video image is upside down (pixel rows are in the bitmap order) => flip it
            this._image.RenderTransform = new ScaleTransform(1, -1);
        }

        private void CompositionTarget_Rendering(object sender, EventArgs e)
        {
            if (_timerDecoder == null  || !_timerDecoder.Enabled)
                return;

            long elapsed = _stopwatch.ElapsedMilliseconds;

            if (elapsed - _lastTime >= _timerDecoder.Interval)
            {
                if (_renderQueue.TryDequeue(out byte[] decoded))
                {
                    _wb.Lock();
                    Marshal.Copy(decoded, (int)(3 * (Source.Info.Height - Source.Info.OriginalHeight) * Source.Info.OriginalWidth), _wb.BackBuffer, (int)(Source.Info.OriginalWidth * Source.Info.OriginalHeight * 3));
                    _wb.AddDirtyRect(_rect);
                    _wb.Unlock();

                    ArrayPool<byte>.Shared.Return(decoded);
                    _lastTime = elapsed;
                }
            }
        }

        private async void OnTickDecoder(object sender, ElapsedEventArgs e)
        {
            await _semaphore.WaitAsync();

            try
            {
                while (_renderQueue.Count <= 3) // taking just 1 frame seems to leak native memory, TODO: investigate
                {
                    byte[] sample = await Source.GetSampleAsync();
                    if (sample != null)
                        _renderQueue.Enqueue(sample);
                    else
                        break;
                }
            }
            finally
            {
                _semaphore.Release();
            }            
        }

        private void StartPlaying()
        {
            try
            {
                _timerDecoder?.Stop();

                if (_timerDecoder == null)
                {
                    _wb = new WriteableBitmap(
                        (int)Source.Info.OriginalWidth,
                        (int)Source.Info.OriginalHeight,
                        96,
                        96,
                        PixelFormats.Bgr24,
                        null);
                    this._image.Source = _wb;
                    _rect = new Int32Rect(0, 0, (int)Source.Info.OriginalWidth, (int)Source.Info.OriginalHeight);
                    _timerDecoder = new System.Timers.Timer();
                    _timerDecoder.Elapsed += OnTickDecoder;
                    _timerDecoder.Interval = 1000 * Source.Info.FpsDenom / Source.Info.FpsNom;
                    _timerDecoder.Start();
                    _stopwatch.Start();
                }
                else
                {
                    _timerDecoder.Start();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }
    }
}
