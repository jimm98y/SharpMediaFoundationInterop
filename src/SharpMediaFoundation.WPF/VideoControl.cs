using SharpMediaFoundation.Wave;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
            sender._source = source;
            if(source != null)
            {
                sender._videoRenderThread = Task.Run(sender.VideoRenderThread);
                sender._audioRenderThread = Task.Run(sender.AudioRenderThread);
            }
        }

        private Image _image;
        private Int32Rect _croppingRect;
        private WriteableBitmap _canvas;

        private AutoResetEvent _eventVideo = new AutoResetEvent(true);
        private AutoResetEvent _eventAudio = new AutoResetEvent(true);
        private Stopwatch _stopwatch = new Stopwatch();

        private long _videoFrames = 0;
        private long _audioFrames = 0;

        private Queue<byte[]> _videoFrameQueue = new Queue<byte[]>();

        private bool _disposedValue;

        private Task _videoRenderThread = null;
        private Task _audioRenderThread = null;

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
            long elapsed = 0;
            if (_waveOut != null)
            {
                var audioSource = _source as IAudioSource;
                var audioInfo = audioSource.AudioInfo;
                elapsed = _waveOut.GetPosition() * 10000L / (audioInfo.SampleRate * audioInfo.Channels);
                //Debug.WriteLine($"Audio {elapsed / 10000d}");
            }
            else
            {
                elapsed = _stopwatch.ElapsedMilliseconds * 10L;
            }

            if (_videoFrameQueue.Count == 0)
                return;

            long currentTimestamp = _videoFrames * 10000L / (_source.VideoInfo.FpsNom / _source.VideoInfo.FpsDenom);
            long nextTimestamp = (_videoFrames + 1) * 10000L / (_source.VideoInfo.FpsNom / _source.VideoInfo.FpsDenom);

            if(elapsed > currentTimestamp && elapsed < nextTimestamp)
            {
                return;
            }

            //Debug.WriteLine($"Video {currentTimestamp / 10000d}, next {nextTimestamp / 10000d}");

            // immediately start decoding new frame
            this._eventVideo.Set();

            byte[] decoded = _videoFrameQueue.Dequeue();
            Interlocked.Increment(ref _videoFrames);

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

            _source.ReturnVideoSample(decoded);
        }

        private async Task VideoRenderThread()
        {
            var videoSource = _source;
            if (videoSource != null)
            {
                // initialize
                await videoSource.InitializeAsync();

                var videoInfo = videoSource.VideoInfo;
                await Dispatcher.InvokeAsync(() =>
                {
                    _canvas = new WriteableBitmap(
                        (int)videoInfo.OriginalWidth,
                        (int)videoInfo.OriginalHeight,
                        96,
                        96,
                        videoInfo.PixelFormat == PixelFormat.BGRA32 ? PixelFormats.Bgra32 : PixelFormats.Bgr24,
                        null);
                    this._image.Source = _canvas;
                });
                this._croppingRect = new Int32Rect(0, 0, (int)videoInfo.OriginalWidth, (int)videoInfo.OriginalHeight);
                this._stopwatch.Restart();

                // stream samples
                while(true)
                {
                    if (_videoFrameQueue.Count > 0)
                    {
                        this._eventVideo.WaitOne();
                    }

                    if(videoSource.GetVideoSample(out var sample))
                    {
                        if(sample != null)
                            _videoFrameQueue.Enqueue(sample);
                    }
                    else
                    {
                        break;
                    }
                }

                Debug.WriteLine($"Video completed {_videoFrames}");
            }
        }

        private async Task AudioRenderThread()
        {
            var audioSource = _source as IAudioSource;
            if(audioSource != null)
            {
                // initialize
                await audioSource.InitializeAsync();

                var audioInfo = audioSource.AudioInfo;
                this._waveOut = new WaveOut();
                this._waveOut.Initialize(audioInfo.SampleRate, audioInfo.Channels, audioInfo.BitsPerSample);
                this._waveOut.OnPlaybackCompleted += (o, e) =>
                {
                    this._eventAudio.Set();
                };

                // stream samples
                while(true)
                {
                    if (_waveOut.QueuedFrames > 2) // have at least 2 frames in the queue
                    {
                        this._eventAudio.WaitOne();
                    }

                    if (audioSource.GetAudioSample(out var sample))
                    {
                        if (sample != null)
                        {
                            _waveOut.Enqueue(sample, (uint)sample.Length);
                            Interlocked.Increment(ref _audioFrames);
                            ((IAudioSource)_source).ReturnAudioSample(sample);
                        }
                    }
                    else
                    {
                        break;
                    }
                }

                Debug.WriteLine($"Audio completed {_audioFrames}");
            }
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
