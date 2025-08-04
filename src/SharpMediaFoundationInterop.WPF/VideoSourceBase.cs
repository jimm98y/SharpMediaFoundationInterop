using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Win32;
using SharpMediaFoundationInterop.Transforms;
using SharpMediaFoundationInterop.Transforms.AAC;
using SharpMediaFoundationInterop.Transforms.Colors;
using SharpMediaFoundationInterop.Transforms.H264;
using SharpMediaFoundationInterop.Transforms.H265;
using SharpMediaFoundationInterop.Utils;
using System.Threading;
using System.Collections.Concurrent;
using SharpMediaFoundationInterop.Transforms.AV1;
using SharpMediaFoundationInterop.Transforms.Opus;

namespace SharpMediaFoundationInterop.WPF
{
    public abstract class VideoSourceBase : IVideoSource, IAudioSource
    {
        public VideoInfo VideoInfo { get; protected set; }
        public AudioInfo AudioInfo { get; protected set; }

        protected virtual bool IsStreaming { get; }
        public byte[] Empty { get; private set; } = new byte[0];

        protected IMediaVideoTransform _videoDecoder;
        protected IMediaVideoTransform _nv12Decoder;
        protected IMediaAudioTransform _audioDecoder;

        protected ConcurrentQueue<byte[]> _videoRenderQueue = new ConcurrentQueue<byte[]>();
        protected ConcurrentQueue<byte[]> _audioRenderQueue = new ConcurrentQueue<byte[]>();

        protected byte[] _nv12Buffer;
        protected byte[] _rgbBuffer;
        private byte[] _pcmBuffer;
        private int _bytesPerPixel;
        private int _imageBufferLen;
        protected long _videoFrames = 0;
        protected long _audioFrames = 0;
        protected bool _isLowLatency = false;
        private bool _disposedValue;

        public abstract Task InitializeAsync();

        public virtual byte[] GetAudioSample()
        {
            var audioInfo = AudioInfo;
            if (audioInfo == null)
            {
                return null;
            }

            if (_audioDecoder == null)
            {
                CreateAudioDecoder(audioInfo);
            }

            if (_audioRenderQueue.TryDequeue(out var sample))
                return sample;

            IList<byte[]> frame;
            while (_audioRenderQueue.Count == 0 && (frame = ReadNextAudio()) != null)
            {
                if (_audioDecoder.ProcessInput(frame[0], 0))
                {
                    while (_audioDecoder.ProcessOutput(ref _pcmBuffer, out var pcmSize))
                    {
                        byte[] decoded = ArrayPool<byte>.Shared.Rent((int)pcmSize);
                        Buffer.BlockCopy(_pcmBuffer, 0, decoded, 0, (int)pcmSize);
                        _audioRenderQueue.Enqueue(decoded);
                        Interlocked.Increment(ref _audioFrames);
                    }
                }
            }

            if (_audioRenderQueue.TryDequeue(out sample))
            {
                return sample;
            }
            else
            {
                if (IsStreaming)
                {
                    return Empty;
                }
                else
                {
                    CompletedAudio();
                    return null;
                }
            }
        }

        protected abstract IList<byte[]> ReadNextAudio();

