using System.Runtime.Versioning;
using System;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Transforms
{
    [SupportedOSPlatform("windows10.0.17763.0")]
    public abstract class AudioTransformBase : MediaTransformBase, IMediaAudioTransform
    {
        private bool _disposedValue;
        protected long _sampleDuration = 1;
        protected IMFTransform _transform;
        private MFT_OUTPUT_DATA_BUFFER[] _dataBuffer;

        public uint OutputSize { get; private set; }

        /// <summary>
        /// The size of the buffer each output is read into, where the transform says none - as FLAC's, ALAC's and MP3's
        /// encoders, and ALAC's decoder, say none, and given no buffer fail. Overridden by a codec that knows its largest.
        /// </summary>
        protected virtual uint DefaultOutputSize => 256 * 1024;

        public uint Channels { get; private set; }
        public uint SampleRate { get; private set; }
        public uint BitsPerSample { get; private set; }

        protected AudioTransformBase(long sampleDuration, uint channels, uint sampleRate, uint bitsPerSample) : base()
        {
            _sampleDuration = sampleDuration;
            Channels = channels;
            SampleRate = sampleRate;
            BitsPerSample = bitsPerSample;
        }

        public void Initialize()
        {
            _transform = Create();
            _transform.GetOutputStreamInfo(0, out var streamInfo);
            OutputSize = streamInfo.cbSize > 0 ? streamInfo.cbSize : DefaultOutputSize;
            // a sample of ours whatever the transform says: FLAC's encoder says it provides its own, and given none hands out
            // one that cannot be read; given one, it hands out a good one of its own in its place
            _dataBuffer = MediaUtils.CreateOutputDataBuffer(OutputSize);
        }

        protected abstract IMFTransform Create();

        public bool ProcessInput(byte[] data, long timestamp)
        {
            return ProcessInput(new ReadOnlySpan<byte>(data), timestamp);
        }

        /// <summary>One frame in, copied straight into the transform's media buffer: no managed copy of it is made.</summary>
        public virtual bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            return ProcessInput(_transform, ReadOnlySpan<byte>.Empty, data, _sampleDuration, timestamp);
        }

        public virtual bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            return ProcessOutput(_transform, _dataBuffer, ref buffer, out length);
        }

        /// <summary>Drains the transform and takes input again, letting go of what the drain hands out.</summary>
        public virtual bool Drain()
        {
            BeginDrain();
            EndDrain();
            return true;
        }

        /// <summary>
        /// Asks the transform for everything it still holds: read it with <see cref="ProcessOutput(ref byte[], out uint)"/>
        /// until it returns false before <see cref="EndDrain"/>. End of stream then drain, and nothing else: ending the
        /// streaming first resets a decoder - FLAC's decoded its last frame anew, its first samples wrong.
        /// </summary>
        public virtual void BeginDrain()
        {
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, default);
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN, default);
        }

        /// <summary>Restarts the transform so it takes input again after a drain.</summary>
        public virtual void EndDrain()
        {
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, default);
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, default);
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, default);
        }

        public virtual void Flush()
        {
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, default);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    if (_transform != null)
                    {
                        DestroyTransform(_transform);
                        _transform = null;
                    }
                }

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
