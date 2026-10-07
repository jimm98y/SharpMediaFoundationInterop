using System;
using SharpMediaFoundationInterop.Codecs;

namespace SharpMediaFoundationInterop.Input
{
    /// <summary>What captures media - a camera, a screen - and hands out its samples as they come.</summary>
    public interface IMediaSource : IDisposable, IMediaOutput
    {
        void Initialize();
        bool ReadSample(byte[] sampleBytes, out long timestamp);
    }

    public interface IMediaVideoSource : IMediaSource, IVideoDescriptor
    { }

    public interface IMediaAudioSource : IMediaSource, IAudioDescriptor
    { }
}
