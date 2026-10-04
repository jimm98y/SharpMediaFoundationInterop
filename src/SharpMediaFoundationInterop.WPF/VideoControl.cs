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
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SharpMediaFoundationInterop.WPF
{
    [TemplatePart(Name = "PART_Image", Type = typeof(Image))]
    [TemplatePart(Name = "PART_PlayPause", Type = typeof(ButtonBase))]
    [TemplatePart(Name = "PART_Rewind", Type = typeof(ButtonBase))]
    [TemplatePart(Name = "PART_FastForward", Type = typeof(ButtonBase))]
    [TemplatePart(Name = "PART_Seek", Type = typeof(Slider))]
    [TemplatePart(Name = "PART_Time", Type = typeof(TextBlock))]
    public class VideoControl : Control, IDisposable
    {
        private object _waveSync = new object();
        private WaveOut _waveOut;

        private Image _image;
        private ButtonBase _playPauseButton;
        private ButtonBase _rewindButton;
        private ButtonBase _fastForwardButton;
        private Slider _seekSlider;
        private TextBlock _timeText;
        private Int32Rect _videoRect;
        private WriteableBitmap _canvas;

        private Stopwatch _stopwatch = new Stopwatch();

        private long _videoFrames = 0;
        private long _audioFrames = 0;

        /// <summary>
        /// Decoded frames waiting to be shown, each with its time - -1 to be shown as it comes - and the number of the seek
        /// it is of, which a later seek leaves it behind.
        /// </summary>
        private ConcurrentQueue<(byte[] Frame, long Timestamp, long Request)> _videoOut = new ConcurrentQueue<(byte[] Frame, long Timestamp, long Request)>();

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
            sender.Wake();
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

        #region Trick play properties

        /// <summary>Whether the bar of buttons, the seek bar and the time are shown over the bottom of the video.</summary>
        public bool ShowControls
        {
            get { return (bool)GetValue(ShowControlsProperty); }
            set { SetValue(ShowControlsProperty, value); }
        }

        public static readonly DependencyProperty ShowControlsProperty =
            DependencyProperty.Register("ShowControls", typeof(bool), typeof(VideoControl), new PropertyMetadata(true));

        public bool IsPaused
        {
            get { return (bool)GetValue(IsPausedProperty); }
            set { SetValue(IsPausedProperty, value); }
        }

        public static readonly DependencyProperty IsPausedProperty =
            DependencyProperty.Register("IsPaused", typeof(bool), typeof(VideoControl), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsPausedChanged));

        private static void OnIsPausedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((VideoControl)d).SetPaused((bool)e.NewValue);
        }

        /// <summary>
        /// How fast, and which way, the video plays: 1 forwards as recorded, 2, 4 and 8 that many times faster, and the same
        /// below 0 backwards. Only a source that can be sought in plays at any other than 1.
        /// </summary>
        public int PlaybackRate
        {
            get { return (int)GetValue(PlaybackRateProperty); }
            private set { SetValue(PlaybackRatePropertyKey, value); }
        }

        private static readonly DependencyPropertyKey PlaybackRatePropertyKey =
            DependencyProperty.RegisterReadOnly("PlaybackRate", typeof(int), typeof(VideoControl), new PropertyMetadata(1));
        public static readonly DependencyProperty PlaybackRateProperty = PlaybackRatePropertyKey.DependencyProperty;

        /// <summary>The time of the frame shown, from the start of the video.</summary>
        public TimeSpan Position
        {
            get { return (TimeSpan)GetValue(PositionProperty); }
            private set { SetValue(PositionPropertyKey, value); }
        }

        private static readonly DependencyPropertyKey PositionPropertyKey =
            DependencyProperty.RegisterReadOnly("Position", typeof(TimeSpan), typeof(VideoControl), new PropertyMetadata(TimeSpan.Zero));
        public static readonly DependencyProperty PositionProperty = PositionPropertyKey.DependencyProperty;

        /// <summary>How long the video is; zero where it is not known - a live stream.</summary>
        public TimeSpan Duration
        {
            get { return (TimeSpan)GetValue(DurationProperty); }
            private set { SetValue(DurationPropertyKey, value); }
        }

        private static readonly DependencyPropertyKey DurationPropertyKey =
            DependencyProperty.RegisterReadOnly("Duration", typeof(TimeSpan), typeof(VideoControl), new PropertyMetadata(TimeSpan.Zero));
        public static readonly DependencyProperty DurationProperty = DurationPropertyKey.DependencyProperty;

        /// <summary>Whether the video can be sought in, and played at other rates: a file can, a live stream cannot.</summary>
        public bool CanSeek
        {
            get { return (bool)GetValue(CanSeekProperty); }
            private set { SetValue(CanSeekPropertyKey, value); }
        }

        private static readonly DependencyPropertyKey CanSeekPropertyKey =
            DependencyProperty.RegisterReadOnly("CanSeek", typeof(bool), typeof(VideoControl), new PropertyMetadata(false));
        public static readonly DependencyProperty CanSeekProperty = CanSeekPropertyKey.DependencyProperty;

        #endregion

        static VideoControl()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(VideoControl), new FrameworkPropertyMetadata(typeof(VideoControl)));
        }

        public VideoControl()
        {
            Loaded += VideoControl_Loaded;
            Unloaded += VideoControl_Unloaded;
            IsVisibleChanged += VideoControl_IsVisibleChanged;
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }

        private void VideoControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _hidden = true;
            ApplyRunning();
        }

        /// <summary>
        /// Out of sight - on another tab, collapsed - nothing of the control is decoded, made or started. The sound and the
        /// clock wait with the video, rather than the sound queued playing out alone, and go on where they were when it is
        /// in sight again.
        /// </summary>
        private void VideoControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            _hidden = !(bool)e.NewValue;
            ApplyRunning();
        }

        private void VideoControl_Loaded(object sender, RoutedEventArgs e)
        {
            _hidden = !IsVisible;
            ApplyRunning();
            StartThreads();

            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.Closing += (s1, e1) =>
                {
                    CompositionTarget.Rendering -= CompositionTarget_Rendering;
                    StopThreads();
                };
            }
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            this._image = this.Template.FindName("PART_Image", this) as Image;

            if (_playPauseButton != null) _playPauseButton.Click -= PlayPauseButton_Click;
            if (_rewindButton != null) _rewindButton.Click -= RewindButton_Click;
            if (_fastForwardButton != null) _fastForwardButton.Click -= FastForwardButton_Click;
            if (_seekSlider != null)
            {
                _seekSlider.ValueChanged -= SeekSlider_ValueChanged;
                _seekSlider.RemoveHandler(Thumb.DragStartedEvent, (DragStartedEventHandler)SeekSlider_DragStarted);
                _seekSlider.RemoveHandler(Thumb.DragCompletedEvent, (DragCompletedEventHandler)SeekSlider_DragCompleted);
            }

            _playPauseButton = this.Template.FindName("PART_PlayPause", this) as ButtonBase;
            _rewindButton = this.Template.FindName("PART_Rewind", this) as ButtonBase;
            _fastForwardButton = this.Template.FindName("PART_FastForward", this) as ButtonBase;
            _seekSlider = this.Template.FindName("PART_Seek", this) as Slider;
            _timeText = this.Template.FindName("PART_Time", this) as TextBlock;

            if (_playPauseButton != null) _playPauseButton.Click += PlayPauseButton_Click;
            if (_rewindButton != null) _rewindButton.Click += RewindButton_Click;
            if (_fastForwardButton != null) _fastForwardButton.Click += FastForwardButton_Click;
            if (_seekSlider != null)
            {
                _seekSlider.ValueChanged += SeekSlider_ValueChanged;
                _seekSlider.AddHandler(Thumb.DragStartedEvent, (DragStartedEventHandler)SeekSlider_DragStarted);
                _seekSlider.AddHandler(Thumb.DragCompletedEvent, (DragCompletedEventHandler)SeekSlider_DragCompleted);
            }

            UpdateControls();
        }

        #region Trick play

        // The state the user asked for, kept here for the decode thread and the renderer: the dependency properties are the
        // UI thread's.
        private volatile bool _paused;
        private volatile int _rate = 1;

        /// <summary>The number of the seek asked for last: frames of the ones before it are not shown.</summary>
        private long _request;

        /// <summary>The number of the seek the frame shown last was of: the first of another is shown even paused.</summary>
        private long _shownRequest = -1;

        /// <summary>The number of the seek the clock was set for, by its first frame.</summary>
        private long _anchoredRequest = -1;

        /// <summary>The time the seek asked for last is to: what the clock starts at, of a file.</summary>
        private long _seekTime = -1;

        /// <summary>The number of the seek whose frames ran out - the end, or the start backwards; -1 while there are frames.</summary>
        private long _endedRequest = -1;

        // The sound waits from a seek until the first frame of it is shown, the video having to be decoded from the key frame
        // before: the queued sound of before it is let go of, on the decode thread, which the wave device is written from.
        private volatile bool _audioHold;
        private volatile bool _audioReset;

        // Whether the seek bar is being dragged, and whether its value is being set by playback, not by the user.
        private bool _dragging;
        private bool _updatingSlider;

        private ISeekableVideoSource SeekableSource => _source is ISeekableVideoSource seekable && seekable.CanSeek ? seekable : null;

        /// <summary>Plays forwards, at 1x - from the start again, where the video had come to its end.</summary>
        public void Play()
        {
            var seekable = SeekableSource;
            if (seekable != null && Interlocked.Read(ref _endedRequest) == Interlocked.Read(ref _request))
                SeekTo(seekable.StartTime, 1);
            else if (_rate != 1)
                SeekTo(CurrentTime(), 1);
            IsPaused = false;
        }

        public void Pause()
        {
            IsPaused = true;
        }

        public void TogglePlayPause()
        {
            if (IsPaused || _rate != 1)
                Play();
            else
                Pause();
        }

        /// <summary>Moves to a time from the start of the video, playing on, or paused, as before.</summary>
        public void Seek(TimeSpan position)
        {
            var seekable = SeekableSource;
            if (seekable == null)
                return;

            long time = seekable.StartTime + Math.Max(0, position.Ticks);
            if (seekable.Duration >= 0)
                time = Math.Min(time, seekable.StartTime + seekable.Duration);
            SeekTo(time, _rate);
        }

        /// <summary>Moves by a time, forwards or - below 0 - backwards.</summary>
        public void SeekBy(TimeSpan offset)
        {
            var seekable = SeekableSource;
            if (seekable == null)
                return;
            Seek(TimeSpan.FromTicks(CurrentTime() - seekable.StartTime + offset.Ticks));
        }

        /// <summary>Plays forwards faster: 2x, then 4x, then 8x, then 2x again.</summary>
        public void FastForward()
        {
            SetRate(_rate >= 2 && _rate < 8 ? _rate * 2 : 2);
        }

        /// <summary>Plays backwards: 1x, then 2x, 4x and 8x, then 1x again.</summary>
        public void Rewind()
        {
            SetRate(_rate <= -1 && _rate > -8 ? _rate * 2 : -1);
        }

        /// <summary>Plays at a rate, from the frame shown: see <see cref="PlaybackRate"/>.</summary>
        public void SetRate(int rate)
        {
            var seekable = SeekableSource;
            if (seekable == null)
                return;
            SeekTo(CurrentTime(), rate);
            IsPaused = false;
        }

        /// <summary>The time of the frame shown: what a new rate plays on from.</summary>
        private long CurrentTime()
        {
            var seekable = SeekableSource;
            long shown = Interlocked.Read(ref _lastShownTime);
            return shown >= 0 ? shown : seekable?.StartTime ?? 0;
        }

        private void SeekTo(long time, int rate)
        {
            var seekable = SeekableSource;
            if (seekable == null)
                return;

            _rate = rate;
            PlaybackRate = rate;
            _seekTime = time;
            Interlocked.Exchange(ref _request, seekable.Seek(time, rate));
            _audioHold = true;
            _audioReset = true;
            Wake();
            UpdateControls();
        }

        private void SetPaused(bool paused)
        {
            _paused = paused;
            ApplyRunning();
            UpdateControls();
        }

        /// <summary>Whether the control is out of sight - not yet shown, on another tab, collapsed - and nothing of it decoded.</summary>
        private volatile bool _hidden = true;

        /// <summary>Whether the clock and the sound stand still: paused, or out of sight.</summary>
        private bool IsHalted => _paused || _hidden;

        /// <summary>Runs the clock and the sound, or stops them where they are, as <see cref="IsHalted"/> says.</summary>
        private void ApplyRunning()
        {
            Wake();
            if (IsHalted)
                _stopwatch.Stop();
            else
                _stopwatch.Start();
            lock (_waveSync)
            {
                if (IsHalted)
                    _waveOut?.Pause();
                else
                    _waveOut?.Resume();
            }
        }

        /// <summary>The frames ran out, at the end or, backwards, the start: paused there, or, looping, from the start again.</summary>
        private void OnEnded(long request)
        {
            Interlocked.Exchange(ref _endedRequest, request);
            Dispatcher.InvokeAsync(() =>
            {
                if (Interlocked.Read(ref _request) != request)
                    return; // sought elsewhere since

                if (_isLooping && _rate > 0)
                {
                    Interlocked.Exchange(ref _endedRequest, -1);
                    SeekTo(SeekableSource?.StartTime ?? 0, _rate);
                }
                else
                {
                    IsPaused = true;
                }
            });
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => TogglePlayPause();
        private void RewindButton_Click(object sender, RoutedEventArgs e) => Rewind();
        private void FastForwardButton_Click(object sender, RoutedEventArgs e) => FastForward();

        private void SeekSlider_DragStarted(object sender, DragStartedEventArgs e) => _dragging = true;

        private void SeekSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _dragging = false;
            Seek(TimeSpan.FromTicks((long)_seekSlider.Value));
        }

        private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // The user's, by a click on the bar, or a drag of its thumb: dragged, each move is a seek, which the source
            // takes the last of when it gets to it - the video follows the thumb as fast as it can be decoded.
            if (_updatingSlider)
                return;
            Seek(TimeSpan.FromTicks((long)e.NewValue));
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled)
                return;

            switch (e.Key)
            {
                case Key.Space:
                case Key.K:
                    TogglePlayPause();
                    break;
                case Key.J:
                    Rewind();
                    break;
                case Key.L:
                    FastForward();
                    break;
                case Key.Left:
                    SeekBy(TimeSpan.FromSeconds(-5));
                    break;
                case Key.Right:
                    SeekBy(TimeSpan.FromSeconds(5));
                    break;
                case Key.Home:
                    Seek(TimeSpan.Zero);
                    break;
                case Key.End:
                    Seek(Duration);
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        private void UpdateControls()
        {
            bool canSeek = SeekableSource != null;
            CanSeek = canSeek;

            if (_playPauseButton != null)
                _playPauseButton.Content = _paused || _rate != 1 ? "" : ""; // play : pause
            if (_rewindButton != null)
                _rewindButton.IsEnabled = canSeek;
            if (_fastForwardButton != null)
                _fastForwardButton.IsEnabled = canSeek;
            if (_seekSlider != null)
            {
                _seekSlider.IsEnabled = canSeek;
                _updatingSlider = true;
                _seekSlider.Maximum = Math.Max(1, Duration.Ticks);
                _updatingSlider = false;
            }
            UpdatePosition();
        }

        private void UpdatePosition()
        {
            var seekable = SeekableSource;
            long shown = Interlocked.Read(ref _lastShownTime);
            if (seekable != null && shown >= 0)
                Position = TimeSpan.FromTicks(Math.Max(0, shown - seekable.StartTime));

            if (_seekSlider != null && !_dragging)
            {
                _updatingSlider = true;
                _seekSlider.Value = Math.Min(_seekSlider.Maximum, Position.Ticks);
                _updatingSlider = false;
            }

            if (_timeText != null)
            {
                string rate = _rate == 1 ? "" : _rate > 0 ? $"  ×{_rate}" : $"  ◀×{-_rate}";
                _timeText.Text = Duration > TimeSpan.Zero
                    ? $"{FormatTime(Position)} / {FormatTime(Duration)}{rate}"
                    : $"{FormatTime(Position)}{rate}";
            }
        }

        private static string FormatTime(TimeSpan time)
        {
            return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
        }

        #endregion

        /// <summary>
        /// How far a frame's time may be from the clock before the clock is set by it again: a stream whose times jump -
        /// one that starts over, a live one the sender's clock has drifted from - is followed rather than frozen on, or
        /// raced through.
        /// </summary>
        private static readonly long ResyncThreshold = TimeSpan.FromSeconds(1).Ticks;

        // The playback clock: the stopwatch since the first frame shown, against the frames' own times since that one's, at
        // the rate played - backwards, the clock runs down.
        private long _clockFrameTime = -1;
        private long _clockStart;

        /// <summary>The time of the frame shown last; -1 before the first.</summary>
        private long _lastShownTime = -1;

        /// <summary>How late after the sound a frame may be shown: later, it is let go of, for the video to catch up.</summary>
        private static readonly long LateFrame = TimeSpan.FromMilliseconds(100).Ticks;
        private const int MaxLateFramesDropped = 10;
        private int _lateFramesDropped;

        // The sound's clock: the time of the first frame of sound played since the device was reset or made, and how many
        // bytes it plays a second - its position, in bytes, is how far on from that frame it is. -1 where the sound has
        // no time: of a source that gives none, or none played yet.
        private long _audioClockStart = -1;
        private long _audioBytesPerSecond;

        /// <summary>
        /// The time of the sound the device is playing, on the clock of the video's frames: -1 where none of a time plays -
        /// paused, sought and not yet playing, played at another rate than 1, or all of it played.
        /// </summary>
        private long AudioClock()
        {
            if (IsHalted || _rate != 1 || _audioHold)
                return -1;

            lock (_waveSync)
            {
                if (_waveOut == null || _audioClockStart < 0 || _audioBytesPerSecond <= 0 || _waveOut.QueuedFrames <= 0)
                    return -1;
                try
                {
                    return _audioClockStart + _waveOut.GetPosition() * TimeSpan.TicksPerSecond / _audioBytesPerSecond;
                }
                catch (Exception)
                {
                    return -1; // a device that does not tell its position in bytes
                }
            }
        }

        private void CompositionTarget_Rendering(object sender, EventArgs e)
        {
            if (_canvas == null)
                return;

            (byte[] Frame, long Timestamp, long Request) next;
            long request = Interlocked.Read(ref _request);
            while (true)
            {
                if (!_videoOut.TryPeek(out next))
                    return;
                if (next.Request >= request)
                    break;
                // of before a seek
                if (_videoOut.TryDequeue(out var stale))
                    _source.ReturnVideoSample(stale.Frame);
                _videoWake.Set();
            }

            // The first frame of a seek sets the clock: at the time sought, of a file - the key frame after it, played fast,
            // is shown when the clock gets to it - else at the frame's own. It is shown even paused: it is what was sought to.
            bool first = next.Request != _shownRequest;
            if (next.Request != _anchoredRequest && next.Timestamp >= 0)
            {
                _anchoredRequest = next.Request;
                _clockFrameTime = SeekableSource != null && _seekTime >= 0 ? _seekTime : next.Timestamp;
                _clockStart = _stopwatch.Elapsed.Ticks;
            }
            if (_paused && !first)
                return;

            int rate = _rate;
            if (next.Timestamp >= 0 && !(first && _paused))
            {
                // A frame of before the one shown - which a decoder that joined a stream between its key frames can hand
                // out late - is dropped rather than shown out of order; a jump back as far as a resync is followed.
                long behind = rate > 0 ? _lastShownTime - next.Timestamp : next.Timestamp - _lastShownTime;
                if (!first && _lastShownTime >= 0 && behind > 0 && behind <= ResyncThreshold)
                {
                    if (_videoOut.TryDequeue(out var late))
                        _source.ReturnVideoSample(late.Frame);
                    _videoWake.Set();
                    return;
                }

                // Each frame is shown at its own time, as the source gives it - of the file's samples, of the RTP
                // timestamps, of the capture - against the clock, at the rate played. Where sound plays, of a time, the
                // clock is the sound's: what of it the device has played. The stopwatch carries on from there where the
                // sound stops - at its end, or the decode thread held up - and sound playing again takes the clock back.
                long now = _stopwatch.Elapsed.Ticks;
                long audio = AudioClock();
                if (audio >= 0)
                {
                    _clockFrameTime = audio;
                    _clockStart = now;
                }
                long clock = _clockFrameTime + (now - _clockStart) * rate;
                long ahead = (next.Timestamp - clock) * Math.Sign(rate);

                // Behind the sound, frames are let go of until one is in time - as many as are behind, but never so many in
                // a row that a decoder slower than the video shows nothing.
                if (audio >= 0 && -ahead > LateFrame && _lateFramesDropped < MaxLateFramesDropped)
                {
                    if (_videoOut.TryDequeue(out var behindSound))
                        _source.ReturnVideoSample(behindSound.Frame);
                    _videoWake.Set();
                    _lateFramesDropped++;
                    return;
                }
                _lateFramesDropped = 0;

                // A stream's times may jump either way, and are followed. A file's are its own - key frames alone, played
                // fast, are seconds apart - and a frame ahead of the clock is waited for: only a clock run ahead of frames
                // decoded too slowly is set again.
                long off = SeekableSource == null ? Math.Abs(ahead) : -ahead;
                if (_clockFrameTime < 0 || off > ResyncThreshold * Math.Abs(rate))
                {
                    _clockFrameTime = next.Timestamp;
                    _clockStart = now;
                }
                else if (ahead > 0)
                {
                    return;
                }

                if (Log.InfoEnabled) Log.Info($"Video {next.Timestamp / (double)TimeSpan.TicksPerSecond}, clock {clock / (double)TimeSpan.TicksPerSecond}");
            }

            if (!_videoOut.TryDequeue(out next))
                return;

            byte[] decoded = next.Frame;
            _videoFrames++;
            _shownRequest = next.Request;
            if (next.Timestamp >= 0)
                Interlocked.Exchange(ref _lastShownTime, next.Timestamp);
            if (first && _audioHold)
            {
                _audioHold = false;
                _audioWake.Set();
            }
            _videoWake.Set(); // room for the next frame

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

            UpdatePosition();
        }

        private async Task InitializeVideo(IVideoSource videoSource)
        {
            InitializeSource(videoSource);

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
                        if (IsHalted)
                            this._stopwatch.Reset();
                        else
                            this._stopwatch.Restart();

                        var seekable = SeekableSource;
                        Duration = seekable != null && seekable.Duration > 0 ? TimeSpan.FromTicks(seekable.Duration) : TimeSpan.Zero;
                        UpdateControls();
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


        #region Decode threads

        // Each control decodes on two threads of its own, the video's and the sound's: neither waits for the other, nor
        // for another control - a camera started, a file opened, a seek decoded through a long group of pictures. Each
        // sleeps until there is room for what it makes - a frame shown, a buffer of sound played - or something is asked
        // of it, and looks again after IdleWait where its source had nothing ready.
        private Thread _videoThread;
        private Thread _audioThread;
        private readonly AutoResetEvent _videoWake = new AutoResetEvent(false);
        private readonly AutoResetEvent _audioWake = new AutoResetEvent(false);
        private StopFlag _stop;

        /// <summary>Asks a pair of threads to end: each pair has its own, so threads started again never run beside old ones.</summary>
        private sealed class StopFlag
        {
            public volatile bool Requested;
        }
        private const int IdleWait = 5;

        // A source is initialized by one thread at a time: both need it, and its initialization reads the file, or
        // connects, once.
        private readonly object _initLock = new object();

        private void InitializeSource(object source)
        {
            lock (_initLock)
            {
                if (source is IVideoSource video)
                    video.InitializeAsync().GetAwaiter().GetResult();
                else if (source is IAudioSource audio)
                    audio.InitializeAsync().GetAwaiter().GetResult();
            }
        }

        private void StartThreads()
        {
            if (_videoThread != null)
                return;

            var stop = _stop = new StopFlag();
            _videoThread = new Thread(() => VideoLoop(stop)) { IsBackground = true, Name = "VideoControl video" };
            _audioThread = new Thread(() => AudioLoop(stop)) { IsBackground = true, Name = "VideoControl audio" };
            _videoThread.Start();
            _audioThread.Start();
        }

        /// <summary>Ends the threads, without waiting for them: what they are doing may need the UI thread.</summary>
        private void StopThreads()
        {
            if (_stop != null)
                _stop.Requested = true;
            _videoThread = null;
            _audioThread = null;
            Wake();
        }

        private void Wake()
        {
            _videoWake.Set();
            _audioWake.Set();
        }

        private void VideoLoop(StopFlag stop)
        {
            while (!stop.Requested)
            {
                bool worked = false;
                try
                {
                    if (!_hidden && _source is IVideoSource videoSource)
                        worked = DecodeVideoPass(videoSource);
                }
                catch (Exception ex)
                {
                    if (Log.ErrorEnabled) Log.Error(ex.Message, ex);
                }

                if (!worked)
                    _videoWake.WaitOne(IdleWait);
            }
        }

        private void AudioLoop(StopFlag stop)
        {
            while (!stop.Requested)
            {
                bool worked = false;
                try
                {
                    if (!_hidden && _source is IAudioSource audioSource)
                        worked = DecodeAudioPass(audioSource);
                }
                catch (Exception ex)
                {
                    if (Log.ErrorEnabled) Log.Error(ex.Message, ex);
                }

                if (!worked)
                    _audioWake.WaitOne(IdleWait);
            }
        }

        /// <returns>Whether a frame was made, or let go of: there may be more to do at once.</returns>
        private bool DecodeVideoPass(IVideoSource videoSource)
        {
            var seekable = SeekableSource;
            if (_canvas == null)
            {
                InitializeVideo(videoSource).GetAwaiter().GetResult();
                if (_canvas == null)
                    return false;
            }

            // the frames ran out: nothing more to decode, until a seek
            bool ended = seekable != null && Interlocked.Read(ref _endedRequest) == Interlocked.Read(ref _request);

            if (!ended && _videoOut.Count < 1)
            {
                var sample = videoSource.GetVideoSample(out long timestamp);
                if (sample == null)
                {
                    if (seekable != null)
                        OnEnded(seekable.Request);
                    else
                        UninitializeVideo(videoSource);
                    return false;
                }

                if (sample.Length == 0)
                    return false; // none ready yet

                // read after the frame: a seek is done before the frame after it is handed out, on this thread
                long request = seekable?.Request ?? 0;
                _videoOut.Enqueue((sample, timestamp, request));
                return true;
            }
            else if (_paused && seekable == null && _videoOut.Count >= 1)
            {
                // live, paused: what comes is let go of, to show what is live again on play
                if (_videoOut.TryDequeue(out var dropped))
                    videoSource.ReturnVideoSample(dropped.Frame);
                return true;
            }

            return false;
        }

        /// <returns>Whether sound was queued, or let go of: there may be more to do at once.</returns>
        private bool DecodeAudioPass(IAudioSource audioSource)
        {
            if (_waveOut == null)
            {
                InitializeAudio(audioSource);
                if (_waveOut == null)
                    return false;
            }

            if (_audioReset)
            {
                _audioReset = false;
                lock (_waveSync)
                {
                    if (_waveOut != null)
                    {
                        _waveOut.Reset();
                        _audioClockStart = -1;
                        if (IsHalted)
                            _waveOut.Pause();
                        else
                            _waveOut.Resume();
                    }
                }
            }

            // a source of no video has no frame for the sound to wait for
            if (_audioHold && (_source as IVideoSource)?.VideoInfo == null)
                _audioHold = false;

            // as much sound as the queue is short of, or, live and paused, whatever comes, to let go of
            bool live = SeekableSource == null;
            bool worked = false;
            while (_waveOut != null && !_audioHold && (IsAudioQueueShort() || (live && _paused)))
            {
                if (!DecodeAudio(audioSource, drop: live && _paused, out bool queued))
                {
                    UninitializeAudio(audioSource);
                    return false;
                }
                if (!queued)
                    break; // none ready yet
                worked = true;
            }
            return worked;
        }

        #endregion

        private Task InitializeAudio(IAudioSource audioSource)
        {
            InitializeSource(audioSource);

            var audioInfo = audioSource.AudioInfo;
            if (audioInfo != null)
            {
                lock (_waveSync)
                {
                    this._waveOut = new WaveOut();
                    this._waveOut.Initialize(audioInfo.SampleRate, audioInfo.ChannelCount, audioInfo.BitsPerSample);
                    this._audioClockStart = -1;
                    this._audioBytesPerSecond = (long)audioInfo.SampleRate * audioInfo.ChannelCount * audioInfo.BitsPerSample / 8;
                    // a buffer played is room for the next
                    this._waveOut.OnPlaybackCompleted += (s, e) => _audioWake.Set();
                    if (IsHalted)
                        this._waveOut.Pause();
               }
            }

            return Task.CompletedTask;
        }

        private Task UninitializeAudio(IAudioSource audioSource)
        {
            lock (_waveSync)
            {
                this._waveOut.Dispose();
                this._waveOut = null;
                this._audioClockStart = -1;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// How much sound is kept queued to play: what the decode thread, held up by a frame of video, or the machine, by
        /// another thread, may be late by without the sound stopping. It costs no delay - a seek resets the device, a
        /// pause pauses it - only memory.
        /// </summary>
        private static readonly long AudioQueueTime = TimeSpan.FromMilliseconds(300).Ticks;

        /// <summary>How long the last frame of sound queued plays for: what the queue's frames are counted in.</summary>
        private long _audioFrameTime;

        private bool IsAudioQueueShort()
        {
            int frames = _audioFrameTime > 0 ? (int)Math.Clamp(AudioQueueTime / _audioFrameTime, 2, 100) : 5;
            return _waveOut.QueuedFrames < frames;
        }

        /// <param name="drop">Whether to let the sound go rather than play it: live, and paused.</param>
        /// <param name="queued">Whether a frame of sound was had, to play or to let go of: none is ready where it was not.</param>
        private bool DecodeAudio(IAudioSource audioSource, bool drop, out bool queued)
        {
            queued = false;
            var sample = audioSource.GetAudioSample(out long timestamp);
            if (sample != null)
            {
                if (sample.Length > 0)
                {
                    queued = true;
                    var audioInfo = audioSource.AudioInfo;
                    long bytesPerSecond = audioInfo == null ? 0 : (long)audioInfo.SampleRate * audioInfo.ChannelCount * audioInfo.BitsPerSample / 8;
                    if (bytesPerSecond > 0)
                        _audioFrameTime = sample.Length * TimeSpan.TicksPerSecond / bytesPerSecond;

                    if (!drop)
                    {
                        if (_isMute)
                        {
                            Array.Fill<byte>(sample, 0);
                        }

                        lock (_waveSync)
                        {
                            if (_waveOut != null)
                            {
                                // the first frame since the device was reset is where its position counts from
                                if (_audioClockStart < 0 && _waveOut.QueuedFrames == 0 && timestamp >= 0)
                                    _audioClockStart = timestamp;
                                _waveOut.Enqueue(sample, (uint)sample.Length);
                            }
                        }
                        Interlocked.Increment(ref _audioFrames);
                    }
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
            Interlocked.Exchange(ref _lastShownTime, -1);
            _shownRequest = -1;
            _anchoredRequest = -1;
            _seekTime = -1;
            Interlocked.Exchange(ref _endedRequest, -1);
            Interlocked.Exchange(ref _request, _source is ISeekableVideoSource seekable ? seekable.Request : 0);
            _rate = 1;
            PlaybackRate = 1;

            // The sound waits for the first frame shown, as after a seek: what comes before it - the decoder made, the
            // first frames decoded - holds the decode thread up longer than the sound queued plays.
            _audioHold = true;
            if (!IsHalted)
                _stopwatch.Restart();
            else
                _stopwatch.Reset();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                }

                StopThreads();

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
