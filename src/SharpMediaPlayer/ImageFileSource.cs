using SharpISOBMFF;
using SharpMediaFoundationInterop.Transforms.H265;
using SharpMediaFoundationInterop.Utils;
using SharpMP4.Readers;
using System.IO;

namespace SharpMediaFoundationInterop.WPF
{
    public class ImageFileSource : VideoSourceBase
    {
        private string _path;
        private BufferedStream _fs;
        private bool _initial = true;

        private ImageReader _reader;

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
            return null;
        }

        protected override IList<byte[]> ReadNextVideo()
        {
            if (_reader.Track != null)
            {
                if (_initial)
                {
                    _initial = false;
                    var videoUnits = _reader.Track.GetContainerSamples();
                    return videoUnits.ToList();
                }

                var sample = _reader.ReadSample();
                IEnumerable<byte[]> units = _reader.ParseSample(sample.Data);
                return units.ToList();
            }
            return null;
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

        private Task<(VideoInfo Video, AudioInfo Audio)> LoadFileAsync(string fileName)
        {
            VideoInfo videoInfo = new VideoInfo();

            if (_fs != null)
            {
                _fs.Dispose();
                _fs = null;
            }

            _fs = new BufferedStream(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read));
            var mp4 = new Container();
            mp4.Read(new IsoStream(_fs));

            _reader = new ImageReader();
            _reader.Parse(mp4);         

            videoInfo.OriginalWidth = _reader.Ispe.ImageWidth;
            videoInfo.OriginalHeight = _reader.Ispe.ImageHeight;
            videoInfo.FpsNom = 1;
            videoInfo.FpsDenom = 1;

            videoInfo.VideoCodec = "H265";
            videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, H265Decoder.H265_RES_MULTIPLE);
            videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, H265Decoder.H265_RES_MULTIPLE);

            VideoInfo = videoInfo;

            return Task.FromResult<(VideoInfo Video, AudioInfo Audio)>((videoInfo, null));
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
