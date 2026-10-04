using SharpAV1;
using SharpAVX;
using SharpVP9;
using SharpH264;
using SharpH265;
using SharpH26X;
using SharpISOBMFF;
using SharpISOBMFF.Extensions;
using SharpMediaFoundationInterop.Transforms.H264;
using SharpMediaFoundationInterop.Transforms.AV1;
using SharpMediaFoundationInterop.Transforms.H265;
using SharpMediaFoundationInterop.Transforms.VP9;
using SharpMediaFoundationInterop.Utils;
using SharpRTSPClient;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;

namespace SharpMediaFoundationInterop.WPF
{
    public class RtspSource : VideoSourceBase
    {
        private RTSPClient _rtspClient;
        private string _uri;
        private string _userName;
        private string _password;

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

        private PooledUnits RentUnits() => _spareUnits.TryTake(out var units) ? units : new PooledUnits();

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
        private byte[][] _parameterSets;

        protected override bool IsStreaming { get { return true; } }

        public RtspSource(string uri, string userName = null, string password = null)
        {
            this._uri = uri ?? throw new ArgumentNullException(nameof(uri));
            this._userName = userName;
            this._password = password;
            this._isLowLatency = true;
        }

        public async override Task InitializeAsync()
        {
            // One connection, made once: the client reconnects by itself, and initialized again - by the sound's thread as
            // by the video's - a second would be made beside it, feeding the same queues and decoders.
            if (_rtspClient == null)
            {
                var ret = await CreateClient(_uri, _userName, _password);
                VideoInfo = ret.Video;
                AudioInfo = ret.Audio;
            }
        }

        private async Task<(VideoInfo Video, AudioInfo Audio)> CreateClient(string uri, string userName, string password)
        {
            // Completed from the client's events, on its thread that reads the connection: run on that thread, whatever
            // awaits the setup - a caller with no synchronization context - kept it from reading another packet.
            var tcsSetupCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            VideoInfo videoInfo = null;
            AudioInfo audioInfo = null;

            // The first video track and the first audio track, which the client sets up when AcceptTrack is left unset;
            // their data is told apart by the index of its track.
            int videoTrackIndex = -1, audioTrackIndex = -1;

            _rtspClient = new RTSPClient();
            _rtspClient.NewTrack += (o, e) =>
            {
                if (e.Kind == TrackKind.Video && videoTrackIndex < 0)
                {
                    videoTrackIndex = e.TrackIndex;
                    videoInfo = CreateVideoInfo(e);
                }
                else if (e.Kind == TrackKind.Audio && audioTrackIndex < 0)
                {
                    audioTrackIndex = e.TrackIndex;
                    audioInfo = CreateAudioInfo(e);
                }
            };
            _rtspClient.SetupMessageCompleted += (o, e) =>
            {
                if (videoInfo != null && videoInfo.Width > 0 && videoInfo.Height > 0)
                {
                    tcsSetupCompleted.TrySetResult(true);
                }
            };

            _rtspClient.ReceivedData += (o, e) =>
            {
                if (e.TrackIndex == videoTrackIndex)
                {
                    var sample = RentUnits();
                    var parameterSets = Interlocked.Exchange(ref _parameterSets, null);
                    if (parameterSets != null)
                    {
                        foreach (var parameterSet in parameterSets)
                            sample.Add(parameterSet);
                    }
                    foreach (var received in e.Data.Data)
                        sample.Add(received.Span);
                    sample.Timestamp = VideoTime(e.Data);

                    // AV1 and VP9 say nothing of the picture's size in the SDP: the first sequence header, or key frame, does
                    if (videoInfo.Width == 0 || videoInfo.Height == 0)
                    {
                        if (TryReadSize(videoInfo.VideoCodec, sample.Units, out uint width, out uint height))
                        {
                            videoInfo.OriginalWidth = width;
                            videoInfo.OriginalHeight = height;
                            uint multiple = videoInfo.VideoCodec == "VP9" ? VP9Decoder.VP9_RES_MULTIPLE : AV1Decoder.AV1_RES_MULTIPLE;
                            videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, multiple);
                            videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, multiple);
                            tcsSetupCompleted.TrySetResult(true);
                        }
                    }
                    _videoSampleQueue.Enqueue(sample);
                }
                else if (e.TrackIndex == audioTrackIndex)
                {
                    // the time of the packet's first frame: that of the rest follows from the frames decoded before them
                    long time = AudioTime(e.Data);
                    foreach (var received in e.Data.Data)
                    {
                        var frame = RentUnits();
                        frame.Add(received.Span);
                        frame.Timestamp = time;
                        time = -1;
                        _audioSampleQueue.Enqueue(frame);
                    }
                }
            };
            _rtspClient.Connect(uri, RTPTransport.TCP, userName, password);

