using SharpISOBMFF;
using SharpMediaFoundationInterop.Codecs;
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

        protected override IList<ArraySegment<byte>> ReadNextAudio()
        {
            return null;
        }

        // What a read hands out, filled again by each: views of the image's sample, decoded before the next read.
        private readonly List<ArraySegment<byte>> _videoUnits = new List<ArraySegment<byte>>();

        protected override IList<ArraySegment<byte>> ReadNextVideo(out long timestamp)
        {
            timestamp = -1;
            if (_reader.Track != null)
            {
                _videoUnits.Clear();
                if (_initial)
                {
                    // the parameter sets, in front of the picture
                    _initial = false;
                    foreach (var unit in _reader.Track.GetContainerSamples())
                        _videoUnits.Add(new ArraySegment<byte>(unit));
                }

                var sample = _reader.ReadSample();
                if (sample == null)
                    return null; // the image is shown

                // an image is one picture, shown from the start
                timestamp = 0;
                foreach (var unit in _reader.ParseSample(sample.Data))
                    _videoUnits.Add(unit);
                return _videoUnits;
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
            videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, MediaCodecs.DecoderAlignment(VideoCodec.H265));
            videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, MediaCodecs.DecoderAlignment(VideoCodec.H265));

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
