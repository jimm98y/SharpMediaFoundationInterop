using SharpMediaFoundation.Transforms.H264;
using SharpMediaFoundation.Transforms.H265;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Win32;
using SharpMediaFoundation.Transforms;
using SharpMediaFoundation.Transforms.Colors;

namespace SharpMediaFoundation.WPF
{
    public abstract class VideoSourceBase : IVideoSource
    {
        public VideoInfo Info { get; protected set; }

        protected IMediaVideoTransform _videoDecoder;
        protected ColorConverter _nv12Decoder;
        protected Queue<IList<byte[]>> _videoSampleQueue = new Queue<IList<byte[]>>();
        protected Queue<byte[]> _videoRenderQueue = new Queue<byte[]>();
        protected byte[] _nv12Buffer;
        protected long _time = 0;
        protected bool _isLowLatency = false;
        private bool _disposedValue;

        public abstract Task InitializeAsync();

        public virtual Task FinalizeAsync() { return Task.CompletedTask; }

        public async virtual Task<byte[]> GetSampleAsync()
        {
            if (_videoDecoder == null || _nv12Decoder == null)
            {
                CreateDecoder(Info);
            }

            byte[] existing;
            if (_videoRenderQueue.TryDequeue(out existing))
                return existing;

            while (_videoRenderQueue.Count == 0 && _videoSampleQueue.TryDequeue(out var au))
            {
                foreach (var nalu in au)
                {
                    if (_videoDecoder.ProcessInput(nalu, _time))
                    {
                        while (_videoDecoder.ProcessOutput(ref _nv12Buffer, out _))
                        {
                            _nv12Decoder.ProcessInput(_nv12Buffer, _time);

                            byte[] decoded = ArrayPool<byte>.Shared.Rent((int)_nv12Decoder.OutputSize);
                            _nv12Decoder.ProcessOutput(ref decoded, out _);

                            _videoRenderQueue.Enqueue(decoded);
                        }
                    }
                }
                _time += 10000 * 1000 / (Info.FpsNom / Info.FpsDenom); // 100ns units
            }

            if (_videoRenderQueue.TryDequeue(out existing))
            {
                return existing;
            }
            else
            {
                await FinalizeAsync();
                return null;
            }
        }

        protected virtual void CreateDecoder(VideoInfo info)
        {
            // decoders must be created on the same thread as the samples
            if (info.VideoCodec == "H264")
            {
                _videoDecoder = new H264Decoder(info.OriginalWidth, info.OriginalHeight, info.FpsNom, info.FpsDenom, _isLowLatency);
                _videoDecoder.Initialize();
            }
            else if (info.VideoCodec == "H265")
            {
                _videoDecoder = new H265Decoder(info.OriginalWidth, info.OriginalHeight, info.FpsNom, info.FpsDenom, _isLowLatency);
                _videoDecoder.Initialize();
            }
            else
            {
                throw new NotSupportedException();
            }

            _nv12Decoder = new ColorConverter(PInvoke.MFVideoFormat_NV12, PInvoke.MFVideoFormat_RGB24, info.Width, info.Height);
            _nv12Decoder.Initialize();

            _nv12Buffer = new byte[_videoDecoder.OutputSize];
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    
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
