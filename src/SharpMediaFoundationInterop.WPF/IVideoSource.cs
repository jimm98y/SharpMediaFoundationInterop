using System;
using System.Threading.Tasks;
using SharpMediaFoundationInterop.Codecs;

namespace SharpMediaFoundationInterop.WPF
{
    public enum PixelFormat
    {
        BGR24,
        BGRA32,

        /// <summary>
        /// As a decoder makes it: a plane of luma, then one of chroma at half the size each way, of the coded size -
        /// <see cref="VideoInfo.Width"/> by <see cref="VideoInfo.Height"/> - the picture its top left
        /// <see cref="VideoInfo.OriginalWidth"/> by <see cref="VideoInfo.OriginalHeight"/>, not yet turned - see
        /// <see cref="VideoInfo.Orientation"/>. What a GPU converts for itself, where the others are converted on the CPU,
        /// of <see cref="VideoInfo.ShownWidth"/> by <see cref="VideoInfo.ShownHeight"/>.
        /// </summary>
        NV12,

        /// <summary>
        /// As a decoder makes it, as NV12 is, but of another <see cref="VideoInfo.FrameFormat"/> - of more than 8 bits, of
        /// 4:2:2, 4:4:4 or monochrome - of the coded size: what a GPU converts for itself, as it does NV12. A source asked
        /// for NV12 hands these out of a stream NV12 does not hold.
        /// </summary>
        Yuv
    }

    /// <summary>How a frame holds the views of the two eyes, where it holds both.</summary>
    public enum StereoLayout
    {
        /// <summary>One picture, for both eyes.</summary>
        Mono,

        /// <summary>The left eye's picture in the left half of the frame, the right eye's in the right.</summary>
        SideBySide,

        /// <summary>The left eye's picture in the top half of the frame, the right eye's in the bottom.</summary>
        TopBottom
    }

    /// <summary>What each eye's picture is of.</summary>
    public enum VideoProjection
    {
        /// <summary>A flat picture, shown as it is.</summary>
        Flat,

        /// <summary>
        /// Part of a sphere around the camera, laid out by longitude across and latitude down: of all of it, 360 by 180
        /// degrees, less what <see cref="VideoInfo.ProjectionBounds"/> crops - a 180 degree video is half of it.
        /// </summary>
        Equirectangular
    }

