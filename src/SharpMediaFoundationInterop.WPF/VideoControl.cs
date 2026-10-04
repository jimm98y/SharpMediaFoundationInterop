using SharpMediaFoundationInterop.Wave;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SharpMediaFoundationInterop.WPF
{
    [TemplatePart(Name = "PART_image", Type = typeof(Image))]
    public class VideoControl : Control, IDisposable
    {
        private static object _syncRoot = new object();
        private static VideoControl[] _controls = Array.Empty<VideoControl>();
        private static Task _decodeThread;

        private object _waveSync = new object();
        private WaveOut _waveOut;

        private Image _image;
        private Int32Rect _videoRect;
        private WriteableBitmap _canvas;

        private Stopwatch _stopwatch = new Stopwatch();

        private long _videoFrames = 0;
        private long _audioFrames = 0;

        /// <summary>Decoded frames waiting to be shown, each with its time; -1 to be shown as it comes.</summary>
        private ConcurrentQueue<(byte[] Frame, long Timestamp)> _videoOut = new ConcurrentQueue<(byte[] Frame, long Timestamp)>();

        private bool _disposedValue;

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
            if (source != null && sender.AutoPlay)
            {
                sender.StartPlaying();
            }
        }

        private bool _isLooping = false;
        private bool _isMute = false;

        public bool Looping
        {
            get { return (bool)GetValue(LoopingProperty); }
            set { SetValue(LoopingProperty, value); }
        }

        public static readonly DependencyProperty LoopingProperty =
            DependencyProperty.Register("Looping", typeof(bool), typeof(VideoControl), new PropertyMetadata(false, OnIsLoopingChanged));

        private static void OnIsLoopingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var sender = d as VideoControl;
            if (sender != null)
            {
                sender._isLooping = (bool)e.NewValue;
            }
        }

        public bool AutoPlay
        {
            get { return (bool)GetValue(AutoPlayProperty); }
            set { SetValue(AutoPlayProperty, value); }
        }

        public static readonly DependencyProperty AutoPlayProperty =
            DependencyProperty.Register("AutoPlay", typeof(bool), typeof(VideoControl), new PropertyMetadata(true));

        public bool Mute
        {
            get { return (bool)GetValue(MuteProperty); }
            set { SetValue(MuteProperty, value); }
        }

        public static readonly DependencyProperty MuteProperty =
            DependencyProperty.Register("Mute", typeof(bool), typeof(VideoControl), new PropertyMetadata(false, OnMuteChanged));

        private static void OnMuteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var sender = d as VideoControl;
            if (sender != null)
            {
                sender._isMute = (bool)e.NewValue;
            }
        }

        static VideoControl()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(VideoControl), new FrameworkPropertyMetadata(typeof(VideoControl)));
            _decodeThread = Task.Factory.StartNew(DecodeThread, TaskCreationOptions.LongRunning);
        }

        public VideoControl()
        {
            Loaded += VideoControl_Loaded;
            Unloaded += VideoControl_Unloaded;
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }

        private void VideoControl_Unloaded(object sender, RoutedEventArgs e)
        {
            lock (_syncRoot)
            {
                var controls = _controls.ToHashSet();
                controls.Remove(this);
                _controls = controls.ToArray();
            }
        }

        private void VideoControl_Loaded(object sender, RoutedEventArgs e)
        {
            lock (_syncRoot)
            {
                var controls = _controls.ToHashSet();
                controls.Add(this);
                _controls = controls.ToArray();
            }

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

        /// <summary>
        /// How far a frame's time may be from the clock before the clock is set by it again: a stream whose times jump -
        /// one that starts over, a live one the sender's clock has drifted from - is followed rather than frozen on, or
        /// raced through.
        /// </summary>
        private static readonly long ResyncThreshold = TimeSpan.FromSeconds(1).Ticks;

        // The playback clock: the stopwatch since the first frame shown, against the frames' own times since that one's.
        private long _clockFrameTime = -1;
        private long _clockStart;

        /// <summary>The time of the frame shown last; -1 before the first.</summary>
        private long _lastShownTime = -1;

        private void CompositionTarget_Rendering(object sender, EventArgs e)
        {
            if (_canvas == null || !_videoOut.TryPeek(out var next))
                return;

            // Each frame is shown at its own time, as the source gives it: of the file's samples, of the RTP timestamps,
            // of the capture. A frame of no time is shown as it comes.
            if (next.Timestamp >= 0)
            {
                // A frame of before the one shown - which a decoder that joined a stream between its key frames can hand
                // out late - is dropped rather than shown out of order; a jump back as far as a resync is followed.
                if (_lastShownTime >= 0 && next.Timestamp < _lastShownTime && _lastShownTime - next.Timestamp <= ResyncThreshold)
                {
                    if (_videoOut.TryDequeue(out var late))
                        _source.ReturnVideoSample(late.Frame);
                    return;
                }

                long now = _stopwatch.Elapsed.Ticks;
                long due = next.Timestamp - _clockFrameTime;
                long elapsed = now - _clockStart;
                if (_clockFrameTime < 0 || Math.Abs(due - elapsed) > ResyncThreshold)
                {
                    _clockFrameTime = next.Timestamp;
                    _clockStart = now;
                }
                else if (elapsed < due)
                {
                    return;
                }

                if (Log.InfoEnabled) Log.Info($"Video {next.Timestamp / (double)TimeSpan.TicksPerSecond}, clock {(now - _clockStart + _clockFrameTime) / (double)TimeSpan.TicksPerSecond}");
            }

            if (!_videoOut.TryDequeue(out next))
                return;

            byte[] decoded = next.Frame;
            _videoFrames++;
            if (next.Timestamp >= 0)
                _lastShownTime = next.Timestamp;

            var videoInfo = _source.VideoInfo;
            if(videoInfo == null)
            {
                _source.ReturnVideoSample(decoded);
                return;
            }

            _canvas.Lock();           

            // TODO: bitmap stride?
            Marshal.Copy(
                decoded,
                0,
                _canvas.BackBuffer,
                (int)(videoInfo.OriginalWidth * videoInfo.OriginalHeight * (videoInfo.PixelFormat == PixelFormat.BGRA32 ? 4 : 3))
            );

            _canvas.AddDirtyRect(_videoRect);
            _canvas.Unlock();

            _source.ReturnVideoSample(decoded);
        }

        private async Task InitializeVideo(IVideoSource videoSource)
        {
            await videoSource.InitializeAsync();

            var videoInfo = videoSource.VideoInfo;
            if (videoInfo != null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    var image = this._image;
                    if (image != null)
                    {
                        var canvas = new WriteableBitmap(
                            (int)videoInfo.OriginalWidth,
                            (int)videoInfo.OriginalHeight,
                            96,
                            96,
                            videoInfo.PixelFormat == PixelFormat.BGRA32 ? PixelFormats.Bgra32 : PixelFormats.Bgr24,
                            null);
                        image.Source = canvas;
                        this._canvas = canvas;

                        this._videoRect = new Int32Rect(0, 0, (int)videoInfo.OriginalWidth, (int)videoInfo.OriginalHeight);
                        this._stopwatch.Restart();
                    }
                });
            }
        }

        private Task UninitializeVideo(IVideoSource videoSource)
        {
            while (_videoOut.TryDequeue(out var sample))
                videoSource.ReturnVideoSample(sample.Frame);
            _canvas = null;

            if (_isLooping)
                StartPlaying();

            return Task.CompletedTask;
        }


        private static async Task DecodeThread()
        {
            while (true)
            {
                bool rendered = false;
                var controls = _controls;

                foreach (var control in controls)
                {
                    if (control._source is IVideoSource videoSource)
                    {
                        if (control._canvas == null)
                        {
                            await control.InitializeVideo(videoSource);
                        }

                        if (control._videoOut.Count < 1)
                        {
                            rendered = control.DecodeVideo(videoSource);

                            if (!rendered)
                            {
                                await control.UninitializeVideo(videoSource);
                            }
                        }
                    }

                    if (control._source is IAudioSource audioSource)
                    {
                        if (control._waveOut == null)
                        {
                            await control.InitializeAudio(audioSource);
                        }

                        if (control._waveOut != null && control._waveOut.QueuedFrames < 5)
                        {
                            rendered = control.DecodeAudio(audioSource);

                            if (!rendered)
                            {
                                await control.UninitializeAudio(audioSource);
                            }
                        }
                    }
                }

                await Task.Delay(1);
            }
        }

        private bool DecodeVideo(IVideoSource videoSource)
        {
            var sample = videoSource.GetVideoSample(out long timestamp);
            if (sample != null)
            {
                if (sample.Length > 0)
                {
                    _videoOut.Enqueue((sample, timestamp));
                }

                return true;
            }

            return false;
        }

        private async Task InitializeAudio(IAudioSource audioSource)
        {
            await audioSource.InitializeAsync();

            var audioInfo = audioSource.AudioInfo;
            if (audioInfo != null)
            {
                lock (_waveSync)
                {
                    this._waveOut = new WaveOut();
                    this._waveOut.Initialize(audioInfo.SampleRate, audioInfo.ChannelCount, audioInfo.BitsPerSample);
               }
            }
        }

        private Task UninitializeAudio(IAudioSource audioSource)
        {
            lock (_waveSync)
            {
                this._waveOut.Dispose();
                this._waveOut = null;
            }

            return Task.CompletedTask;
        }

        private bool DecodeAudio(IAudioSource audioSource)
        {
            var sample = audioSource.GetAudioSample();
            if (sample != null)
            {
                if (sample.Length > 0)
                {
                    if (_isMute)
                    {
                        Array.Fill<byte>(sample, 0);
                    }

                    _waveOut.Enqueue(sample, (uint)sample.Length);
                    Interlocked.Increment(ref _audioFrames);
                    ((IAudioSource)_source).ReturnAudioSample(sample);
                }

                return true;
            }

            return false;
        }

        private void StartPlaying()
        {
            // cleanup all samples from previous playback session
            while (_videoOut.TryDequeue(out var sample))
                _source.ReturnVideoSample(sample.Frame);

            _videoFrames = 0;
            _audioFrames = 0;
            _clockFrameTime = -1; // set again by the first frame shown
            _lastShownTime = -1;
            _stopwatch.Restart();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                }

                if (_waveOut != null)
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
