using SharpMediaFoundation.H264;
using SharpMediaFoundation.H265;
using SharpRTSPClient;

namespace SharpMediaFoundation.WPF
{
    public class RtspSource : VideoSourceBase
    {
        private RTSPClient _rtspClient;
        private string _uri;
        private string _userName;
        private string _password;

        public RtspSource(string uri, string userName = null, string password = null)
        {
            this._uri = uri ?? throw new ArgumentNullException(nameof(uri));
            this._userName = userName;
            this._password = password;
        }

        public async override Task InitializeAsync()
        {
            Info = await CreateClient(_uri, _userName, _password);
        }

        private Task<VideoInfo> CreateClient(string uri, string userName, string password)
        {
            var tcs = new TaskCompletionSource<VideoInfo>();

            _rtspClient = new RTSPClient();
            _rtspClient.NewVideoStream += (o, e) =>
            {
                var videoInfo = new VideoInfo();

                if (e.StreamConfigurationData is H264StreamConfigurationData h264cfg)
                {
                    _videoSampleQueue.Enqueue(new List<byte[]> { h264cfg.SPS, h264cfg.PPS });

                    var decodedSPS = SharpMp4.H264SpsNalUnit.Parse(h264cfg.SPS);
                    var dimensions = decodedSPS.CalculateDimensions();
                    videoInfo.OriginalWidth = dimensions.Width;
                    videoInfo.OriginalHeight = dimensions.Height;
                    videoInfo.Width = MFUtils.RoundToMultipleOf(videoInfo.OriginalWidth, H264Decoder.H264_RES_MULTIPLE);
                    videoInfo.Height = MFUtils.RoundToMultipleOf(videoInfo.OriginalHeight, H264Decoder.H264_RES_MULTIPLE);

                    var timescale = decodedSPS.CalculateTimescale();
                    videoInfo.FpsNom = (uint)timescale.Timescale;
                    videoInfo.FpsDenom = (uint)timescale.FrameTick;

                    videoInfo.VideoCodec = "H264";
                }
                else if (e.StreamConfigurationData is H265StreamConfigurationData h265cfg)
                {
                    _videoSampleQueue.Enqueue(new List<byte[]> { h265cfg.VPS, h265cfg.SPS, h265cfg.PPS });

                    var decodedSPS = SharpMp4.H265SpsNalUnit.Parse(h265cfg.SPS);
                    var dimensions = decodedSPS.CalculateDimensions();
                    videoInfo.OriginalWidth = dimensions.Width;
                    videoInfo.OriginalHeight = dimensions.Height;
                    videoInfo.Width = MFUtils.RoundToMultipleOf(videoInfo.OriginalWidth, H265Decoder.H265_RES_MULTIPLE);
                    videoInfo.Height = MFUtils.RoundToMultipleOf(videoInfo.OriginalHeight, H265Decoder.H265_RES_MULTIPLE);

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

                tcs.SetResult(videoInfo);
            };

            _rtspClient.ReceivedVideoData += _rtspClient_ReceivedVideoData;
            _rtspClient.Connect(uri, RTPTransport.TCP, userName, password, MediaRequest.VIDEO_ONLY, false);

            return tcs.Task;
        }

        private void _rtspClient_ReceivedVideoData(object sender, SimpleDataEventArgs e)
        {
            _videoSampleQueue.Enqueue(e.Data.Select(x => x.ToArray()).ToList());
        }
    }
}
