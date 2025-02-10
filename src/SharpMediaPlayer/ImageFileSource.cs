using SharpMediaFoundationInterop.Transforms.H265;
using SharpMediaFoundationInterop.Utils;
using SharpMp4;
using System.IO;

namespace SharpMediaFoundationInterop.WPF
{
    public class ImageFileSource : VideoSourceBase
    {
        private string _path;
        private BufferedStream _fs;
        private FragmentedMp4 _fmp4;
        private bool _initial = true;

        private FragmentedMp4Extensions.MdatParserContext _context;

        public ImageFileSource(string path)
        {
            this._path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public async override Task InitializeAsync()
        {
            if (VideoInfo == null)
            {
                var ret = await LoadFileAsync(_path);
                VideoInfo = ret.Video;
            }
        }

        protected override IList<byte[]> ReadNextAudio()
        {
            return _fmp4.ReadNextTrackAsync(_context, (int)_context.AudioTrackId).Result;
        }

        protected override IList<byte[]> ReadNextVideo()
        {
            if (_initial)
            {
                _initial = false;
                return _context.VideoNALUs;
            }
            else
            {
                if (_context.VideoTrack != null)
                {
                    return _fmp4.ReadNextTrackAsync(_context, (int)_context.VideoTrackId).Result;
                }
                else
                {
                    // image
                    return _fmp4.ReadNextImageAsync(_context).Result;
                }
            }
        }

        protected override void CompletedVideo()
        {
            VideoInfo = null;
            AudioInfo = null;
            base.CompletedVideo();
        }

        protected override void CompletedAudio()
        {
            VideoInfo = null;
            AudioInfo = null;
            base.CompletedAudio();
        }

        private async Task<(VideoInfo Video, AudioInfo Audio)> LoadFileAsync(string fileName)
        {
            VideoInfo videoInfo = new VideoInfo();

            if (_fs != null)
            {
                _fs.Dispose();
                _fs = null;
            }

            _fs = new BufferedStream(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read));
            _fmp4 = await FragmentedMp4.ParseAsync(_fs);

            var hevcCfg = _fmp4.GetMeta().GetIprp().GetIpco()
                .Children.Single((Mp4Box x) => x is HevcConfigurationBox) as HevcConfigurationBox;

            _context = new FragmentedMp4Extensions.MdatParserContext()
            {
                VideoNALUs = hevcCfg.HevcDecoderConfigurationRecord.NalArrays.SelectMany(x => x.NalUnits).ToList()
            };
            _context.Mdat[0] = _fmp4.Children.FirstOrDefault(x => x is MdatBox) as MdatBox;

            var ispe = _fmp4.GetMeta().GetIprp().GetIpco()
                .Children.FirstOrDefault((Mp4Box x) => x is IspeBox) as IspeBox;

            videoInfo.OriginalWidth = (uint)ispe.ImageWidth;
            videoInfo.OriginalHeight = (uint)ispe.ImageHeight;
            videoInfo.FpsNom = 1;
            videoInfo.FpsDenom = 1;

            videoInfo.VideoCodec = "H265";
            videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, H265Decoder.H265_RES_MULTIPLE);
            videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, H265Decoder.H265_RES_MULTIPLE);

            return (videoInfo, null);
        }

        protected override void Dispose(bool disposing)
        {
            if(disposing)
            {
                if(_fs != null)
                {
                    _fs.Dispose();  
                    _fs = null;
                }
            }
        }
    }
}
