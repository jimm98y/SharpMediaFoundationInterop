using SharpH264;
using SharpH265;
using SharpH26X;
using SharpISOBMFF;
using SharpISOBMFF.Extensions;
using SharpMediaFoundationInterop.Transforms.H264;
using SharpMediaFoundationInterop.Transforms.H265;
using SharpMediaFoundationInterop.Utils;
using SharpRTSPClient;
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

        protected ConcurrentQueue<IList<byte[]>> _videoSampleQueue = new ConcurrentQueue<IList<byte[]>>();
        protected ConcurrentQueue<IList<byte[]>> _audioSampleQueue = new ConcurrentQueue<IList<byte[]>>();

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
            var tcsSetupCompleted = new TaskCompletionSource<bool>();
            VideoInfo videoInfo = null;
            AudioInfo audioInfo = null;

            _rtspClient = new RTSPClient();
            _rtspClient.NewVideoStream += (o, e) =>
            {
                if (e.StreamConfigurationData is H264StreamConfigurationData h264cfg)
                {
                    videoInfo = new VideoInfo();
                    _videoSampleQueue.Enqueue(new List<byte[]> { h264cfg.SPS, h264cfg.PPS });

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
                    videoInfo = new VideoInfo(); 
                    _videoSampleQueue.Enqueue(new List<byte[]> { h265cfg.VPS, h265cfg.SPS, h265cfg.PPS });

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
                else if (e.StreamConfigurationData is H266StreamConfigurationData h266cfg)
                {
                    // H266 is as of 8/3/2025 not supported by Media Foundation
                    throw new NotSupportedException();
                }
                else if(e.StreamType == "AV1")
                {
                    videoInfo = new VideoInfo();
                    
                    //videoInfo.OriginalWidth = 1280;
                    //videoInfo.OriginalHeight = 720;
                    //videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, 1);
                    //videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, 1);

                    videoInfo.VideoCodec = "AV1";
                }
                else
                {
                    throw new NotSupportedException();
                }

                if (videoInfo.FpsNom == 0 || videoInfo.FpsDenom == 0)
                {
                    videoInfo.FpsNom = 24000;
                    videoInfo.FpsDenom = 1001;
                }
            };
            _rtspClient.NewAudioStream += (o, e) =>
            {
                if (e.StreamConfigurationData is AACStreamConfigurationData aaccfg)
                {
                    audioInfo = new AudioInfo();
                    audioInfo.AudioCodec = "AAC"; 
                    audioInfo.BitsPerSample = 16;

                    var descriptor = new AudioSpecificConfig();
                    descriptor.SamplingFrequencyIndex = (byte)aaccfg.FrequencyIndex;
                    descriptor.ChannelConfiguration = (byte)aaccfg.ChannelConfiguration;
                    descriptor.ExtensionAudioObjectType = new GetAudioObjectType() { AudioObjectTypeExt = 5 }; // TODO
                    descriptor.AudioObjectType = new GetAudioObjectType() { AudioObjectType = 2 }; // TODO simplify API
                    descriptor._GASpecificConfig = new GASpecificConfig((int)AudioSpecificConfigDescriptor.SamplingFrequencyMap[(uint)aaccfg.FrequencyIndex], aaccfg.ChannelConfiguration, 2);
                    descriptor.SyncExtensionType = 695; // TODO

                    audioInfo.UserData = descriptor.ToBytes();
                    audioInfo.ChannelCount = (uint)aaccfg.ChannelConfiguration;
                    audioInfo.SampleRate = AudioSpecificConfigDescriptor.SamplingFrequencyMap[(uint)aaccfg.FrequencyIndex];
                }
                else
                {
                    throw new NotSupportedException();
                }
            };
            _rtspClient.SetupMessageCompleted += (o, e) =>
            {
                tcsSetupCompleted.SetResult(true);
            };

            _rtspClient.ReceivedVideoData += _rtspClient_ReceivedVideoData;
            _rtspClient.ReceivedAudioData += _rtspClient_ReceivedAudioData;
            _rtspClient.Connect(uri, RTPTransport.TCP, userName, password);

            await tcsSetupCompleted.Task;
            return (videoInfo, audioInfo);
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

        private void _rtspClient_ReceivedAudioData(object sender, SimpleDataEventArgs e)
        {
            foreach (var sample in e.Data)
            {
                _audioSampleQueue.Enqueue(new List<byte[]> { sample.ToArray() });
            }
        }

        private void _rtspClient_ReceivedVideoData(object sender, SimpleDataEventArgs e)
        {
            _videoSampleQueue.Enqueue(e.Data.Select(x => x.ToArray()).ToList());
        }

        protected override IList<byte[]> ReadNextAudio()
        {
            if (_audioSampleQueue.TryDequeue(out var frame))
                return frame;
            else
                return null;
        }

        protected override IList<byte[]> ReadNextVideo()
        {
            if (_videoSampleQueue.TryDequeue(out var frame))
                return frame;
            else
                return null;
        }
    }
}
