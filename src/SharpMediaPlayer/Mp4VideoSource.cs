using SharpAV1;
using SharpH264;
using SharpH265;
using SharpISOBMFF;
using SharpISOBMFF.Extensions;
using SharpMediaFoundationInterop.Transforms.AV1;
using SharpMediaFoundationInterop.Transforms.H264;
using SharpMediaFoundationInterop.Transforms.H265;
using SharpMediaFoundationInterop.Utils;
using SharpMediaFoundationInterop.WPF;
using SharpMP4.Readers;
using SharpMP4.Tracks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SharpMediaFoundationInterop
{
    public class Mp4VideoSource : IVideoPlayerSource
    {
        private readonly string _filePath;
        private BufferedStream _fileStream;
        private VideoReader _reader;
        private ITrack _videoTrack;
        private ITrack _audioTrack;

        // Timescales extracted during initialization
        private uint _videoTimescale;     // normalized ITrack.Timescale (consistent with FpsDenom)
        private uint _videoMdhdTimescale; // actual mdhd.Timescale (used for raw trun/sample.Duration values)
        private uint _audioTimescale;     // normalized ITrack.Timescale for audio
        private uint _audioMdhdTimescale; // actual mdhd.Timescale for audio (used for raw sample.Duration values)

        // Buffered NALUs/frames within the current sample
        private readonly Queue<byte[]> _pendingVideoUnits = new();
        private readonly Queue<byte[]> _pendingAudioUnits = new();

        // Running PTS accumulators (timescale units)
        private long _videoAccumulated;
        private long _audioAccumulated;
        private long _videoSampleTs; // timestamp of the last dequeued video sample

        // True until the decoder init data (SPS/PPS/VPS) has been sent
        private bool _sendInitUnits;

        // ── IVideoPlayerSource properties ──────────────────────────────────────────

        public bool HasVideo { get; private set; }
        public uint VideoWidth { get; private set; }
        public uint VideoHeight { get; private set; }
        public uint OriginalVideoWidth { get; private set; }
        public uint OriginalVideoHeight { get; private set; }

        public uint FpsNom { get; private set; }
        public uint FpsDenom { get; private set; }
        public string VideoCodec { get; private set; }

        public bool HasAudio { get; private set; }
        public uint AudioChannels { get; private set; }
        public uint AudioSampleRate { get; private set; }
        public string AudioCodec { get; private set; }
        public byte[] AACUserData { get; private set; }
        public int AudioChannelConfiguration { get; private set; }

        public bool CanSeek => true;

        // Total duration in 100-nanosecond units; computed from the MovieHeaderBox.
        public long Duration { get; private set; }

        public Mp4VideoSource(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        public void Initialize()
        {
            OpenFile();
        }

        // ── IVideoPlayerSource methods ─────────────────────────────────────────────

        public bool Seek(long timestamp)
        {
            OpenFile(); // Resets all state and file position to the beginning.
            if (timestamp == 0) return true;

            // Video decoders (MFTs) require starting from a keyframe (random-access point).
            // Pass 1: scan forward to find the last keyframe at or before the target timestamp.
            long seekTimestamp = 0;
            if (HasVideo)
            {
                long lastKeyframeAccumulated = 0;
                long accumulated = 0;
                while (accumulated * 10_000_000L / _videoTimescale <= timestamp)
                {
                    var s = _reader.ReadSample(_videoTrack.TrackID);
                    if (s == null) break;
                    if (s.IsRandomAccessPoint)
                        lastKeyframeAccumulated = accumulated;
                    accumulated += FpsDenom > 0 ? FpsDenom : AdvanceVideo(s.Duration);
                }
                seekTimestamp = lastKeyframeAccumulated * 10_000_000L / _videoTimescale;

                // Pass 2: reopen and skip the reader to just before the keyframe so the
                // next ReadNextVideoUnit returns the keyframe sample at seekTimestamp.
                OpenFile();
                accumulated = 0;
                while (accumulated < lastKeyframeAccumulated)
                {
                    var s = _reader.ReadSample(_videoTrack.TrackID);
                    if (s == null) break;
                    accumulated += FpsDenom > 0 ? FpsDenom : AdvanceVideo(s.Duration);
                }
                _videoAccumulated = lastKeyframeAccumulated;
                _videoSampleTs = seekTimestamp;
                _pendingVideoUnits.Clear();
            }

            // Seek audio to the originally requested timestamp, not the keyframe.
            // Audio frames are independent so no keyframe alignment is needed, and
            // keeping audio at the target keeps it in sync during the video pre-roll.
            if (HasAudio)
            {
                while (_audioAccumulated * 10_000_000L / _audioTimescale < timestamp)
                {
                    var sample = _reader.ReadSample(_audioTrack.TrackID);
                    if (sample == null) break;
                    if (_audioTrack is AACTrack && sample.Duration != 1024)
                        continue;
                    _audioAccumulated += (long)sample.Duration;
                }
                _pendingAudioUnits.Clear();
            }

            Debug.WriteLine($"Seek: target={timestamp / 10_000_000.0:F2}s  keyframe={seekTimestamp / 10_000_000.0:F2}s  audio={_audioAccumulated * 10_000_000L / _audioTimescale / 10_000_000.0:F2}s");

            return true;
        }

        public bool ReadNextVideoUnit(ref byte[] buffer, out uint length, out long timestamp)
        {
            // 1. Return any buffered NALUs from the previous sample first.
            if (_pendingVideoUnits.Count > 0)
            {
                var u = _pendingVideoUnits.Dequeue();
                CopyOut(ref buffer, u, out length);
                timestamp = _videoSampleTs;
                return true;
            }

            // 2. On the first read, return decoder init data (SPS/PPS/VPS/OBU) at ts=0.
            if (_sendInitUnits)
            {
                _sendInitUnits = false;
                var initUnits = _videoTrack.GetContainerSamples().ToList();
                if (initUnits.Count > 0)
                {
                    for (int i = 1; i < initUnits.Count; i++)
                        _pendingVideoUnits.Enqueue(initUnits[i]);
                    CopyOut(ref buffer, initUnits[0], out length);
                    timestamp = 0;
                    return true;
                }
            }

            // 3. Read the next sample from the demuxer.
            var sample = _reader.ReadSample(_videoTrack.TrackID);
            if (sample == null) { length = 0; timestamp = 0; return false; }

            _videoSampleTs = _videoAccumulated * 10_000_000L / _videoTimescale;
            _videoAccumulated += AdvanceVideo(sample.Duration);

            var units = _reader.ParseSample(_videoTrack.TrackID, sample.Data).ToList();
            if (units.Count == 0) { length = 0; timestamp = _videoSampleTs; return true; }

            for (int i = 1; i < units.Count; i++)
                _pendingVideoUnits.Enqueue(units[i]);

            CopyOut(ref buffer, units[0], out length);
            timestamp = _videoSampleTs;
            return true;
        }

        public bool ReadNextAudioUnit(ref byte[] buffer, out uint length, out long timestamp)
        {
            if (_pendingAudioUnits.Count > 0)
            {
                var u = _pendingAudioUnits.Dequeue();
                CopyOut(ref buffer, u, out length);
                timestamp = _audioAccumulated * 10_000_000L / _audioMdhdTimescale;
                return true;
            }

            var sample = _reader.ReadSample(_audioTrack.TrackID);
            if (sample == null) { length = 0; timestamp = 0; return false; }

            long sampleTs = _audioAccumulated * 10_000_000L / _audioMdhdTimescale;
            _audioAccumulated += (long)sample.Duration;

            var frames = _reader.ParseSample(_audioTrack.TrackID, sample.Data).ToList();
            if (frames.Count == 0) { length = 0; timestamp = sampleTs; return true; }

            for (int i = 1; i < frames.Count; i++)
                _pendingAudioUnits.Enqueue(frames[i]);

            CopyOut(ref buffer, frames[0], out length);
            timestamp = sampleTs;
            return true;
        }

        // ── Private helpers ────────────────────────────────────────────────────────

        private void OpenFile()
        {
            _fileStream?.Dispose();
            _fileStream = new BufferedStream(new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read));

            var container = new Container();
            container.Read(new IsoStream(_fileStream));

            // Compute total duration from available ISOBMFF boxes.
            var moov = container.Children.OfType<MovieBox>().FirstOrDefault();
            var mvhd = moov?.Children.OfType<MovieHeaderBox>().FirstOrDefault();
            if (mvhd != null && mvhd.Duration > 0)
            {
                // Non-fragmented MP4: duration is in the movie header.
                Duration = (long)(mvhd.Duration * 10_000_000.0 / mvhd.Timescale);
            }
            else if (mvhd != null)
            {
                // Fragmented MP4: duration lives in mehd (MovieExtendsHeaderBox) inside mvex.
                var mehd = moov.Children.OfType<MovieExtendsBox>().FirstOrDefault()
                                        ?.Children.OfType<MovieExtendsHeaderBox>().FirstOrDefault();
                Duration = mehd != null && mehd.FragmentDuration > 0
                    ? (long)(mehd.FragmentDuration * 10_000_000.0 / mvhd.Timescale)
                    : 0;
            }
            else Duration = 0;

            _reader = new VideoReader();
            _reader.Parse(container);

            var tracks = _reader.GetTracks().ToList();
            _videoTrack = tracks.FirstOrDefault(t => t.HandlerType == HandlerTypes.Video);
            _audioTrack = tracks.FirstOrDefault(t => t.HandlerType == HandlerTypes.Sound);

            HasVideo = _videoTrack != null;
            HasAudio = _audioTrack != null;

            if (HasVideo) ExtractVideoInfo();
            if (HasAudio) ExtractAudioInfo();

            if (HasAudio)
            {
                var mdhd = moov?.Children.OfType<TrackBox>()
                    .Where(t => t.Children.OfType<TrackHeaderBox>().Any(h => h.TrackID == _audioTrack.TrackID))
                    .SelectMany(t => t.Children.OfType<MediaBox>())
                    .SelectMany(m => m.Children.OfType<MediaHeaderBox>())
                    .FirstOrDefault();
                _audioMdhdTimescale = (mdhd != null && mdhd.Timescale > 0) ? mdhd.Timescale : _audioTimescale;
            }

            if (HasVideo)
            {
                // For fragmented MP4, SharpMP4 normalises ITrack.Timescale/DefaultSampleDuration by a
                // constant factor (observed: 256) but leaves MediaSample.Duration in raw mdhd units.
                // Read the actual mdhd timescale so we can correctly convert raw trun durations to 100ns.
                var mdhd = moov?.Children.OfType<TrackBox>()
                    .Where(t => t.Children.OfType<TrackHeaderBox>().Any(h => h.TrackID == _videoTrack.TrackID))
                    .SelectMany(t => t.Children.OfType<MediaBox>())
                    .SelectMany(m => m.Children.OfType<MediaHeaderBox>())
                    .FirstOrDefault();
                _videoMdhdTimescale = (mdhd != null && mdhd.Timescale > 0) ? mdhd.Timescale : _videoTimescale;
            }

            if (Duration == 0 && HasVideo && _videoMdhdTimescale > 0)
                Duration = ComputeFragmentedDuration(container, _videoTrack.TrackID, _videoMdhdTimescale, FpsDenom);

            _pendingVideoUnits.Clear();
            _pendingAudioUnits.Clear();
            _videoAccumulated = 0;
            _audioAccumulated = 0;
            _videoSampleTs = 0;
            _sendInitUnits = HasVideo;
        }

        private static long ComputeFragmentedDuration(Container container, uint videoTrackId, uint timescale, uint defaultSampleDuration)
        {
            long total = 0;
            foreach (var moof in container.Children.OfType<MovieFragmentBox>())
            {
                foreach (var traf in moof.Children.OfType<TrackFragmentBox>())
                {
                    var tfhd = traf.Children.OfType<TrackFragmentHeaderBox>().FirstOrDefault();
                    if (tfhd == null || tfhd.TrackID != videoTrackId) continue;

                    uint fragDefault = tfhd.DefaultSampleDuration > 0 ? tfhd.DefaultSampleDuration : defaultSampleDuration;

                    foreach (var trun in traf.Children.OfType<TrackRunBox>())
                    {
                        bool perSampleDuration = (trun.Flags & 0x000100) != 0;
                        if (perSampleDuration && trun._TrunEntry != null)
                        {
                            foreach (var entry in trun._TrunEntry)
                                total += entry.SampleDuration > 0 ? entry.SampleDuration : fragDefault;
                        }
                        else
                        {
                            total += trun.SampleCount * (long)fragDefault;
                        }
                    }
                }
            }
            return timescale > 0 ? total * 10_000_000L / timescale : 0;
        }

        private void ExtractVideoInfo()
        {
            if (_videoTrack is H264Track h264)
            {
                var dims = h264.Sps.First().Value.CalculateDimensions();
                VideoWidth  = dims.Width;
                VideoHeight = dims.Height;
                OriginalVideoWidth  = MediaUtils.RoundToMultipleOf(dims.Width, H264Decoder.H264_RES_MULTIPLE); 
                OriginalVideoHeight = MediaUtils.RoundToMultipleOf(dims.Height, H264Decoder.H264_RES_MULTIPLE);
                FpsNom      = h264.Timescale;
                FpsDenom    = (uint)h264.DefaultSampleDuration;
                VideoCodec  = "H264";
                _videoTimescale = h264.Timescale;
            }
            else if (_videoTrack is H265Track h265)
            {
                var dims = h265.Sps.First().Value.CalculateDimensions();
                VideoWidth  = dims.Width;
                VideoHeight = dims.Height;
                OriginalVideoWidth = MediaUtils.RoundToMultipleOf(dims.Width, H265Decoder.H265_RES_MULTIPLE);
                OriginalVideoHeight = MediaUtils.RoundToMultipleOf(dims.Height, H265Decoder.H265_RES_MULTIPLE);
                FpsNom      = h265.Timescale;
                FpsDenom    = (uint)h265.DefaultSampleDuration;
                VideoCodec  = "H265";
                _videoTimescale = h265.Timescale;
            }
            else if (_videoTrack is AV1Track av1)
            {
                var dims = av1.SequenceHeaderObu.CalculateDimensions();
                VideoWidth  = dims.Width;
                VideoHeight = dims.Height;
                OriginalVideoWidth = MediaUtils.RoundToMultipleOf(dims.Width, AV1Decoder.AV1_RES_MULTIPLE);
                OriginalVideoHeight = MediaUtils.RoundToMultipleOf(dims.Height, AV1Decoder.AV1_RES_MULTIPLE);
                FpsNom      = av1.Timescale;
                FpsDenom    = (uint)av1.DefaultSampleDuration;
                VideoCodec  = "AV1";
                _videoTimescale = av1.Timescale;
            }
            else
            {
                HasVideo = false;
            }
        }

        private void ExtractAudioInfo()
        {
            if (_audioTrack is AACTrack aac)
            {
                // ChannelConfiguration == 1 is mono even if ChannelCount reports 2
                AudioChannels          = aac.ChannelConfiguration == 1 ? 1u : aac.ChannelCount;
                AudioSampleRate        = aac.SamplingRate;
                AudioCodec             = "AAC";
                AACUserData            = aac.AudioSpecificConfig.ToBytes();
                AudioChannelConfiguration = aac.ChannelConfiguration;
                _audioTimescale        = aac.Timescale;
            }
            else if (_audioTrack is OpusTrack opus)
            {
                AudioChannels          = opus.ChannelCount;
                AudioSampleRate        = opus.SamplingRate;
                AudioCodec             = "OPUS";
                AudioChannelConfiguration = (int)opus.ChannelCount;
                _audioTimescale        = opus.Timescale;
            }
            else
            {
                HasAudio = false;
            }
        }

        // Returns the number of _videoTimescale ticks to advance for one video sample.
        // FpsDenom is normalized (consistent with _videoTimescale). rawSampleDuration is in
        // _videoMdhdTimescale units; we normalise it by the same ratio when FpsDenom is absent.
        private long AdvanceVideo(long rawSampleDuration)
        {
            if (FpsDenom > 0) return FpsDenom;
            if (_videoMdhdTimescale > 0 && _videoMdhdTimescale != _videoTimescale && rawSampleDuration > 0)
                return rawSampleDuration * _videoTimescale / _videoMdhdTimescale;
            return rawSampleDuration > 0 ? rawSampleDuration : 1L;
        }

        private static void CopyOut(ref byte[] dest, byte[] src, out uint length)
        {
            if (dest == null || dest.Length < src.Length)
                dest = new byte[src.Length];
            Buffer.BlockCopy(src, 0, dest, 0, src.Length);
            length = (uint)src.Length;
        }

        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _fileStream?.Dispose();
            _fileStream = null;
        }
    }
}
