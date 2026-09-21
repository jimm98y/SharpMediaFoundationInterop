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

        public static Result Write(
            string basePath, string dependentPath, string outputPath,
            string templatePath, StereoMetadata stereo)
        {
            var baseTrack = MvHevcReader.Read(basePath);
            var dependentTrack = MvHevcReader.Read(dependentPath);

            if (baseTrack.AccessUnits.Count != dependentTrack.AccessUnits.Count)
                Console.WriteLine($"  the two inputs differ in length: {baseTrack.AccessUnits.Count} " +
                    $"against {dependentTrack.AccessUnits.Count}; the shorter one decides");

            int count = Math.Min(baseTrack.AccessUnits.Count, dependentTrack.AccessUnits.Count);

            // The video parameter set comes from an existing MV-HEVC file. Its extension ties
            // together layer sets, output layer sets, representation formats and a DPB table;
            // patching one a decoder already accepts beats assembling those by hand.
            var template = MvHevcReader.Read(templatePath);
            var builder = new MultiviewBuilder { InterLayerPrediction = false };
            builder.LoadTemplate(template.BaseParameterSets);
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

            var accessUnits = new List<MultiviewAccessUnit>(count);
            for (int i = 0; i < count; i++)
            {
                var source = baseTrack.AccessUnits[i];
                var dependent = dependentTrack.AccessUnits[i];

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
                    accessUnit.LayerNalus.Add(restamper.ToLayerOne(nalu.Data, MultiviewBuilder.LayerPpsId));
                }

                accessUnits.Add(accessUnit);
            }

            MvHevcWriter.Write(outputPath, builder.BaseParameterSets, builder.LayerParameterSets,
                accessUnits, baseTrack.Timescale, stereo,
                baseTrack.HasAudio ? baseTrack : dependentTrack.HasAudio ? dependentTrack : null);

            return new Result
            {
                AccessUnits = accessUnits.Count,
                Bytes = new FileInfo(outputPath).Length,
                Path = outputPath,
            };
        }
    }
}
