using System;

namespace SharpMediaFoundation
{
    public interface IMediaTransform : IDisposable
    {
        uint OutputSize { get; }
        Guid OutputFormat { get; }
        Guid InputFormat { get; }
        void Initialize();
        bool ProcessInput(byte[] data, long timestamp);
        bool ProcessOutput(ref byte[] buffer, out uint length);
    }

    public interface IVideoTransform : IMediaTransform
    {
        uint OriginalWidth { get; }
        uint OriginalHeight { get; }
        uint Width { get; }
        uint Height { get; }
    }

    public interface IAudioTransform : IMediaTransform
    {
        uint Channels { get; }
        uint SampleRate { get; }
    }
}