    /// <summary>
    /// The parts of a whole equirectangular picture - 360 degrees across, 180 down - cropped from each side, as fractions of
    /// its width and height: a 180 degree video crops a quarter from the left and the right.
    /// </summary>
    public readonly struct ProjectionBounds
    {
        public ProjectionBounds(double left, double top, double right, double bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public double Left { get; }
        public double Top { get; }
        public double Right { get; }
        public double Bottom { get; }

        /// <summary>The whole sphere: nothing cropped.</summary>
        public static ProjectionBounds Full => default;

        /// <summary>The half in front of the camera, 180 degrees across and the whole height.</summary>
        public static ProjectionBounds Half => new ProjectionBounds(0.25, 0, 0.25, 0);
    }

    public class VideoInfo
    {
        public string VideoCodec { get; set; }
        public uint Width { get; set; }
        public uint OriginalWidth { get; set; }
        public uint Height { get; set; }
        public uint OriginalHeight { get; set; }
        public uint FpsNom { get; set; }
        public uint FpsDenom { get; set; }
        public PixelFormat PixelFormat { get; set; }

        /// <summary>
        /// The layout of the decoded frames, of the stream's chroma and bits: what <see cref="PixelFormat.NV12"/> and
        /// <see cref="PixelFormat.Yuv"/> frames are of, and what a source converts on the CPU.
        /// </summary>
        public VideoFrameFormat FrameFormat { get; set; } = VideoFrameFormat.NV12;

        /// <summary>
        /// How the decoded samples become sRGB, as the stream says; null where it says nothing - see
        /// <see cref="VideoColor.Default"/>.
        /// </summary>
        public VideoColor Color { get; set; }

        /// <summary>
        /// How the picture is turned and flipped to be shown, as the track says - a phone's upright video, recorded on its
        /// side. Frames converted on the CPU are of the picture shown already; those as decoded are turned as they are
        /// converted, on the GPU.
        /// </summary>
        public VideoOrientation Orientation { get; set; }

        /// <summary>The width of the picture as it is shown: <see cref="OriginalWidth"/>, or its height, turned on its side.</summary>
        public uint ShownWidth => Orientation.SwapsAxes ? OriginalHeight : OriginalWidth;

        /// <summary>The height of the picture as it is shown.</summary>
        public uint ShownHeight => Orientation.SwapsAxes ? OriginalWidth : OriginalHeight;

        /// <summary>The color, as the stream says it, or what a stream that says nothing is taken to be.</summary>
        public VideoColor ColorOrDefault => Color ??= VideoColor.Default(OriginalHeight);

        /// <summary>How the frame holds the eyes' views, as the source says: a file by its 'st3d' box.</summary>
        public StereoLayout StereoLayout { get; set; }

        /// <summary>What each eye's picture is of, as the source says: a file by its 'sv3d' box.</summary>
        public VideoProjection Projection { get; set; }

        /// <summary>Of an <see cref="VideoProjection.Equirectangular"/> picture, how much of the sphere it leaves out.</summary>
        public ProjectionBounds ProjectionBounds { get; set; }
    }

    public class AudioInfo
    {
        /// <summary>"AAC", "OPUS", "MP3", "FLAC", "ALAC" or "PCM".</summary>
        public string AudioCodec { get; set; }
        public uint ChannelCount { get; set; }
        public uint SampleRate { get; set; }
        /// <summary>The codec's configuration: of AAC the AudioSpecificConfig, of FLAC its metadata blocks, of ALAC its ALACSpecificConfig, of Opus its 'dOps' payload.</summary>
        public byte[] UserData { get; set; }

        /// <summary>The bits of the PCM played: of a stream of 24 bits, or of Opus, 32 - the decoded sound widened.</summary>
        public uint BitsPerSample { get; set; }
        public int ChannelConfiguration { get; set; }

        /// <summary>
        /// Samples, of each channel, at the start of the decoded sound that are not of it: Opus's pre-skip. Dropped again
        /// after a seek, as the decoder settles in anew.
        /// </summary>
        public uint SkipSamples { get; set; }

        /// <summary>
        /// The speakers the decoded channels are for, as WAVEFORMATEXTENSIBLE's dwChannelMask has them: of 0, those
        /// <see cref="SharpMediaFoundationInterop.Output.SpeakerLayout.ChannelMask"/> says of their count. AAC's 4.0 is 0x107.
        /// </summary>
        public uint ChannelMask { get; set; }

        /// <summary>Of "PCM", the samples' format, as the container says it.</summary>
        public PcmFormat PcmFormat { get; set; }

        /// <summary>
        /// Whether the channels are a sound field - ambisonics, of ACN order and SN3D, with or without two of head-locked
        /// stereo after it - as Opus' channel mapping family 2 and Google's 'SA3D' say: played on the speakers there are,
        /// turned as a spherical video's view is.
        /// </summary>
        public bool IsAmbisonic { get; set; }
    }

    public interface IVideoSource : IDisposable
    {
        VideoInfo VideoInfo { get; }
        Task InitializeAsync();

        /// <summary>
        /// Asks for frames of a format other than the source's own, before it is initialized: what a control that converts
        /// frames for itself would rather have. <see cref="VideoInfo.PixelFormat"/> says what the frames are.
        /// </summary>
        /// <returns>Whether the source gives frames of it.</returns>
        bool TrySetOutputFormat(PixelFormat format) => false;

        /// <summary>
        /// Whether it is a picture, not a video: of one frame, shown and held, of no time - an image. A control shows no
        /// position, nor a bar to seek with.
        /// </summary>
        bool IsStill => false;

        /// <summary>
        /// Asks for frames decoded on a device, before the source is initialized: where its decoder can decode on the GPU,
        /// <see cref="GetVideoFrame"/> hands them out as textures of it, <see cref="GpuVideoFrame"/>s, never leaving the GPU.
        /// </summary>
        /// <returns>Whether the source can: that it decodes on the GPU depends on the GPU, and the format.</returns>
        bool TryUseDirect3D(Direct3DDevice device) => false;

        /// <summary>
        /// The next frame, as <see cref="GetVideoSample"/>, or a <see cref="GpuVideoFrame"/> - decoded on the GPU, where
        /// <see cref="TryUseDirect3D"/> was asked for. Given back with <see cref="ReturnVideoFrame"/>.
        /// </summary>
        object GetVideoFrame(out long timestamp) => GetVideoSample(out timestamp);

        /// <summary>Gives a frame of <see cref="GetVideoFrame"/> back.</summary>
        void ReturnVideoFrame(object frame)
        {
            if (frame is GpuVideoFrame gpu)
                gpu.Release();
            else if (frame is byte[] bytes)
                ReturnVideoSample(bytes);
        }
        /// <summary>
        /// The next frame - empty when none is ready yet, null when there are no more - and when it is shown.
        /// </summary>
        /// <param name="timestamp">
        /// When the frame is shown, in 100 ns units, measured against the source's other frames rather than a wall clock;
        /// -1 for a frame to be shown as soon as it comes.
        /// </param>
        byte[] GetVideoSample(out long timestamp);
        void ReturnVideoSample(byte[] sample);
    }

    /// <summary>
    /// A source whose frames can be had from any time, at any rate, either way: a file. A seek, or a new rate, is asked for
    /// from any thread and done by the thread that reads the frames, before the next is handed out.
    /// </summary>
    public interface ISeekableVideoSource : IVideoSource
    {
        /// <summary>Whether it can be: a live stream cannot.</summary>
        bool CanSeek { get; }

        /// <summary>How long the video is, from <see cref="StartTime"/>, in 100 ns units; -1 where it is not known.</summary>
        long Duration { get; }

        /// <summary>The time of the first frame, which a position is counted from.</summary>
        long StartTime { get; }

        /// <summary>
        /// The rate frames are handed out at: 1 is forwards as recorded, 2, 4 and 8 that many times faster, and the same
        /// numbers below 0 backwards - in the order they are shown at that rate, each with its own time.
        /// </summary>
        int Rate { get; }

        /// <summary>
        /// Moves to a time, to play on from it at a rate: the frame handed out next is the one shown at it, or the first
        /// after - not the key frame before, which decoding starts at.
        /// </summary>
        /// <returns>
        /// The number of the request, which <see cref="Request"/> is once it is done: frames handed out before then are of
        /// the time before it.
        /// </returns>
        long Seek(long time, int rate);

        /// <summary>The number of the last seek done: that of the frame handed out last.</summary>
        long Request { get; }

        /// <summary>How many frames there are, of a source that indexes them - a file; -1 of one that does not.</summary>
        long FrameCount => -1;

        /// <summary>The number of the frame shown at a time, from 0 - the last at or before it; -1 where frames are not indexed.</summary>
        long FrameAt(long time) => -1;

        /// <summary>The time a frame is shown at, of its number; -1 where frames are not indexed, or there is no such frame.</summary>
        long TimeOfFrame(long frame) => -1;
    }

    public interface IAudioSource : IDisposable
    {
        AudioInfo AudioInfo { get; }
        Task InitializeAsync();
        byte[] GetAudioSample();

        /// <summary>
        /// The next frame of sound, as <see cref="GetAudioSample()"/>, and the time it starts at, on the clock of the
        /// video's frames: what a player keeps them in step by. -1 where the source does not know it.
        /// </summary>
        byte[] GetAudioSample(out long timestamp)
        {
            timestamp = -1;
            return GetAudioSample();
        }
        void ReturnAudioSample(byte[] sample);
    }
}
