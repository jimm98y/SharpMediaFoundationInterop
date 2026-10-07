using System;
using System.Collections.Generic;

namespace SharpMediaFoundationInterop.Codecs
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

        /// <summary>
        /// Lets go of everything the transform holds, without its being put out: what a seek needs, after which the input
        /// starts again elsewhere - at a key frame.
        /// </summary>
        void Flush();
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

    /// <summary>An encoder: of <see cref="MediaCodecs.CreateVideoEncoder"/>, made for a <see cref="VideoEncoderOptions"/>.</summary>
    public interface IMediaVideoEncoder : IMediaVideoTransform
    {
        /// <summary>
        /// The settings of its options the encoder did not take, each with why, known once it is initialized. What an
        /// encoder takes varies by codec and by vendor, so a setting is reported here rather than failing the encoder.
        /// </summary>
        IReadOnlyList<string> UnappliedSettings { get; }
    }

    public interface IMediaAudioTransform : IMediaTransform, IAudioDescriptor
    {
        /// <summary>Asks for everything the transform holds; read it out with ProcessOutput before <see cref="EndDrain"/>.</summary>
        void BeginDrain();

        /// <summary>Takes input again after a drain.</summary>
        void EndDrain();
    }

    /// <summary>An encoder: of <see cref="MediaCodecs.CreateAudioEncoder"/>, made for an <see cref="AudioEncoderOptions"/>.</summary>
    public interface IMediaAudioEncoder : IMediaAudioTransform
    {
        /// <summary>
        /// The codec's configuration, for the container to carry, known once the encoder is initialized: of AAC the
        /// AudioSpecificConfig; of Opus null, as its header is made of the channels and sample rate alone.
        /// </summary>
        byte[] Config { get; }
    }
}
