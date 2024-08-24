﻿using SharpMediaFoundation.Wave;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SharpMediaFoundation.WPF
{
    [TemplatePart(Name = "PART_image", Type = typeof(Image))]
    public class VideoControl : Control, IDisposable
    {
        private static object _syncRoot = new object();
        private static List<VideoControl> _controls = new List<VideoControl>();

        private WaveOut _waveOut;

        private Image _image;
        private Int32Rect _videoRect;
        private WriteableBitmap _canvas;

        private Stopwatch _stopwatch = new Stopwatch();

        private long _videoFrames = 0;
        private long _audioFrames = 0;

        private ConcurrentQueue<byte[]> _videoOut = new ConcurrentQueue<byte[]>();

        private bool _disposedValue;

        private static Task _renderThread = null;

        private object _waveSync = new object();

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
            _renderThread = Task.Factory.StartNew(RenderThread, TaskCreationOptions.LongRunning);
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
                _controls.Remove(this);
            }
        }

        private void VideoControl_Loaded(object sender, RoutedEventArgs e)
        {
            lock (_syncRoot)
            {
                _controls.Add(this);
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

        private void CompositionTarget_Rendering(object sender, EventArgs e)
        {
            if (_videoOut.Count == 0)
                return;

            long elapsed;
            lock (_waveSync)
            {
                if (_waveOut != null)
                {
                    // if we have audio, synchronize it with video
                    var audioSource = _source as IAudioSource;
                    var audioInfo = audioSource.AudioInfo;
                    elapsed = _waveOut.GetPosition() * 10000L / (audioInfo.SampleRate * audioInfo.Channels * audioInfo.BitsPerSample / 8);

                    if (Log.InfoEnabled) Log.Info($"Audio {elapsed / 10000d}");
                }
                else
                {
                    elapsed = _stopwatch.ElapsedMilliseconds * 10L;
                }
            }

            long currentTimestamp = _videoFrames * 10000L / (_source.VideoInfo.FpsNom / _source.VideoInfo.FpsDenom);
            long nextTimestamp = (_videoFrames + 1) * 10000L / (_source.VideoInfo.FpsNom / _source.VideoInfo.FpsDenom);

            if (elapsed < currentTimestamp)
            {
                if (Log.WarnEnabled) Log.Warn("Elapsed time is less than the current frame time!");
                return;
            }

            if (elapsed > currentTimestamp && elapsed < nextTimestamp)
                return;

            if (Log.InfoEnabled) Log.Info($"Video {currentTimestamp / 10000d}, next {nextTimestamp / 10000d}");

            byte[] decoded;
            if (!_videoOut.TryDequeue(out decoded))
                return;

            Interlocked.Increment(ref _videoFrames);

            _canvas.Lock();

            // TODO: bitmap stride?
            Marshal.Copy(
                decoded,
                0,
                _canvas.BackBuffer,
                (int)(_source.VideoInfo.OriginalWidth * _source.VideoInfo.OriginalHeight * (_source.VideoInfo.PixelFormat == PixelFormat.BGRA32 ? 4 : 3))
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
                    _canvas = new WriteableBitmap(
                        (int)videoInfo.OriginalWidth,
                        (int)videoInfo.OriginalHeight,
                        96,
                        96,
                        videoInfo.PixelFormat == PixelFormat.BGRA32 ? PixelFormats.Bgra32 : PixelFormats.Bgr24,
                        null);
                    this._image.Source = _canvas;
                });
                this._videoRect = new Int32Rect(0, 0, (int)videoInfo.OriginalWidth, (int)videoInfo.OriginalHeight);
                this._stopwatch.Restart();
            }
        }

        private async Task UninitializeVideo(IVideoSource videoSource)
        {
            _videoOut.Clear();
            _canvas = null;
        }


        private static async Task RenderThread()
        {
            while (true)
            {
                bool rendered = false;
                foreach (var control in _controls.ToArray())
                {
                    if (control._source is IVideoSource videoSource)
                    {
                        if (control._canvas == null)
                        {
                            await control.InitializeVideo(videoSource);
                        }

                        if (control._videoOut.Count < 1)
                        {
                            rendered = control.RenderVideo(videoSource);

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

                        if (control._waveOut.QueuedFrames < 4)
                        {
                            rendered = control.RenderAudio(audioSource);

                            if (!rendered)
                            {
                                await control.UninitializeAudio(audioSource);
                            }
                        }
                    }
                }
            }
        }

        private bool RenderVideo(IVideoSource videoSource)
        {
            if (videoSource.GetVideoSample(out var sample))
            {
                if (sample != null)
                {
                    _videoOut.Enqueue(sample);
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
                    this._waveOut.Initialize(audioInfo.SampleRate, audioInfo.Channels, audioInfo.BitsPerSample);
               }
            }
        }

        private async Task UninitializeAudio(IAudioSource audioSource)
        {
            lock (_waveSync)
            {
                this._waveOut.Dispose();
                this._waveOut = null;
            }
        }

        private void AudioCallback(object sender, EventArgs e)
        {

        }

        private bool RenderAudio(IAudioSource audioSource)
        {
            if (audioSource.GetAudioSample(out var sample))
            {
                if (sample != null)
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
            while (_videoOut.Count > 0)
            {
                if (_videoOut.TryDequeue(out var sample))
                    _source.ReturnVideoSample(sample);
            }

            Interlocked.Exchange(ref _videoFrames, 0);
            Interlocked.Exchange(ref _audioFrames, 0);
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
