using SharpAV1;
using SharpH264;
using SharpH265;
using SharpISOBMFF;
using SharpISOBMFF.Extensions;
using SharpMediaFoundationInterop.Transforms.AV1;
using SharpMediaFoundationInterop.Transforms.H264;
using SharpMediaFoundationInterop.Transforms.H265;
using SharpMediaFoundationInterop.Transforms.VP9;
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

        private VideoReader _reader;
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

        // What a read hands out, filled again by each: the units are views of the reader's buffer for the track, valid until
        // its next sample is read, by which time they have been decoded - nothing is copied.
        private readonly List<ArraySegment<byte>> _audioUnits = new List<ArraySegment<byte>>();
        private readonly List<ArraySegment<byte>> _videoUnits = new List<ArraySegment<byte>>();

        protected override IList<ArraySegment<byte>> ReadNextAudio()
        {
            if (_audioTrack != null)
            {
                var sample = _reader.ReadSample(_audioTrack.TrackID);
                if (sample != null)
                {
                    _audioUnits.Clear();
                    foreach (var unit in _reader.ParseSample(_audioTrack.TrackID, sample.Data))
                        _audioUnits.Add(unit);
                    return _audioUnits;
                }
            }
            return null;
        }

        protected override IList<ArraySegment<byte>> ReadNextVideo(out long timestamp)
        {
            timestamp = -1;
            if (_videoTrack != null)
            {
                _videoUnits.Clear();
                if (_initial)
                {
                    // the parameter sets the sample entry holds, once, in front of the first sample and at its time
                    _initial = false;
                    foreach (var unit in _videoTrack.GetContainerSamples())
                        _videoUnits.Add(new ArraySegment<byte>(unit));
                }

                var sample = _reader.ReadSample(_videoTrack.TrackID);
                if (sample != null)
                {
                    // when the sample is shown, of its decode time and composition offset
                    timestamp = MediaUtils.ToTicks(sample.PTS, _videoTrack.Timescale);
                    foreach (var unit in _reader.ParseSample(_videoTrack.TrackID, sample.Data))
                        _videoUnits.Add(unit);
                    return _videoUnits;
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

            _reader = new VideoReader();
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
                    else if (_videoTrack is VP9Track vp9Track)
                    {
                        videoInfo.VideoCodec = "VP9";

                        // VP9 has no parameter sets to read the size of: the sample entry gives it, as the frames do
                        var entry = _reader.Tracks[vp9Track.TrackID].Stbl
                            .Children.OfType<SampleDescriptionBox>().Single()
                            .Children.OfType<VisualSampleEntry>().First();
                        videoInfo.OriginalWidth = entry.Width;
                        videoInfo.OriginalHeight = entry.Height;

                        videoInfo.FpsNom = vp9Track.Timescale;
                        videoInfo.FpsDenom = (uint)vp9Track.DefaultSampleDuration;

                        videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, VP9Decoder.VP9_RES_MULTIPLE);
                        videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, VP9Decoder.VP9_RES_MULTIPLE);
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
                        audioInfo.ChannelCount = aacTrack.ChannelConfiguration == 1 ? 1u : aacTrack.ChannelCount; // ChannelConfiguration = 1 means mono even though ChannelCount = 2
                        audioInfo.ChannelConfiguration = aacTrack.ChannelConfiguration;
                        audioInfo.SampleRate = aacTrack.SamplingRate;
                    }
                    else if(_audioTrack is OpusTrack opusTrack)
                    {
                        audioInfo.AudioCodec = "OPUS";
                        audioInfo.BitsPerSample = 32; // Opus is always 32 bit, but we transform it to 16-bit PCM
                        audioInfo.ChannelCount = opusTrack.ChannelCount;
                        audioInfo.ChannelConfiguration = opusTrack.ChannelCount;
                        audioInfo.SampleRate = opusTrack.SamplingRate;
                    }
                    else
                    {
                        //throw new NotSupportedException();
                        // no audio
                        audioInfo = null;
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
