using SharpISOBMFF;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>
    /// Demuxes an Apple MV-HEVC ("spatial video") track: one hvc1 track carrying both views, with
    /// the base view configured by hvcC and the dependent view by lhvC.
    /// </summary>
    public sealed class MvHevcTrack
    {
        public uint DisplayWidth { get; set; }
        public uint DisplayHeight { get; set; }
        public int NalLengthSize { get; set; }
        public uint Timescale { get; set; }
        public uint FpsNom { get; set; }
        public uint FpsDenom { get; set; }

        /// <summary>VPS/SPS/PPS/SEI from hvcC, describing the base view.</summary>
        public List<byte[]> BaseParameterSets { get; } = new List<byte[]>();

        /// <summary>SPS/PPS from lhvC, describing the dependent view.</summary>
        public List<byte[]> LayerParameterSets { get; } = new List<byte[]>();

        public List<AccessUnit> AccessUnits { get; } = new List<AccessUnit>();

        /// <summary>
        /// Where each sample is in the file, so the samples can be read one at a time instead of
        /// all being held - see <see cref="MvHevcReader.StreamAccessUnits"/>.
        /// </summary>
        public string Path { get; set; }
        public List<long> SampleOffsets { get; set; } = new List<long>();
        public uint[] SampleSizes { get; set; } = Array.Empty<uint>();
        public uint[] SampleDurations { get; set; } = Array.Empty<uint>();
        public int[] SampleCompositionOffsets { get; set; } = Array.Empty<int>();

        public bool IsMultiview => LayerParameterSets.Count > 0;

        /// <summary>Raw byte of the stereo view information box (vexu/eyes/stri), or null.</summary>
        public byte? StereoViewInfo { get; set; }

        /// <summary>Audio samples copied verbatim from the source, or empty if there is no audio.</summary>
        public List<byte[]> AudioSamples { get; } = new List<byte[]>();
        public List<uint> AudioSampleDurations { get; } = new List<uint>();

        /// <summary>
        /// Box carrying the elementary stream descriptor, used to configure the output track. This
        /// is the descriptor itself, or the QuickTime 'wave' box wrapping it.
        /// </summary>
        public Box AudioConfig { get; set; }
        public uint AudioTimescale { get; set; }

        public bool HasAudio => AudioSamples.Count > 0 && AudioConfig != null;

        public bool HasLeftEyeView => StereoViewInfo.HasValue && (StereoViewInfo.Value & 0x01) != 0;
        public bool HasRightEyeView => StereoViewInfo.HasValue && (StereoViewInfo.Value & 0x02) != 0;
        public bool HasAdditionalViews => StereoViewInfo.HasValue && (StereoViewInfo.Value & 0x04) != 0;
        public bool EyeViewsReversed => StereoViewInfo.HasValue && (StereoViewInfo.Value & 0x08) != 0;
    }

    public static class MvHevcReader
    {
        /// <summary>
        /// Reads a track. With <paramref name="loadSamples"/> false the samples are left in the
        /// file and only their positions are recorded, which is all a caller that streams them - or
        /// only wants the parameter sets - needs.
        /// </summary>
        public static MvHevcTrack Read(string path, bool loadSamples = true)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var container = new Container();
            container.Read(new IsoStream(new StreamWrapper(stream)));

            var moov = container.Children.OfType<MovieBox>().Single();
            var track = moov.Children.OfType<TrackBox>().First(x => x
                .Children.OfType<MediaBox>().Single()
                .Children.OfType<MediaInformationBox>().Single()
                .Children.OfType<VideoMediaHeaderBox>().FirstOrDefault() != null);

            var media = track.Children.OfType<MediaBox>().Single();
            var mdhd = media.Children.OfType<MediaHeaderBox>().Single();
            var stbl = media.Children.OfType<MediaInformationBox>().Single()
                .Children.OfType<SampleTableBox>().Single();

            var visualSample = stbl.Children.OfType<SampleDescriptionBox>().Single()
                .Children.OfType<VisualSampleEntry>().Single();

            var hvcC = visualSample.Children.OfType<HEVCConfigurationBox>().SingleOrDefault();
            if (hvcC == null)
                throw new NotSupportedException("The video track is not HEVC.");
            var lhvC = visualSample.Children.OfType<LHEVCConfigurationBox>().SingleOrDefault();

            var result = new MvHevcTrack
            {
                DisplayWidth = visualSample.Width,
                DisplayHeight = visualSample.Height,
                NalLengthSize = hvcC._HEVCConfig.LengthSizeMinusOne + 1,
                Timescale = mdhd.Timescale,
            };

            result.StereoViewInfo = ReadStereoViewInfo(stream, visualSample);

            foreach (var array in hvcC._HEVCConfig.NalUnit)
                foreach (var nalu in array)
                    result.BaseParameterSets.Add(nalu);

            if (lhvC != null)
            {
                foreach (var array in lhvC._LHEVCConfig.NalUnit)
                    foreach (var nalu in array)
                        result.LayerParameterSets.Add(nalu);
            }

            var sizes = SampleSizes(stbl);
            var offsets = SampleOffsets(stbl, sizes.Length);
            var durations = SampleDurations(stbl, sizes.Length);
            var compositionOffsets = CompositionOffsets(stbl, sizes.Length);

            result.Path = path;
            result.SampleOffsets = offsets;
            result.SampleSizes = sizes;
            result.SampleDurations = durations;
            result.SampleCompositionOffsets = compositionOffsets;

            if (loadSamples)
                for (int i = 0; i < offsets.Count; i++)
                    result.AccessUnits.Add(ReadAccessUnit(stream, result, i));

            // Derive a nominal frame rate from the most common sample duration.
            if (durations.Length > 0)
            {
                uint common = durations.GroupBy(d => d).OrderByDescending(g => g.Count()).First().Key;
                if (common > 0)
                {
                    result.FpsNom = result.Timescale;
                    result.FpsDenom = common;
                }
            }
            if (result.FpsNom == 0) { result.FpsNom = 30; result.FpsDenom = 1; }

            ReadAudioTrack(stream, moov, result);

            return result;
        }

        /// <summary>
        /// Reads the track's access units one at a time, in decode order, holding only the one
        /// being handed out. For a caller that goes through the video once - decoding it, say -
        /// this keeps memory flat however long the clip is.
        /// </summary>
        public static IEnumerable<AccessUnit> StreamAccessUnits(MvHevcTrack track, int limit = int.MaxValue)
        {
            using var stream = new FileStream(track.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            int count = Math.Min(limit, track.SampleOffsets.Count);
            for (int i = 0; i < count; i++)
                yield return ReadAccessUnit(stream, track, i);
        }

        private static AccessUnit ReadAccessUnit(FileStream stream, MvHevcTrack track, int i)
        {
            stream.Seek(track.SampleOffsets[i], SeekOrigin.Begin);
            var buffer = new byte[track.SampleSizes[i]];
            stream.ReadExactly(buffer);

            var accessUnit = new AccessUnit
            {
                Index = i,
                Duration = track.SampleDurations[i],
                CompositionOffset = track.SampleCompositionOffsets[i],
            };

            int p = 0;
            while (p + track.NalLengthSize <= buffer.Length)
            {
                int length = 0;
                for (int b = 0; b < track.NalLengthSize; b++)
                    length = (length << 8) | buffer[p + b];
                p += track.NalLengthSize;

                if (length <= 0 || p + length > buffer.Length)
                    break;

                var data = new byte[length];
                Array.Copy(buffer, p, data, 0, length);
                p += length;

                accessUnit.Nalus.Add(new Nalu { Data = data });
            }

            return accessUnit;
        }

        /// <summary>
        /// Copies the audio track's samples and its elementary stream descriptor, so the audio can
        /// be carried into the output without being decoded or re-encoded.
        /// </summary>
        private static void ReadAudioTrack(FileStream stream, MovieBox moov, MvHevcTrack result)
        {
            var audioTrack = moov.Children.OfType<TrackBox>().FirstOrDefault(x => x
                .Children.OfType<MediaBox>().Single()
                .Children.OfType<MediaInformationBox>().Single()
                .Children.OfType<SoundMediaHeaderBox>().FirstOrDefault() != null);
            if (audioTrack == null)
                return;

            var media = audioTrack.Children.OfType<MediaBox>().Single();
            result.AudioTimescale = media.Children.OfType<MediaHeaderBox>().Single().Timescale;

            var stbl = media.Children.OfType<MediaInformationBox>().Single()
                .Children.OfType<SampleTableBox>().Single();

            var sampleEntry = stbl.Children.OfType<SampleDescriptionBox>().Single()
                .Children.OfType<AudioSampleEntry>().FirstOrDefault();

            // QuickTime nests the elementary stream descriptor inside a 'wave' box rather than
            // placing it directly in the sample entry. AACTrack accepts either, but wants the box
            // whose parent is the sample entry, so hand it the wrapper when there is one.
            var descriptor = sampleEntry == null ? null : FindDescendant<ESDBox>(sampleEntry);
            if (descriptor != null)
                result.AudioConfig = descriptor.GetParent() is AudioSampleEntry
                    ? descriptor
                    : descriptor.GetParent() as Box ?? descriptor;
            if (result.AudioConfig == null)
                return;

            var sizes = SampleSizes(stbl);
            var offsets = SampleOffsets(stbl, sizes.Length);
            var durations = SampleDurations(stbl, sizes.Length);

            for (int i = 0; i < offsets.Count; i++)
            {
                stream.Seek(offsets[i], SeekOrigin.Begin);
                var buffer = new byte[sizes[i]];
                stream.ReadExactly(buffer);
                result.AudioSamples.Add(buffer);
                result.AudioSampleDurations.Add(durations[i]);
            }
        }

        /// <summary>
        /// Reads the stereo view information box (vexu/eyes/stri) out of the visual sample entry.
        /// SharpISOBMFF does not model the video extended usage boxes, so it is scanned by hand.
        /// </summary>
        private static byte? ReadStereoViewInfo(FileStream stream, VisualSampleEntry visualSample)
        {
            // Child boxes of a VisualSampleEntry start after its 8 byte box header and its
            // 78 byte body (reserved fields, resolution, compressor name, depth).
            long start = visualSample.GetBoxOffset() + 8 + 78;
            long end = visualSample.GetBoxOffset() + (long)visualSample.Size;
            long? vexu = FindBox(stream, "vexu", start, end);
            if (vexu == null) return null;

            long? eyes = FindBox(stream, "eyes", vexu.Value + 8, end);
            if (eyes == null) return null;

            long? stri = FindBox(stream, "stri", eyes.Value + 8, end);
            if (stri == null) return null;

            // FullBox: 8 byte header, 4 bytes of version and flags, then a single byte of flags.
            stream.Seek(stri.Value + 12, SeekOrigin.Begin);
            int value = stream.ReadByte();
            return value < 0 ? (byte?)null : (byte)value;
        }

        private static long? FindBox(FileStream stream, string type, long start, long end)
        {
            long position = start;
            var header = new byte[8];
            while (position + 8 <= end)
            {
                stream.Seek(position, SeekOrigin.Begin);
                if (stream.Read(header, 0, 8) != 8) return null;

                long size = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                string boxType = System.Text.Encoding.ASCII.GetString(header, 4, 4);
                if (boxType == type) return position;
                if (size < 8) return null;
                position += size;
            }
            return null;
        }

        private static T FindDescendant<T>(IHasBoxChildren box) where T : class
        {
            if (box.Children == null)
                return null;

            foreach (var child in box.Children)
            {
                if (child == null)
                    continue;
                if (child is T match)
                    return match;
                if (child is IHasBoxChildren container)
                {
                    var found = FindDescendant<T>(container);
                    if (found != null)
                        return found;
                }
            }
            return null;
        }

        private static uint[] SampleSizes(SampleTableBox stbl)
        {
            var stsz = stbl.Children.OfType<SampleSizeBox>().Single();
            return stsz.SampleSize != 0
                ? Enumerable.Repeat(stsz.SampleSize, (int)stsz.SampleCount).ToArray()
                : stsz.EntrySize;
        }

        private static List<long> SampleOffsets(SampleTableBox stbl, int sampleCount)
        {
            var stsc = stbl.Children.OfType<SampleToChunkBox>().Single();
            var stco = stbl.Children.OfType<ChunkOffsetBox>().SingleOrDefault();
            var co64 = stbl.Children.OfType<ChunkLargeOffsetBox>().SingleOrDefault();

            long[] chunkOffsets = stco != null
                ? stco.ChunkOffset.Select(x => (long)x).ToArray()
                : co64.ChunkOffset.Select(x => (long)x).ToArray();

            var sizes = SampleSizes(stbl);
            var offsets = new List<long>(sampleCount);

            int sample = 0;
            for (int chunk = 0; chunk < chunkOffsets.Length && sample < sampleCount; chunk++)
            {
                uint samplesPerChunk = 0;
                for (int e = 0; e < stsc.FirstChunk.Length; e++)
                    if (chunk + 1 >= stsc.FirstChunk[e])
                        samplesPerChunk = stsc.SamplesPerChunk[e];

                long position = chunkOffsets[chunk];
                for (int s = 0; s < samplesPerChunk && sample < sampleCount; s++)
                {
                    offsets.Add(position);
                    position += sizes[sample];
                    sample++;
                }
            }

            return offsets;
        }

        private static uint[] SampleDurations(SampleTableBox stbl, int sampleCount)
        {
            var stts = stbl.Children.OfType<TimeToSampleBox>().Single();
            var durations = new uint[sampleCount];

            int sample = 0;
            for (int e = 0; e < stts.SampleCount.Length && sample < sampleCount; e++)
            {
                for (uint n = 0; n < stts.SampleCount[e] && sample < sampleCount; n++)
                    durations[sample++] = stts.SampleDelta[e];
            }

            return durations;
        }

        private static int[] CompositionOffsets(SampleTableBox stbl, int sampleCount)
        {
            var offsets = new int[sampleCount];
            var ctts = stbl.Children.OfType<CompositionOffsetBox>().SingleOrDefault();
            if (ctts == null)
                return offsets;

            int sample = 0;
            for (int e = 0; e < ctts.SampleCount.Length && sample < sampleCount; e++)
            {
                int value = ctts.Version == 0 ? (int)ctts.SampleOffset[e] : ctts.SampleOffset0[e];
                for (uint n = 0; n < ctts.SampleCount[e] && sample < sampleCount; n++)
                    offsets[sample++] = value;
            }

            return offsets;
        }
    }
}
