using SharpMediaFoundationInterop.Wave;
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
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// What a video control does whatever it draws with: its properties, its threads, its clock, trick play and its bar of
    /// controls. Drawing a frame is left to the control made of it - <see cref="VideoControl"/> into a bitmap,
    /// <see cref="VideoControlD3D"/> with Direct3D - which makes the surface the frames are drawn on, draws each, and says
    /// what frames it takes.
    /// </summary>
    /// <remarks>
    /// <para>The bar of controls is the template's, and styled or templated from XAML as any control's is. Its buttons are
    /// whatever sends the control one of WPF's <see cref="MediaCommands"/> - <see cref="MediaCommands.TogglePlayPause"/>,
    /// <see cref="MediaCommands.Play"/>, <see cref="MediaCommands.Pause"/>, <see cref="MediaCommands.Rewind"/>,
    /// <see cref="MediaCommands.FastForward"/>, <see cref="MediaCommands.MuteVolume"/> - from within the template or
    /// without. What they show is of the control's properties: <see cref="IsPlaying"/>, <see cref="CanSeek"/>,
    /// <see cref="PlaybackRate"/>, <see cref="Position"/>, <see cref="Duration"/>, <see cref="TimeText"/>.</para>
    /// <para>The bar is shown as the mouse moves over the video, and hidden again a while after it stops, as it leaves the
    /// video, or never - see <see cref="AutoHideControls"/>: <see cref="AreControlsVisible"/> says which, and the template
    /// is put in the visual state <c>ControlsVisible</c> or <c>ControlsHidden</c> of the group <c>ControlsStates</c>.</para>
    /// <para>Parts of the template, each optional: <c>PART_Image</c>, the image frames are drawn on; <c>PART_Seek</c>, a
    /// slider of the position, which seeks; <c>PART_ControlsBar</c>, the bar, kept shown while the mouse is over it.</para>
    /// <para>Subtitles are the template's too: the text shown now is <see cref="SubtitleText"/>, shown twice - over each
    /// eye's view - where <see cref="ShowsSideBySide"/>; the default template draws it with the data template
    /// <see cref="SubtitleTemplateKey"/>.</para>
    /// </remarks>
    [TemplatePart(Name = "PART_Image", Type = typeof(Image))]
    [TemplatePart(Name = "PART_Seek", Type = typeof(Slider))]
    [TemplatePart(Name = "PART_ControlsBar", Type = typeof(FrameworkElement))]
    [TemplateVisualState(GroupName = ControlsStates, Name = ControlsVisibleState)]
    [TemplateVisualState(GroupName = ControlsStates, Name = ControlsHiddenState)]
    public abstract class VideoControlBase : Control, IDisposable
    {
        private object _waveSync = new object();
        private WaveOut _waveOut;

        private const string ControlsStates = "ControlsStates";
        private const string ControlsVisibleState = "ControlsVisible";
        private const string ControlsHiddenState = "ControlsHidden";

        /// <summary>
        /// The style of the default template's buttons, to base another on:
        /// <c>BasedOn="{StaticResource {x:Static local:VideoControlBase.ButtonStyleKey}}"</c>.
        /// </summary>
        public static ComponentResourceKey ButtonStyleKey { get; } = new ComponentResourceKey(typeof(VideoControlBase), "ButtonStyle");

        /// <summary>
        /// The style of the default template's seek bar - a thin track of the accent colour, its Foreground, and a round
        /// thumb with a dot in it - to base another on: <c>BasedOn="{StaticResource {x:Static local:VideoControlBase.SeekSliderStyleKey}}"</c>.
        /// </summary>
        public static ComponentResourceKey SeekSliderStyleKey { get; } = new ComponentResourceKey(typeof(VideoControlBase), "SeekSliderStyle");

        /// <summary>
        /// The data template the default template draws a subtitle with, its text the content: to draw them otherwise, one of
        /// this key in the application's resources.
        /// </summary>
        public static ComponentResourceKey SubtitleTemplateKey { get; } = new ComponentResourceKey(typeof(VideoControlBase), "SubtitleTemplate");

        private Image _image;
        private Slider _seekSlider;
        private FrameworkElement _controlsBar;

        private Stopwatch _stopwatch = new Stopwatch();

        private long _videoFrames = 0;
        private long _audioFrames = 0;

        /// <summary>
        /// Decoded frames waiting to be shown, each with its time - -1 to be shown as it comes - the number of the seek it is
        /// of, which a later seek leaves it behind, and when it was queued, on the clock's stopwatch.
        /// </summary>
        private ConcurrentQueue<(object Frame, long Timestamp, long Request, long Queued)> _videoOut = new ConcurrentQueue<(object Frame, long Timestamp, long Request, long Queued)>();

        private bool _disposedValue;

        private IVideoSource _source = null;

        public IVideoSource Source
        {
            get { return (IVideoSource)GetValue(SourceProperty); }
            set { SetValue(SourceProperty, value); }
        }

        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register("Source", typeof(IVideoSource), typeof(VideoControlBase), new PropertyMetadata(null, OnSourceChanged));

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var sender = (VideoControlBase)d;
            var source = e.NewValue as IVideoSource;
            sender._source = source;
            if (source != null && sender.AutoPlay)
            {
                sender.StartPlaying();
            }
            // of the source before, none: those of this one as it is initialized
            sender.UpdateSubtitleTracks();
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
            DependencyProperty.Register("Looping", typeof(bool), typeof(VideoControlBase), new PropertyMetadata(false, OnIsLoopingChanged));

        private static void OnIsLoopingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var sender = d as VideoControlBase;
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
            DependencyProperty.Register("AutoPlay", typeof(bool), typeof(VideoControlBase), new PropertyMetadata(true));

        public bool Mute
        {
            get { return (bool)GetValue(MuteProperty); }
            set { SetValue(MuteProperty, value); }
        }

        public static readonly DependencyProperty MuteProperty =
            DependencyProperty.Register("Mute", typeof(bool), typeof(VideoControlBase), new PropertyMetadata(false, OnMuteChanged));

        private static void OnMuteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var sender = d as VideoControlBase;
            if (sender != null)
            {
                sender._isMute = (bool)e.NewValue;
            }
        }

        #region Trick play properties

        /// <summary>Whether the bar of buttons, the seek bar and the time are shown over the bottom of the video at all.</summary>
        public bool ShowControls
        {
            get { return (bool)GetValue(ShowControlsProperty); }
            set { SetValue(ShowControlsProperty, value); }
        }

        public static readonly DependencyProperty ShowControlsProperty =
            DependencyProperty.Register("ShowControls", typeof(bool), typeof(VideoControlBase), new PropertyMetadata(true, OnControlsShownChanged));

        /// <summary>
        /// Whether the bar is shown only while the mouse moves over the video, or a key is pressed, and hidden
        /// <see cref="ControlsHideDelay"/> after - or as the mouse leaves - as a player's is; paused, it stays. If not, it is
        /// shown all the time.
        /// </summary>
        public bool AutoHideControls
        {
            get { return (bool)GetValue(AutoHideControlsProperty); }
            set { SetValue(AutoHideControlsProperty, value); }
        }

        public static readonly DependencyProperty AutoHideControlsProperty =
            DependencyProperty.Register("AutoHideControls", typeof(bool), typeof(VideoControlBase), new PropertyMetadata(true, OnControlsShownChanged));

        /// <summary>How long the bar stays after the mouse stops moving over the video.</summary>
        public TimeSpan ControlsHideDelay
        {
            get { return (TimeSpan)GetValue(ControlsHideDelayProperty); }
            set { SetValue(ControlsHideDelayProperty, value); }
        }

        public static readonly DependencyProperty ControlsHideDelayProperty =
            DependencyProperty.Register("ControlsHideDelay", typeof(TimeSpan), typeof(VideoControlBase), new PropertyMetadata(TimeSpan.FromSeconds(2.5)));

        /// <summary>Whether the bar is shown now: for a template's triggers, as the visual states say it.</summary>
        public bool AreControlsVisible
        {
            get { return (bool)GetValue(AreControlsVisibleProperty); }
            private set { SetValue(AreControlsVisiblePropertyKey, value); }
        }

        private static readonly DependencyPropertyKey AreControlsVisiblePropertyKey =
            DependencyProperty.RegisterReadOnly("AreControlsVisible", typeof(bool), typeof(VideoControlBase), new PropertyMetadata(false));
        public static readonly DependencyProperty AreControlsVisibleProperty = AreControlsVisiblePropertyKey.DependencyProperty;

        private static void OnControlsShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((VideoControlBase)d).UpdateControlsVisibility(true);
        }

        public bool IsPaused
        {
            get { return (bool)GetValue(IsPausedProperty); }
            set { SetValue(IsPausedProperty, value); }
        }

        public static readonly DependencyProperty IsPausedProperty =
            DependencyProperty.Register("IsPaused", typeof(bool), typeof(VideoControlBase), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsPausedChanged));

        /// <summary>
        /// Whether the video plays forwards at 1x: what a play/pause button pauses - paused, or played at another rate, it
        /// plays.
        /// </summary>
        public bool IsPlaying
        {
            get { return (bool)GetValue(IsPlayingProperty); }
            private set { SetValue(IsPlayingPropertyKey, value); }
        }

        private static readonly DependencyPropertyKey IsPlayingPropertyKey =
            DependencyProperty.RegisterReadOnly("IsPlaying", typeof(bool), typeof(VideoControlBase), new PropertyMetadata(false));
        public static readonly DependencyProperty IsPlayingProperty = IsPlayingPropertyKey.DependencyProperty;

        private static void OnIsPausedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((VideoControlBase)d).SetPaused((bool)e.NewValue);
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
            DependencyProperty.RegisterReadOnly("PlaybackRate", typeof(int), typeof(VideoControlBase), new PropertyMetadata(1));
        public static readonly DependencyProperty PlaybackRateProperty = PlaybackRatePropertyKey.DependencyProperty;

        /// <summary>The time of the frame shown, from the start of the video.</summary>
        public TimeSpan Position
        {
            get { return (TimeSpan)GetValue(PositionProperty); }
            private set { SetValue(PositionPropertyKey, value); }
        }

        private static readonly DependencyPropertyKey PositionPropertyKey =
            DependencyProperty.RegisterReadOnly("Position", typeof(TimeSpan), typeof(VideoControlBase), new PropertyMetadata(TimeSpan.Zero));
        public static readonly DependencyProperty PositionProperty = PositionPropertyKey.DependencyProperty;

        /// <summary>How long the video is; zero where it is not known - a live stream.</summary>
        public TimeSpan Duration
        {
            get { return (TimeSpan)GetValue(DurationProperty); }
            private set { SetValue(DurationPropertyKey, value); }
        }

        private static readonly DependencyPropertyKey DurationPropertyKey =
            DependencyProperty.RegisterReadOnly("Duration", typeof(TimeSpan), typeof(VideoControlBase), new PropertyMetadata(TimeSpan.Zero));
        public static readonly DependencyProperty DurationProperty = DurationPropertyKey.DependencyProperty;

        /// <summary>Whether the video can be sought in, and played at other rates: a file can, a live stream cannot.</summary>
        public bool CanSeek
        {
            get { return (bool)GetValue(CanSeekProperty); }
            private set { SetValue(CanSeekPropertyKey, value); }
        }

        private static readonly DependencyPropertyKey CanSeekPropertyKey =
            DependencyProperty.RegisterReadOnly("CanSeek", typeof(bool), typeof(VideoControlBase), new PropertyMetadata(false));
        public static readonly DependencyProperty CanSeekProperty = CanSeekPropertyKey.DependencyProperty;

        /// <summary>
        /// The position, the duration where it is known and the rate where it is not 1, as the bar shows them:
        /// <c>1:23 / 4:56  ×2</c>.
        /// </summary>
        public string TimeText
        {
            get { return (string)GetValue(TimeTextProperty); }
            private set { SetValue(TimeTextPropertyKey, value); }
        }

        private static readonly DependencyPropertyKey TimeTextPropertyKey =
            DependencyProperty.RegisterReadOnly("TimeText", typeof(string), typeof(VideoControlBase), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty TimeTextProperty = TimeTextPropertyKey.DependencyProperty;

        #endregion

        #region Stereo and spherical view properties

        /// <summary>Which eye's view of a stereo video is shown: both, as the frame has them, or one alone.</summary>
        public EyeView EyeView
        {
            get { return (EyeView)GetValue(EyeViewProperty); }
            set { SetValue(EyeViewProperty, value); }
        }

        public static readonly DependencyProperty EyeViewProperty =
            DependencyProperty.Register("EyeView", typeof(EyeView), typeof(VideoControlBase),
                new FrameworkPropertyMetadata(EyeView.Both, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnViewChanged));

        /// <summary>
        /// How the frames hold the eyes' views, where the source does not say, or says otherwise; null, as it is by default,
        /// for what the source says - see <see cref="VideoInfo.StereoLayout"/>.
        /// </summary>
        public StereoLayout? StereoLayout
        {
            get { return (StereoLayout?)GetValue(StereoLayoutProperty); }
            set { SetValue(StereoLayoutProperty, value); }
        }

        public static readonly DependencyProperty StereoLayoutProperty =
            DependencyProperty.Register("StereoLayout", typeof(StereoLayout?), typeof(VideoControlBase), new PropertyMetadata(null, OnViewChanged));

        /// <summary>
        /// What each eye's picture is of, where the source does not say, or says otherwise; null, as it is by default, for
        /// what the source says - see <see cref="VideoInfo.Projection"/>. An equirectangular video the source says nothing of
        /// is taken to be of 180 degrees, as a VR180 camera's is.
        /// </summary>
        public VideoProjection? Projection
        {
            get { return (VideoProjection?)GetValue(ProjectionProperty); }
            set { SetValue(ProjectionProperty, value); }
        }

        public static readonly DependencyProperty ProjectionProperty =
            DependencyProperty.Register("Projection", typeof(VideoProjection?), typeof(VideoControlBase), new PropertyMetadata(null, OnViewChanged));

        /// <summary>Of a spherical video, degrees to the right of straight ahead the view looks: dragged with the mouse.</summary>
        public double Yaw
        {
            get { return (double)GetValue(YawProperty); }
            set { SetValue(YawProperty, value); }
        }

        public static readonly DependencyProperty YawProperty =
            DependencyProperty.Register("Yaw", typeof(double), typeof(VideoControlBase),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnViewChanged));

        /// <summary>Of a spherical video, degrees up from straight ahead the view looks, -90 to 90: dragged with the mouse.</summary>
        public double Pitch
        {
            get { return (double)GetValue(PitchProperty); }
            set { SetValue(PitchProperty, value); }
        }

        public static readonly DependencyProperty PitchProperty =
            DependencyProperty.Register("Pitch", typeof(double), typeof(VideoControlBase),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnViewChanged, CoercePitch));

        private static object CoercePitch(DependencyObject d, object value) => Math.Clamp((double)value, -90.0, 90.0);

        /// <summary>Of a spherical video, degrees across each eye's view, 20 to 150: zoomed with the mouse wheel.</summary>
        public double FieldOfView
        {
            get { return (double)GetValue(FieldOfViewProperty); }
            set { SetValue(FieldOfViewProperty, value); }
        }

        public static readonly DependencyProperty FieldOfViewProperty =
            DependencyProperty.Register("FieldOfView", typeof(double), typeof(VideoControlBase),
                new FrameworkPropertyMetadata(DefaultFieldOfView, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnViewChanged, CoerceFieldOfView));

        private const double DefaultFieldOfView = 90;

        private static object CoerceFieldOfView(DependencyObject d, object value) => Math.Clamp((double)value, 20.0, 150.0);

        /// <summary>Whether the video is of two eyes' views: what an eye can be chosen of.</summary>
        public bool IsStereo
        {
            get { return (bool)GetValue(IsStereoProperty); }
            private set { SetValue(IsStereoPropertyKey, value); }
        }

        private static readonly DependencyPropertyKey IsStereoPropertyKey =
            DependencyProperty.RegisterReadOnly("IsStereo", typeof(bool), typeof(VideoControlBase), new PropertyMetadata(false));
        public static readonly DependencyProperty IsStereoProperty = IsStereoPropertyKey.DependencyProperty;

        /// <summary>
        /// Whether the video is shown as a view into a sphere, looked around with the mouse: a spherical video, in a control
        /// that draws one - <see cref="VideoControlD3D"/>; <see cref="VideoControl"/> shows it flat.
        /// </summary>
        public bool IsSpherical
        {
            get { return (bool)GetValue(IsSphericalProperty); }
            private set { SetValue(IsSphericalPropertyKey, value); }
        }

        private static readonly DependencyPropertyKey IsSphericalPropertyKey =
            DependencyProperty.RegisterReadOnly("IsSpherical", typeof(bool), typeof(VideoControlBase), new PropertyMetadata(false));
        public static readonly DependencyProperty IsSphericalProperty = IsSphericalPropertyKey.DependencyProperty;

        /// <summary>Shows the next eye's view: both, then the left, then the right. The E key, over the video.</summary>
        public static RoutedUICommand NextEyeViewCommand { get; } =
            new RoutedUICommand("Next eye", "NextEyeView", typeof(VideoControlBase));

        /// <summary>Looks straight ahead again, at the field of view a spherical video starts at.</summary>
        public static RoutedUICommand ResetViewCommand { get; } =
            new RoutedUICommand("Reset view", "ResetView", typeof(VideoControlBase));

        private static void OnViewChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((VideoControlBase)d).OnViewChanged(e.Property);
        }

        /// <summary>
        /// Whether what is shown is two eyes' views side by side - a stereo frame side by side, both eyes, or both eyes'
        /// views of a spherical video: what is shown over the video, a subtitle, is shown over each.
        /// </summary>
        public bool ShowsSideBySide
        {
            get { return (bool)GetValue(ShowsSideBySideProperty); }
            private set { SetValue(ShowsSideBySidePropertyKey, value); }
        }

        private static readonly DependencyPropertyKey ShowsSideBySidePropertyKey =
            DependencyProperty.RegisterReadOnly("ShowsSideBySide", typeof(bool), typeof(VideoControlBase), new PropertyMetadata(false));
        public static readonly DependencyProperty ShowsSideBySideProperty = ShowsSideBySidePropertyKey.DependencyProperty;

        #endregion

        #region Subtitle properties

        /// <summary>
        /// The track of subtitles shown, of <see cref="SubtitleTracks"/>; -1, as it is by default, for none - but a forced
        /// track, which is shown whether subtitles are on or not.
        /// </summary>
        public int SubtitleTrack
        {
            get { return (int)GetValue(SubtitleTrackProperty); }
            set { SetValue(SubtitleTrackProperty, value); }
        }

        public static readonly DependencyProperty SubtitleTrackProperty =
            DependencyProperty.Register("SubtitleTrack", typeof(int), typeof(VideoControlBase),
                new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSubtitleTrackChanged));

        private static void OnSubtitleTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((VideoControlBase)d).LoadSubtitles();
        }

        /// <summary>The tracks of subtitles the source has: of a file, its own and those of files beside it.</summary>
        public IReadOnlyList<SubtitleTrackInfo> SubtitleTracks
        {
            get { return (IReadOnlyList<SubtitleTrackInfo>)GetValue(SubtitleTracksProperty); }
            private set { SetValue(SubtitleTracksPropertyKey, value); }
        }

        private static readonly DependencyPropertyKey SubtitleTracksPropertyKey =
            DependencyProperty.RegisterReadOnly("SubtitleTracks", typeof(IReadOnlyList<SubtitleTrackInfo>), typeof(VideoControlBase),
                new PropertyMetadata(Array.Empty<SubtitleTrackInfo>()));
        public static readonly DependencyProperty SubtitleTracksProperty = SubtitleTracksPropertyKey.DependencyProperty;

        /// <summary>Whether the source has subtitles to choose.</summary>
        public bool HasSubtitles
        {
            get { return (bool)GetValue(HasSubtitlesProperty); }
            private set { SetValue(HasSubtitlesPropertyKey, value); }
        }

        private static readonly DependencyPropertyKey HasSubtitlesPropertyKey =
            DependencyProperty.RegisterReadOnly("HasSubtitles", typeof(bool), typeof(VideoControlBase), new PropertyMetadata(false));
        public static readonly DependencyProperty HasSubtitlesProperty = HasSubtitlesPropertyKey.DependencyProperty;

        /// <summary>The subtitle shown now, of the frame shown: empty where there is none. Lines apart by line breaks.</summary>
        public string SubtitleText
        {
            get { return (string)GetValue(SubtitleTextProperty); }
            private set { SetValue(SubtitleTextPropertyKey, value); }
        }

        private static readonly DependencyPropertyKey SubtitleTextPropertyKey =
            DependencyProperty.RegisterReadOnly("SubtitleText", typeof(string), typeof(VideoControlBase), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty SubtitleTextProperty = SubtitleTextPropertyKey.DependencyProperty;

        /// <summary>Shows the next track of subtitles: none, then each track, then none again. The C key, over the video.</summary>
        public static RoutedUICommand NextSubtitleTrackCommand { get; } =
            new RoutedUICommand("Next subtitles", "NextSubtitleTrack", typeof(VideoControlBase));

        #endregion

        static VideoControlBase()
        {
            // one template for every control made of this: each is styled as this, the drawing of frames aside
            DefaultStyleKeyProperty.OverrideMetadata(typeof(VideoControlBase), new FrameworkPropertyMetadata(typeof(VideoControlBase)));
        }

        protected VideoControlBase()
        {
            Loaded += VideoControl_Loaded;
            Unloaded += VideoControl_Unloaded;
            IsVisibleChanged += VideoControl_IsVisibleChanged;
            CompositionTarget.Rendering += CompositionTarget_Rendering;

            _hideTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
            _hideTimer.Tick += HideTimer_Tick;

            // the buttons of any template, or of none: WPF's media commands, routed to the control
            CommandBindings.Add(new CommandBinding(MediaCommands.TogglePlayPause, (s, e) => TogglePlayPause()));
            CommandBindings.Add(new CommandBinding(MediaCommands.Play, (s, e) => Play()));
            CommandBindings.Add(new CommandBinding(MediaCommands.Pause, (s, e) => Pause()));
            CommandBindings.Add(new CommandBinding(MediaCommands.Rewind, (s, e) => Rewind(), CanSeekCommand));
            CommandBindings.Add(new CommandBinding(MediaCommands.FastForward, (s, e) => FastForward(), CanSeekCommand));
            CommandBindings.Add(new CommandBinding(MediaCommands.MuteVolume, (s, e) => Mute = !Mute));
            CommandBindings.Add(new CommandBinding(NextEyeViewCommand, (s, e) => EyeView = EyeView == EyeView.Both ? EyeView.Left : EyeView == EyeView.Left ? EyeView.Right : EyeView.Both,
                (s, e) => e.CanExecute = IsStereo));
            CommandBindings.Add(new CommandBinding(ResetViewCommand, (s, e) => ResetView(), (s, e) => e.CanExecute = IsSpherical));
            CommandBindings.Add(new CommandBinding(NextSubtitleTrackCommand,
                (s, e) => SubtitleTrack = SubtitleTrack + 1 >= SubtitleTracks.Count ? -1 : SubtitleTrack + 1,
                (s, e) => e.CanExecute = HasSubtitles));
        }

        private void CanSeekCommand(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = SeekableSource != null;
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

            if (_seekSlider != null)
            {
                _seekSlider.ValueChanged -= SeekSlider_ValueChanged;
                _seekSlider.RemoveHandler(Thumb.DragStartedEvent, (DragStartedEventHandler)SeekSlider_DragStarted);
                _seekSlider.RemoveHandler(Thumb.DragCompletedEvent, (DragCompletedEventHandler)SeekSlider_DragCompleted);
            }

            _seekSlider = this.Template.FindName("PART_Seek", this) as Slider;
            _controlsBar = this.Template.FindName("PART_ControlsBar", this) as FrameworkElement;

            if (_seekSlider != null)
            {
                _seekSlider.ValueChanged += SeekSlider_ValueChanged;
                _seekSlider.AddHandler(Thumb.DragStartedEvent, (DragStartedEventHandler)SeekSlider_DragStarted);
                _seekSlider.AddHandler(Thumb.DragCompletedEvent, (DragCompletedEventHandler)SeekSlider_DragCompleted);
            }

            UpdateControls();
            UpdateControlsVisibility(false);
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

        /// <summary>Whether a seek was asked for and none of its frames shown yet: decoding from the key frame before it.</summary>
        private bool IsSeekPending => _seekTime >= 0 && _shownRequest != Interlocked.Read(ref _request);

        /// <summary>
        /// The time of the frame shown - or sought, where none of the seek is shown yet: what a new rate, or a seek by an
        /// offset, plays on from. Not from the frame of before a seek, which a key pressed while it decodes would go back to.
        /// </summary>
        private long CurrentTime()
        {
            var seekable = SeekableSource;
            if (IsSeekPending)
                return _seekTime;
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
            UpdateControlsVisibility(true);
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

        private void SeekSlider_DragStarted(object sender, DragStartedEventArgs e)
        {
            _dragging = true;
            UpdateControlsVisibility(true);
        }

        private void SeekSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _dragging = false;
            Seek(TimeSpan.FromTicks((long)_seekSlider.Value));
            ShowControlsForAWhile();
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
            ShowControlsForAWhile();
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
                case Key.E when IsStereo:
                    NextEyeViewCommand.Execute(null, this);
                    break;
                case Key.C when HasSubtitles:
                    NextSubtitleTrackCommand.Execute(null, this);
                    break;
                default:
                    return;
            }
            e.Handled = true;
            ShowControlsForAWhile();
        }

        #endregion

        #region Showing and hiding the controls

        private readonly DispatcherTimer _hideTimer;

        /// <summary>Whether the mouse moved over the video, or a key was pressed, less than <see cref="ControlsHideDelay"/> ago.</summary>
        private bool _recentActivity;

        /// <summary>Where the mouse was last over the control: WPF tells of a move as what is under it changes too, of no move.</summary>
        private Point _lastMouse = new Point(double.NaN, double.NaN);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var position = e.GetPosition(this);
            if (position == _lastMouse)
                return;
            _lastMouse = position;
            if (_viewDrag != null)
            {
                DragView(position);
                return; // looking around, not at the controls
            }
            ShowControlsForAWhile();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _lastMouse = new Point(double.NaN, double.NaN);
            _recentActivity = false;
            _hideTimer.Stop();
            UpdateControlsVisibility(true);
        }

        /// <summary>Shows the bar, and hides it again <see cref="ControlsHideDelay"/> from now, where it hides itself.</summary>
        private void ShowControlsForAWhile()
        {
            _recentActivity = true;
            _hideTimer.Stop();
            _hideTimer.Interval = ControlsHideDelay;
            _hideTimer.Start();
            UpdateControlsVisibility(true);
        }

        private void HideTimer_Tick(object sender, EventArgs e)
        {
            _hideTimer.Stop();
            _recentActivity = false;
            UpdateControlsVisibility(true);
        }

        /// <summary>
        /// Shows the bar or hides it: shown where it does not hide itself, and where it does, while paused, the seek bar
        /// dragged, the mouse on the bar, or moved over the video a moment ago.
        /// </summary>
        private void UpdateControlsVisibility(bool useTransitions)
        {
            bool visible = ShowControls &&
                (!AutoHideControls || _paused || _dragging || _recentActivity || (_controlsBar?.IsMouseOver ?? false));
            AreControlsVisible = visible;
            VisualStateManager.GoToState(this, visible ? ControlsVisibleState : ControlsHiddenState, useTransitions);
        }

        private void UpdateControls()
        {
            UpdateViewState();

            // A live source is played as it comes: there is nothing to seek in, no other rate to play it at and no position
            // to show - pausing is all there is to do, and the template shows no more.
            bool canSeek = SeekableSource != null;
            if (CanSeek != canSeek)
            {
                CanSeek = canSeek;
                CommandManager.InvalidateRequerySuggested();
            }
            IsPlaying = !_paused && _rate == 1;

            if (_seekSlider != null)
            {
                _updatingSlider = true;
                _seekSlider.Maximum = Math.Max(1, Duration.Ticks);
                _updatingSlider = false;
            }
            UpdatePosition();
        }

        private void UpdatePosition()
        {
            UpdateSubtitle();

            // the frame shown - but sought, and none of the seek shown yet, the time sought: the bar stays where it was let go,
            // rather than going back to the frame of before until the one after is decoded
            var seekable = SeekableSource;
            long shown = Interlocked.Read(ref _lastShownTime);
            if (IsSeekPending)
                shown = _seekTime;
            if (seekable != null && shown >= 0)
                Position = TimeSpan.FromTicks(Math.Max(0, shown - seekable.StartTime));

            if (_seekSlider != null && !_dragging)
            {
                _updatingSlider = true;
                _seekSlider.Value = Math.Min(_seekSlider.Maximum, Position.Ticks);
                _updatingSlider = false;
            }

            // made again only as the second shown, the duration or the rate changes: not of every frame
            long seconds = (long)Position.TotalSeconds, durationSeconds = (long)Duration.TotalSeconds;
            if (seconds != _timeTextSeconds || durationSeconds != _timeTextDuration || _rate != _timeTextRate)
            {
                _timeTextSeconds = seconds;
                _timeTextDuration = durationSeconds;
                _timeTextRate = _rate;
                string rate = _rate == 1 ? "" : _rate > 0 ? $"  ×{_rate}" : $"  ◀×{-_rate}";
                TimeText = Duration > TimeSpan.Zero
                    ? $"{FormatTime(Position)} / {FormatTime(Duration)}{rate}"
                    : $"{FormatTime(Position)}{rate}";
            }
        }

        // what TimeText was made of last
        private long _timeTextSeconds = -1;
        private long _timeTextDuration = -1;
        private int _timeTextRate;

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

        // The sound's clock: the time the device's position 0 is at - from the first frame of a time queued since it was
        // reset or made, and the bytes queued before it - and how many bytes it plays a second, its position counting on
        // from there. -1 where the sound has no time: of a source that gives none, or none of a time queued yet.
        private long _audioClockStart = -1;
        private long _audioBytesPerSecond;
        private long _audioBytesQueued;

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

        #region Stereo and spherical view

        /// <summary>
        /// Whether the control draws a spherical video as a view into the sphere: if not, it shows it flat, and it is not
        /// looked around.
        /// </summary>
        protected virtual bool CanShowSpherical => false;

        /// <summary>
        /// What is shown of a frame of the source: the eye and the view, of the layout and projection the source says, but
        /// where the control's properties say otherwise. On the UI thread.
        /// </summary>
        protected VideoView GetView(VideoInfo info)
        {
            var layout = StereoLayout ?? info?.StereoLayout ?? WPF.StereoLayout.Mono;
            var projection = Projection ?? info?.Projection ?? VideoProjection.Flat;
            if (!CanShowSpherical)
                projection = VideoProjection.Flat;
            var bounds = info != null && info.Projection == VideoProjection.Equirectangular ? info.ProjectionBounds : ProjectionBounds.Half;
            return new VideoView(EyeView, layout, projection, bounds, Yaw, Pitch, FieldOfView);
        }

        /// <summary>
        /// Draws the frame shown last again, of the view as it is now: as the view is turned, paused. Whether the control
        /// could - if not, the frame is decoded again.
        /// </summary>
        protected virtual bool RedrawView() => false;

        private void UpdateViewState()
        {
            var view = GetView(_source?.VideoInfo);
            bool stereo = view.Layout != WPF.StereoLayout.Mono;
            bool spherical = view.Projection == VideoProjection.Equirectangular;
            ShowsSideBySide = view.IsBothSpherical ||
                (view.Projection == VideoProjection.Flat && view.Layout == WPF.StereoLayout.SideBySide && view.Eye == EyeView.Both);
            if (IsStereo != stereo || IsSpherical != spherical)
            {
                IsStereo = stereo;
                IsSpherical = spherical;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void OnViewChanged(DependencyProperty property)
        {
            UpdateViewState();
            if (RedrawView())
                return;

            // paused, a new frame comes only of a seek: the one shown, decoded again - not as the view is turned, which a
            // control that can turn it redraws
            bool turned = property == YawProperty || property == PitchProperty || property == FieldOfViewProperty;
            if (!turned && _paused && SeekableSource != null && Interlocked.Read(ref _lastShownTime) >= 0)
                SeekTo(CurrentTime(), _rate);
        }

        /// <summary>Looks straight ahead again, at the field of view a view starts at.</summary>
        public void ResetView()
        {
            Yaw = 0;
            Pitch = 0;
            FieldOfView = DefaultFieldOfView;
        }

        // A drag of the view: where the mouse was last, while the left button is down over the video.
        private Point? _viewDrag;

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.Handled || !IsSpherical || (_controlsBar?.IsMouseOver ?? false))
                return;
            if (e.ClickCount == 2)
            {
                ResetView();
                e.Handled = true;
                return;
            }
            _viewDrag = e.GetPosition(this);
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_viewDrag == null)
                return;
            _viewDrag = null;
            ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            _viewDrag = null;
        }

        /// <summary>
        /// The view follows the mouse, as if the picture were held and moved: dragged right, it looks left - by as many degrees
        /// as the pixels moved are of the field of view.
        /// </summary>
        private void DragView(Point position)
        {
            var last = _viewDrag.Value;
            _viewDrag = position;
            double viewWidth = Math.Max(1, ActualWidth / (GetView(_source?.VideoInfo).IsBothSpherical ? 2 : 1));
            double degreesPerPixel = FieldOfView / viewWidth;
            Yaw = NormalizeYaw(Yaw - (position.X - last.X) * degreesPerPixel);
            Pitch += (position.Y - last.Y) * degreesPerPixel;
        }

        private static double NormalizeYaw(double yaw)
        {
            yaw %= 360;
            return yaw > 180 ? yaw - 360 : yaw < -180 ? yaw + 360 : yaw;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            if (e.Handled || !IsSpherical)
                return;
            // a notch in, a tenth closer
            FieldOfView *= Math.Pow(0.9, e.Delta / 120.0);
            e.Handled = true;
        }

        #endregion

        #region Subtitles

        // The subtitles of the track shown, and those shown now: the first and last of them, and how many.
        private IReadOnlyList<Subtitle> _subtitles = Array.Empty<Subtitle>();
        private (int First, int Last, int Count) _shownSubtitles = (-1, -1, 0);

        /// <summary>
        /// The source's tracks of subtitles, as it is initialized: the track chosen kept where the source has it. On the UI
        /// thread.
        /// </summary>
        private void UpdateSubtitleTracks()
        {
            var tracks = (_source as ISubtitleSource)?.SubtitleTracks ?? Array.Empty<SubtitleTrackInfo>();
            if (!ReferenceEquals(tracks, SubtitleTracks))
            {
                SubtitleTracks = tracks;
                HasSubtitles = tracks.Count > 0;
                CommandManager.InvalidateRequerySuggested();
                if (SubtitleTrack >= tracks.Count)
                    SubtitleTrack = -1;
            }
            LoadSubtitles();
        }

        /// <summary>The subtitles of the track chosen - or, of none, of a forced track - shown from the frame shown.</summary>
        private void LoadSubtitles()
        {
            var source = _source as ISubtitleSource;
            var tracks = SubtitleTracks;
            int track = SubtitleTrack;
            if (track < 0)
            {
                track = -1;
                for (int i = 0; i < tracks.Count; i++)
                    if (tracks[i].Forced) { track = i; break; }
            }
            _subtitles = source != null && track >= 0 && track < tracks.Count ? source.GetSubtitles(track) : Array.Empty<Subtitle>();
            _shownSubtitles = (-1, -1, 0);
            SubtitleText = string.Empty;
            UpdateSubtitle();
        }

        /// <summary>
        /// The subtitle of the frame shown: those shown at its time, all of them where they overlap. The text is made again
        /// only as they change, not of every frame.
        /// </summary>
        private void UpdateSubtitle()
        {
            var subtitles = _subtitles;
            long time = Interlocked.Read(ref _lastShownTime);
            if (subtitles.Count == 0 || time < 0)
            {
                SetShownSubtitles(-1, -1, 0);
                return;
            }

            // the last to start at or before the time, then back over those still shown - a few at most overlap
            int low = 0, high = subtitles.Count - 1, last = -1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                if (subtitles[middle].Start <= time)
                {
                    last = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }
            int first = -1, count = 0, lastShown = -1;
            for (int i = last; i >= 0 && i > last - MaxOverlappingSubtitles; i--)
            {
                if (subtitles[i].End > time)
                {
                    first = i;
                    if (lastShown < 0)
                        lastShown = i;
                    count++;
                }
            }
            SetShownSubtitles(first, lastShown, count);
        }

        private const int MaxOverlappingSubtitles = 8;

        private void SetShownSubtitles(int first, int last, int count)
        {
            if (_shownSubtitles == (first, last, count))
                return;
            _shownSubtitles = (first, last, count);
            if (count == 0)
            {
                SubtitleText = string.Empty;
                return;
            }
            if (count == 1)
            {
                SubtitleText = _subtitles[first].Text;
                return;
            }

            // overlapping: each shown, in the order they started
            long time = Interlocked.Read(ref _lastShownTime);
            var lines = new List<string>(count);
            for (int i = first; i <= last; i++)
                if (_subtitles[i].Start <= time && _subtitles[i].End > time)
                    lines.Add(_subtitles[i].Text);
            SubtitleText = string.Join("\n", lines);
        }

        #endregion

        #region Drawing

        /// <summary>Whether there is a surface to draw on. Asked by the video's thread as well as the UI's.</summary>
        protected abstract bool HasSurface { get; }

        /// <summary>
        /// Makes the surface frames of this format are drawn on, the source of the template's image. On the UI thread.
        /// </summary>
        /// <returns>Whether it was made: frames are not decoded until it is.</returns>
        protected abstract bool CreateSurface(Image image, VideoInfo videoInfo);

        /// <summary>
        /// Lets go of the surface, for another to be made: at the end of a source that cannot be sought in, whose next play
        /// is of a source made again. On the video's thread - what belongs to the UI's is let go of there.
        /// </summary>
        protected abstract void ReleaseSurface();

        /// <summary>
        /// Draws a frame, in the format the source gives - see <see cref="VideoInfo.PixelFormat"/>. On the UI thread, as each
        /// is due; the frame goes back to the source after.
        /// </summary>
        protected abstract void Present(object frame, VideoInfo videoInfo);

        /// <summary>
        /// The source, before it is initialized: where the control would rather have frames of another format than the
        /// source's own, it asks for it here.
        /// </summary>
        protected virtual void OnSourceInitializing(IVideoSource source) { }

        #endregion

        private void CompositionTarget_Rendering(object sender, EventArgs e)
        {
            if (!HasSurface)
                return;

            (object Frame, long Timestamp, long Request, long Queued) next;
            long request = Interlocked.Read(ref _request);
            while (true)
            {
                if (!_videoOut.TryPeek(out next))
                    return;
                if (next.Request >= request)
                    break;
                // of before a seek
                if (_videoOut.TryDequeue(out var stale))
                    _source.ReturnVideoFrame(stale.Frame);
                _videoWake.Set();
            }

            // The first frame of a seek sets the clock: at the time sought, of a file - the key frame after it, played fast,
            // is shown when the clock gets to it - else at the frame's own. It is shown even paused: it is what was sought to.
            // A file's clock starts as its first frame is shown; a live source's as its first frame came, so that showing it
            // late - the first, held up by the bitmap made for it - does not hold every frame after it back by as long, and
            // the source with them, whose next frame waits for room.
            bool first = next.Request != _shownRequest;
            if (next.Request != _anchoredRequest && next.Timestamp >= 0)
            {
                _anchoredRequest = next.Request;
                bool live = SeekableSource == null;
                _clockFrameTime = !live && _seekTime >= 0 ? _seekTime : next.Timestamp;
                _clockStart = live ? next.Queued : _stopwatch.Elapsed.Ticks;
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
                        _source.ReturnVideoFrame(late.Frame);
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

                // A live frame cannot come early. One that came ahead of the clock shows that the clock is behind the
                // source - by as much more as the frame it was set by took to come, the first, made slow by buffers
                // touched for the first time - and moves it on: the clock keeps to the quickest the frames have come,
                // and none waits for a delay that was another's.
                if (audio < 0 && SeekableSource == null && _clockFrameTime >= 0)
                {
                    long early = next.Timestamp - (_clockFrameTime + (next.Queued - _clockStart));
                    if (early > 0)
                    {
                        _clockFrameTime += early;
                        clock += early;
                    }
                }

                long ahead = (next.Timestamp - clock) * Math.Sign(rate);

                // Behind the sound, frames are let go of until one is in time - as many as are behind, but never so many in
                // a row that a decoder slower than the video shows nothing.
                if (audio >= 0 && -ahead > LateFrame && _lateFramesDropped < MaxLateFramesDropped)
                {
                    if (_videoOut.TryDequeue(out var behindSound))
                        _source.ReturnVideoFrame(behindSound.Frame);
                    _videoWake.Set();
                    _lateFramesDropped++;
                    return;
                }
                _lateFramesDropped = 0;

                // A stream's times may jump either way, and are followed - from when the frame came, as the clock starts:
                // set from when it is shown, a frame late in the queue would leave every frame after it waiting as long.
                // A file's are its own - played fast, or of key frames alone, frames can be far apart - and a frame ahead
                // of the clock is waited for: only a clock run ahead of frames decoded too slowly is set again.
                bool live = SeekableSource == null;
                long off = live ? Math.Abs(ahead) : -ahead;
                if (_clockFrameTime < 0 || off > ResyncThreshold * Math.Abs(rate))
                {
                    _clockFrameTime = next.Timestamp;
                    _clockStart = live ? next.Queued : now;
                }
                else if (ahead > 0)
                {
                    return;
                }

                if (Log.InfoEnabled) Log.Info($"Video {next.Timestamp / (double)TimeSpan.TicksPerSecond}, clock {clock / (double)TimeSpan.TicksPerSecond}");
            }

            if (!_videoOut.TryDequeue(out next))
                return;

            // Live, a frame with a frame behind it that is due too is late: it is let go of, and the newer shown - the
            // newest of a screen captured faster than it is drawn. A frame not yet due behind it, as a decoder hands a
            // group of pictures out at once, waits its turn: frames are let go of for being late, not for being older.
            if (SeekableSource == null && _clockFrameTime >= 0)
            {
                long clockNow = _clockFrameTime + (_stopwatch.Elapsed.Ticks - _clockStart);
                while (_videoOut.TryPeek(out var behind) && behind.Timestamp >= 0 && behind.Timestamp <= clockNow
                    && _videoOut.TryDequeue(out var newer))
                {
                    _source.ReturnVideoFrame(next.Frame);
                    next = newer;
                }
            }

            object decoded = next.Frame;
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
                _source.ReturnVideoFrame(decoded);
                return;
            }

            Present(decoded, videoInfo);

            _source.ReturnVideoFrame(decoded);

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
                    if (image != null && CreateSurface(image, videoInfo))
                    {
                        if (IsHalted)
                            this._stopwatch.Reset();
                        else
                            this._stopwatch.Restart();

                        var seekable = SeekableSource;
                        Duration = seekable != null && seekable.Duration > 0 ? TimeSpan.FromTicks(seekable.Duration) : TimeSpan.Zero;
                        UpdateSubtitleTracks();
                        UpdateControls();
                    }
                });
            }
        }

        private Task UninitializeVideo(IVideoSource videoSource)
        {
            while (_videoOut.TryDequeue(out var sample))
                videoSource.ReturnVideoFrame(sample.Frame);
            ReleaseSurface();

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
                {
                    OnSourceInitializing(video);
                    video.InitializeAsync().GetAwaiter().GetResult();
                }
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

        /// <summary>How many frames of a live source are taken ahead of the one shown: a group of pictures' worth, roughly.</summary>
        private const int LiveQueueLength = 4;

        /// <returns>Whether a frame was made, or let go of: there may be more to do at once.</returns>
        private bool DecodeVideoPass(IVideoSource videoSource)
        {
            var seekable = SeekableSource;
            if (!HasSurface)
            {
                InitializeVideo(videoSource).GetAwaiter().GetResult();
                if (!HasSurface)
                    return false;
            }

            // the frames ran out: nothing more to decode, until a seek
            bool ended = seekable != null && Interlocked.Read(ref _endedRequest) == Interlocked.Read(ref _request);

            // A file's frames are each shown at a time of their own, decoded one ahead. A live source's are taken as they come,
            // a few ahead - the next frame of a screen captured as the last is drawn, a group of pictures a decoder hands out
            // at once - each shown at its own time, and those late let go of by the renderer. Paused, the oldest is let go
            // of to take the next, so that what is live is what is shown on play.
            bool live = seekable == null;
            if (live && _paused && _videoOut.Count >= LiveQueueLength && _videoOut.TryDequeue(out var stale))
                videoSource.ReturnVideoFrame(stale.Frame);
            if (!ended && _videoOut.Count < (live ? LiveQueueLength : 1))
            {
                var sample = videoSource.GetVideoFrame(out long timestamp);
                if (sample == null)
                {
                    if (seekable != null)
                        OnEnded(seekable.Request);
                    else
                        UninitializeVideo(videoSource);
                    return false;
                }

                if (sample is byte[] bytes && bytes.Length == 0)
                    return false; // none ready yet

                // read after the frame: a seek is done before the frame after it is handed out, on this thread
                long request = seekable?.Request ?? 0;
                _videoOut.Enqueue((sample, timestamp, request, _stopwatch.Elapsed.Ticks));
                return true;
            }

            return false;
        }

        /// <returns>Whether sound was queued, or let go of: there may be more to do at once.</returns>
        private bool DecodeAudioPass(IAudioSource audioSource)
        {
            if (_waveOut == null)
            {
                // a source of video is initialized by the video's thread: the sound waits for it rather than initialize it
                // a second time beside it
                if (audioSource.AudioInfo == null && _source is IVideoSource)
                    return false;
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
                        _audioBytesQueued = 0;
                        if (IsHalted)
                            _waveOut.Pause();
                        else
                            _waveOut.Resume();
                    }
                }
            }

            // A source of no video has no frame for the sound to wait for; a live one keeps sending while it would wait, and
            // what it sent would be played that late - the video follows the sound's clock instead, where it has one.
            if (_audioHold && ((_source as IVideoSource)?.VideoInfo == null || SeekableSource == null))
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
                    this._audioBytesQueued = 0;
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
                                // the first frame of a time since the device was reset, after what was queued before it
                                if (_audioClockStart < 0 && timestamp >= 0 && _audioBytesPerSecond > 0)
                                    _audioClockStart = timestamp - _audioBytesQueued * TimeSpan.TicksPerSecond / _audioBytesPerSecond;
                                _waveOut.Enqueue(sample, (uint)sample.Length);
                                _audioBytesQueued += sample.Length;
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
                _source.ReturnVideoFrame(sample.Frame);

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

        ~VideoControlBase()
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
