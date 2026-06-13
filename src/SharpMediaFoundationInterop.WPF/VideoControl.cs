using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpMediaFoundationInterop.Transforms;
using SharpMediaFoundationInterop.Transforms.AAC;
using SharpMediaFoundationInterop.Transforms.AV1;
using SharpMediaFoundationInterop.Transforms.H264;
using SharpMediaFoundationInterop.Transforms.H265;
using SharpMediaFoundationInterop.Transforms.Opus;
using SharpMediaFoundationInterop.Wave;

namespace SharpMediaFoundationInterop.WPF
{
    [TemplatePart(Name = "PART_Image", Type = typeof(Image))]
    [TemplatePart(Name = "PART_PlayPauseButton", Type = typeof(Button))]
    [TemplatePart(Name = "PART_PositionSlider", Type = typeof(Slider))]
    public class VideoControl : Control
    {
        private const int MaxVideoQueueSize = 4;
        private const int TargetAudioQueueFrames = 100;

        private Image _image;
        private Button _playPauseButton;
        private Slider _positionSlider;
        private WriteableBitmap _bitmap;
        private Int32Rect _fullRect;

        private CancellationTokenSource _cts;
        private Thread _decodeThread;

        private readonly ConcurrentQueue<DecodedFrame> _frameQueue = new();
        private readonly object _poolLock = new();
        private readonly Stack<byte[]> _bgraPool = new();

        private WaveOut _waveOut;
        private readonly Stopwatch _clock = new();

        private uint _displayWidth;
        private uint _displayHeight;

        private long _videoPtsStart = -1;
        private volatile bool _clockResetNeeded;

        private volatile bool _looping;
        private volatile bool _mute;

        // All three only accessed on the UI thread (OnRendering + event handlers).
        private bool _paused;
        private bool _isDraggingSlider;
        private bool _updatingSlider;

        private readonly struct DecodedFrame
        {
            public readonly byte[] Bgra;
            public readonly int Size;
            public readonly long Timestamp;

            public DecodedFrame(byte[] bgra, int size, long ts) { Bgra = bgra; Size = size; Timestamp = ts; }
        }

