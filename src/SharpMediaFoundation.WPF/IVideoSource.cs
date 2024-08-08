﻿using System;
using System.Threading.Tasks;

namespace SharpMediaFoundation.WPF
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
        public uint Channels { get; set; }
        public uint SampleRate { get; set; }
        public byte[] UserData { get; set; }
        public uint BitsPerSample { get; set; }
    }

    public interface IVideoSource : IDisposable
    {
        VideoInfo VideoInfo { get; }
        Task InitializeAsync();
        bool GetVideoSample(out byte[] sample);
        void ReturnVideoSample(byte[] sample);
    }

    public interface IAudioSource : IDisposable
    {
        AudioInfo AudioInfo { get; }
        Task InitializeAsync();
        bool GetAudioSample(out byte[] sample);
        void ReturnAudioSample(byte[] sample);
    }
}
