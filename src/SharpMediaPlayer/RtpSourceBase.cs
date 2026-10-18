using SharpAV1;
using SharpAVX;
using SharpVP9;
using SharpH264;
using SharpH265;
using SharpH26X;
using SharpMediaFoundationInterop.Codecs;
using SharpMediaFoundationInterop.Utils;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// What a source of RTP streams - RTSP's, WebRTC's - shares: the access units and audio frames received, queued until they
    /// are decoded; the parameter sets sent again after the decoder is drained; the frames' times of their RTP timestamps, put on
    /// the sender's clock by its reports; and the picture's size, of the parameter sets or of the first frame that says it.
    /// </summary>
    public abstract class RtpSourceBase : MediaSourceBase
    {
        /// <summary>
        /// An access unit, or an audio frame, as received: copied into arrays of the shared pool, as the client's buffers
        /// are its own again once its event returns, and queued until it is decoded. Its arrays go back as the next one is
        /// read, and it goes back to be filled again.
        /// </summary>
        protected sealed class PooledUnits
        {
            public List<ArraySegment<byte>> Units { get; } = new List<ArraySegment<byte>>();

            /// <summary>When it is shown, in 100 ns units from the stream's first frame; -1 for parameter sets.</summary>
            public long Timestamp { get; set; } = -1;

            public void Add(ReadOnlySpan<byte> unit)
            {
                byte[] array = ArrayPool<byte>.Shared.Rent(unit.Length);
                unit.CopyTo(array);
                Units.Add(new ArraySegment<byte>(array, 0, unit.Length));
            }

            /// <summary>A unit put in front of the others, copied as <see cref="Add"/> copies.</summary>
            public void Prepend(ReadOnlySpan<byte> unit)
            {
                byte[] array = ArrayPool<byte>.Shared.Rent(unit.Length);
                unit.CopyTo(array);
                Units.Insert(0, new ArraySegment<byte>(array, 0, unit.Length));
            }

            public void Release()
            {
                foreach (var unit in Units)
                    ArrayPool<byte>.Shared.Return(unit.Array);
                Units.Clear();
                Timestamp = -1;
            }
        }

        protected ConcurrentQueue<PooledUnits> _videoSampleQueue = new ConcurrentQueue<PooledUnits>();
        protected ConcurrentQueue<PooledUnits> _audioSampleQueue = new ConcurrentQueue<PooledUnits>();

        private readonly ConcurrentBag<PooledUnits> _spareUnits = new ConcurrentBag<PooledUnits>();

        // what was last handed out, given back as the next one is read: it has been decoded by then
        private PooledUnits _videoInUse;
        private PooledUnits _audioInUse;

        protected PooledUnits RentUnits() => _spareUnits.TryTake(out var units) ? units : new PooledUnits();

        private void Release(ref PooledUnits units)
        {
            if (units == null)
                return;
            units.Release();
            _spareUnits.Add(units);
            units = null;
        }

        /// <summary>
        /// The parameter sets the SDP gives, put in front of the first frame received and sent at its time: a decoder gives
        /// a frame the time of the first input that went into it, and these on their own have none.
        /// </summary>
        protected byte[][] _parameterSets;

        // The stream's parameter sets, of its description, kept to be sent to the decoder again after it is drained; and
        // whether to, with the next frame read.
        protected byte[][] _streamParameterSets;
        private volatile bool _resendParameterSets;

        protected override void OnVideoDecoderDrained()
        {
            _resendParameterSets = _streamParameterSets != null;
        }

        protected override bool IsStreaming { get { return true; } }

        protected RtpSourceBase()
        {
            this._isLowLatency = true;
        }

        #region Video information

        /// <summary>The video of an H.264 stream, of its SPS: its size, rounded up to what the decoder gives, and its frame rate.</summary>
        protected static VideoInfo CreateH264VideoInfo(byte[] sps)
        {
            var videoInfo = new VideoInfo();
            var decodedSPS = ParseH264SPS(sps);
            var dimensions = decodedSPS.CalculateDimensions();
            videoInfo.OriginalWidth = dimensions.Width;
            videoInfo.OriginalHeight = dimensions.Height;
            videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, MediaCodecs.DecoderAlignment(VideoCodec.H264));
            videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, MediaCodecs.DecoderAlignment(VideoCodec.H264));

            var timescale = decodedSPS.CalculateTimescale();
            videoInfo.FpsNom = (uint)timescale.Timescale;
            videoInfo.FpsDenom = (uint)timescale.FrameTick;

            videoInfo.VideoCodec = "H264";
            return videoInfo;
        }

        /// <summary>The video of an H.265 stream, of its SPS.</summary>
        protected static VideoInfo CreateH265VideoInfo(byte[] sps)
        {
            var videoInfo = new VideoInfo();
            var decodedSPS = ParseH265SPS(sps);
            var dimensions = decodedSPS.CalculateDimensions();
            videoInfo.OriginalWidth = dimensions.Width;
            videoInfo.OriginalHeight = dimensions.Height;
            // the frames of the coded size, the picture cropped of them: 1088 lines of a 1080 line picture
            (videoInfo.Width, videoInfo.Height) = TrackColor.HevcFrameSize(decodedSPS, videoInfo.OriginalWidth, videoInfo.OriginalHeight);

            var timescale = decodedSPS.CalculateTimescale();
            videoInfo.FpsNom = (uint)timescale.Timescale;
            videoInfo.FpsDenom = (uint)timescale.FrameTick;

            videoInfo.VideoCodec = "H265";
            return videoInfo;
        }

        /// <summary>
        /// A NAL unit as the depacketizers hand it out - of a start code of Annex B, 00 00 01 or 00 00 00 01, in front of it -
        /// without its start code.
        /// </summary>
        protected static ReadOnlySpan<byte> WithoutStartCode(ReadOnlySpan<byte> unit)
        {
            if (unit.Length >= 4 && unit[0] == 0 && unit[1] == 0 && unit[2] == 0 && unit[3] == 1)
                return unit.Slice(4);
            if (unit.Length >= 3 && unit[0] == 0 && unit[1] == 0 && unit[2] == 1)
                return unit.Slice(3);
            return unit;
        }

        /// <summary>The type of a NAL unit, of a start code in front of it or none: of H.264 its 5 bits, of H.265 its 6; -1 of none.</summary>
        protected static int NalUnitType(string codec, ReadOnlySpan<byte> unit)
        {
            unit = WithoutStartCode(unit);
            if (unit.Length == 0)
                return -1;
            return codec == "H265" ? (unit[0] >> 1) & 0x3F : unit[0] & 0x1F;
        }

        /// <summary>Whether a NAL unit is a parameter set: of H.264 an SPS or PPS; of H.265 a VPS, SPS or PPS.</summary>
        protected static bool IsParameterSet(string codec, ReadOnlySpan<byte> unit)
        {
            unit = WithoutStartCode(unit);
            if (unit.Length == 0)
                return false;
            if (codec == "H264")
            {
                int type = unit[0] & 0x1F;
                return type == 7 || type == 8;
            }
            if (codec == "H265")
            {
                int type = (unit[0] >> 1) & 0x3F;
                return type >= 32 && type <= 34;
            }
            return false;
        }

        /// <summary>
        /// The picture's size, of a stream whose SDP does not say it: H.264's and H.265's of an SPS in the stream, AV1's of its
        /// sequence header, VP9's of a key frame's header - of the first frame of a superframe, where a key frame is. False until
        /// one of them comes.
        /// </summary>
        protected static bool TryReadSize(string codec, IList<ArraySegment<byte>> units, out VideoInfo videoInfo)
        {
            videoInfo = null;
            foreach (var unit in units)
            {
                if (unit.Count == 0)
                    continue;

                if (codec == "H264")
                {
                    if (NalUnitType(codec, unit) != 7)
                        continue;
                    videoInfo = CreateH264VideoInfo(WithoutStartCode(unit).ToArray());
                    return true;
                }

                if (codec == "H265")
                {
                    if (NalUnitType(codec, unit) != 33)
                        continue;
                    videoInfo = CreateH265VideoInfo(WithoutStartCode(unit).ToArray());
                    return true;
                }

                if (codec == "AV1")
                {
                    int obuType = (unit[0] & 0x78) >> 3;
                    if (obuType != 1)
                        continue;

                    var context = new AV1Context();
                    using (var stream = new AomStream(new MemoryStream(unit.Array, unit.Offset, unit.Count, writable: false)))
                        context.Read(stream, unit.Count);
                    videoInfo = SizedVideoInfo(codec, (uint)(context._MaxFrameWidthMinus1 + 1), (uint)(context._MaxFrameHeightMinus1 + 1));
                    return true;
                }

                if (codec == "VP9")
                {
                    int[] sizes = VP9Context.SuperframeFrameSizes(unit.Array, unit.Offset, unit.Count);
                    int first = sizes != null && sizes.Length > 0 && sizes[0] > 0 && sizes[0] <= unit.Count ? sizes[0] : unit.Count;

                    var context = new VP9Context();
                    try
                    {
                        using (var stream = new AomStream(new MemoryStream(unit.Array, unit.Offset, first, writable: false)))
                            context.Read(stream, first);
                    }
                    catch (Exception)
                    {
                        // an inter frame before the first key frame cannot be read without one: wait for the key frame
                        continue;
                    }

                    if (context._ShowExistingFrame != 0 || context._FrameType != VP9Constants.KEY_FRAME)
                        continue;
                    videoInfo = SizedVideoInfo(codec, (uint)context._FrameWidth, (uint)context._FrameHeight);
                    return true;
                }
            }
            return false;
        }

        // of AV1 and VP9: the frames of the size the decoder aligns them to
        private static VideoInfo SizedVideoInfo(string codec, uint width, uint height)
        {
            uint multiple = MediaCodecs.DecoderAlignment(codec == "VP9" ? VideoCodec.VP9 : VideoCodec.AV1);
            return new VideoInfo
            {
                VideoCodec = codec,
                OriginalWidth = width,
                OriginalHeight = height,
                Width = MediaUtils.RoundToMultipleOf(width, multiple),
                Height = MediaUtils.RoundToMultipleOf(height, multiple),
            };
        }

        private static SharpH265.SeqParameterSetRbsp ParseH265SPS(byte[] sample)
        {
            SharpH265.H265Context context = new SharpH265.H265Context();
            using (ItuStream stream = new ItuStream(new MemoryStream(sample)))
            {
                ulong ituSize = 0;
                var nu = new SharpH265.NalUnit((uint)sample.Length);
                context.NalHeader = nu;
                ituSize += nu.Read(context, stream);

                if (nu.NalUnitHeader.NalUnitType == SharpH265.H265NALTypes.SPS_NUT)
                {
                    context.SeqParameterSetRbsp = new SharpH265.SeqParameterSetRbsp();
                    context.SeqParameterSetRbsp.Read(context, stream);
                    return context.SeqParameterSetRbsp;
                }
                else
                {
                    throw new InvalidDataException($"Expected SPS NAL unit, but found: {nu.NalUnitHeader.NalUnitType}");
                }
            }
        }

        private static SharpH264.SeqParameterSetRbsp ParseH264SPS(byte[] sample)
        {
            SharpH264.H264Context context = new SharpH264.H264Context();
            using (ItuStream stream = new ItuStream(new MemoryStream(sample)))
            {
                ulong ituSize = 0;
                var nu = new SharpH264.NalUnit((uint)sample.Length);
                context.NalHeader = nu;
                ituSize += nu.Read(context, stream);

                if (nu.NalUnitType == SharpH264.H264NALTypes.SPS)
                {
                    context.SeqParameterSetRbsp = new SharpH264.SeqParameterSetRbsp();
                    context.SeqParameterSetRbsp.Read(context, stream);
                    return context.SeqParameterSetRbsp;
                }
                else
                {
                    throw new InvalidDataException($"Expected SPS NAL unit, but found: {nu.NalUnitType}");
                }
            }
        }

        #endregion // Video information

        #region Reading

        protected override IList<ArraySegment<byte>> ReadNextAudio() => ReadNextAudio(out _);

        protected override IList<ArraySegment<byte>> ReadNextAudio(out long timestamp)
        {
            timestamp = -1;
            Release(ref _audioInUse);
            if (!_audioSampleQueue.TryDequeue(out _audioInUse))
                return null;
            timestamp = _audioInUse.Timestamp;
            return _audioInUse.Units;
        }

        protected override IList<ArraySegment<byte>> ReadNextVideo(out long timestamp)
        {
            timestamp = -1;
            Release(ref _videoInUse);
            if (!_videoSampleQueue.TryDequeue(out _videoInUse))
                return null;
            if (_resendParameterSets)
            {
                // copied in, as the units are, into arrays of the pool they all go back to
                _resendParameterSets = false;
                var parameterSets = _streamParameterSets;
                for (int i = parameterSets.Length - 1; i >= 0; i--)
                    _videoInUse.Prepend(parameterSets[i]);
            }
            timestamp = _videoInUse.Timestamp;
            return _videoInUse.Units;
        }

        #endregion // Reading

        #region Timing

        /// <summary>The clock of the RTP timestamps of video: 90 kHz, for H.264, H.265, AV1 and VP9 alike.</summary>
        protected const long VideoClockRate = 90000;

        private bool _hasRtpTime;
        private uint _lastRtpTime;
        private long _rtpTime;

        /// <summary>
        /// A frame's RTP timestamp as a time from the stream's first frame, in 100 ns units. It is 32 bits and wraps, every
        /// thirteen hours or so at 90 kHz; it is followed from frame to frame by the signed difference, which also lets it
        /// go back, as it does for a frame shown before the one decoded ahead of it.
        /// </summary>
        private long RtpVideoTime(uint rtpTimestamp)
        {
            if (!_hasRtpTime)
            {
                _hasRtpTime = true;
                _rtpTime = 0;
            }
            else
            {
                _rtpTime += (int)(rtpTimestamp - _lastRtpTime);
            }
            _lastRtpTime = rtpTimestamp;
            return MediaUtils.ToTicks(_rtpTime, VideoClockRate);
        }

        // The sender's clock, in its ticks, at the time 0 of the frames: set as the first sender report of the video comes,
        // so that the frames' times go on from where their RTP timestamps had them. Both are read and written by the
        // thread that receives, the one thread both streams' data comes on.
        private bool _hasSenderEpoch;
        private long _senderEpoch;

        /// <summary>
        /// A video frame's time, in 100 ns units: of its RTP timestamp, from the stream's first frame - and, once a sender
        /// report has come, of the sender's clock, which the sound is timed by too. The RTP timestamps of the video and of the
        /// sound start where the sender picked, at random: only its reports put the two on one clock.
        /// </summary>
        protected long VideoTime(uint rtpTimestamp, bool hasSenderSync, DateTime senderTime)
        {
            if (_restartTimeline)
            {
                // the new stream's time 0 where the old one's frames stopped, and as much after as the stream was gone
                _restartTimeline = false;
                _timelineOffset = _lastVideoTime < 0 ? 0 : _lastVideoTime + Stopwatch.GetElapsedTime(_lastVideoTimeAt).Ticks;
            }

            long time;
            long rtpTime = RtpVideoTime(rtpTimestamp);
            if (!hasSenderSync)
            {
                time = _hasSenderEpoch ? -1 : rtpTime;
            }
            else
            {
                if (!_hasSenderEpoch)
                {
                    _hasSenderEpoch = true;
                    _senderEpoch = senderTime.Ticks - rtpTime;
                }
                time = senderTime.Ticks - _senderEpoch;
            }

            if (time < 0)
                return time;
            time += _timelineOffset;
            _lastVideoTime = time;
            _lastVideoTimeAt = Stopwatch.GetTimestamp();
            return time;
        }

        /// <summary>A packet of sound's time, on the video's clock: -1 until both are on the sender's - see <see cref="VideoTime"/>.</summary>
        protected long AudioTime(bool hasSenderSync, DateTime senderTime)
        {
            return hasSenderSync && _hasSenderEpoch ? senderTime.Ticks - _senderEpoch + _timelineOffset : -1;
        }

        // Of a stream started again - a session reconnected - its clocks are new: its RTP timestamps and its sender's
        // clock start wherever they will. Its times go on from the last frame's, by as long as it was gone.
        private volatile bool _restartTimeline;
        private long _timelineOffset;
        private long _lastVideoTime = -1;
        private long _lastVideoTimeAt;

        /// <summary>
        /// The stream starts again, of new clocks: its next frame is timed from the last one's time, and as long after it as
        /// passed, rather than of its own RTP timestamps and sender, which have nothing to do with the old ones. Called before
        /// the new stream's first packet.
        /// </summary>
        protected void RestartTimeline()
        {
            _hasRtpTime = false;
            _hasSenderEpoch = false;
            _restartTimeline = true;
        }

        #endregion // Timing
    }
}
