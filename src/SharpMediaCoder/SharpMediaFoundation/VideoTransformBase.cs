using System;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation
{
    public abstract class VideoTransformBase : MFTBase, IVideoTransform
    {
        protected long _sampleDuration = 1;
        private IMFTransform _transform;
        private MFT_OUTPUT_DATA_BUFFER[] _dataBuffer;
        private bool _disposedValue;

        public uint OriginalWidth { get; }
        public uint OriginalHeight { get; } 

        public uint Width { get; } 
        public uint Height { get; }

        public uint FpsNom { get; }
        public uint FpsDenom { get; }

        public uint OutputSize { get; private set; }

        public abstract Guid InputFormat { get; }
        public abstract Guid OutputFormat { get; }

        protected VideoTransformBase(uint width, uint height)
          : this(1, width, height, 1, 1)
        { }

        protected VideoTransformBase(uint resMultiple, uint width, uint height, uint fpsNom, uint fpsDenom)
        {
            this.FpsNom = fpsNom;
            this.FpsDenom = fpsDenom;
            _sampleDuration = MFTUtils.CalculateSampleDuration(FpsNom, FpsDenom);

            this.OriginalWidth = width;
            this.OriginalHeight = height;
            this.Width = MathUtils.RoundToMultipleOf(width, resMultiple);
            this.Height = MathUtils.RoundToMultipleOf(height, resMultiple);
        }

        public void Initialize()
        {
            _transform = Create();
            _transform.GetOutputStreamInfo(0, out var streamInfo);
            _dataBuffer = MFTUtils.CreateOutputDataBuffer(streamInfo.cbSize);
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
                {
                    MFTUtils.DestroyTransform(_transform);
                    _transform = null;
                }

                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            System.GC.SuppressFinalize(this);
        }
    }
}
