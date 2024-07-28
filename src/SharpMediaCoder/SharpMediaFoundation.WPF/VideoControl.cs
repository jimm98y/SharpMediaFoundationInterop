using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpMediaFoundation.Utils;

namespace SharpMediaFoundation.WPF
{
    [TemplatePart(Name = "PART_image", Type = typeof(Image))]
    public class VideoControl : Control
    {
        private IVideoSource _nextSource = null;
        private IVideoSource _source = null;

        public IVideoSource Source
        {
            get { return (IVideoSource)GetValue(SourceProperty); }
            set { SetValue(SourceProperty, value); }
        }

        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register("Source", typeof(IVideoSource), typeof(VideoControl), new PropertyMetadata(null, OnSourceChanged));

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var sender = (VideoControl)d;
            var source = e.NewValue as IVideoSource;
            sender._nextSource = source;
            if(source != null)
            {
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
            _timer = new System.Timers.Timer();
            _timer.Elapsed += OnTickDecoder;
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

                    int bytesPerPixel = 3;
                    if (_source.Info.PixelFormat == PixelFormat.BGRA32)
                    {
                        bytesPerPixel = 4;
                    }

                    // decoded video image is upside down (pixel rows are in the bitmap order) => flip it
                    BitmapUtils.CopyBitmap(
                        decoded, 
                        (int)_source.Info.Width,
                        (int)_source.Info.Height,
                        _canvas.BackBuffer, 
                        (int)_source.Info.OriginalWidth, 
                        (int)_source.Info.OriginalHeight,
                        bytesPerPixel,
                        true);

                    _canvas.AddDirtyRect(_croppingRect);
                    _canvas.Unlock();

                    _source.Return(decoded);
                    _lastTime = elapsed;
                }
            }
        }       

        private async void OnTickDecoder(object sender, ElapsedEventArgs e)
        {
            await _semaphore.WaitAsync();

            // Initialize
            var nextSource = _nextSource;
            var currentSource = _source;
            if(nextSource != currentSource)
            {
                if (nextSource != null)
                {
                    // calling initialize from the same thread as GetSampleAsync
                    await nextSource.InitializeAsync();
                    _source = nextSource;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        var info = nextSource.Info;
                        _canvas = new WriteableBitmap(
                            (int)info.OriginalWidth,
                            (int)info.OriginalHeight,
                            96,
                            96,
                            info.PixelFormat == PixelFormat.BGRA32 ? PixelFormats.Bgra32 : PixelFormats.Bgr24,
                            null);
                        this._image.Source = _canvas;
                        _croppingRect = new Int32Rect(0, 0, (int)info.OriginalWidth, (int)info.OriginalHeight);
                        _timer.Interval = 1000 * info.FpsDenom / info.FpsNom;
                    });
                }
                else
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        this._image.Source = null;
                    });

                    _source = nextSource;
                    StopPlaying();
                }
            }

            // Get Sample
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
            _timer.Start();
            _stopwatch.Restart();
        }

        private void StopPlaying()
        {
            _timer.Stop();
            _stopwatch.Stop();
        }
    }
}
