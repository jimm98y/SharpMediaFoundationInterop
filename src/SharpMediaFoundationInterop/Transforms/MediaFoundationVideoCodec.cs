using System;
using System.Runtime.Versioning;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Transforms
{
    /// <summary>
    /// A video codec of Media Foundation's, chosen by its <see cref="VideoCodec"/>: the transform of that codec, which
    /// this hands everything on to. <see cref="Transform"/> reaches what is of the codec alone.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public abstract class MediaFoundationVideoCodec : IMediaFoundationVideoTransform
    {
        private bool _disposedValue;

        /// <summary>The codec asked for.</summary>
        public VideoCodec Codec { get; }

        /// <summary>The transform of the codec: an H265Decoder, an H264Encoder and so on.</summary>
        public VideoTransformBase Transform { get; }

        protected MediaFoundationVideoCodec(VideoCodec codec, VideoTransformBase transform)
        {
            Codec = codec;
            Transform = transform;
        }

        public Guid InputFormat => Transform.InputFormat;
        public Guid OutputFormat => Transform.OutputFormat;
        public uint OutputSize => Transform.OutputSize;

        public uint OriginalWidth => Transform.OriginalWidth;
        public uint OriginalHeight => Transform.OriginalHeight;
        public uint Width => Transform.Width;
        public uint Height => Transform.Height;

        public IMFDXGIDeviceManager DeviceManager
        {
            get => Transform.DeviceManager;
            set => Transform.DeviceManager = value;
        }

        public bool UsesDevice => Transform.UsesDevice;
        public bool ProvidesSamples => Transform.ProvidesSamples;

        public virtual void Initialize() => Transform.Initialize();

        public bool ProcessInput(byte[] data, long timestamp) => Transform.ProcessInput(data, timestamp);
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp) => Transform.ProcessInput(data, timestamp);

        public virtual bool ProcessOutput(ref byte[] buffer, out uint length) => Transform.ProcessOutput(ref buffer, out length);

        /// <summary>The next frame out, and its time; overridden by a decoder that hands out only some of the frames.</summary>
        public virtual bool ProcessOutput(ref byte[] buffer, out uint length, out long timestamp) =>
            Transform.ProcessOutput(ref buffer, out length, out timestamp);

        /// <summary>The next frame as the transform hands it out, not copied; as <see cref="ProcessOutput(ref byte[], out uint, out long)"/>.</summary>
        public virtual bool ProcessOutput(out IMFSample sample, out long timestamp) => Transform.ProcessOutput(out sample, out timestamp);

        public bool Drain() => Transform.Drain();
        public void BeginDrain() => Transform.BeginDrain();
        public void EndDrain() => Transform.EndDrain();
        public void Flush() => Transform.Flush();

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
