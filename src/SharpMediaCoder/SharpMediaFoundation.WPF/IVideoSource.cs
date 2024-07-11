using System;
using System.Threading.Tasks;

namespace SharpMediaFoundation.WPF
{
    public struct VideoInfo
    {
        public string VideoCodec { get; set; }
        public uint Width { get; set; }
        public uint OriginalWidth { get; set; }
        public uint Height { get; set; }
        public uint OriginalHeight { get; set; }
        public uint FpsNom { get; set; }
        public uint FpsDenom { get; set; }
    }

    public interface IVideoSource : IDisposable
    {
        VideoInfo Info { get; }
        Task InitializeAsync();
        Task<byte[]> GetSampleAsync();
    }
}
