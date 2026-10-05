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
    }

    public class AudioInfo
    {
        public string AudioCodec { get; set; }
        public uint ChannelCount { get; set; }
        public uint SampleRate { get; set; }
        public byte[] UserData { get; set; }
        public uint BitsPerSample { get; set; }
        public int ChannelConfiguration { get; set; }
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
