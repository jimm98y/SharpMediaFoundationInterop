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
        private IVideoControlSource _source = null;

        public IVideoControlSource Source
        {
            get { return (IVideoControlSource)GetValue(SourceProperty); }
            set { SetValue(SourceProperty, value); }
        }

        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register("Source", typeof(IVideoControlSource), typeof(VideoControl), new PropertyMetadata(null, OnSourceChanged));

        private static async void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var sender = (VideoControl)d;
            var source = e.NewValue as IVideoControlSource;
            sender._source = source;
            if(source != null)
            {
                await source.InitializeAsync();
                sender.StartPlaying();
            }   
        }

        private Image _image;
        private WriteableBitmap _wb;
        private System.Timers.Timer _timerDecoder;

        private ConcurrentQueue<byte[]> _renderQueue = new ConcurrentQueue<byte[]>();
        private SemaphoreSlim _semaphore = new SemaphoreSlim(1);
        private Int32Rect _rect;
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

            // decoded video image is upside down (pixel rows are in the bitmap order) => flip it
            //this._image.RenderTransform = new ScaleTransform(1, -1);
            //this._image.RenderTransformOrigin = new Point(0.5, 0.5);
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
                    BitmapUtils.CopyBitmap(
                        decoded, 
                        _wb.BackBuffer, 
                        (int)_source.Info.OriginalWidth, 
                        (int)_source.Info.OriginalHeight, 
                        (int)_source.Info.Width,
                        (int)_source.Info.Height,
                        true);
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
                _timerDecoder?.Stop();

                if (_source != null)
                {
                    var info = _source.Info;
                    _wb = new WriteableBitmap(
                        (int)info.OriginalWidth,
                        (int)info.OriginalHeight,
                        96,
                        96,
                        PixelFormats.Bgr24,
                        null);
                    this._image.Source = _wb;
                    _rect = new Int32Rect(0, 0, (int)info.OriginalWidth, (int)info.OriginalHeight);
                    _timerDecoder = new System.Timers.Timer();
                    _timerDecoder.Elapsed += OnTickDecoder;
                    _timerDecoder.Interval = 1000 * info.FpsDenom / info.FpsNom;
                    _timerDecoder.Start();
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