        public virtual byte[] GetVideoSample()
        {
            var videoInfo = VideoInfo;
            if (videoInfo == null)
            {
                return null;
            }

            if (_videoDecoder == null || _nv12Decoder == null)
            {
                CreateVideoDecoder(videoInfo);
            }

            if (_videoRenderQueue.TryDequeue(out var sample))
                return sample;

            IList<byte[]> au;
            while (_videoRenderQueue.Count == 0 && (au = ReadNextVideo()) != null)
            {
                foreach (var nalu in au)
                {
                    long videoTime = _videoFrames * 10000L / (videoInfo.FpsNom / videoInfo.FpsDenom);
                    if (_videoDecoder.ProcessInput(nalu, videoTime))
                    {
                        while (_videoDecoder.ProcessOutput(ref _nv12Buffer, out _))
                        {
                            _nv12Decoder.ProcessInput(_nv12Buffer, videoTime);

                            if (_nv12Decoder.ProcessOutput(ref _rgbBuffer, out _))
                            {
                                byte[] decoded = ArrayPool<byte>.Shared.Rent(_imageBufferLen);

                                BitmapUtils.CopyBitmap(
                                    _rgbBuffer,
                                    (int)videoInfo.Width,
                                    (int)videoInfo.Height,
                                    decoded,
                                    (int)videoInfo.OriginalWidth,
                                    (int)videoInfo.OriginalHeight,
                                    _bytesPerPixel,
                                    true);

                                _videoRenderQueue.Enqueue(decoded);
                                Interlocked.Increment(ref _videoFrames);
                            }
                        }
                    }
                }
            }

            if (_videoRenderQueue.TryDequeue(out sample))
            {
                return sample;
            }
            else
            {
                if (IsStreaming)
                {
                    return Empty;
                }
                else
                {
                    CompletedVideo();
                    return null;
                }
            }
        }

        protected abstract IList<byte[]> ReadNextVideo();

        protected virtual void CompletedVideo()
        {
            _videoDecoder.Drain();
            Interlocked.Exchange(ref _videoFrames, 0);
        }
        protected virtual void CompletedAudio() 
        {
            _audioDecoder.Drain();
            Interlocked.Exchange(ref _audioFrames, 0);
        }

        protected virtual void CreateVideoDecoder(VideoInfo info)
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
            else if (info.VideoCodec == "H266")
            {
                // H266 is as of 8/3/2025 not supported by Media Foundation
                throw new NotSupportedException();
            }
            else if (info.VideoCodec == "AV1")
            {
                _videoDecoder = new AV1Decoder(info.OriginalWidth, info.OriginalHeight, info.FpsNom, info.FpsDenom, _isLowLatency);
                _videoDecoder.Initialize();
            }
            else
            {
                throw new NotSupportedException();
            }

            _nv12Decoder = new ColorConverter(PInvoke.MFVideoFormat_NV12, PInvoke.MFVideoFormat_RGB24, info.Width, info.Height);
            _nv12Decoder.Initialize();

            _bytesPerPixel = 3;

            _nv12Buffer = new byte[_videoDecoder.OutputSize];
            _rgbBuffer = new byte[_nv12Decoder.OutputSize];
            _imageBufferLen = (int)_nv12Decoder.OutputSize;
        }

        private void CreateAudioDecoder(AudioInfo info)
        {
            // decoders must be created on the same thread as the samples
            if (info.AudioCodec == "AAC")
            {
                _audioDecoder = new AACDecoder(info.ChannelCount, info.SampleRate, AACDecoder.CreateUserData(info.UserData), info.ChannelConfiguration);
                _audioDecoder.Initialize();
            }
            else if (info.AudioCodec == "OPUS")
            {
                _audioDecoder = new OpusDecoder(info.ChannelCount, info.SampleRate);
                _audioDecoder.Initialize();
            }
            else
            {
                throw new NotSupportedException();
            }

            _pcmBuffer = new byte[_audioDecoder.OutputSize];
        }

        public void ReturnVideoSample(byte[] decoded)
        {
            ArrayPool<byte>.Shared.Return(decoded);
        }

        public void ReturnAudioSample(byte[] decoded)
        {
            ArrayPool<byte>.Shared.Return(decoded);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    if (_videoDecoder != null)
                    {
                        _videoDecoder.Dispose();
                        _videoDecoder = null;
                    }

                    if (_nv12Decoder != null)
                    {
                        _nv12Decoder.Dispose();
                        _nv12Decoder = null;
                    }

                    if (_audioDecoder != null)
                    {
                        _audioDecoder.Dispose();
                        _audioDecoder = null;
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
