using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>
    /// The reverse of the split: takes one ordinary HEVC file per eye and writes a single MV-HEVC
    /// file carrying both. No pixels are touched - the coded slices are moved into their layers and
    /// remuxed, so whatever quality the two inputs had is what comes out.
    ///
    /// The two layers are coded independently, because nothing here can encode a view against
    /// another one. The file is a conformant MV-HEVC stream that declares no prediction between the
    /// layers, which costs about what the two inputs cost together rather than what Apple manages.
    /// </summary>
    public static class LeftRightTranscoder
    {
        public sealed class Result
        {
            public int AccessUnits { get; set; }
            public long Bytes { get; set; }
            public string Path { get; set; }
        }

        private static bool IsVps(byte[] nalu) => ((nalu[0] >> 1) & 0x3F) == 32;

        /// <summary>
        /// The right eye goes in the base layer and the left in layer 1, which is what the eye
        /// mapping SEI carried over from the template says - see MultiviewBuilder.
        /// </summary>
        /// <param name="templateParameterSets">
        /// What the stereo pair is described with - see <see cref="MultiviewTemplate"/>. The
        /// built-in ones unless a different recording's are wanted.
        /// </param>
        public static Result Write(
            string leftPath, string rightPath, string outputPath,
            StereoMetadata stereo, List<byte[]> templateParameterSets = null)
        {
            // Only the indexes are read: both files are worked through an access unit at a time,
            // so a recording of any length costs the same.
            var baseTrack = MvHevcReader.Read(rightPath, loadSamples: false);
            var dependentTrack = MvHevcReader.Read(leftPath, loadSamples: false);

            int baseCount = baseTrack.SamplePositions.Count;
            int dependentCount = dependentTrack.SamplePositions.Count;
            if (baseCount != dependentCount)
                Console.WriteLine($"  the two inputs differ in length: {baseCount} " +
                    $"against {dependentCount}; the shorter one decides");

            // The video parameter set is patched rather than assembled: its extension ties
            // together layer sets, output layer sets, representation formats and a DPB table, and
            // starting from one a decoder already accepts beats building those by hand.
            var builder = new MultiviewBuilder();
            builder.LoadTemplate(templateParameterSets ?? MultiviewTemplate.ParameterSets());
            builder.Build(baseTrack.BaseParameterSets, dependentTrack.BaseParameterSets,
                (int)baseTrack.DisplayWidth, (int)baseTrack.DisplayHeight);

            if (!builder.RoundTripsCleanly)
                Console.WriteLine("  the template video parameter set does not survive a read and a write " +
                    "unchanged, so the one written here differs from the one that was verified");

            // The restamper reads slices against the dependent view's original parameter sets and
            // writes them against layer 1's, so it needs both. It also needs the multiview video
            // parameter set: above layer 0 the slice header's own syntax depends on the layer's
            // place in it, so the writer reads the extension back while writing.
            // The dependent view's own video parameter set describes a single layer stream, and
            // would replace the multiview one, so only its sequence and picture parameter sets go in.
            var multiviewVps = builder.BaseParameterSets.Where(IsVps);
            var restamper = new LayerRestamper(multiviewVps
                .Concat(dependentTrack.BaseParameterSets.Where(n => !IsVps(n)))
                .Concat(builder.LayerParameterSets.Where(n => !IsVps(n))));

            int written = 0;

            // Built one at a time as the writer asks for them, so only the access unit being
            // written is ever in memory. The shorter file ends the pairing.
            IEnumerable<MultiviewAccessUnit> AccessUnits()
            {
                using var sources = MvHevcReader.StreamAccessUnits(baseTrack).GetEnumerator();
                using var dependents = MvHevcReader.StreamAccessUnits(dependentTrack).GetEnumerator();

                // Layer 1's slices are restamped into this, which is emptied for each access unit:
                // the writer has written the last one out before it asks for the next.
                var layerOne = new MemoryStream(1 << 20);

                while (sources.MoveNext() && dependents.MoveNext())
                {
                    var source = sources.Current;
                    var dependent = dependents.Current;
                    layerOne.SetLength(0);

                        var accessUnit = new MultiviewAccessUnit
                    {
                        Duration = (int)source.Duration,
                        CompositionOffset = source.CompositionOffset,
                        IsRandomAccessPoint = source.Nalus.Any(n => n.IsIrap),
                    };

                    foreach (var nalu in source.Nalus)
                    {
                        if (LayerRestamper.IsParameterSet(nalu.Data))
                            continue;   // parameter sets live in the sample entry
                        accessUnit.BaseNalus.Add(nalu.Data);
                    }

                    foreach (var nalu in dependent.Nalus)
                    {
                        if (LayerRestamper.IsParameterSet(nalu.Data))
                            continue;
                        if (!LayerRestamper.IsSlice(nalu.Data))
                            continue;   // messages belong to the view they came from and are dropped
                        accessUnit.LayerNalus.Add(restamper.ToLayerOne(nalu.Data, MultiviewBuilder.LayerPpsId, layerOne));
                    }

                    written++;
                    yield return accessUnit;
                }
            }

            MvHevcWriter.Write(outputPath, builder.BaseParameterSets, builder.LayerParameterSets,
                AccessUnits(), baseTrack.Timescale, stereo,
                baseTrack.HasAudio ? baseTrack : dependentTrack.HasAudio ? dependentTrack : null);

            return new Result
            {
                AccessUnits = written,
                Bytes = new FileInfo(outputPath).Length,
                Path = outputPath,
            };
        }
    }
}
