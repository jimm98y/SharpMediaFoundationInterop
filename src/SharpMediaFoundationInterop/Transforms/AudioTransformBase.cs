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
            _dataBuffer = MediaUtils.CreateOutputDataBuffer(streamInfo.cbSize);
            OutputSize = streamInfo.cbSize;
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

        public bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            return ProcessOutput(_transform, _dataBuffer, ref buffer, out length);
        }

        public virtual bool Drain()
        {
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, default);
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, default);
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN, default);
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, default);
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, default);
            return true;
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
