using SharpMediaFoundation.H264;
using SharpMediaFoundation.H265;
using SharpMediaFoundation.NV12;
using SharpRTSPClient;
using System.Buffers;

namespace SharpMediaFoundation.WPF
{
    public class RtspSource : IVideoSource
    {
        public VideoInfo Info { get; set; }

        private IVideoTransform _videoDecoder;
        private NV12toRGB _nv12Decoder;
        private Queue<IList<byte[]>> _sampleQueue = new Queue<IList<byte[]>>();
        private Queue<byte[]> _renderQueue = new Queue<byte[]>();
        private byte[] _nv12Buffer;
        private long _time = 0;

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

        public async Task InitializeAsync()
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
                    _sampleQueue.Enqueue(new List<byte[]> { h264cfg.SPS, h264cfg.PPS });

                    var decodedSPS = SharpMp4.H264SpsNalUnit.Parse(h264cfg.SPS);
                    var dimensions = decodedSPS.CalculateDimensions();
                    videoInfo.OriginalWidth = dimensions.Width;
                    videoInfo.OriginalHeight = dimensions.Height;
                    videoInfo.Width = MathUtils.RoundToMultipleOf(videoInfo.OriginalWidth, H264Decoder.H264_RES_MULTIPLE);
                    videoInfo.Height = MathUtils.RoundToMultipleOf(videoInfo.OriginalHeight, H264Decoder.H264_RES_MULTIPLE);

                    var timescale = decodedSPS.CalculateTimescale();
                    videoInfo.FpsNom = (uint)timescale.Timescale;
                    videoInfo.FpsDenom = (uint)timescale.FrameTick;

                    videoInfo.VideoCodec = "H264";
                }
                else if (e.StreamConfigurationData is H265StreamConfigurationData h265cfg)
                {
                    _sampleQueue.Enqueue(new List<byte[]> { h265cfg.VPS, h265cfg.SPS, h265cfg.PPS });

                    var decodedSPS = SharpMp4.H265SpsNalUnit.Parse(h265cfg.SPS);
                    var dimensions = decodedSPS.CalculateDimensions();
                    videoInfo.OriginalWidth = dimensions.Width;
                    videoInfo.OriginalHeight = dimensions.Height;
                    videoInfo.Width = MathUtils.RoundToMultipleOf(videoInfo.OriginalWidth, H265Decoder.H265_RES_MULTIPLE);
                    videoInfo.Height = MathUtils.RoundToMultipleOf(videoInfo.OriginalHeight, H265Decoder.H265_RES_MULTIPLE);

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
            _sampleQueue.Enqueue(e.Data.Select(x => x.ToArray()).ToList());
        }

        public Task<byte[]> GetSampleAsync()
        {
            if (_videoDecoder == null || _nv12Decoder == null)
            {
                CreateDecoder(Info);
            }

            byte[] existing;
            if (_renderQueue.TryDequeue(out existing))
                return Task.FromResult(existing);

            while (_renderQueue.Count == 0 && _sampleQueue.TryDequeue(out var au))
            {
                foreach (var nalu in au)
                {
                    if (_videoDecoder.ProcessInput(nalu, _time))
                    {
                        while (_videoDecoder.ProcessOutput(ref _nv12Buffer, out _))
                        {
                            _nv12Decoder.ProcessInput(_nv12Buffer, _time);

                            byte[] decoded = ArrayPool<byte>.Shared.Rent((int)(Info.Width * Info.Height * 3));
                            _nv12Decoder.ProcessOutput(ref decoded, out _);

                            _renderQueue.Enqueue(decoded);
                        }
                    }
                }
                _time += 10000 * 1000 / (Info.FpsNom / Info.FpsDenom); // 100ns units
            }

            if (_renderQueue.TryDequeue(out existing))
            {
                return Task.FromResult(existing);
            }

            return Task.FromResult<byte[]>(null);
        }

        private void CreateDecoder(VideoInfo info)
        {
            // decoders must be created on the same thread as the samples
            if (info.VideoCodec == "H264")
            {
                _videoDecoder = new H264Decoder(info.OriginalWidth, info.OriginalHeight, info.FpsNom, info.FpsDenom);
            }
            else if (info.VideoCodec == "H265")
            {
                _videoDecoder = new H265Decoder(info.OriginalWidth, info.OriginalHeight, info.FpsNom, info.FpsDenom);
            }
            else
            {
                throw new NotSupportedException();
            }

            _nv12Decoder = new NV12toRGB(info.Width, info.Height);
            _nv12Buffer = new byte[info.Width * info.Height * 3];
        }
    }
}
