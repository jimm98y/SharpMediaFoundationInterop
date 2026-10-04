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

        private void Enqueue(ConcurrentQueue<PooledUnits> queue, params byte[][] units)
        {
            var pooled = RentUnits();
            foreach (var unit in units)
                pooled.Add(unit);
            queue.Enqueue(pooled);
        }

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
            if (VideoInfo == null || _videoSampleQueue.Count == 0)
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
                    foreach (var received in e.Data.Data)
                        sample.Add(received.Span);

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
                    foreach (var received in e.Data.Data)
                    {
                        var frame = RentUnits();
                        frame.Add(received.Span);
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
                Enqueue(_videoSampleQueue, h264cfg.SPS, h264cfg.PPS);

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
                Enqueue(_videoSampleQueue, h265cfg.VPS, h265cfg.SPS, h265cfg.PPS);

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

            if (videoInfo.FpsNom == 0 || videoInfo.FpsDenom == 0)
            {
                videoInfo.FpsNom = 24000;
                videoInfo.FpsDenom = 1001;
            }

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

        protected override IList<ArraySegment<byte>> ReadNextAudio()
        {
            Release(ref _audioInUse);
            if (!_audioSampleQueue.TryDequeue(out _audioInUse))
                return null;
            return _audioInUse.Units;
        }

        protected override IList<ArraySegment<byte>> ReadNextVideo()
        {
            Release(ref _videoInUse);
            if (!_videoSampleQueue.TryDequeue(out _videoInUse))
                return null;
            return _videoInUse.Units;
        }
    }
}
