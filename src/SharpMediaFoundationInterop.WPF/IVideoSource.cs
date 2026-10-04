using System;
using System.Threading.Tasks;

namespace SharpMediaFoundationInterop.WPF
{
    public enum PixelFormat
    {
        BGR24,
        BGRA32
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
        /// The next frame - empty when none is ready yet, null when there are no more - and when it is shown.
        /// </summary>
        /// <param name="timestamp">
        /// When the frame is shown, in 100 ns units, measured against the source's other frames rather than a wall clock;
        /// -1 for a frame to be shown as soon as it comes.
        /// </param>
        byte[] GetVideoSample(out long timestamp);
        void ReturnVideoSample(byte[] sample);
    }

    public interface IAudioSource : IDisposable
    {
        AudioInfo AudioInfo { get; }
        Task InitializeAsync();
        byte[] GetAudioSample();
        void ReturnAudioSample(byte[] sample);
    }
}
