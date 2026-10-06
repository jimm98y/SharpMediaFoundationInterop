using System;
using System.Runtime.Versioning;

namespace SharpMediaFoundationInterop.Transforms
{
    /// <summary>
    /// An audio codec of Media Foundation's, chosen by its <see cref="AudioCodec"/>: the transform of that codec, which
    /// this hands everything on to. <see cref="Transform"/> reaches what is of the codec alone.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public abstract class MediaFoundationAudioCodec : IMediaAudioTransform
    {
        private bool _disposedValue;

        /// <summary>The codec asked for.</summary>
        public AudioCodec Codec { get; }

        /// <summary>The transform of the codec: an AACDecoder, an OpusEncoder and so on.</summary>
        public AudioTransformBase Transform { get; }

        protected MediaFoundationAudioCodec(AudioCodec codec, AudioTransformBase transform)
        {
            Codec = codec;
            Transform = transform;
        }

        public Guid InputFormat => Transform.InputFormat;
        public Guid OutputFormat => Transform.OutputFormat;
        public uint OutputSize => Transform.OutputSize;

        public uint Channels => Transform.Channels;
        public uint SampleRate => Transform.SampleRate;
        public uint BitsPerSample => Transform.BitsPerSample;

        public virtual void Initialize() => Transform.Initialize();

        public bool ProcessInput(byte[] data, long timestamp) => Transform.ProcessInput(data, timestamp);
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp) => Transform.ProcessInput(data, timestamp);

        public virtual bool ProcessOutput(ref byte[] buffer, out uint length) => Transform.ProcessOutput(ref buffer, out length);

        public bool Drain() => Transform.Drain();
        public virtual void Flush() => Transform.Flush();

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                    Transform.Dispose();

                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
