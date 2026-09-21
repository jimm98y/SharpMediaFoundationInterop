using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>
    /// Reads an MV-HEVC file and writes it back out, changing nothing. Both views are already in
    /// their layers and the parameter sets already describe them, so nothing needs building or
    /// re-stamping - which makes this the one round trip that can be checked byte for byte rather
    /// than by decoding and comparing pictures.
    ///
    /// It is a test of the writer, not a useful conversion: what comes out should be what went in.
    /// </summary>
    public static class MvHevcRemuxer
    {
        public sealed class Result
        {
            public string Path { get; set; }
            public int AccessUnits { get; set; }
            public int NalUnits { get; set; }
            public long CodedBytes { get; set; }
        }

        public static Result Write(string sourcePath, string outputPath)
        {
            var track = MvHevcReader.Read(sourcePath);

            var stereo = new StereoMetadata
            {
                BaseLayerIsLeftEye = !track.EyeViewsReversed,
            };

            var accessUnits = new List<MultiviewAccessUnit>(track.AccessUnits.Count);
            int nalUnits = 0;
            long codedBytes = 0;

            foreach (var source in track.AccessUnits)
            {
                var accessUnit = new MultiviewAccessUnit
                {
                    Duration = (int)source.Duration,
                    CompositionOffset = source.CompositionOffset,
                    IsRandomAccessPoint = source.Nalus.Any(n => n.IsIrap),
                };

                // The layer a NAL unit belongs to is already in its header, so each one simply goes
                // back where it came from.
                foreach (var nalu in source.Nalus)
                {
                    if (LayerRestamper.IsParameterSet(nalu.Data))
                        continue;

                    if (nalu.LayerId == 0)
                        accessUnit.BaseNalus.Add(nalu.Data);
                    else
                        accessUnit.LayerNalus.Add(nalu.Data);

                    nalUnits++;
                    codedBytes += nalu.Data.Length;
                }

                accessUnits.Add(accessUnit);
            }

            MvHevcWriter.Write(outputPath, track.BaseParameterSets, track.LayerParameterSets,
                accessUnits, track.Timescale, stereo, track.HasAudio ? track : null);

            return new Result
            {
                Path = outputPath,
                AccessUnits = accessUnits.Count,
                NalUnits = nalUnits,
                CodedBytes = codedBytes,
            };
        }

        /// <summary>
        /// Compares the coded pictures of two MV-HEVC files NAL unit by NAL unit. The containers
        /// around them differ - box order, free space, what the muxer chose to write - so what is
        /// worth comparing is the bitstream they carry.
        /// </summary>
        public static void Compare(string leftPath, string rightPath)
        {
            var left = MvHevcReader.Read(leftPath);
            var right = MvHevcReader.Read(rightPath);

            Console.WriteLine($"  access units: {left.AccessUnits.Count} against {right.AccessUnits.Count}");

            int compared = 0, identical = 0;
            var mismatches = new List<string>();

            int count = Math.Min(left.AccessUnits.Count, right.AccessUnits.Count);
            for (int i = 0; i < count; i++)
            {
                var a = left.AccessUnits[i].Nalus;
                var b = right.AccessUnits[i].Nalus;

                for (int j = 0; j < Math.Min(a.Count, b.Count); j++)
                {
                    compared++;
                    if (a[j].Data.SequenceEqual(b[j].Data))
                        identical++;
                    else if (mismatches.Count < 5)
                        mismatches.Add($"access unit {i} NAL {j}: " +
                            $"{a[j].Data.Length} bytes against {b[j].Data.Length}");
                }

                if (a.Count != b.Count && mismatches.Count < 5)
                    mismatches.Add($"access unit {i} holds {a.Count} NAL units against {b.Count}");
            }

            Console.WriteLine($"  coded NAL units: {identical}/{compared} byte identical");
            foreach (var mismatch in mismatches)
                Console.WriteLine($"    {mismatch}");

            CompareParameterSets("base", left.BaseParameterSets, right.BaseParameterSets);
            CompareParameterSets("layer", left.LayerParameterSets, right.LayerParameterSets);
        }

        private static void CompareParameterSets(string name, List<byte[]> left, List<byte[]> right)
        {
            int identical = 0;
            int count = Math.Min(left.Count, right.Count);
            for (int i = 0; i < count; i++)
                if (left[i].SequenceEqual(right[i]))
                    identical++;

            Console.WriteLine($"  {name} parameter sets: {identical}/{count} byte identical" +
                (left.Count == right.Count ? "" : $" ({left.Count} against {right.Count})"));
        }
    }
}
