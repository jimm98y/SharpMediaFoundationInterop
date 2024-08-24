using SharpMediaFoundation.Transforms.H264;
using SharpMediaFoundation.Transforms.H265;
using SharpMediaFoundation.Utils;
using SharpMp4;
using SharpRTSPClient;

namespace SharpMediaFoundation.WPF
{
    public class RtspSource : VideoSourceBase
    {
        private RTSPClient _rtspClient;
        private string _uri;
        private string _userName;
        private string _password;

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

                    var decodedSPS = H264SpsNalUnit.Parse(h264cfg.SPS);
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

                    var decodedSPS = H265SpsNalUnit.Parse(h265cfg.SPS);
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

                    var descriptor = new AudioSpecificConfigDescriptor();
                    descriptor.SamplingFrequencyIndex = aaccfg.FrequencyIndex;
                    descriptor.ChannelConfiguration = aaccfg.ChannelConfiguration;
                    descriptor.ExtensionAudioObjectType = 5; // TODO
                    descriptor.GaSpecificConfig = true;
                    descriptor.OriginalAudioObjectType = 2; // AAC
                    descriptor.OuterSyncExtensionType = 695; // TODO
                    descriptor.SyncExtensionType = 695; // TODO

                    audioInfo.UserData = descriptor.ToBytes().Result;
                    audioInfo.Channels = (uint)aaccfg.ChannelConfiguration;
                    audioInfo.SampleRate = (uint)AudioSpecificConfigDescriptor.SamplingFrequencyMap[aaccfg.FrequencyIndex];
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

        private void _rtspClient_ReceivedAudioData(object sender, SimpleDataEventArgs e)
        {
            foreach (var sample in e.Data)
            {
                _audioSampleQueue.Enqueue(sample.ToArray());
            }
        }

        private void _rtspClient_ReceivedVideoData(object sender, SimpleDataEventArgs e)
        {
            _videoSampleQueue.Enqueue(e.Data.Select(x => x.ToArray()).ToList());
        }
    }
}
