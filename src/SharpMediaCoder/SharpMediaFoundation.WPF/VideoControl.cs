using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SharpMediaFoundation.WPF
{
    [TemplatePart(Name = "PART_image", Type = typeof(Image))]
    public class VideoControl : Control
    {
        private IVideoSource _source = null;

        public IVideoSource Source
        {
            get { return (IVideoSource)GetValue(SourceProperty); }
            set { SetValue(SourceProperty, value); }
        }

        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register("Source", typeof(IVideoSource), typeof(VideoControl), new PropertyMetadata(null, OnSourceChanged));

        private static async void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var sender = (VideoControl)d;
            var source = e.NewValue as IVideoSource;
            sender._source = source;
            if(source != null)
            {
                await source.InitializeAsync();
                sender.StartPlaying();
            }   
        }

        private Image _image;
        private Int32Rect _croppingRect;
        private WriteableBitmap _canvas;
        private System.Timers.Timer _timer;
        private SemaphoreSlim _semaphore = new SemaphoreSlim(1);

        private ConcurrentQueue<byte[]> _renderQueue = new ConcurrentQueue<byte[]>();
        private Stopwatch _stopwatch = new Stopwatch();
        private long _lastTime = 0;

        static VideoControl()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(VideoControl), new FrameworkPropertyMetadata(typeof(VideoControl)));
        }

        public VideoControl()
        {
            Loaded += VideoControl_Loaded;
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }

        private void VideoControl_Loaded(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.Closing += (s1, e1) =>
                {
                    CompositionTarget.Rendering -= CompositionTarget_Rendering;
                };
            }
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            this._image = this.Template.FindName("PART_Image", this) as Image;
        }

        private void CompositionTarget_Rendering(object sender, EventArgs e)
        {
            if (_timer == null  || !_timer.Enabled)
                return;

            long elapsed = _stopwatch.ElapsedMilliseconds;

            if (elapsed - _lastTime >= _timer.Interval)
            {
                if (_renderQueue.TryDequeue(out byte[] decoded))
                {
                    _canvas.Lock();

                    // decoded video image is upside down (pixel rows are in the bitmap order) => flip it
                    BitmapUtils.CopyBitmap(
                        decoded, 
                        (int)_source.Info.Width,
                        (int)_source.Info.Height,
                        _canvas.BackBuffer, 
                        (int)_source.Info.OriginalWidth, 
                        (int)_source.Info.OriginalHeight, 
                        true);

                    _canvas.AddDirtyRect(_croppingRect);
                    _canvas.Unlock();

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
                while (_renderQueue.Count <= 2) // taking just 1 frame seems to leak native memory, TODO: investigate
                {
                    byte[] sample = await _source.GetSampleAsync();
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
                _timer?.Stop();

                if (_source != null)
                {
                    var info = _source.Info;
                    _canvas = new WriteableBitmap(
                        (int)info.OriginalWidth,
                        (int)info.OriginalHeight,
                        96,
                        96,
                        PixelFormats.Bgr24,
                        null);
                    this._image.Source = _canvas;
                    _croppingRect = new Int32Rect(0, 0, (int)info.OriginalWidth, (int)info.OriginalHeight);
                    _timer = new System.Timers.Timer();
                    _timer.Elapsed += OnTickDecoder;
                    _timer.Interval = 1000 * info.FpsDenom / info.FpsNom;
                    _timer.Start();
                    _stopwatch.Start();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }
    }
}
