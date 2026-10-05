namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>Which eye's view of a stereo video is shown.</summary>
    public enum EyeView
    {
        /// <summary>Both: the frame as it is, side by side - or, of a spherical video, each eye's view side by side.</summary>
        Both,

        /// <summary>The left eye's alone.</summary>
        Left,

        /// <summary>The right eye's alone.</summary>
        Right
    }

    /// <summary>
    /// What is shown of a frame: which eye, of what layout, and of a spherical video, which way the view looks and how much
    /// of it it takes in.
    /// </summary>
    public readonly struct VideoView
    {
        public VideoView(EyeView eye, StereoLayout layout, VideoProjection projection, ProjectionBounds bounds,
            double yaw, double pitch, double fieldOfView)
        {
            Eye = eye;
            Layout = layout;
            Projection = projection;
            Bounds = bounds;
            Yaw = yaw;
            Pitch = pitch;
            FieldOfView = fieldOfView;
        }

        public EyeView Eye { get; }
        public StereoLayout Layout { get; }
        public VideoProjection Projection { get; }
        public ProjectionBounds Bounds { get; }

        /// <summary>Degrees to the right of straight ahead.</summary>
        public double Yaw { get; }

        /// <summary>Degrees up from straight ahead.</summary>
        public double Pitch { get; }

        /// <summary>Degrees across each eye's view.</summary>
        public double FieldOfView { get; }

        /// <summary>Whether one eye's picture is shown of a stereo frame, rather than the whole frame.</summary>
        public bool IsOneEye => Layout != StereoLayout.Mono && Eye != EyeView.Both;

        /// <summary>Whether the frame is shown as it is: flat, and both eyes - or of a mono video, either.</summary>
        public bool IsWholeFrame => Projection == VideoProjection.Flat && !IsOneEye;

        /// <summary>Whether each eye's view of a spherical video is shown side by side.</summary>
        public bool IsBothSpherical => Projection != VideoProjection.Flat && Layout != StereoLayout.Mono && Eye == EyeView.Both;

        /// <summary>Where an eye's picture is in the frame, in pixels of the picture: all of it, where there is one.</summary>
        public (uint X, uint Y, uint Width, uint Height) EyeRect(VideoInfo info, bool right)
        {
            uint width = info.OriginalWidth, height = info.OriginalHeight;
            switch (Layout)
            {
                case StereoLayout.SideBySide:
                    return (right ? width / 2 : 0, 0, width / 2, height);
                case StereoLayout.TopBottom:
                    return (0, right ? height / 2 : 0, width, height / 2);
                default:
                    return (0, 0, width, height);
            }
        }
    }
}
