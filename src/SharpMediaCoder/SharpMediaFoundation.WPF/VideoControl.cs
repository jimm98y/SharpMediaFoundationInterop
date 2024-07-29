using SharpMediaFoundation.Wave;
using System;
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
    [TemplatePart(Name = "PART_image", Type = typeof(Image))]
    public class VideoControl : Control, IDisposable
    {
        private WaveOut _waveOut;

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
        private Stopwatch _stopwatch = new Stopwatch();

        private ConcurrentQueue<byte[]> _videoRenderQueue = new ConcurrentQueue<byte[]>();
        private long _videoLastTime = 0;
        private bool _disposedValue;

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

            if (elapsed - _videoLastTime >= _timer.Interval)
            {
                if (_videoRenderQueue.TryDequeue(out byte[] decoded))
                {
                    _canvas.Lock();

                    // TODO: bitmap stride?
                    Marshal.Copy(
                        decoded, 
                        0,
                        _canvas.BackBuffer,
                        (int)(_source.VideoInfo.OriginalWidth * _source.VideoInfo.OriginalHeight * (_source.VideoInfo.PixelFormat == PixelFormat.BGRA32 ? 4 : 3))
                    );

                    _canvas.AddDirtyRect(_croppingRect);
                    _canvas.Unlock();

                    _source.ReturnVideoFrame(decoded);
                    _videoLastTime = elapsed;
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
                    await nextSource.InitializeVideoAsync();
                    if (nextSource is IAudioSource audioSource)
                    {
                        await audioSource.InitializeAudioAsync();
                    }

                    _source = nextSource;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        var videoInfo = nextSource.VideoInfo;
                        _canvas = new WriteableBitmap(
                            (int)videoInfo.OriginalWidth,
                            (int)videoInfo.OriginalHeight,
                            96,
                            96,
                            videoInfo.PixelFormat == PixelFormat.BGRA32 ? PixelFormats.Bgra32 : PixelFormats.Bgr24,
                            null);
                        this._image.Source = _canvas;
                        _croppingRect = new Int32Rect(0, 0, (int)videoInfo.OriginalWidth, (int)videoInfo.OriginalHeight);
                        _timer.Interval = 1000 * videoInfo.FpsDenom / videoInfo.FpsNom;
                    });

                    if (nextSource is IAudioSource nextAudioSource)
                    {
                        var audioInfo = nextAudioSource.AudioInfo;
                        if (audioInfo != null)
                        {
                            _waveOut = new WaveOut();
                            _waveOut.Initialize(audioInfo.SampleRate, audioInfo.Channels, audioInfo.BitsPerSample);
                        }
                    }
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
                if (_waveOut != null)
                {
                    while (_waveOut.QueuedFrames <= 20)
                    {
                        byte[] sample = await ((IAudioSource)_source).GetAudioSampleAsync();
                        if (sample != null)
                        {
                            _waveOut.Enqueue(sample, (uint)sample.Length);
                            ((IAudioSource)_source).ReturnAudioFrame(sample);
                        }
                        else
                            break;
                    }
                }

                while (_videoRenderQueue.Count <= 2) // taking just 1 frame seems to leak native memory, TODO: investigate
                {
                    byte[] sample = await _source.GetVideoSampleAsync();
                    if (sample != null)
                        _videoRenderQueue.Enqueue(sample);
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

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                }

                if(_waveOut != null)
                {
                    _waveOut.Dispose();
                    _waveOut = null;
                }

                _disposedValue = true;
            }
        }
        ~VideoControl()
        {
            Dispose(disposing: false);
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
