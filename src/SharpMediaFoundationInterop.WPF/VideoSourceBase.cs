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
using SharpMediaFoundationInterop.Transforms.VP9;

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

        /// <summary>Decoded frames, each with the time it is shown at.</summary>
        protected ConcurrentQueue<(byte[] Frame, long Timestamp)> _videoRenderQueue = new ConcurrentQueue<(byte[] Frame, long Timestamp)>();
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

            IList<ArraySegment<byte>> frame;
            while (_audioRenderQueue.Count == 0 && (frame = ReadNextAudio()) != null)
            {
                if (_audioDecoder.ProcessInput(frame[0], 0))
                {
                    while (_audioDecoder.ProcessOutput(ref _pcmBuffer, out var pcmSize))
                    {
                        if (_audioDecoder is OpusDecoder)
                        {
                            byte[] decoded = RentAudio((int)pcmSize);
                            for (int i = 0; i < pcmSize / 4; i++)
                            {
                                float ieeeFloat = BitConverter.ToSingle(_pcmBuffer, i * 4);
                                ieeeFloat = Math.Clamp(ieeeFloat, -1.0f, 1.0f);                                
                                int pcm = (int)(ieeeFloat * int.MaxValue);
                                decoded[i * 4 + 0] = (byte)((pcm & 0x000000FF) >> 0);
                                decoded[i * 4 + 1] = (byte)((pcm & 0x0000FF00) >> 8);
                                decoded[i * 4 + 2] = (byte)((pcm & 0x00FF0000) >> 16);
                                decoded[i * 4 + 3] = (byte)((pcm & 0xFF000000) >> 24);
                            }
                            _audioRenderQueue.Enqueue(decoded);
                            Interlocked.Increment(ref _audioFrames);
                        }
                        else
                        {
                            byte[] decoded = RentAudio((int)pcmSize);
                            Buffer.BlockCopy(_pcmBuffer, 0, decoded, 0, (int)pcmSize);
                            _audioRenderQueue.Enqueue(decoded);
                            Interlocked.Increment(ref _audioFrames);
                        }
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

        /// <summary>
        /// The next audio frame, as views of the source's buffer, valid until this is called again: it is decoded before
        /// then, so the source need not copy it.
        /// </summary>
        protected abstract IList<ArraySegment<byte>> ReadNextAudio();

        /// <summary>
        /// The decoded audio frames, of one size for a stream, kept for reuse: a frame handed out is the exact size of its
        /// PCM, which its consumer takes its length from, so ArrayPool's larger arrays would not do.
        /// </summary>
        private readonly ConcurrentBag<byte[]> _audioPool = new ConcurrentBag<byte[]>();

        private byte[] RentAudio(int size)
        {
            while (_audioPool.TryTake(out var pooled))
            {
                if (pooled.Length == size)
                    return pooled;
                // a frame of a size the stream no longer decodes to is let go
            }
            return new byte[size];
        }

        /// <summary>The time of the access unit decoded last: what one with no time of its own goes in at.</summary>
        private long _lastVideoTime;

        /// <summary>Whether the decoder has given up what it held at the end of the stream - see <see cref="GetVideoSample"/>.</summary>
        private bool _videoDrained;

        public virtual byte[] GetVideoSample(out long timestamp)
        {
            timestamp = -1;
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
            {
                timestamp = sample.Timestamp;
                return sample.Frame;
            }

            IList<ArraySegment<byte>> au;
            while (_videoRenderQueue.Count == 0 && (au = ReadNextVideo(out long auTime)) != null)
            {
                // The source's time of the access unit, which every unit of it carries in: the decoder hands it back with
                // the frame, in the order the frames are shown.
                long videoTime = auTime >= 0 ? auTime : _lastVideoTime;
                _lastVideoTime = videoTime;
                foreach (var nalu in au)
                {
                    if (_videoDecoder.ProcessInput(nalu, videoTime))
                    {
                        CollectVideoFrames(videoInfo);
                    }
                }
            }

            // At the end of a file the decoder still holds the frames it was keeping to put the next ones in order - a
            // whole group of pictures, some decoders: drained, they come out, and are shown before the end is.
            if (_videoRenderQueue.Count == 0 && !IsStreaming && !_videoDrained)
            {
                _videoDrained = true;
                _videoDecoder.BeginDrain();
                CollectVideoFrames(videoInfo);
                _videoDecoder.EndDrain();
            }

            if (_videoRenderQueue.TryDequeue(out sample))
            {
                timestamp = sample.Timestamp;
                return sample.Frame;
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

        /// <summary>The frames the decoder has ready, made into pictures and queued, each with its time.</summary>
        private void CollectVideoFrames(VideoInfo videoInfo)
        {
            while (_videoDecoder.ProcessOutput(ref _nv12Buffer, out _, out long frameTime))
            {
                _nv12Decoder.ProcessInput(_nv12Buffer, frameTime);

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

                    _videoRenderQueue.Enqueue((decoded, frameTime));
                    Interlocked.Increment(ref _videoFrames);
                }
            }
        }

        /// <summary>
        /// The next access unit's units, as views of the source's buffer, valid until this is called again: they are
        /// decoded before then, so the source need not copy them. Parameter sets go with the first access unit, at its
        /// time: a decoder gives a frame the time of the first input that went into it.
        /// </summary>
        /// <param name="timestamp">
        /// When the access unit is shown, in 100 ns units from wherever the source's clock starts - the times of one
        /// source are measured against each other, never against a wall clock - or -1 where it has none.
        /// </param>
        protected abstract IList<ArraySegment<byte>> ReadNextVideo(out long timestamp);

        protected virtual void CompletedVideo()
        {
            // drained already, with what it gave up shown
            _videoDrained = false;
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
            else if (info.VideoCodec == "VP9")
            {
                _videoDecoder = new VP9Decoder(info.OriginalWidth, info.OriginalHeight, info.FpsNom, info.FpsDenom, _isLowLatency);
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
                _audioDecoder = new OpusDecoder(960, info.ChannelCount, info.SampleRate, info.BitsPerSample);
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
            if (decoded != null && decoded.Length > 0)
                _audioPool.Add(decoded);
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
