using SharpAV1;
using SharpH264;
using SharpH265;
using SharpISOBMFF;
using SharpISOBMFF.Extensions;
using SharpMediaFoundationInterop.Transforms.AV1;
using SharpMediaFoundationInterop.Transforms.H264;
using SharpMediaFoundationInterop.Transforms.H265;
using SharpMediaFoundationInterop.Utils;
using SharpMP4.Readers;
using SharpMP4.Tracks;
using System.IO;

namespace SharpMediaFoundationInterop.WPF
{
    public class VideoFileSource : VideoSourceBase
    {
        private string _path;
        private BufferedStream _fs;
        private bool _initial = true;

        private Mp4Reader _reader;
        private ITrack _videoTrack;
        private ITrack _audioTrack;

        public VideoFileSource(string path)
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

        protected override IList<byte[]> ReadNextAudio()
        {
            if (_audioTrack != null)
            {
                var sample = _reader.ReadSample(_audioTrack.TrackID);
                if (sample != null)
                {
                    IEnumerable<byte[]> units = _reader.ParseSample(_audioTrack.TrackID, sample.Data);
                    return units.ToList();
                }
            }
            return null;
        }

        protected override IList<byte[]> ReadNextVideo()
        {
            if (_videoTrack != null)
            {
                if (_initial)
                {
                    _initial = false;
                    var videoUnits = _videoTrack.GetContainerSamples();
                    return videoUnits.ToList();
                }

                var sample = _reader.ReadSample(_videoTrack.TrackID);
                if (sample != null)
                {
                    IEnumerable<byte[]> units = _reader.ParseSample(_videoTrack.TrackID, sample.Data);
                    return units.ToList();
                }
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
            AudioInfo audioInfo = null;

            if (_fs != null)
            {
                _fs.Dispose();
                _fs = null;
            }

            _fs = new BufferedStream(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read));
            var mp4 = new Container();
            mp4.Read(new IsoStream(_fs));

            _reader = new Mp4Reader();
            _reader.Parse(mp4);
            IEnumerable<ITrack> inputTracks = _reader.GetTracks();

            _videoTrack = inputTracks.FirstOrDefault(t => t.HandlerType == HandlerTypes.Video);
            _audioTrack = inputTracks.FirstOrDefault(t => t.HandlerType == HandlerTypes.Sound);

            if (_videoTrack != null || _audioTrack != null)
            {
                _initial = true;

                if (_videoTrack != null)
                {
                    if (_videoTrack is H264Track h264Track)
                    {
                        videoInfo.VideoCodec = "H264";

                        var dimensions = h264Track.Sps.First().Value.CalculateDimensions();
                        videoInfo.OriginalWidth = dimensions.Width;
                        videoInfo.OriginalHeight = dimensions.Height;

                        videoInfo.FpsNom = h264Track.Timescale;
                        videoInfo.FpsDenom = (uint)h264Track.DefaultSampleDuration;

                        videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, H264Decoder.H264_RES_MULTIPLE);
                        videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, H264Decoder.H264_RES_MULTIPLE);
                    }
                    else if (_videoTrack is H265Track h265Track)
                    {
                        videoInfo.VideoCodec = "H265";

                        var dimensions = h265Track.Sps.First().Value.CalculateDimensions();
                        videoInfo.OriginalWidth = dimensions.Width;
                        videoInfo.OriginalHeight = dimensions.Height;

                        videoInfo.FpsNom = h265Track.Timescale;
                        videoInfo.FpsDenom = (uint)h265Track.DefaultSampleDuration;

                        videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, H265Decoder.H265_RES_MULTIPLE);
                        videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, H265Decoder.H265_RES_MULTIPLE);
                    }
                    else if (_videoTrack is AV1Track av1Track)
                    {
                        videoInfo.VideoCodec = "AV1";

                        var dimensions = av1Track.SequenceHeaderObu.CalculateDimensions();
                        videoInfo.OriginalWidth = dimensions.Width;
                        videoInfo.OriginalHeight = dimensions.Height;

                        videoInfo.FpsNom = av1Track.Timescale;
                        videoInfo.FpsDenom = (uint)av1Track.DefaultSampleDuration;

                        videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, AV1Decoder.AV1_RES_MULTIPLE);
                        videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, AV1Decoder.AV1_RES_MULTIPLE);
                    }
                    else
                    {
                        throw new NotSupportedException();
                    }
                }

                if (_audioTrack != null)
                {
                    audioInfo = new AudioInfo();

                    if (_audioTrack is AACTrack aacTrack)
                    {
                        audioInfo.AudioCodec = "AAC";
                        audioInfo.BitsPerSample = 16;
                        audioInfo.UserData = aacTrack.AudioSpecificConfig.ToBytes();
                        audioInfo.ChannelCount = aacTrack.ChannelCount;
                        audioInfo.ChannelConfiguration = aacTrack.ChannelConfiguration;
                        audioInfo.SampleRate = aacTrack.SamplingRate;
                    }
                    else if(_audioTrack is OpusTrack opusTrack)
                    {
                        audioInfo.AudioCodec = "OPUS";
                        audioInfo.BitsPerSample = 16; // Opus is always 32 bit, but we transform it to 16-bit PCM
                        audioInfo.ChannelCount = opusTrack.ChannelCount;
                        audioInfo.ChannelConfiguration = opusTrack.ChannelCount;
                        audioInfo.SampleRate = opusTrack.SamplingRate;
                    }
                    else
                    {
                        throw new NotSupportedException();
                    }
                }
            }
            else
            {
                throw new NotSupportedException();
            }

            return Task.FromResult((videoInfo, audioInfo));
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
