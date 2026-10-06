using System;
using System.Threading.Tasks;

namespace SharpMediaFoundationInterop.WPF
{
    public enum PixelFormat
    {
        BGR24,
        BGRA32,

        /// <summary>
        /// As a decoder makes it: a plane of luma, then one of chroma at half the size each way, of the coded size -
        /// <see cref="VideoInfo.Width"/> by <see cref="VideoInfo.Height"/> - the picture its top left
        /// <see cref="VideoInfo.OriginalWidth"/> by <see cref="VideoInfo.OriginalHeight"/>. What a GPU converts for
        /// itself, where the others are converted on the CPU.
        /// </summary>
        NV12
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

        /// <summary>How the frame holds the eyes' views, as the source says: a file by its 'st3d' box.</summary>
        public StereoLayout StereoLayout { get; set; }

        /// <summary>What each eye's picture is of, as the source says: a file by its 'sv3d' box.</summary>
        public VideoProjection Projection { get; set; }

        /// <summary>Of an <see cref="VideoProjection.Equirectangular"/> picture, how much of the sphere it leaves out.</summary>
        public ProjectionBounds ProjectionBounds { get; set; }
    }

    public class AudioInfo
    {
        public string AudioCodec { get; set; }
        public uint ChannelCount { get; set; }
        public uint SampleRate { get; set; }
        public byte[] UserData { get; set; }
        public uint BitsPerSample { get; set; }
        public int ChannelConfiguration { get; set; }

        /// <summary>
        /// Samples, of each channel, at the start of the decoded sound that are not of it: Opus's pre-skip. Dropped again
        /// after a seek, as the decoder settles in anew.
        /// </summary>
        public uint SkipSamples { get; set; }
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
