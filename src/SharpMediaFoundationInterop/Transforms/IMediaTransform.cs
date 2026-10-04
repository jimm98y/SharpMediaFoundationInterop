using System;

namespace SharpMediaFoundationInterop.Transforms
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
        /// <summary>A sample in without a managed copy: a view of a reader's buffer, or of a pooled one.</summary>
        bool ProcessInput(ReadOnlySpan<byte> data, long timestamp);
        bool ProcessOutput(ref byte[] buffer, out uint length);
        bool Drain();
    }

    public interface IMediaVideoTransform : IMediaTransform, IVideoDescriptor
    {
        /// <summary>
        /// The next frame out, and its time: the sample time of the input it came of. A decoder hands frames out in the
        /// order they are shown, not the order they went in, so this is what places each.
        /// </summary>
        bool ProcessOutput(ref byte[] buffer, out uint length, out long timestamp);

        /// <summary>Asks for everything the transform holds; read it out with ProcessOutput before <see cref="EndDrain"/>.</summary>
        void BeginDrain();

        /// <summary>Takes input again after a drain.</summary>
        void EndDrain();
    }

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