        static VideoControl()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(VideoControl), new FrameworkPropertyMetadata(typeof(VideoControl)));
        }

        public VideoControl()
        {
            Unloaded += (_, _) => StopPlayback();
        }

        #region Dependency Properties

        public bool Looping
        {
            get => (bool)GetValue(LoopingProperty);
            set => SetValue(LoopingProperty, value);
        }
        public static readonly DependencyProperty LoopingProperty =
            DependencyProperty.Register(nameof(Looping), typeof(bool), typeof(VideoControl),
                new PropertyMetadata(false, (d, e) => ((VideoControl)d)._looping = (bool)e.NewValue));

        public bool Mute
        {
            get => (bool)GetValue(MuteProperty);
            set => SetValue(MuteProperty, value);
        }
        public static readonly DependencyProperty MuteProperty =
            DependencyProperty.Register(nameof(Mute), typeof(bool), typeof(VideoControl),
                new PropertyMetadata(false, (d, e) => ((VideoControl)d)._mute = (bool)e.NewValue));

        public IVideoPlayerSource Source
        {
            get => (IVideoPlayerSource)GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }
        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register(nameof(Source), typeof(IVideoPlayerSource), typeof(VideoControl),
                new PropertyMetadata(null, OnSourceChanged));

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (VideoControl)d;
            ctrl.StopPlayback();
            if (e.NewValue is IVideoPlayerSource src)
                ctrl.StartPlayback(src);
        }

        public bool IsPlaying
        {
            get => (bool)GetValue(IsPlayingProperty);
            private set => SetValue(IsPlayingProperty, value);
        }
        public static readonly DependencyProperty IsPlayingProperty =
            DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(VideoControl),
                new PropertyMetadata(false));

        public bool CanSeek
        {
            get => (bool)GetValue(CanSeekProperty);
            private set => SetValue(CanSeekProperty, value);
        }
        public static readonly DependencyProperty CanSeekProperty =
            DependencyProperty.Register(nameof(CanSeek), typeof(bool), typeof(VideoControl),
                new PropertyMetadata(false));

        // Total video duration in seconds; drives the slider maximum.
        public double Duration
        {
            get => (double)GetValue(DurationProperty);
            private set => SetValue(DurationProperty, value);
        }
        public static readonly DependencyProperty DurationProperty =
            DependencyProperty.Register(nameof(Duration), typeof(double), typeof(VideoControl),
                new PropertyMetadata(0.0));

        #endregion

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            if (_playPauseButton != null)
                _playPauseButton.Click -= OnPlayPauseClick;
            if (_positionSlider != null)
            {
                _positionSlider.ValueChanged -= OnSliderValueChanged;
                _positionSlider.RemoveHandler(Thumb.DragStartedEvent,  (DragStartedEventHandler)OnSliderDragStarted);
                _positionSlider.RemoveHandler(Thumb.DragCompletedEvent, (DragCompletedEventHandler)OnSliderDragCompleted);
            }

            _image = GetTemplateChild("PART_Image") as Image;
            if (_image != null && _bitmap != null)
                _image.Source = _bitmap;

            _playPauseButton = GetTemplateChild("PART_PlayPauseButton") as Button;
            if (_playPauseButton != null)
                _playPauseButton.Click += OnPlayPauseClick;

            _positionSlider = GetTemplateChild("PART_PositionSlider") as Slider;
            if (_positionSlider != null)
            {
                _positionSlider.ValueChanged += OnSliderValueChanged;
                _positionSlider.AddHandler(Thumb.DragStartedEvent,  (DragStartedEventHandler)OnSliderDragStarted);
                _positionSlider.AddHandler(Thumb.DragCompletedEvent, (DragCompletedEventHandler)OnSliderDragCompleted);
            }
        }

        // Resume a paused video. No-op if already playing or no source is loaded.
        public void Play()
        {
            if (!IsPlaying && _decodeThread != null)
            {
                _paused = false;
                _waveOut?.Resume();
                _clock.Start();
                IsPlaying = true;
            }
        }

        // Pause playback without losing the current position. Resume with Play().
        public void Pause()
        {
            if (IsPlaying)
            {
                _clock.Stop();
                _waveOut?.Pause();
                _paused = true;
                IsPlaying = false;
            }
        }

        // Seek to an arbitrary position. Returns immediately; the seek runs on a background thread.
        public void Seek(TimeSpan position)
        {
            var source = Source;
            if (source == null) return;
            long ts = (long)(position.TotalSeconds * 10_000_000);

            var cts = _cts;
            var thread = _decodeThread;
            Task.Run(() =>
            {
                cts?.Cancel();
                thread?.Join(2000);
                source.Seek(ts);
                Dispatcher.Invoke(RestartAfterSeek);
            });
        }

        private void OnPlayPauseClick(object sender, RoutedEventArgs e)
        {
            if (IsPlaying) Pause();
            else Play();
        }

        private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingSlider || _isDraggingSlider) return;
            Seek(TimeSpan.FromSeconds(e.NewValue));
        }

        private void OnSliderDragStarted(object sender, DragStartedEventArgs e)
        {
            _isDraggingSlider = true;
        }

        private void OnSliderDragCompleted(object sender, DragCompletedEventArgs e)
        {
            double value = _positionSlider.Value;
            _isDraggingSlider = false;
            Seek(TimeSpan.FromSeconds(value));
        }

        private void RestartAfterSeek()
        {
            var source = Source;
            CompositionTarget.Rendering -= OnRendering;
            DrainFrameQueue();
            _waveOut?.Dispose();
            _waveOut = null;
            _clock.Stop();
            _videoPtsStart = -1;
            _clockResetNeeded = false;
            _paused = false;
            _cts?.Dispose();
            _cts = null;
            _decodeThread = null;
            ClearBgraPool();
            if (source != null)
                StartPlayback(source);
        }

        private void StartPlayback(IVideoPlayerSource source)
        {
            _displayWidth  = source.VideoWidth;
            _displayHeight = source.VideoHeight;

            CanSeek  = source.CanSeek;
            Duration = source.Duration > 0 ? source.Duration / 10_000_000.0 : 0.0;
            IsPlaying = true;

            if (source.HasVideo)
            {
                _bitmap = new WriteableBitmap(
                    (int)_displayWidth, (int)_displayHeight, 96, 96, PixelFormats.Bgra32, null);
                _fullRect = new Int32Rect(0, 0, (int)_displayWidth, (int)_displayHeight);
                if (_image != null) _image.Source = _bitmap;
            }

            CompositionTarget.Rendering += OnRendering;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _decodeThread = new Thread(() => DecodeLoop(source, token))
            {
                IsBackground = true,
                Name = "VideoDecodeThread"
            };
            _decodeThread.Start();
        }

        private void StopPlayback()
        {
            CompositionTarget.Rendering -= OnRendering;
            _cts?.Cancel();
            _decodeThread?.Join(2000);
            _decodeThread = null;
            _cts?.Dispose();
            _cts = null;
            _clock.Stop();
            _videoPtsStart = -1;
            _clockResetNeeded = false;
            _paused = false;
            IsPlaying = false;
            CanSeek = false;
            Duration = 0.0;

            DrainFrameQueue();
            ClearBgraPool();

            _waveOut?.Dispose();
            _waveOut = null;

            _bitmap = null;
            if (_image != null) _image.Source = null;
        }

        private void DrainFrameQueue()
        {
            while (_frameQueue.TryDequeue(out var f))
                ReturnBgra(f.Bgra);
        }

        private void ClearBgraPool()
        {
            lock (_poolLock) { _bgraPool.Clear(); }
        }

        private byte[] RentBgra()
        {
            lock (_poolLock)
            {
                if (_bgraPool.Count > 0) return _bgraPool.Pop();
            }
            return new byte[_displayWidth * _displayHeight * 4];
        }

        private void ReturnBgra(byte[] buf)
        {
            if (buf == null) return;
            lock (_poolLock)
            {
                if (_bgraPool.Count < MaxVideoQueueSize + 2)
                    _bgraPool.Push(buf);
            }
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Decode loop (background thread)
        // ──────────────────────────────────────────────────────────────────────────

        private void DecodeLoop(IVideoPlayerSource source, CancellationToken token)
        {
            int frameByteSize = (int)(_displayWidth * _displayHeight * 4);

            IMediaVideoTransform videoDecoder = source.HasVideo ? CreateVideoDecoder(source) : null;
            videoDecoder?.Initialize();

            IMediaAudioTransform audioDecoder = source.HasAudio ? CreateAudioDecoder(source) : null;
            audioDecoder?.Initialize();

            if (audioDecoder != null)
            {
                _waveOut = new WaveOut();
                _waveOut.Initialize(audioDecoder.SampleRate, audioDecoder.Channels, 16);
            }

            byte[] videoCompBuf = source.HasVideo ? new byte[2 * 1024 * 1024] : null;
            byte[] nv12Buf      = source.HasVideo ? new byte[videoDecoder.OutputSize] : null;
            byte[] audioCompBuf = source.HasAudio ? new byte[64 * 1024] : null;
            byte[] pcmBuf       = source.HasAudio ? new byte[audioDecoder.OutputSize] : null;

            bool videoEOS = !source.HasVideo;
            bool audioEOS = !source.HasAudio;

            while (!token.IsCancellationRequested)
            {
                bool worked = false;

                // ── Audio: keep WaveOut queue filled ─────────────────────────────
                if (!audioEOS && _waveOut != null && _waveOut.QueuedFrames < TargetAudioQueueFrames)
                {
                    if (source.ReadNextAudioUnit(ref audioCompBuf, out uint audioLen, out long audioTs))
                    {
                        var naluAudio = MakeSlice(audioCompBuf, audioLen);
                        if (audioDecoder.ProcessInput(naluAudio, audioTs))
                        {
                            while (audioDecoder.ProcessOutput(ref pcmBuf, out uint pcmLen) && !token.IsCancellationRequested)
                            {
                                if (!_mute)
                                {
                                    byte[] pcm16 = source.AudioCodec == "OPUS"
                                        ? ConvertFloatToInt16(pcmBuf, pcmLen)
                                        : MakeSlice(pcmBuf, pcmLen);
                                    _waveOut.Enqueue(pcm16, (uint)pcm16.Length);
                                }
                            }
                        }
                        worked = true;
                    }
                    else audioEOS = true;
                }

                // ── Video: decode and queue frames ───────────────────────────────
                if (!videoEOS && _frameQueue.Count < MaxVideoQueueSize)
                {
                    if (source.ReadNextVideoUnit(ref videoCompBuf, out uint videoLen, out long videoTs))
                    {
                        var naluVideo = MakeSlice(videoCompBuf, videoLen);
                        if (videoDecoder.ProcessInput(naluVideo, videoTs))
                        {
                            while (videoDecoder.ProcessOutput(ref nv12Buf, out _) && !token.IsCancellationRequested)
                            {
                                byte[] bgra = RentBgra();
                                ConvertNV12ToBgra(nv12Buf, (int)videoDecoder.Width, (int)videoDecoder.Height,
                                    (int)_displayWidth, (int)_displayHeight, bgra);
                                _frameQueue.Enqueue(new DecodedFrame(bgra, frameByteSize, videoTs));
                            }
                        }
                        worked = true;
                    }
                    else videoEOS = true;
                }

                // ── EOS handling ─────────────────────────────────────────────────
                if (videoEOS && audioEOS)
                {
                    if (_looping && !token.IsCancellationRequested)
                    {
                        if (videoDecoder != null)
                        {
                            videoDecoder.Drain();
                            while (videoDecoder.ProcessOutput(ref nv12Buf, out _) && !token.IsCancellationRequested) { }
                        }
                        if (audioDecoder != null)
                        {
                            audioDecoder.Drain();
                            while (audioDecoder.ProcessOutput(ref pcmBuf, out _) && !token.IsCancellationRequested) { }
                        }

                        while (_frameQueue.Count > 0 && !token.IsCancellationRequested)
                            Thread.Sleep(5);

                        _waveOut?.Reset();

                        videoDecoder?.Flush();
                        audioDecoder?.Flush();

                        source.Seek(0);
                        _clockResetNeeded = true;
                        videoEOS = !source.HasVideo;
                        audioEOS = !source.HasAudio;
                    }
                    else break;
                }
                else if (!worked)
                {
                    Thread.Sleep(5);
                }
            }

            videoDecoder?.Dispose();
            audioDecoder?.Dispose();
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Presentation (UI thread, fires at display refresh rate)
        // ──────────────────────────────────────────────────────────────────────────

        private void OnRendering(object sender, EventArgs e)
        {
            if (_bitmap == null || _frameQueue.IsEmpty) return;

            if (_paused) return;

            if (_clockResetNeeded)
            {
                _clockResetNeeded = false;
                _videoPtsStart = -1;
                _clock.Restart();
            }
            else if (!_clock.IsRunning)
            {
                _clock.Restart();
            }

            long elapsed = _clock.ElapsedTicks * 10_000_000L / Stopwatch.Frequency;

            DecodedFrame? toPresent = null;
            while (_frameQueue.TryPeek(out var next))
            {
                if (_videoPtsStart < 0) _videoPtsStart = next.Timestamp;

                var pts = next.Timestamp - _videoPtsStart;
                if (pts > elapsed) break;
                _frameQueue.TryDequeue(out var f);
                if (toPresent.HasValue) ReturnBgra(toPresent.Value.Bgra);
                toPresent = f;
            }

            // Update position slider while the user is not dragging it.
            if (_positionSlider != null && !_isDraggingSlider && _videoPtsStart >= 0 && Duration > 0)
            {
                _updatingSlider = true;
                _positionSlider.Value = Math.Min((_videoPtsStart + elapsed) / 10_000_000.0, Duration);
                _updatingSlider = false;
            }

            if (!toPresent.HasValue) return;

            var frame = toPresent.Value;
            _bitmap.Lock();
            try
            {
                Marshal.Copy(frame.Bgra, 0, _bitmap.BackBuffer, frame.Size);
                _bitmap.AddDirtyRect(_fullRect);
            }
            finally
            {
                _bitmap.Unlock();
                ReturnBgra(frame.Bgra);
            }
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────────────────

        private static IMediaVideoTransform CreateVideoDecoder(IVideoPlayerSource source) =>
            source.VideoCodec switch
            {
                "H264" => new H264Decoder(source.VideoWidth, source.VideoHeight, source.FpsNom, source.FpsDenom),
                "H265" => new H265Decoder(source.VideoWidth, source.VideoHeight, source.FpsNom, source.FpsDenom),
                "AV1"  => new AV1Decoder(source.VideoWidth, source.VideoHeight, source.FpsNom, source.FpsDenom),
                _      => throw new NotSupportedException($"Unsupported video codec: {source.VideoCodec}")
            };

        private static IMediaAudioTransform CreateAudioDecoder(IVideoPlayerSource source) =>
            source.AudioCodec switch
            {
                "AAC"  => new AACDecoder(source.AudioChannels, source.AudioSampleRate,
                              AACDecoder.CreateUserData(source.AACUserData), source.AudioChannelConfiguration),
                "OPUS" => new OpusDecoder(960, source.AudioChannels, source.AudioSampleRate),
                _      => throw new NotSupportedException($"Unsupported audio codec: {source.AudioCodec}")
            };

        private static byte[] MakeSlice(byte[] src, uint length)
        {
            var s = new byte[(int)length];
            Buffer.BlockCopy(src, 0, s, 0, (int)length);
            return s;
        }

        private static byte[] ConvertFloatToInt16(byte[] floatPcm, uint byteCount)
        {
            int samples = (int)(byteCount / 4);
            var result = new byte[samples * 2];
            for (int i = 0; i < samples; i++)
            {
                float f = BitConverter.ToSingle(floatPcm, i * 4);
                float c = f < -1f ? -1f : f > 1f ? 1f : f;
                short s = (short)(c * 32767f);
                result[i * 2]     = (byte)(s & 0xFF);
                result[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            return result;
        }

        private static unsafe void ConvertNV12ToBgra(
            byte[] nv12, int strideW, int strideH, int displayW, int displayH, byte[] bgra)
        {
            fixed (byte* pSrc = nv12, pDst = bgra)
            {
                byte* pUV = pSrc + strideW * strideH;
                for (int row = 0; row < displayH; row++)
                {
                    byte* yRow  = pSrc + row * strideW;
                    byte* uvRow = pUV + (row >> 1) * strideW;
                    byte* dRow  = pDst + row * displayW * 4;

                    for (int col = 0; col < displayW; col++)
                    {
                        int y = yRow[col];
                        int uvBase = col & ~1;
                        int u = uvRow[uvBase]     - 128;
                        int v = uvRow[uvBase + 1] - 128;

                        int c = (y - 16) * 298;
                        int r = (c + 409 * v + 128) >> 8;
                        int g = (c - 100 * u - 208 * v + 128) >> 8;
                        int b = (c + 516 * u + 128) >> 8;

                        byte* px = dRow + col * 4;
                        px[0] = b < 0 ? (byte)0 : b > 255 ? (byte)255 : (byte)b;
                        px[1] = g < 0 ? (byte)0 : g > 255 ? (byte)255 : (byte)g;
                        px[2] = r < 0 ? (byte)0 : r > 255 ? (byte)255 : (byte)r;
                        px[3] = 255;
                    }
                }
            }
        }
    }
}
