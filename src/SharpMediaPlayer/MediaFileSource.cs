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
                    timestamp = MediaUtils.ToTicks(sample.PTS, _audioTrack.Timescale) + _audioShift;
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
                    timestamp = MediaUtils.ToTicks(sample.PTS, _videoTrack.Timescale) + _videoShift;
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
            if (_videoTrack == null)
                return _syncTimes[i]; // of sound alone: the sound is sought on its own, by SeekAudio

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
                long frame = Math.Max(0, time - _audioShift) * _audioTrack.Timescale / TimeSpan.TicksPerSecond / duration;
                _reader.SeekSample(_audioTrack.TrackID, (uint)Math.Min(frame, uint.MaxValue));
                return;
            }
            if (_audioTimes == null)
                return;

            int i = Array.BinarySearch(_audioTimes, time);
            i = i >= 0 ? i : Math.Max(0, ~i - 1);
            _reader.SeekSample(_audioTrack.TrackID, (uint)i);
        }

        // Of each track, what is added to its samples' times, of its edit list: the tracks' edits made to start together, and
        // no time below 0. And where the sound's edit starts - before which its decoder's output is its encoder's priming,
        // not of the sound.
        private long _videoShift, _audioShift;
        private long _audioStart = long.MinValue;

        /// <summary>
        /// Of the tracks' edit lists (ISO/IEC 14496-12 8.6.6), how each is shown: from where its first edit of media starts,
        /// after the empty edits before it. A video's starts at its first frame's composition offset, as B-frames delay it;
        /// a sound's past its encoder's priming - of AAC 1024 samples or 2112, of MP2 481, Opus's pre-skip. Played as the
        /// file says, they start together: each track's times shifted by the difference, and the sound before its edit not
        /// played. A track of no edit list starts at its media's start. Edits after the first - of a cut - are not followed.
        /// </summary>
        private void ReadEditLists()
        {
            long videoStart = 0, videoDelay = 0, audioStart = 0, audioDelay = 0;
            bool audioEdited = false;
            if (_videoTrack != null)
                EditOf(_videoTrack, out videoStart, out videoDelay, out _);
            if (_audioTrack != null)
                EditOf(_audioTrack, out audioStart, out audioDelay, out audioEdited);

            // the offset of each into its media of the shared clock's start, and the clock moved on so that none is below 0
            long videoOffset = videoStart - videoDelay, audioOffset = audioStart - audioDelay;
            long origin = Math.Max(0, Math.Max(_videoTrack != null ? videoOffset : 0, _audioTrack != null ? audioOffset : 0));
            _videoShift = origin - videoOffset;
            _audioShift = origin - audioOffset;
            _audioStart = audioEdited ? audioStart + _audioShift : long.MinValue;

            // the subtitles - of the movie's clock, of no edit of their own - as the clock is moved on
            if (origin != 0)
                _subtitles = _subtitles.Select(cues => cues.Select(c => new Subtitle(c.Start + origin, c.End + origin, c.Text)).ToList()).ToList();
        }

        /// <summary>
        /// Of a track's edit list, its first edit of media: where it starts in the media, in ticks, and the empty edits'
        /// duration before it, of the movie's timescale. Zero both, of a track of none.
        /// </summary>
        private void EditOf(ITrack track, out long mediaStart, out long emptyBefore, out bool edited)
        {
            mediaStart = 0;
            emptyBefore = 0;
            edited = false;
            var trak = _reader.Tracks[track.TrackID].Stbl?.GetParent()?.GetParent()?.GetParent() as Box;
            var elst = trak?.Children?.OfType<EditBox>().FirstOrDefault()?.Children?.OfType<EditListBox>().FirstOrDefault();
            if (elst?.MediaTime == null)
                return;
            uint movieTimescale = (trak.GetParent() as Box)?.Children?.OfType<MovieHeaderBox>().FirstOrDefault()?.Timescale ?? 0;
            for (int i = 0; i < elst.MediaTime.Length; i++)
            {
                if (elst.MediaTime[i] == -1)
                {
                    // an empty edit: the track starts that much later
                    if (movieTimescale > 0 && elst.EditDuration != null && i < elst.EditDuration.Length)
                        emptyBefore += MediaUtils.ToTicks((long)elst.EditDuration[i], movieTimescale);
                    continue;
                }
                mediaStart = MediaUtils.ToTicks(elst.MediaTime[i], track.Timescale);
                edited = elst.MediaTime[i] > 0;
                return;
            }
        }

        /// <summary>Where the sound's edit starts: its decoded sound before it - its encoder's priming - not played.</summary>
        protected override long AudioStart => _audioStart;

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
            {
                ReadSoundTimes();
                return;
            }

            var syncs = new List<(long Time, uint Sample)>();
            var frames = new long[video.Length];
            long start = long.MaxValue, end = long.MinValue;
            for (int i = 0; i < video.Length; i++)
            {
                long time = MediaUtils.ToTicks(video[i].PTS, _videoTrack.Timescale) + _videoShift;
                frames[i] = time;
                start = Math.Min(start, time);
                end = Math.Max(end, MediaUtils.ToTicks(video[i].PTS + video[i].Duration, _videoTrack.Timescale) + _videoShift);
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
                _audioTimes = audio.Select(a => MediaUtils.ToTicks(a.PTS, _audioTrack.Timescale) + _audioShift).ToArray();
        }

        /// <summary>
        /// Of a file of sound alone, the sound's times, which seeking finds its way by, as it does by a video's key frames:
        /// each frame's - every one a frame to start at - or of PCM, of a frame a sample, a tenth of a second's, of the
        /// track's duration.
        /// </summary>
        private void ReadSoundTimes()
        {
            if (_audioTrack == null)
                return;

            long start, end;
            if (_audioTrack is PcmTrack)
            {
                var media = _reader.Tracks[_audioTrack.TrackID].Stbl?.GetParent()?.GetParent() as Box;
                var header = media?.Children?.OfType<MediaHeaderBox>().FirstOrDefault();
                if (header == null || header.Timescale == 0 || header.Duration == 0)
                    return;
                start = 0;
                end = MediaUtils.ToTicks((long)header.Duration, header.Timescale);
                long step = TimeSpan.TicksPerSecond / 10;
                _audioTimes = null;
                _syncTimes = new long[(end + step - 1) / step];
                for (int i = 0; i < _syncTimes.Length; i++)
                    _syncTimes[i] = i * step;
            }
            else
            {
                var audio = _reader.GetSampleTimings(_audioTrack.TrackID);
                if (audio == null || audio.Length == 0)
                    return;
                _audioTimes = audio.Select(a => MediaUtils.ToTicks(a.PTS, _audioTrack.Timescale) + _audioShift).ToArray();
                _syncTimes = _audioTimes;
                // of its edit: its sound from where the edit starts, not its encoder's priming before it
                start = Math.Max(_audioTimes.Min(), _audioStart);
                end = audio.Max(a => MediaUtils.ToTicks(a.PTS + a.Duration, _audioTrack.Timescale) + _audioShift);
            }
            _startTime = start;
            _duration = end - start;
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
                ReadEditLists();
                ReadSampleTimes();

                if (_videoTrack != null)
                {
                    _wholeSamples = _videoTrack is H262Track || _videoTrack is H263Track || _videoTrack is MPEG4Track || _videoTrack is MjpegTrack;
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
                        var codec = _videoTrack switch
                        {
                            H262Track => VideoCodec.H262,
                            H263Track => VideoCodec.H263,
                            MjpegTrack => VideoCodec.Mjpeg,
                            _ => VideoCodec.Mpeg4,
                        };
                        videoInfo.VideoCodec = codec switch
                        {
                            VideoCodec.H262 => "H262",
                            VideoCodec.H263 => "H263",
                            VideoCodec.Mjpeg => "MJPEG",
                            _ => "MPEG4",
                        };
                        uint multiple = MediaCodecs.DecoderAlignment(codec);

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
                    // Motion JPEG of 4:2:2, as cameras' is, of the first picture's frame header: YUY2, its chroma as it is
                    if (_videoTrack is MjpegTrack && FirstSample(_videoTrack) is byte[] picture &&
                        MjpegTrack.TryReadFrameHeader(picture, 0, picture.Length, out _, out _, out int jpegChroma) && jpegChroma == 2)
                        videoInfo.FrameFormat = VideoFrameFormat.YUY2;
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
                        // 4.0 is front left, right and centre and back centre, as Windows' decoder puts it out (ISO/IEC 14496-3 1.6.3.4)
                        audioInfo.ChannelMask = aacTrack.ChannelConfiguration == 4 ? 0x107u : 0;
                        audioInfo.SampleRate = aacTrack.SamplingRate;
                    }
                    else if(_audioTrack is OpusTrack opusTrack)
                    {
                        audioInfo.AudioCodec = "OPUS";
                        audioInfo.BitsPerSample = 32; // Opus decodes to 32 bit float, which is turned into 32 bit integer PCM
                        audioInfo.ChannelCount = opusTrack.ChannelCount;
                        audioInfo.ChannelConfiguration = opusTrack.ChannelCount;
                        audioInfo.SampleRate = opusTrack.SamplingRate;
                        // of an edit list, the pre-skip is its: informative in 'dOps' (Opus in ISOBMFF 4.3.2), not dropped twice
                        audioInfo.SkipSamples = _audioStart != long.MinValue ? 0u : opusTrack.PreSkip;
                        audioInfo.UserData = OpusConfig(opusTrack);
                        // of channel mapping family 2, a sound field (RFC 8486)
                        audioInfo.IsAmbisonic = opusTrack.ChannelMappingFamily == 2 &&
                            SharpMediaFoundationInterop.Output.AmbisonicRenderer.IsAmbisonic(opusTrack.ChannelCount, out _, out _);
                    }
                    else if (_audioTrack is Mp3Track mp3Track)
                    {
                        // Layers I and II - MP2 - of a decoder of their own: of the first frame's header, as the entry
                        // need not say
                        var first = FirstSample(_audioTrack);
                        audioInfo.AudioCodec = first != null && Mp3Track.LayerOf(first, 0) is 1 or 2 ? "MP2" : "MP3";
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
                    else if (_audioTrack is AmrTrack amrTrack)
                    {
                        // Windows' AMR-NB decoder is of Media Foundation's own pipeline alone - made by its class factory or
                        // asked of a source reader, it gives no IMFTransform - and it has none of AMR-WB: no sound, said so
                        // once, as its decoder is not made
                        audioInfo.AudioCodec = amrTrack.IsWideband ? "AMR-WB" : "AMR";
                        audioInfo.BitsPerSample = 16;
                        audioInfo.ChannelCount = amrTrack.ChannelCount;
                        audioInfo.SampleRate = amrTrack.SampleRate;
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
                            Companding = pcmTrack.Companding switch
                            {
                                SharpMP4.Tracks.PcmCompanding.ALaw => SharpMediaFoundationInterop.Codecs.PcmCompanding.ALaw,
                                SharpMP4.Tracks.PcmCompanding.MuLaw => SharpMediaFoundationInterop.Codecs.PcmCompanding.MuLaw,
                                _ => SharpMediaFoundationInterop.Codecs.PcmCompanding.None,
                            },
                        };
                    }
                    else
                    {
                        // no audio: AC-3 and E-AC-3 too, whose decoders Windows no longer has
                        audioInfo = null;
                    }

                    if (audioInfo != null && !audioInfo.IsAmbisonic)
                        audioInfo.IsAmbisonic = HasSoundField(_audioTrack.TrackID, audioInfo.ChannelCount);
                }
            }
            else
            {
                throw new NotSupportedException();
            }

            return Task.FromResult((videoInfo, audioInfo));
        }

        /// <summary>
        /// The 'dOps' box's payload (Opus in ISOBMFF 4.3.2), as <see cref="AudioDecoderOptions.Config"/> has it of Opus: of
        /// its channel mapping, which a decoder of more than two channels needs.
        /// </summary>
        private static byte[] OpusConfig(OpusTrack track)
        {
            var mapping = track.ChannelMappingFamily != 0 ? track.ChannelMapping ?? Array.Empty<byte>() : Array.Empty<byte>();
            var config = new byte[11 + (track.ChannelMappingFamily != 0 ? 2 + mapping.Length : 0)];
            config[0] = 0;
            config[1] = track.ChannelCount;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(config.AsSpan(2), track.PreSkip);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(config.AsSpan(4), track.InputSampleRate);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(config.AsSpan(8), track.OutputGain);
            config[10] = track.ChannelMappingFamily;
            if (track.ChannelMappingFamily != 0)
            {
                config[11] = track.StreamCount;
                config[12] = track.CoupledCount;
                mapping.CopyTo(config, 13);
            }
            return config;
        }

        /// <summary>
        /// Whether the track's sample entry says its channels are a sound field, of Google's 'SA3D' (Spatial Audio RFC):
        /// periphonic, of ACN order and SN3D, each channel where it is - with or without head-locked stereo after it.
        /// </summary>
        private bool HasSoundField(uint trackID, uint channels)
        {
            var box = _reader.Tracks[trackID].Stbl
                .Children.OfType<SampleDescriptionBox>().SingleOrDefault()?
                .Children.FirstOrDefault()?
                .Children?.OfType<SpatialAudioBox>().FirstOrDefault();
            if (box == null || box.AmbisonicType != 0 || box.AmbisonicChannelOrdering != 0 || box.AmbisonicNormalization != 0 || box.NumChannels != channels)
                return false;
            for (int i = 0; i < (box.ChannelMap?.Length ?? 0); i++)
                if (box.ChannelMap[i] != i)
                    return false;
            return SharpMediaFoundationInterop.Output.AmbisonicRenderer.IsAmbisonic(channels, out int order, out _) && order == box.AmbisonicOrder;
        }

        /// <summary>
        /// The track's first sample, read and the track sought back to it: what a codec says of itself in its frames alone -
        /// MPEG audio its layer, JPEG its chroma. Null where it has none.
        /// </summary>
        private byte[] FirstSample(ITrack track)
        {
            var sample = _reader.ReadSample(track.TrackID);
            _reader.SeekSample(track.TrackID, 0);
            return sample?.Data.ToArray();
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