            await tcsSetupCompleted.Task;

            return (videoInfo, audioInfo);
        }

        private VideoInfo CreateVideoInfo(NewTrackEventArgs e)
        {
            var videoInfo = new VideoInfo();

            if (e.StreamConfigurationData is H264StreamConfigurationData h264cfg)
            {
                _parameterSets = new[] { h264cfg.SPS, h264cfg.PPS };

                var decodedSPS = ParseH264SPS(h264cfg.SPS);
                var dimensions = decodedSPS.CalculateDimensions();
                videoInfo.OriginalWidth = dimensions.Width;
                videoInfo.OriginalHeight = dimensions.Height;
                videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, H264Decoder.H264_RES_MULTIPLE);
                videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, H264Decoder.H264_RES_MULTIPLE);

                var timescale = decodedSPS.CalculateTimescale();
                videoInfo.FpsNom = (uint)timescale.Timescale;
                videoInfo.FpsDenom = (uint)timescale.FrameTick;

                videoInfo.VideoCodec = "H264";
            }
            else if (e.StreamConfigurationData is H265StreamConfigurationData h265cfg)
            {
                _parameterSets = new[] { h265cfg.VPS, h265cfg.SPS, h265cfg.PPS };

                var decodedSPS = ParseH265SPS(h265cfg.SPS);
                var dimensions = decodedSPS.CalculateDimensions();
                videoInfo.OriginalWidth = dimensions.Width;
                videoInfo.OriginalHeight = dimensions.Height;
                videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, H265Decoder.H265_RES_MULTIPLE);
                videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, H265Decoder.H265_RES_MULTIPLE);

                var timescale = decodedSPS.CalculateTimescale();
                videoInfo.FpsNom = (uint)timescale.Timescale;
                videoInfo.FpsDenom = (uint)timescale.FrameTick;

                videoInfo.VideoCodec = "H265";
            }
            else if (e.StreamConfigurationData is H266StreamConfigurationData)
            {
                // H266 is as of 8/3/2025 not supported by Media Foundation
                throw new NotSupportedException();
            }
            else if (e.Codec == "AV1" || e.Codec == "VP9")
            {
                // the size comes with the first sequence header, or key frame - see TryReadSize
                videoInfo.VideoCodec = e.Codec;
            }
            else
            {
                throw new NotSupportedException($"Video codec {e.Codec}");
            }

            // A frame rate is left unknown where the stream does not say it: every frame is shown by its RTP timestamp.

            return videoInfo;
        }

        private static AudioInfo CreateAudioInfo(NewTrackEventArgs e)
        {
            var audioInfo = new AudioInfo();

            if (e.StreamConfigurationData is AACStreamConfigurationData aaccfg)
            {
                audioInfo.AudioCodec = "AAC";
                audioInfo.BitsPerSample = 16;

                var descriptor = new AudioSpecificConfig();
                descriptor.SamplingFrequencyIndex = (byte)aaccfg.FrequencyIndex;
                descriptor.ChannelConfiguration = (byte)aaccfg.ChannelConfiguration;
                descriptor.ExtensionAudioObjectType = new GetAudioObjectType() { AudioObjectTypeExt = 5 }; // TODO
                descriptor.AudioObjectType = new GetAudioObjectType() { AudioObjectType = 2 }; // TODO simplify API
                descriptor._GASpecificConfig = new GASpecificConfig(aaccfg.FrequencyIndex, aaccfg.ChannelConfiguration, 2);

                audioInfo.UserData = descriptor.ToBytes();
                audioInfo.ChannelCount = aaccfg.ChannelConfiguration == 1 ? 1u : (uint)aaccfg.ChannelConfiguration; // ChannelConfiguration = 1 means mono even though ChannelCount = 2
                audioInfo.ChannelConfiguration = aaccfg.ChannelConfiguration;
                audioInfo.SampleRate = AudioSpecificConfigDescriptor.SamplingFrequencyMap[(uint)aaccfg.FrequencyIndex];
            }
            else if (e.Codec == "OPUS")
            {
                audioInfo.AudioCodec = "OPUS";
                audioInfo.BitsPerSample = 32;
                audioInfo.ChannelCount = 2;
                audioInfo.SampleRate = 48000;
            }
            else
            {
                throw new NotSupportedException($"Audio codec {e.Codec}");
            }

            return audioInfo;
        }

        /// <summary>
        /// The picture's size, of a stream whose SDP does not say it: AV1's of its sequence header, VP9's of a key frame's
        /// header - of the first frame of a superframe, where a key frame is. False until one of them comes.
        /// </summary>
        private static bool TryReadSize(string codec, IList<ArraySegment<byte>> units, out uint width, out uint height)
        {
            width = height = 0;
            foreach (var unit in units)
            {
                if (unit.Count == 0)
                    continue;

                if (codec == "AV1")
                {
                    int obuType = (unit[0] & 0x78) >> 3;
                    if (obuType != 1)
                        continue;

                    var context = new AV1Context();
                    using (var stream = new AomStream(new MemoryStream(unit.Array, unit.Offset, unit.Count, writable: false)))
                        context.Read(stream, unit.Count);
                    width = (uint)(context._MaxFrameWidthMinus1 + 1);
                    height = (uint)(context._MaxFrameHeightMinus1 + 1);
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
                    width = (uint)context._FrameWidth;
                    height = (uint)context._FrameHeight;
                    return true;
                }
            }
            return false;
        }

        private SharpH265.SeqParameterSetRbsp ParseH265SPS(byte[] sample)
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

        private SharpH264.SeqParameterSetRbsp ParseH264SPS(byte[] sample)
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
            timestamp = _videoInUse.Timestamp;
            return _videoInUse.Units;
        }

        /// <summary>The clock of the RTP timestamps of video: 90 kHz, for H.264, H.265, AV1 and VP9 alike.</summary>
        private const long VideoClockRate = 90000;

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
        // client's thread that receives, the one thread both streams' data comes on.
        private bool _hasSenderEpoch;
        private long _senderEpoch;

        /// <summary>
        /// A video frame's time, in 100 ns units: of its RTP timestamp, from the stream's first frame - and, once a sender
        /// report has come, of the sender's clock, which the sound is timed by too. The RTP timestamps of the video and of the
        /// sound start where the sender picked, at random: only its reports put the two on one clock.
        /// </summary>
        private long VideoTime(SimpleDataEventArgs data)
        {
            long rtpTime = RtpVideoTime(data.RtpTimestamp);
            if (!data.HasSenderSync)
                return _hasSenderEpoch ? -1 : rtpTime;

            if (!_hasSenderEpoch)
            {
                _hasSenderEpoch = true;
                _senderEpoch = data.Timestamp.Ticks - rtpTime;
            }
            return data.Timestamp.Ticks - _senderEpoch;
        }

        /// <summary>A packet of sound's time, on the video's clock: -1 until both are on the sender's - see <see cref="VideoTime"/>.</summary>
        private long AudioTime(SimpleDataEventArgs data)
        {
            return data.HasSenderSync && _hasSenderEpoch ? data.Timestamp.Ticks - _senderEpoch : -1;
        }
    }
}
