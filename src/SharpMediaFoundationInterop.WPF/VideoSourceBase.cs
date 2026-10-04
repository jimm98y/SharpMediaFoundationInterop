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
    public abstract class VideoSourceBase : ISeekableVideoSource, IAudioSource
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
        /// <summary>Decoded frames of sound, each with the time it starts at; -1 where the source gives none.</summary>
        protected ConcurrentQueue<(byte[] Pcm, long Timestamp)> _audioRenderQueue = new ConcurrentQueue<(byte[] Pcm, long Timestamp)>();

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

        public byte[] GetAudioSample() => GetAudioSample(out _);

        /// <summary>
        /// The time of the next frame of sound decoded: that of the first frame read since a seek, then on by each frame's
        /// length - the decoder's own delay, of a frame or two, is the same for every frame, and its outputs are not timed.
        /// -1 until a frame of a time is read.
        /// </summary>
        private long _audioOutTime = -1;

        public virtual byte[] GetAudioSample(out long timestamp)
        {
            timestamp = -1;
            var audioInfo = AudioInfo;
            if (audioInfo == null)
            {
                return null;
            }

            if (_audioDecoder == null)
            {
                CreateAudioDecoder(audioInfo);
            }

            ApplyPendingAudioSeek();

            // no sound in trick play: faster or backwards, it is noise; at 1x again, the seek to where the video is brings
            // it back in step
            if (Rate != 1 || _audioEnded)
                return Empty;

            if (_audioRenderQueue.TryDequeue(out var sample))
            {
                timestamp = sample.Timestamp;
                return sample.Pcm;
            }

            long bytesPerSecond = (long)audioInfo.SampleRate * audioInfo.ChannelCount * audioInfo.BitsPerSample / 8;
            IList<ArraySegment<byte>> frame;
            while (_audioRenderQueue.Count == 0 && (frame = ReadNextAudio(out long frameTime)) != null)
            {
                if (_audioOutTime < 0 && frameTime >= 0)
                    _audioOutTime = frameTime;

                if (_audioDecoder.ProcessInput(frame[0], 0))
                {
                    while (_audioDecoder.ProcessOutput(ref _pcmBuffer, out var pcmSize))
                    {
                        long pcmTime = _audioOutTime;
                        if (_audioOutTime >= 0 && bytesPerSecond > 0)
                            _audioOutTime += pcmSize * TimeSpan.TicksPerSecond / bytesPerSecond;

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
                            _audioRenderQueue.Enqueue((decoded, pcmTime));
                            Interlocked.Increment(ref _audioFrames);
                        }
                        else
                        {
                            byte[] decoded = RentAudio((int)pcmSize);
                            Buffer.BlockCopy(_pcmBuffer, 0, decoded, 0, (int)pcmSize);
                            _audioRenderQueue.Enqueue((decoded, pcmTime));
                            Interlocked.Increment(ref _audioFrames);
                        }
                    }
                }
            }

            if (_audioRenderQueue.TryDequeue(out sample))
            {
                timestamp = sample.Timestamp;
                return sample.Pcm;
            }
            else
            {
                if (IsStreaming)
                {
                    return Empty;
                }
                else if (CanSeek)
                {
                    // the end of the sound need not be the end of the video, and a seek brings it back
                    CompletedAudio();
                    _audioEnded = true;
                    return Empty;
                }
                else
                {
                    CompletedAudio();
                    return null;
                }
            }
        }

        /// <summary>Whether the audio has come to its end, with the video still going on: see <see cref="Seek"/>.</summary>
        private bool _audioEnded;

        /// <summary>The time a seek done on the video's thread is to, for the sound to follow on its own; -1 where there is none.</summary>
        private long _pendingAudioSeek = -1;

        /// <summary>
        /// A seek's part of the sound, on the thread that reads the sound, which its decoder and its reading belong to: what
        /// is decoded is let go of, and reading goes on from the time sought - at 1x; at another rate there is no sound.
        /// </summary>
        private void ApplyPendingAudioSeek()
        {
            long seek = Interlocked.Exchange(ref _pendingAudioSeek, -1);
            if (seek < 0)
                return;

            _audioDecoder.Flush();
            while (_audioRenderQueue.TryDequeue(out var audio))
                ReturnAudioSample(audio.Pcm);
            _audioOutTime = -1;
            _audioEnded = false;
            if (Rate == 1)
                SeekAudio(seek);
        }

        /// <summary>
        /// The next audio frame, as views of the source's buffer, valid until this is called again: it is decoded before
        /// then, so the source need not copy it.
        /// </summary>
        protected abstract IList<ArraySegment<byte>> ReadNextAudio();

        /// <summary>
        /// The next audio frame, as <see cref="ReadNextAudio()"/>, and its time, on the clock of the video's frames; -1
        /// where the source does not know it.
        /// </summary>
        protected virtual IList<ArraySegment<byte>> ReadNextAudio(out long timestamp)
        {
            timestamp = -1;
            return ReadNextAudio();
        }

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

            ApplyPendingSeek();

            if (!_videoRenderQueue.IsEmpty)
                return Dequeue(out timestamp);

            if (CanSeek && Math.Abs(Rate) >= KeyFrameRate)
            {
                // faster than the decoder keeps up with: the key frames alone, each decoded on its own
                if (!NextKeyFrame(videoInfo))
                {
                    CompletedVideo();
                    return null;
                }
                return _videoRenderQueue.IsEmpty ? Empty : Dequeue(out timestamp);
            }

            if (CanSeek && Rate < 0)
            {
                // backwards: a group of pictures decoded forwards, its frames handed out last first
                if (!NextFramesBackwards(videoInfo))
                {
                    // at the start: the end, the other way
                    CompletedVideo();
                    return null;
                }
                return _videoRenderQueue.IsEmpty ? Empty : Dequeue(out timestamp);
            }

            IList<ArraySegment<byte>> au;
            while (_videoRenderQueue.IsEmpty && (au = ReadNextVideo(out long auTime)) != null)
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
            if (_videoRenderQueue.IsEmpty && !IsStreaming && !_videoDrained)
            {
                _videoDrained = true;
                _videoDecoder.BeginDrain();
                CollectVideoFrames(videoInfo);
                _videoDecoder.EndDrain();
            }

            if (!_videoRenderQueue.IsEmpty)
            {
                return Dequeue(out timestamp);
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

        private byte[] Dequeue(out long timestamp)
        {
            timestamp = -1;
            if (!_videoRenderQueue.TryDequeue(out var sample))
                return Empty;
            timestamp = sample.Timestamp;
            if (timestamp >= 0)
                _position = timestamp;
            return sample.Frame;
        }

        /// <summary>
        /// The frames the decoder has ready, made into pictures and queued, each with its time - but those from before the
        /// time a seek is to, which were decoded only for the frames after them to be.
        /// </summary>
        private void CollectVideoFrames(VideoInfo videoInfo)
        {
            while (_videoDecoder.ProcessOutput(ref _nv12Buffer, out _, out long frameTime))
            {
                if (frameTime < _discardBefore)
                    continue;
                EnqueuePicture(_nv12Buffer, frameTime, videoInfo);
            }
        }

        /// <summary>A decoded frame made into the picture shown, in an array of the pool, and queued with its time.</summary>
        private void EnqueuePicture(byte[] nv12, long frameTime, VideoInfo videoInfo)
        {
            _nv12Decoder.ProcessInput(nv12, frameTime);

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

        #region Seeking and trick play

        /// <summary>Whether frames can be had from anywhere, and at any rate: a file's can, a live stream's cannot.</summary>
        public virtual bool CanSeek => false;

        /// <summary>How long the video is, from <see cref="StartTime"/>, in 100 ns units; -1 where it is not known.</summary>
        public virtual long Duration => -1;

        /// <summary>The time of the first frame, which a position is counted from.</summary>
        public virtual long StartTime => 0;

        /// <summary>The time of the frame handed out last; -1 before the first.</summary>
        public long Position => Interlocked.Read(ref _position);
        private long _position = -1;

        /// <summary>
        /// How fast, and which way, frames are handed out: 1 is forwards as recorded, 2, 4 and 8 that many times faster, and
        /// the same numbers below 0 backwards. They are handed out in the order shown at that rate; showing each when its
        /// time comes, at that rate, is the player's.
        /// </summary>
        public int Rate { get; private set; } = 1;

        /// <summary>The number of the seek done last: those before it have no frames handed out any more.</summary>
        public long Request => Interlocked.Read(ref _request);
        private long _request;

        /// <summary>
        /// Rates this fast, either way, are of key frames alone: decoding every frame at 4 times the rate cannot be counted on,
        /// and backwards it costs more again.
        /// </summary>
        public const int KeyFrameRate = 4;

        // The seek asked for and not yet done, and its number: taken together, under the lock, so that the number done is
        // always that of the time and rate done.
        private readonly object _pendingLock = new object();
        private long _pendingSeek = -1;
        private int _pendingRate = 1;
        private long _requested;

        public long Seek(long time, int rate)
        {
            if (rate != 1 && rate != 2 && rate != 4 && rate != 8 && rate != -1 && rate != -2 && rate != -4 && rate != -8)
                throw new ArgumentOutOfRangeException(nameof(rate), rate, "1, 2, 4 or 8, forwards or backwards");
            lock (_pendingLock)
            {
                _pendingSeek = Math.Max(0, time);
                _pendingRate = rate;
                return ++_requested;
            }
        }

        /// <summary>
        /// Moves the video to a key frame, the next access unit read being it, parameter sets and all: the last at or before
        /// <paramref name="time"/>, or with <paramref name="after"/>, the first after it.
        /// </summary>
        /// <returns>The time of the key frame; -1 where there is none.</returns>
        protected virtual long SeekVideoToSync(long time, bool after) => -1;

        /// <summary>Moves the audio to the frame at or before a time.</summary>
        protected virtual void SeekAudio(long time) { }

        // Forwards, after a seek: the frames before the time sought are decoded, for those after them, but not shown.
        private long _discardBefore = -1;

        // Key frames alone: the time of the one handed out last, and whether that was the one sought to.
        private long _keyCursor = -1;
        private bool _keyFirst;

        // Backwards: the frames from before this time are the ones handed out next.
        private long _reverseBefore = -1;

        // The frames of a group of pictures being decoded to be handed out backwards: copies of the decoder's, the latest
        // before the time handed out down to, as many as the memory set aside holds.
        private readonly List<(byte[] Nv12, long Time)> _reverseWindow = new List<(byte[] Nv12, long Time)>();
        private const long ReverseWindowBytes = 256L * 1024 * 1024;

        /// <summary>A seek, or a new rate, asked for: done on the thread that decodes, which the decoders belong to.</summary>
        private void ApplyPendingSeek()
        {
            long seek;
            lock (_pendingLock)
            {
                seek = _pendingSeek;
                if (seek < 0)
                    return;
                _pendingSeek = -1;
                Rate = _pendingRate;
                Interlocked.Exchange(ref _request, _requested);
            }

            if (!CanSeek)
                return;

            while (_videoRenderQueue.TryDequeue(out var queued))
                ArrayPool<byte>.Shared.Return(queued.Frame);
            ClearReverseWindow();
            _videoDecoder.Flush();
            _videoDrained = false;

            // the sound's part of it is done by the thread that reads the sound, before the next frame of it
            Interlocked.Exchange(ref _pendingAudioSeek, seek);

            Interlocked.Exchange(ref _position, seek);
            _discardBefore = -1;
            _keyCursor = -1;
            _reverseBefore = -1;

            if (Math.Abs(Rate) >= KeyFrameRate)
            {
                _keyCursor = seek;
                _keyFirst = true;
            }
            else if (Rate < 0)
            {
                // the frame at the time sought, and those before it
                _reverseBefore = seek + 1;
            }
            else
            {
                // from before the first key frame: from it
                if (SeekVideoToSync(seek, after: false) < 0)
                    SeekVideoToSync(seek, after: true);
                _discardBefore = seek;
            }
        }

        /// <summary>
        /// The next key frame, forwards or backwards, decoded on its own and queued.
        /// </summary>
        /// <returns>False where there is none: the end, or the start.</returns>
        private bool NextKeyFrame(VideoInfo videoInfo)
        {
            // the first the way played, from the time sought - at or after it forwards, at or before it backwards - then the
            // next
            long sync = _keyFirst
                ? Rate > 0 ? SeekVideoToSync(_keyCursor - 1, after: true) : SeekVideoToSync(_keyCursor, after: false)
                : Rate > 0 ? SeekVideoToSync(_keyCursor, after: true) : SeekVideoToSync(_keyCursor - 1, after: false);
            if (sync < 0)
                return false;

            _keyFirst = false;
            _keyCursor = sync;

            _videoDecoder.Flush();
            var au = ReadNextVideo(out long auTime);
            if (au == null)
                return false;
            foreach (var unit in au)
                _videoDecoder.ProcessInput(unit, auTime >= 0 ? auTime : sync);

            // a key frame is all there is to decode: drained, it comes out without the frames after it going in
            _videoDecoder.BeginDrain();
            while (_videoDecoder.ProcessOutput(ref _nv12Buffer, out _, out long frameTime))
                EnqueuePicture(_nv12Buffer, frameTime, videoInfo);
            _videoDecoder.EndDrain();
            return true;
        }

        /// <summary>
        /// The frames before those handed out last, queued last first: the group of pictures they are of decoded from its
        /// key frame, those at or after the frame handed out last let go of, and the latest of the rest kept - as many as
        /// the memory set aside holds; the ones before them are decoded again, for the next lot.
        /// </summary>
        /// <returns>False at the start: there is nothing before it.</returns>
        private bool NextFramesBackwards(VideoInfo videoInfo)
        {
            long sync = SeekVideoToSync(_reverseBefore - 1, after: false);
            if (sync < 0)
                return false;

            int capacity = (int)Math.Clamp(ReverseWindowBytes / Math.Max(1, _nv12Buffer.Length), 4, 120);
            _videoDecoder.Flush();

            bool past = false;
            IList<ArraySegment<byte>> au;
            while (!past && (au = ReadNextVideo(out long auTime)) != null)
            {
                long time = auTime >= 0 ? auTime : sync;
                foreach (var unit in au)
                {
                    if (_videoDecoder.ProcessInput(unit, time))
                        past |= KeepForBackwards(capacity);
                }
            }
            if (!past)
            {
                // the end of the file: what the decoder holds
                _videoDecoder.BeginDrain();
                KeepForBackwards(capacity);
                _videoDecoder.EndDrain();
            }

            for (int i = _reverseWindow.Count - 1; i >= 0; i--)
                EnqueuePicture(_reverseWindow[i].Nv12, _reverseWindow[i].Time, videoInfo);

            // on from the earliest of them; from before the key frame where there were none
            _reverseBefore = _reverseWindow.Count > 0 ? _reverseWindow[0].Time : sync;
            ClearReverseWindow();
            return true;
        }

        /// <summary>
        /// The frames the decoder has ready, kept for <see cref="NextFramesBackwards"/>: the latest from before the frame
        /// handed out last.
        /// </summary>
        /// <returns>Whether a frame from at or after it has come out: the decoder hands frames out in the order shown, so
        /// everything after is too.</returns>
        private bool KeepForBackwards(int capacity)
        {
            bool past = false;
            while (_videoDecoder.ProcessOutput(ref _nv12Buffer, out uint length, out long frameTime))
            {
                if (frameTime >= _reverseBefore)
                {
                    past = true;
                    continue;
                }

                byte[] copy = ArrayPool<byte>.Shared.Rent(_nv12Buffer.Length);
                Buffer.BlockCopy(_nv12Buffer, 0, copy, 0, (int)Math.Min(length, (uint)_nv12Buffer.Length));
                _reverseWindow.Add((copy, frameTime));
                if (_reverseWindow.Count > capacity)
                {
                    ArrayPool<byte>.Shared.Return(_reverseWindow[0].Nv12);
                    _reverseWindow.RemoveAt(0);
                }
            }
            return past;
        }

        private void ClearReverseWindow()
        {
            foreach (var frame in _reverseWindow)
                ArrayPool<byte>.Shared.Return(frame.Nv12);
            _reverseWindow.Clear();
        }

        #endregion

        protected virtual void CompletedAudio() 
        {
            _audioOutTime = -1;
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
