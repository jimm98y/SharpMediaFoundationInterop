using SharpISOBMFF;
using SharpISOBMFF.Extensions;
using SharpMP4.Builders;
using SharpMP4.Tracks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>One access unit: the base view's coded slices, then the dependent view's.</summary>
    public sealed class MultiviewAccessUnit
    {
        public List<byte[]> BaseNalus { get; } = new List<byte[]>();
        public List<byte[]> LayerNalus { get; } = new List<byte[]>();
        public int Duration { get; set; }
        public int CompositionOffset { get; set; }
        public bool IsRandomAccessPoint { get; set; }
    }

    /// <summary>The stereo metadata Apple writes alongside the two views.</summary>
    public sealed class StereoMetadata
    {
        /// <summary>Distance between the two cameras, in micrometres.</summary>
        public uint BaselineMicrometres { get; set; } = 19274;

        /// <summary>Horizontal field of view, in thousandths of a degree.</summary>
        public uint HorizontalFieldOfViewMillidegrees { get; set; } = 63400;

        /// <summary>How far the views are shifted to bring the comfortable depth to the screen.</summary>
        public int DisparityAdjustment { get; set; } = 200;

        /// <summary>True when layer 0 holds the left eye rather than the right.</summary>
        public bool BaseLayerIsLeftEye { get; set; }
    }

    /// <summary>
    /// Writes a two layer MV-HEVC file: one video track whose samples carry both views, with the
    /// base view described by hvcC and the dependent view by lhvC, and the stereo metadata Apple
    /// looks for in vexu.
    ///
    /// The sample entry is assembled in two passes. Mp4Builder writes an ordinary hvc1 entry from
    /// the base view's parameter sets, and the multi-layer boxes are grafted into it afterwards,
    /// which needs the chunk offsets moved by however much the movie box grew.
    /// </summary>
    public static class MvHevcWriter
    {
        public static void Write(
            string path,
            IEnumerable<byte[]> baseParameterSets,
            IEnumerable<byte[]> layerParameterSets,
            IReadOnlyList<MultiviewAccessUnit> accessUnits,
            uint timescale,
            StereoMetadata stereo,
            MvHevcTrack audioSource = null)
        {
            WriteTrack(path, baseParameterSets, accessUnits, timescale, audioSource);
            AddMultiviewBoxes(path, layerParameterSets, stereo);
        }

        private static void WriteTrack(
            string path,
            IEnumerable<byte[]> baseParameterSets,
            IReadOnlyList<MultiviewAccessUnit> accessUnits,
            uint timescale,
            MvHevcTrack audioSource)
        {
            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            var builder = new Mp4Builder(new SingleStreamOutput(output));

            int defaultDuration = accessUnits.Count > 0 ? accessUnits[0].Duration : 1;
            var track = new H265Track(timescale, defaultDuration);
            builder.AddTrack(track);

            AACTrack audioTrack = null;
            if (audioSource != null && audioSource.HasAudio)
            {
                audioTrack = new AACTrack(audioSource.AudioConfig, audioSource.AudioTimescale);
                builder.AddTrack(audioTrack);
            }

            // The base view's parameter sets go into the sample entry rather than the samples.
            foreach (var parameterSet in baseParameterSets)
                builder.ProcessTrackSample(track.TrackID, parameterSet, defaultDuration);

            foreach (var accessUnit in accessUnits)
            {
                var sample = new List<byte>();
                foreach (var nalu in accessUnit.BaseNalus.Concat(accessUnit.LayerNalus))
                    AppendLengthPrefixed(sample, nalu);

                builder.ProcessRawSample(track.TrackID, sample.ToArray(),
                    accessUnit.Duration, accessUnit.IsRandomAccessPoint, accessUnit.CompositionOffset);
            }

            if (audioTrack != null)
            {
                for (int i = 0; i < audioSource.AudioSamples.Count; i++)
                    builder.ProcessRawSample(audioTrack.TrackID, audioSource.AudioSamples[i],
                        (int)audioSource.AudioSampleDurations[i], true);
            }

            builder.FinalizeMedia();
        }

        /// <summary>
        /// Adds lhvC and vexu to the hvc1 sample entry of a file already written. Growing the movie
        /// box pushes the media data along, so every chunk offset moves with it.
        /// </summary>
        private static void AddMultiviewBoxes(string path, IEnumerable<byte[]> layerParameterSets, StereoMetadata stereo)
        {
            string temporary = path + ".tmp";

            // The media data is not held in memory - the box tree points back into the file it was
            // read from - so the input has to stay open while the output is written, which means
            // writing somewhere else and swapping afterwards.
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var container = new Container();
                container.Read(new IsoStream(new StreamWrapper(input)));

                var moov = FindFirst<MovieBox>(container.Children)
                    ?? throw new InvalidOperationException("the file just written has no movie box");
                var sampleEntry = FindFirst<VisualSampleEntry>(container.Children)
                    ?? throw new InvalidOperationException("the file just written has no visual sample entry");

                ulong before = moov.CalculateSize();

                sampleEntry.Children.Add(BuildLhvC(layerParameterSets, sampleEntry));
                sampleEntry.Children.Add(BuildVexu(stereo, sampleEntry));

                ulong after = moov.CalculateSize();
                long delta = ((long)after - (long)before) >> 3;
                if (delta != 0)
                    moov.ModifyChunkOffsets(delta);

                using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.Read);
                container.Write(new IsoStream(new StreamWrapper(output)));
            }

            File.Move(temporary, path, overwrite: true);
        }

        /// <summary>The configuration record for the dependent layer, the counterpart of hvcC.</summary>
        private static Box BuildLhvC(IEnumerable<byte[]> layerParameterSets, IMp4Serializable parent)
        {
            var lhvC = new LHEVCConfigurationBox();
            lhvC.SetParent(parent);

            var config = new LHEVCDecoderConfigurationRecord();
            config.SetParent(lhvC);
            lhvC._LHEVCConfig = config;

            config.ConfigurationVersion = 1;
            config.NumTemporalLayers = 1;
            config.TemporalIdNested = true;
            config.LengthSizeMinusOne = 3;          // four byte NAL unit lengths, as in the samples
            config.ParallelismType = 0;

            // The spec fixes these to all ones; a decoder that checks them rejects the record.
            config.Reserved = 0x0F;
            config.Reserved0 = 0x3F;
            config.Reserved1 = 0x03;

            // One array per NAL unit type, which for a parameter set list means VPS, SPS and PPS.
            var groups = layerParameterSets
                .GroupBy(n => (byte)((n[0] >> 1) & 0x3F))
                .OrderBy(g => g.Key)
                .ToList();

            config.NumOfArrays = (byte)groups.Count;
            config.ArrayCompleteness = groups.Select(_ => true).ToArray();
            config.Reserved2 = groups.Select(_ => false).ToArray();
            config.NALUnitType = groups.Select(g => g.Key).ToArray();
            config.NumNalus = groups.Select(g => (ushort)g.Count()).ToArray();
            config.NalUnitLength = groups.Select(g => g.Select(n => (ushort)n.Length).ToArray()).ToArray();
            config.NalUnit = groups.Select(g => g.ToArray()).ToArray();

            return lhvC;
        }

        /// <summary>
        /// vexu, the box that says the track carries two eyes and how the camera that shot them was
        /// arranged. Written to match what Apple writes, since that is the only known good example.
        /// </summary>
        private static Box BuildVexu(StereoMetadata stereo, IMp4Serializable parent)
        {
            var vexu = new VideoExtendedUsageBox();
            vexu.SetParent(parent);
            vexu.Children = new List<Box>();

            var eyes = new StereoViewBox();
            eyes.SetParent(vexu);
            eyes.Children = new List<Box>();
            vexu.Children.Add(eyes);

            var stri = new StereoViewInformationBox();
            stri.SetParent(eyes);
            stri.HasLeftEyeView = true;
            stri.HasRightEyeView = true;
            stri.HasAdditionalViews = false;
            // The flag says whether the views are stored in the other order than the default, which
            // is the left eye first.
            stri.EyeViewsReversed = !stereo.BaseLayerIsLeftEye;
            eyes.Children.Add(stri);

            var cams = new StereoCameraSystemBox();
            cams.SetParent(eyes);
            cams.Children = new List<Box>();
            eyes.Children.Add(cams);

            var blin = new StereoCameraSystemBaselineBox();
            blin.SetParent(cams);
            blin.BaselineValue = stereo.BaselineMicrometres;
            cams.Children.Add(blin);

            var cmfy = new StereoComfortBox();
            cmfy.SetParent(eyes);
            cmfy.Children = new List<Box>();
            eyes.Children.Add(cmfy);

            var dadj = new StereoComfortDisparityAdjustmentBox();
            dadj.SetParent(cmfy);
            dadj.DisparityAdjustment = stereo.DisparityAdjustment;
            cmfy.Children.Add(dadj);

            return vexu;
        }

        private static T FindFirst<T>(List<Box> children) where T : Box
        {
            if (children == null)
                return null;

            foreach (var child in children)
            {
                if (child is T match)
                    return match;
                if (child is IHasBoxChildren parent)
                {
                    var found = FindFirst<T>(parent.Children);
                    if (found != null)
                        return found;
                }
            }

            return null;
        }

        private static void AppendLengthPrefixed(List<byte> sample, byte[] nalu)
        {
            sample.Add((byte)(nalu.Length >> 24));
            sample.Add((byte)(nalu.Length >> 16));
            sample.Add((byte)(nalu.Length >> 8));
            sample.Add((byte)nalu.Length);
            sample.AddRange(nalu);
        }
    }
}
