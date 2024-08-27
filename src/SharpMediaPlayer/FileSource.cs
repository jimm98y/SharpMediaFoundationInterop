using SharpMediaFoundation.Transforms.H264;
using SharpMediaFoundation.Transforms.H265;
using SharpMediaFoundation.Utils;
using SharpMp4;
using System.IO;

namespace SharpMediaFoundation.WPF
{
    public class FileSource : VideoSourceBase
    {
        private string _path;
        private BufferedStream _fs;
        private FragmentedMp4 _fmp4;
        private FragmentedMp4Extensions.MdatParserContext _context;

        public FileSource(string path)
        {
            this._path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public async override Task InitializeAsync()
        {
            if (VideoInfo == null)
            {
                var ret = await LoadFileAsync(_path);
                VideoInfo = ret.Video;
                AudioInfo = ret.Audio;
            }
        }

        protected override bool ReadNextAudio(out byte[] frame)
        {
            frame = _fmp4.ReadNextTrack(_context, (int)_context.AudioTrackId).Result?.FirstOrDefault(); // TODO async
            return frame != null;
        }

        protected override bool ReadNextVideo(out IList<byte[]> au)
        {
            if (_context.Position[_context.VideoTrackId.Value] == 0)
            {
                var result = _fmp4.ReadNextTrack(_context, (int)_context.VideoTrackId).Result;
                if (result == null)
                {
                    au = null;
                    return false;
                }

                au = _context.VideoNALUs.Concat(result).ToList(); // TODO async
            }
            else
            {
                au = _fmp4.ReadNextTrack(_context, (int)_context.VideoTrackId).Result; // TODO async
            }
            return au != null;
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
            AudioInfo audioInfo = null;

            if (_fmp4 != null)
            {
                _fmp4.Dispose();
                _fmp4 = null;
            }

            if (_fs != null)
            {
                _fs.Dispose();
                _fs = null;
            }

            _fs = new BufferedStream(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read));
            _fmp4 = await FragmentedMp4.ParseAsync(_fs);

            var videoTrackBox = _fmp4.FindVideoTracks().FirstOrDefault();
            var audioTrackBox = _fmp4.FindAudioTracks().FirstOrDefault();
            _context = await _fmp4.ParseMdatAsync();
                    
            var videoTrackId = videoTrackBox.GetTkhd().TrackId;
            var audioTrackId = audioTrackBox?.GetTkhd().TrackId;

            var vsbox = 
                videoTrackBox
                    .GetMdia()
                    .GetMinf()
                    .GetStbl()
                    .GetStsd()
                    .Children.Single((Mp4Box x) => x is VisualSampleEntryBox) as VisualSampleEntryBox;
            videoInfo.OriginalWidth = vsbox.Width;
            videoInfo.OriginalHeight = vsbox.Height;
            videoInfo.FpsNom = _fmp4.CalculateTimescale(videoTrackBox);
            videoInfo.FpsDenom = _fmp4.CalculateSampleDuration(videoTrackBox);
            if (vsbox.Children.FirstOrDefault(x => x is AvcConfigurationBox) != null)
            {
                videoInfo.VideoCodec = "H264";
                videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, H264Decoder.H264_RES_MULTIPLE);
                videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, H264Decoder.H264_RES_MULTIPLE);
            }
            else if (vsbox.Children.FirstOrDefault(x => x is HevcConfigurationBox) != null)
            {
                videoInfo.VideoCodec = "H265";
                videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, H265Decoder.H265_RES_MULTIPLE);
                videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, H265Decoder.H265_RES_MULTIPLE);
            }
            else
            {
                throw new NotSupportedException();
            }

            if (audioTrackBox != null)
            {
                audioInfo = new AudioInfo();

                var sourceAudioSampleBox =
                    audioTrackBox
                        .GetMdia()
                        .GetMinf()
                        .GetStbl()
                        .GetStsd()
                        .Children.Single((Mp4Box x) => x is AudioSampleEntryBox) as AudioSampleEntryBox;
                var audioDescriptor = sourceAudioSampleBox.GetAudioSpecificConfigDescriptor();

                if (sourceAudioSampleBox.Type == AudioSampleEntryBox.TYPE3)
                {
                    audioInfo.AudioCodec = "AAC";
                    audioInfo.BitsPerSample = 16;
                    audioInfo.UserData = await audioDescriptor.ToBytes();
                    audioInfo.Channels = (uint)audioDescriptor.ChannelConfiguration;
                    audioInfo.SampleRate = (uint)audioDescriptor.GetSamplingFrequency();
                }
                else
                {
                    throw new NotSupportedException();
                }
            }

            return (videoInfo, audioInfo);
        }

        protected override void Dispose(bool disposing)
        {
            if(disposing)
            {
                if(_fmp4 != null)
                {
                    _fmp4.Dispose();
                    _fmp4 = null;
                }

                if(_fs != null)
                {
                    _fs.Dispose();  
                    _fs = null;
                }
            }
        }
    }
}
