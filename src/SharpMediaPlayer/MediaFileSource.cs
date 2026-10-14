using SharpAV1;
using SharpH264;
using SharpH265;
using SharpISOBMFF;
using SharpISOBMFF.Extensions;
using SharpMediaFoundationInterop.Codecs;
using SharpMediaFoundationInterop.Utils;
using SharpMP4.Readers;
using SharpMP4.Tracks;
using System.IO;

namespace SharpMediaFoundationInterop.WPF
{
    public class MediaFileSource : MediaSourceBase, ISubtitleSource
    {
        private string _path;
        private BufferedStream _fs;
        private bool _initial = true;

        private VideoReader _reader;
        private ITrack _videoTrack;
        private ITrack _audioTrack;

        public MediaFileSource(string path)
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
                // PCM's every frame a sample: a tenth of a second of them in one read, not a read of each
                var sample = _audioTrack is PcmTrack pcm
                    ? _reader.ReadSamples(_audioTrack.TrackID, (int)Math.Max(1, pcm.SampleRate / 10))
                    : _reader.ReadSample(_audioTrack.TrackID);
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

        #region Subtitles

        private List<SubtitleTrackInfo> _subtitleTracks = new List<SubtitleTrackInfo>();
        private List<List<Subtitle>> _subtitles = new List<List<Subtitle>>();

        public IReadOnlyList<SubtitleTrackInfo> SubtitleTracks => _subtitleTracks;

        public IReadOnlyList<Subtitle> GetSubtitles(int track) =>
            track >= 0 && track < _subtitles.Count ? _subtitles[track] : Array.Empty<Subtitle>();

        /// <summary>
        /// The file's subtitle tracks, every cue of each read now - they are small, and so are there for any time sought to -
        /// then the SubRip and WebVTT files beside it: of its name, or its name and a language, as movie.en.srt.
        /// </summary>
        private void LoadSubtitles(string fileName, IEnumerable<ITrack> tracks)
        {
            var names = new List<SubtitleTrackInfo>();
            var subtitles = new List<List<Subtitle>>();
            int number = 0;
            foreach (var track in tracks.OfType<ISubtitleTrack>())
            {
                number++;
                try
                {
                    subtitles.Add(ReadSubtitles(track));
                    names.Add(new SubtitleTrackInfo($"Track {number}", LanguageOf(track), track.Forced));
                }
                catch (Exception ex)
                {
                    if (Log.ErrorEnabled) Log.Error($"Subtitle track {track.TrackID} could not be read: {ex.Message}", ex);
                }
            }

            foreach (var (path, suffix) in SidecarFiles(fileName))
            {
                try
                {
                    var cues = SubtitleParser.ParseSrtOrWebVtt(File.ReadAllText(path));
                    if (cues.Count == 0)
                        continue;
                    string language = suffix.Split('.', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(part => part.Length is 2 or 3 && part.All(char.IsLetter) && !part.Equals("srt", StringComparison.OrdinalIgnoreCase));
                    bool forced = suffix.Contains("forced", StringComparison.OrdinalIgnoreCase);
                    subtitles.Add(cues);
                    names.Add(new SubtitleTrackInfo(Path.GetFileName(path), language, forced));
                }
                catch (Exception ex)
                {
                    if (Log.ErrorEnabled) Log.Error($"Subtitles {path} could not be read: {ex.Message}", ex);
                }
            }

            _subtitleTracks = names;
            _subtitles = subtitles;
        }

        /// <summary>A track's language, as its media header says it: the reader leaves the track's own unset.</summary>
        private string LanguageOf(ITrack track)
        {
            var media = _reader.Tracks[track.TrackID].Stbl?.GetParent()?.GetParent() as Box;
            return media?.Children?.OfType<MediaHeaderBox>().FirstOrDefault()?.Language ?? track.Language;
        }

        /// <summary>A track's cues, on the clock of the video's frames, made plain; a TTML document's paragraphs each its own.</summary>
        private List<Subtitle> ReadSubtitles(ISubtitleTrack track)
        {
            var subtitles = new List<Subtitle>();
            uint timescale = track.Timescale;
            MediaSample sample;
            while ((sample = _reader.ReadSample(track.TrackID)) != null)
            {
                long duration = sample.LongDuration > 0 ? sample.LongDuration : Math.Max(0, track.DefaultSampleDuration);
                var data = sample.Data;
                foreach (var cue in track.ParseCues(data.Array, data.Offset, data.Count, sample.PTS, duration))
                {
                    long start = MediaUtils.ToTicks(cue.Start, timescale);
                    long end = MediaUtils.ToTicks(cue.End, timescale);
                    if (track is TtmlTrack)
                    {
                        subtitles.AddRange(SubtitleParser.ParseTtml(cue.Text, start, end));
                        continue;
                    }
                    string text = SubtitleParser.ToPlainText(cue.Text);
                    if (text.Length > 0 && end > start)
                        subtitles.Add(new Subtitle(start, end, text));
                }
            }
            subtitles.Sort((a, b) => a.Start.CompareTo(b.Start));
            return subtitles;
        }

        /// <summary>The SubRip and WebVTT files of a video's name beside it, each with what its name has after the video's.</summary>
        private static IEnumerable<(string Path, string Suffix)> SidecarFiles(string fileName)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(fileName));
            string stem = Path.GetFileNameWithoutExtension(fileName);
            if (directory == null || !Directory.Exists(directory))
                yield break;
            foreach (string path in Directory.EnumerateFiles(directory, stem + "*").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string extension = Path.GetExtension(path);
                if (!extension.Equals(".srt", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".vtt", StringComparison.OrdinalIgnoreCase))
                    continue;
                string name = Path.GetFileNameWithoutExtension(path);
                if (name.Length == stem.Length || name[stem.Length] == '.')
                    yield return (path, name.Substring(stem.Length));
            }
        }

