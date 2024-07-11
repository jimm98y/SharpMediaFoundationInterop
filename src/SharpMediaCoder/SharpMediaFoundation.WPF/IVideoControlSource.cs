﻿using System.Threading.Tasks;

namespace SharpMediaFoundation.WPF
{
    public struct VideoInfo
    {
        public uint Width { get; set; }
        public uint OriginalWidth { get; set; }
        public uint Height { get; set; }
        public uint OriginalHeight { get; set; }
        public uint FpsNom { get; set; }
        public uint FpsDenom { get; set; }
    }

    public interface IVideoControlSource
    {
        VideoInfo Info { get; }
        Task InitializeAsync();
        Task<byte[]> GetSampleAsync();
    }
}
