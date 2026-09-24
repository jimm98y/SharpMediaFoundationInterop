using SharpISOBMFF;
using SharpMP4.Readers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>
    /// A NAL unit as a sample stores it: the bytes as they are, with no start code or length in
    /// front of them. What it adds over the plain array SharpMP4 hands back is the handful of
    /// fields in the two byte header, read straight out of it rather than by parsing the unit -
    /// which layer a picture belongs to, and whether it is a slice and a random access point, are
    /// asked of nearly every unit that passes through here.
    /// </summary>
    public sealed class Nalu
    {
        public byte[] Data { get; set; }

        public uint Type => (uint)((Data[0] >> 1) & 0x3F);
        public uint LayerId => (uint)(((Data[0] & 1) << 5) | (Data[1] >> 3));
        public uint TemporalId => (uint)((Data[1] & 0x07) - 1);

        /// <summary>VCL NAL unit, i.e. a coded slice segment.</summary>
        public bool IsSlice => Type <= 31;

        public bool IsIrap => Type >= 16 && Type <= 23;
        public bool IsIdr => Type == 19 || Type == 20;
        public bool IsCra => Type == 21;
        public bool IsRasl => Type == 8 || Type == 9;

        public override string ToString() => $"t={Type} L{LayerId} {Data.Length}B";
    }

    /// <summary>
    /// One sample of the track, split into its NAL units. In an MV-HEVC track that is one access
    /// unit, holding both views' pictures, with the timing SharpMP4 read from the sample tables.
    /// </summary>
    public sealed class AccessUnit
    {
        public int Index { get; set; }
        public List<Nalu> Nalus { get; } = new List<Nalu>();

        /// <summary>Composition time offset from the track's ctts, in media timescale units.</summary>
        public int CompositionOffset { get; set; }
        public uint Duration { get; set; }

        public Nalu SliceOfLayer(uint layerId)
        {
            foreach (var nalu in Nalus)
                if (nalu.IsSlice && nalu.LayerId == layerId)
                    return nalu;
            return null;
        }
    }

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
        /// The file this was read from, so its samples can be read one at a time instead of all
        /// being held - see <see cref="MvHevcReader.StreamAccessUnits"/>.
        /// </summary>
        public string Path { get; set; }

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

            result.Path = path;

            // A nominal frame rate, from the most common sample duration.
            var stts = stbl.Children.OfType<TimeToSampleBox>().SingleOrDefault();
            uint common = stts == null ? 0 : stts.SampleDelta
                .Select((delta, i) => (delta, count: stts.SampleCount[i]))
                .GroupBy(x => x.delta)
                .OrderByDescending(g => g.Sum(x => x.count))
                .Select(g => g.Key)
                .FirstOrDefault();
            if (common > 0)
            {
                result.FpsNom = result.Timescale;
                result.FpsDenom = common;
            }
            else
            {
                result.FpsNom = 30;
                result.FpsDenom = 1;
            }

            var reader = new VideoReader();
            reader.Parse(container);

            if (loadSamples)
                result.AccessUnits.AddRange(ReadAccessUnits(reader, TrackId(reader, HandlerTypes.Video), int.MaxValue));

            ReadAudioTrack(reader, moov, result);

            return result;
        }

        /// <summary>The id of the first track of a kind, or zero if the file has none.</summary>
        private static uint TrackId(VideoReader reader, string handlerType) => reader.Tracks
            .Where(x => x.Value.Track.HandlerType == handlerType)
            .Select(x => x.Key)
            .FirstOrDefault();

        /// <summary>
        /// Reads samples in decode order and splits each into its NAL units. A sample of an
        /// MV-HEVC track is one access unit, holding both views' pictures.
        /// </summary>
        private static IEnumerable<AccessUnit> ReadAccessUnits(VideoReader reader, uint trackId, int limit)
        {
            for (int i = 0; i < limit; i++)
            {
                var sample = reader.ReadSample(trackId);
                if (sample == null)
                    yield break;

                var accessUnit = new AccessUnit
                {
                    Index = i,
                    Duration = (uint)sample.Duration,
                    CompositionOffset = (int)(sample.PTS - sample.DTS),
                };

                foreach (var nalu in reader.ParseSample(trackId, sample.Data))
                    accessUnit.Nalus.Add(new Nalu { Data = nalu });

                yield return accessUnit;
            }
        }

        /// <summary>
        /// Reads the track's access units one at a time, in decode order, holding only the one
        /// being handed out. For a caller that goes through the video once - decoding it, say -
        /// this keeps memory flat however long the clip is.
        /// </summary>
        public static IEnumerable<AccessUnit> StreamAccessUnits(MvHevcTrack track, int limit = int.MaxValue)
        {
            // The file stays open for as long as the caller is reading: each sample is fetched
            // from it as it is asked for, and none of the others are held.
            using var stream = new FileStream(track.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var container = new Container();
            container.Read(new IsoStream(new StreamWrapper(stream)));

            var reader = new VideoReader();
            reader.Parse(container);

            foreach (var accessUnit in ReadAccessUnits(reader, TrackId(reader, HandlerTypes.Video), limit))
                yield return accessUnit;
        }


        /// <summary>
        /// Copies the audio track's samples and its elementary stream descriptor, so the audio can
        /// be carried into the output without being decoded or re-encoded.
        /// </summary>
        private static void ReadAudioTrack(VideoReader reader, MovieBox moov, MvHevcTrack result)
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

            uint trackId = TrackId(reader, HandlerTypes.Sound);
            for (var sample = reader.ReadSample(trackId); sample != null; sample = reader.ReadSample(trackId))
            {
                result.AudioSamples.Add(sample.Data);
                result.AudioSampleDurations.Add((uint)sample.Duration);
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




    }
}