        #endregion

        /// <summary>
        /// How the frames hold the eyes' views, and what they are of, as the video's sample entry says: Google's spherical
        /// video boxes - 'st3d', of the stereo mode, and 'sv3d', of the projection, as a VR180 or 360 degree camera writes them.
        /// Flat and mono where it says nothing.
        /// </summary>
        private void ReadStereoAndProjection(VideoInfo videoInfo)
        {
            var entry = _reader.Tracks[_videoTrack.TrackID].Stbl
                .Children.OfType<SampleDescriptionBox>().SingleOrDefault()?
                .Children.OfType<VisualSampleEntry>().FirstOrDefault();
            if (entry?.Children == null)
                return;

            // stereo_mode: 0 mono, 1 top-bottom, 2 left-right
            var stereo = entry.Children.OfType<Stereoscopic3D>().FirstOrDefault();
            if (stereo != null)
                videoInfo.StereoLayout = stereo.StereoMode == 1 ? StereoLayout.TopBottom : stereo.StereoMode == 2 ? StereoLayout.SideBySide : StereoLayout.Mono;

            // the bounds are 0.32 fixed point fractions of the whole sphere's picture, cropped from each side of each eye's
            var equirectangular = entry.Children.OfType<SphericalVideoBox>().FirstOrDefault()?
                .Children?.OfType<ProjectionBox>().FirstOrDefault()?
                .Children?.OfType<SharpISOBMFF.EquirectangularProjection>().FirstOrDefault();
            if (equirectangular != null)
            {
                const double scale = 1.0 / 4294967296.0;
                videoInfo.Projection = VideoProjection.Equirectangular;
                videoInfo.ProjectionBounds = new ProjectionBounds(
                    equirectangular.ProjectionBoundsLeft * scale,
                    equirectangular.ProjectionBoundsTop * scale,
                    equirectangular.ProjectionBoundsRight * scale,
                    equirectangular.ProjectionBoundsBottom * scale);
            }
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

        // the time each frame is shown at, in the order shown: what a frame's number is of
        private long[] _frameTimes;
        private uint[] _syncSamples;
        private long[] _audioTimes;
        private long _startTime;
        private long _duration = -1;

        public override bool CanSeek => _syncTimes != null && _syncTimes.Length > 0;

        public override long FrameCount => _frameTimes?.Length ?? -1;

        public override long FrameAt(long time)
        {
            if (_frameTimes == null || _frameTimes.Length == 0)
                return -1;
            int i = Array.BinarySearch(_frameTimes, time);
            // the last at or before it; of a time before the first, the first
            return Math.Max(0, i >= 0 ? i : ~i - 1);
        }

        public override long TimeOfFrame(long frame) =>
            _frameTimes != null && frame >= 0 && frame < _frameTimes.Length ? _frameTimes[frame] : -1;
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
            if (_audioTrack is PcmTrack)
            {
                // a frame a sample, each as long as the next: the frame of a time is of the time alone
                long duration = Math.Max(1, _audioTrack.DefaultSampleDuration);
                long frame = Math.Max(0, time) * _audioTrack.Timescale / TimeSpan.TicksPerSecond / duration;
                _reader.SeekSample(_audioTrack.TrackID, (uint)Math.Min(frame, uint.MaxValue));
                return;
            }
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
            _frameTimes = null;
            _audioTimes = null;
            _startTime = 0;
            _duration = -1;

            var video = _videoTrack == null ? null : _reader.GetSampleTimings(_videoTrack.TrackID);
            if (video == null || video.Length == 0)
                return;

            var syncs = new List<(long Time, uint Sample)>();
            var frames = new long[video.Length];
            long start = long.MaxValue, end = long.MinValue;
            for (int i = 0; i < video.Length; i++)
            {
                long time = MediaUtils.ToTicks(video[i].PTS, _videoTrack.Timescale);
                frames[i] = time;
                start = Math.Min(start, time);
                end = Math.Max(end, MediaUtils.ToTicks(video[i].PTS + video[i].Duration, _videoTrack.Timescale));
                if (video[i].IsSyncSample)
                    syncs.Add((time, (uint)i));
            }
            syncs.Sort((a, b) => a.Time.CompareTo(b.Time));
            _syncTimes = syncs.Select(s => s.Time).ToArray();
            _syncSamples = syncs.Select(s => s.Sample).ToArray();
            Array.Sort(frames);
            _frameTimes = frames;
            _startTime = start;
            _duration = end - start;

            // not of PCM, a sample a frame - 48000 a second - which is sought of its time alone
            var audio = _audioTrack == null || _audioTrack is PcmTrack ? null : _reader.GetSampleTimings(_audioTrack.TrackID);
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
            LoadSubtitles(fileName, inputTracks);

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

                        videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, MediaCodecs.DecoderAlignment(VideoCodec.H264));
                        videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, MediaCodecs.DecoderAlignment(VideoCodec.H264));
                    }
                    else if (_videoTrack is H265Track h265Track)
                    {
                        videoInfo.VideoCodec = "H265";

                        var sps = h265Track.Sps.First().Value;
                        var dimensions = sps.CalculateDimensions();
                        videoInfo.OriginalWidth = dimensions.Width;
                        videoInfo.OriginalHeight = dimensions.Height;

                        videoInfo.FpsNom = h265Track.Timescale;
                        videoInfo.FpsDenom = (uint)h265Track.DefaultSampleDuration;

                        // the frames of the coded size, the picture cropped of them: 1088 lines of a 1080 line picture
                        (videoInfo.Width, videoInfo.Height) = TrackColor.HevcFrameSize(sps, videoInfo.OriginalWidth, videoInfo.OriginalHeight);
                    }
                    else if (_videoTrack is AV1Track av1Track)
                    {
                        videoInfo.VideoCodec = "AV1";

                        var dimensions = av1Track.SequenceHeaderObu.CalculateDimensions();
                        videoInfo.OriginalWidth = dimensions.Width;
                        videoInfo.OriginalHeight = dimensions.Height;

                        videoInfo.FpsNom = av1Track.Timescale;
                        videoInfo.FpsDenom = (uint)av1Track.DefaultSampleDuration;

                        videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, MediaCodecs.DecoderAlignment(VideoCodec.AV1));
                        videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, MediaCodecs.DecoderAlignment(VideoCodec.AV1));
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

                        videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, MediaCodecs.DecoderAlignment(VideoCodec.VP9));
                        videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, MediaCodecs.DecoderAlignment(VideoCodec.VP9));
                    }
                    else if (_wholeSamples)
                    {
                        // MPEG-1 as MPEG-2: the one decoder decodes both
                        videoInfo.VideoCodec = _videoTrack is H262Track ? "H262" : _videoTrack is H263Track ? "H263" : "MPEG4";
                        uint multiple = MediaCodecs.DecoderAlignment(
                            _videoTrack is H262Track ? VideoCodec.H262 : _videoTrack is H263Track ? VideoCodec.H263 : VideoCodec.Mpeg4);

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

                    // The bits of the samples and their chroma, of the stream - what its decoder hands out of them - and its
                    // color, of the sample entry's 'colr', else of the stream; 8 bits of 4:2:0 of a codec that says neither.
                    var (bitDepth, chromaFormat) = TrackColor.SamplesOf(_videoTrack) ?? (8, 1);
                    TrackColor.ThrowIfNotDecoded(_videoTrack, bitDepth, chromaFormat);
                    videoInfo.FrameFormat = TrackColor.FrameFormatOf(chromaFormat, bitDepth);
                    var entryColors = _reader.Tracks[_videoTrack.TrackID].Stbl
                        .Children.OfType<SampleDescriptionBox>().Single()
                        .Children.OfType<VisualSampleEntry>().First()
                        .Children.OfType<ColourInformationBox>();
                    videoInfo.Color = TrackColor.Of(entryColors, _videoTrack, bitDepth, sceneReferred: false, videoInfo.OriginalHeight);

                    // turned and flipped as its track's matrix says: a phone's upright video, recorded on its side
                    var matrix = _reader.Track.Select(x => x.Children.OfType<TrackHeaderBox>().First())
                        .First(x => x.TrackID == _videoTrack.TrackID).Matrix;
                    if (matrix != null && matrix.Length >= 5)
                        videoInfo.Orientation = VideoOrientation.FromMatrix(matrix[0], matrix[1], matrix[3], matrix[4]);

                    ReadStereoAndProjection(videoInfo);
                }

                if (_audioTrack != null)
                {
                    audioInfo = new AudioInfo();

                    if (_audioTrack is AACTrack aacTrack)
                    {
                        audioInfo.AudioCodec = "AAC";
                        audioInfo.BitsPerSample = 16;
                        audioInfo.UserData = aacTrack.AudioSpecificConfig.ToBytes();
                        audioInfo.ChannelCount = AacChannels(aacTrack.ChannelConfiguration, aacTrack.ChannelCount);
                        audioInfo.ChannelConfiguration = aacTrack.ChannelConfiguration;
                        audioInfo.SampleRate = aacTrack.SamplingRate;
                    }
                    else if(_audioTrack is OpusTrack opusTrack)
                    {
                        audioInfo.AudioCodec = "OPUS";
                        audioInfo.BitsPerSample = 32; // Opus decodes to 32 bit float, which is turned into 32 bit integer PCM
                        audioInfo.ChannelCount = opusTrack.ChannelCount;
                        audioInfo.ChannelConfiguration = opusTrack.ChannelCount;
                        audioInfo.SampleRate = opusTrack.SamplingRate;
                        audioInfo.SkipSamples = opusTrack.PreSkip;
                    }
                    else if (_audioTrack is Mp3Track mp3Track)
                    {
                        audioInfo.AudioCodec = "MP3";
                        audioInfo.BitsPerSample = 16;
                        audioInfo.ChannelCount = mp3Track.ChannelCount;
                        audioInfo.SampleRate = mp3Track.SamplingRate;
                    }
                    else if (_audioTrack is FlacTrack flacTrack)
                    {
                        audioInfo.AudioCodec = "FLAC";
                        audioInfo.BitsPerSample = flacTrack.BitsPerSample > 16 ? 32u : 16u; // 24 bit decoded, widened to 32 to play
                        audioInfo.ChannelCount = flacTrack.ChannelCount;
                        audioInfo.SampleRate = flacTrack.SamplingRate;
                        audioInfo.UserData = flacTrack.CreateStreamHeader().AsSpan(4).ToArray(); // the blocks, without 'fLaC'
                    }
                    else if (_audioTrack is AlacTrack alacTrack)
                    {
                        audioInfo.AudioCodec = "ALAC";
                        audioInfo.BitsPerSample = alacTrack.BitDepth > 16 ? 32u : 16u; // 24 bit decoded, widened to 32 to play
                        audioInfo.ChannelCount = alacTrack.ChannelCount;
                        audioInfo.SampleRate = alacTrack.SamplingRate;
                        audioInfo.UserData = alacTrack.Config;
                    }
                    else if (_audioTrack is PcmTrack pcmTrack)
                    {
                        // uncompressed: of 16 bits or fewer played as 16, else as 32
                        audioInfo.AudioCodec = "PCM";
                        audioInfo.BitsPerSample = !pcmTrack.IsFloat && pcmTrack.BitsPerSample <= 16 ? 16u : 32u;
                        audioInfo.ChannelCount = pcmTrack.ChannelCount;
                        audioInfo.SampleRate = pcmTrack.SampleRate;
                        audioInfo.PcmFormat = new PcmFormat
                        {
                            BitsPerSample = pcmTrack.BitsPerSample,
                            IsFloat = pcmTrack.IsFloat,
                            IsSigned = pcmTrack.IsSigned,
                            IsLittleEndian = pcmTrack.IsLittleEndian,
                        };
                    }
                    else
                    {
                        // no audio: AC-3 and E-AC-3 too, whose decoders Windows no longer has
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

        /// <summary>
        /// The channels of AAC, of its AudioSpecificConfig's channel configuration (ISO/IEC 14496-3 1.6.3.4) - which says
        /// them where the sample entry's count need not: a file's 'mp4a' of 5.1 says 2, as a file of mono does - 1 to 6
        /// as many, 7, 12 and 14 of 7.1, 11 of 6.1; of 0, whose layout is the config's own, the sample entry's.
        /// </summary>
        private static uint AacChannels(int configuration, uint sampleEntryChannels) => configuration switch
        {
            >= 1 and <= 6 => (uint)configuration,
            7 or 12 or 14 => 8,
            11 => 7,
            _ => sampleEntryChannels,
        };

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
