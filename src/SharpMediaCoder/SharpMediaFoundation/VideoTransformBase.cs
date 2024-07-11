﻿using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation
{
    public abstract class VideoTransformBase : MFTBase, IVideoTransform
    {
        protected long _sampleDuration = 1;
        private IMFTransform _decoder;
        private MFT_OUTPUT_DATA_BUFFER[] _dataBuffer;
        private bool _disposedValue;

        public uint OriginalWidth { get; }
        public uint OriginalHeight { get; } 

        public uint Width { get; } 
        public uint Height { get; }

        public uint FpsNom { get; }
        public uint FpsDenom { get; }

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

            _decoder = Create();
            _decoder.GetOutputStreamInfo(0, out var streamInfo); 
            _dataBuffer = MFTUtils.CreateOutputDataBuffer(streamInfo.cbSize);
        }

        protected abstract IMFTransform Create();

        public virtual bool ProcessInput(byte[] data, long timestamp)
        {
            return ProcessInput(_decoder, data, _sampleDuration, timestamp);
        }

        public bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            return ProcessOutput(_decoder, _dataBuffer, ref buffer, out length);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    MFTUtils.DestroyTransform(_decoder);
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
