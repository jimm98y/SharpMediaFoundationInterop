using System;

namespace SharpMediaFoundation.Transforms
{
    public interface IMediaOutput
    {
        uint OutputSize { get; }
        Guid OutputFormat { get; }
    }

    public interface IMediaInput
    {
        Guid InputFormat { get; }
    }

    public interface IVideoDescriptor
    {
        uint OriginalWidth { get; }
        uint OriginalHeight { get; }
        uint Width { get; }
        uint Height { get; }
    }

    public interface IAudioDescriptor
    {
        uint Channels { get; }
        uint SampleRate { get; }
        uint BitsPerSample { get; }
    }

    public interface IMediaTransform : IDisposable, IMediaInput, IMediaOutput
    {
        void Initialize();
        bool ProcessInput(byte[] data, long timestamp);
        bool ProcessOutput(ref byte[] buffer, out uint length);
        bool Drain();
    }

    public interface IMediaVideoTransform : IMediaTransform, IVideoDescriptor
    { }

    public interface IMediaAudioTransform : IMediaTransform, IAudioDescriptor
    { }

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
