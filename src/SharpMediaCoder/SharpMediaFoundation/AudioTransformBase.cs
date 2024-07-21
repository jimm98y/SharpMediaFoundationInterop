using System;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation
{
    public abstract class AudioTransformBase : MFTBase, IAudioTransform
    {
        private bool _disposedValue;
        protected long _sampleDuration = 1;
        private IMFTransform _transform;
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
            _dataBuffer = MFUtils.CreateOutputDataBuffer(streamInfo.cbSize);
            this.OutputSize = streamInfo.cbSize;
        }

        protected abstract IMFTransform Create();

        public virtual bool ProcessInput(byte[] data, long timestamp)
        {
            return ProcessInput(_transform, data, _sampleDuration, timestamp);
        }

        public bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            return ProcessOutput(_transform, _dataBuffer, ref buffer, out length);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {  }

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
