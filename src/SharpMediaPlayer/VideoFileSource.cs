using SharpAV1;
using SharpH264;
using SharpH265;
using SharpISOBMFF;
using SharpISOBMFF.Extensions;
using SharpMediaFoundationInterop.Transforms.AV1;
using SharpMediaFoundationInterop.Transforms.H264;
using SharpMediaFoundationInterop.Transforms.H262;
using SharpMediaFoundationInterop.Transforms.H263;
using SharpMediaFoundationInterop.Transforms.H265;
using SharpMediaFoundationInterop.Transforms.MPEG4;
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

        /// <summary>
        /// The reader, and the stream under it, are read by the video's thread and the sound's: one at a time, a sample, a
        /// seek or the file read again. What is read is in the track's own buffer, so the lock is not held while it is decoded.
        /// </summary>
        private readonly object _readerLock = new object();

        public override Task InitializeAsync()
        {
            lock (_readerLock)
            {
                if (VideoInfo == null)
                {
                    var ret = LoadFileAsync(_path).GetAwaiter().GetResult();
                    VideoInfo = ret.Video;
                    AudioInfo = ret.Audio;
                }
            }
            return Task.CompletedTask;
        }

        // What a read hands out, filled again by each: the units are views of the reader's buffer for the track, valid until
        // its next sample is read, by which time they have been decoded - nothing is copied.
        private readonly List<ArraySegment<byte>> _audioUnits = new List<ArraySegment<byte>>();
        private readonly List<ArraySegment<byte>> _videoUnits = new List<ArraySegment<byte>>();

        // MPEG-1/2, H.263 and MPEG-4 Part 2 pictures go to their decoders whole, not unit by unit - the headers the sample entry
        // holds in front of the first after opening or seeking, in a buffer kept for it
        private bool _wholeSamples;
        private byte[] _headedSample = Array.Empty<byte>();

        protected override IList<ArraySegment<byte>> ReadNextAudio() => ReadNextAudio(out _);

        protected override IList<ArraySegment<byte>> ReadNextAudio(out long timestamp)
        {
            lock (_readerLock)
                return ReadNextAudioLocked(out timestamp);
        }

        private IList<ArraySegment<byte>> ReadNextAudioLocked(out long timestamp)
        {
            timestamp = -1;
            if (_audioTrack != null)
            {
                var sample = _reader.ReadSample(_audioTrack.TrackID);
                if (sample != null)
                {
                    // of the track's own timescale, on the clock of the video's samples: both count from the file's start
                    timestamp = MediaUtils.ToTicks(sample.PTS, _audioTrack.Timescale);
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
            lock (_readerLock)
                return ReadNextVideoLocked(out timestamp);
        }

        private IList<ArraySegment<byte>> ReadNextVideoLocked(out long timestamp)
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
                    if (_wholeSamples)
                    {
                        _videoUnits.Add(WithHeaders(sample.Data));
                        return _videoUnits;
                    }
                    foreach (var unit in _reader.ParseSample(_videoTrack.TrackID, sample.Data))
                        _videoUnits.Add(unit);
                    return _videoUnits;
                }
            }
            return null;
        }

        /// <summary>The sample with the headers read before it in front of it, in one piece; the sample itself where none were.</summary>
        private ArraySegment<byte> WithHeaders(ArraySegment<byte> sample)
        {
            if (_videoUnits.Count == 0)
                return sample;

            int length = sample.Count;
            foreach (var header in _videoUnits)
                length += header.Count;
            if (_headedSample.Length < length)
                _headedSample = new byte[length];

            int offset = 0;
            foreach (var header in _videoUnits)
            {
                header.AsSpan().CopyTo(_headedSample.AsSpan(offset));
                offset += header.Count;
            }
            sample.AsSpan().CopyTo(_headedSample.AsSpan(offset));
            _videoUnits.Clear();
            return new ArraySegment<byte>(_headedSample, 0, length);
        }

        protected override void CompletedVideo()
        {
            // a file that cannot be sought in is read again from the start, to play again
            if (!CanSeek)
            {
                VideoInfo = null;
                AudioInfo = null;
            }
            base.CompletedVideo();
        }

        protected override void CompletedAudio()
        {
            if (!CanSeek)
            {
                VideoInfo = null;
                AudioInfo = null;
            }
            base.CompletedAudio();
        }

        #region Seeking

        // The times of the video's key frames, in 100 ns units, in order, and the numbers of their samples; the times of the
        // audio's samples, of the 'moov' and of any fragments. Null for a file whose track has no samples.
        private long[] _syncTimes;
        private uint[] _syncSamples;
        private long[] _audioTimes;
        private long _startTime;
        private long _duration = -1;

        public override bool CanSeek => _syncTimes != null && _syncTimes.Length > 0;
        public override long Duration => _duration;
        public override long StartTime => _startTime;

        protected override long SeekVideoToSync(long time, bool after)
        {
            lock (_readerLock)
                return SeekVideoToSyncLocked(time, after);
        }

        private long SeekVideoToSyncLocked(long time, bool after)
        {
            if (!CanSeek)
                return -1;

            int i = Array.BinarySearch(_syncTimes, time);
            if (after)
                i = i >= 0 ? i + 1 : ~i;          // the first after it
            else
                i = i >= 0 ? i : ~i - 1;          // the last at or before it
            if (i < 0 || i >= _syncTimes.Length)
                return -1;

            // the parameter sets go in again, with the key frame
            _initial = true;
            _reader.SeekSample(_videoTrack.TrackID, _syncSamples[i]);
            return _syncTimes[i];
        }

        protected override void SeekAudio(long time)
        {
            lock (_readerLock)
                SeekAudioLocked(time);
        }

        private void SeekAudioLocked(long time)
        {
            if (_audioTimes == null)
                return;

            int i = Array.BinarySearch(_audioTimes, time);
            i = i >= 0 ? i : Math.Max(0, ~i - 1);
            _reader.SeekSample(_audioTrack.TrackID, (uint)i);
        }

        /// <summary>The times of the samples, which seeking finds its way by.</summary>
        private void ReadSampleTimes()
        {
            _syncTimes = null;
            _syncSamples = null;
            _audioTimes = null;
            _startTime = 0;
            _duration = -1;

            var video = _videoTrack == null ? null : _reader.GetSampleTimings(_videoTrack.TrackID);
            if (video == null || video.Length == 0)
                return;

            var syncs = new List<(long Time, uint Sample)>();
            long start = long.MaxValue, end = long.MinValue;
            for (int i = 0; i < video.Length; i++)
            {
                long time = MediaUtils.ToTicks(video[i].PTS, _videoTrack.Timescale);
                start = Math.Min(start, time);
                end = Math.Max(end, MediaUtils.ToTicks(video[i].PTS + video[i].Duration, _videoTrack.Timescale));
                if (video[i].IsSyncSample)
                    syncs.Add((time, (uint)i));
            }
            syncs.Sort((a, b) => a.Time.CompareTo(b.Time));
            _syncTimes = syncs.Select(s => s.Time).ToArray();
            _syncSamples = syncs.Select(s => s.Sample).ToArray();
            _startTime = start;
            _duration = end - start;

            var audio = _audioTrack == null ? null : _reader.GetSampleTimings(_audioTrack.TrackID);
            if (audio != null)
                _audioTimes = audio.Select(a => MediaUtils.ToTicks(a.PTS, _audioTrack.Timescale)).ToArray();
        }

        #endregion

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
                ReadSampleTimes();

                if (_videoTrack != null)
                {
                    _wholeSamples = _videoTrack is H262Track || _videoTrack is H263Track || _videoTrack is MPEG4Track;
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
                    else if (_wholeSamples)
                    {
                        // MPEG-1 as MPEG-2: the one decoder decodes both
                        videoInfo.VideoCodec = _videoTrack is H262Track ? "H262" : _videoTrack is H263Track ? "H263" : "MPEG4";
                        uint multiple = _videoTrack is H262Track ? H262Decoder.H262_RES_MULTIPLE
                            : _videoTrack is H263Track ? H263Decoder.H263_RES_MULTIPLE : Mpeg4Decoder.MPEG4_RES_MULTIPLE;

                        // the sizes the sample entry gives, as for VP9
                        var entry = _reader.Tracks[_videoTrack.TrackID].Stbl
                            .Children.OfType<SampleDescriptionBox>().Single()
                            .Children.OfType<VisualSampleEntry>().First();
                        videoInfo.OriginalWidth = entry.Width;
                        videoInfo.OriginalHeight = entry.Height;

                        videoInfo.FpsNom = _videoTrack.Timescale;
                        videoInfo.FpsDenom = (uint)_videoTrack.DefaultSampleDuration;

                        videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, multiple);
                        videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, multiple);
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
                lock (_readerLock)
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
}
