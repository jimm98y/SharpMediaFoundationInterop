using SharpMediaFoundationInterop.Transforms;
using SharpMediaFoundationInterop.Transforms.H265;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>
    /// Tests whether the dependent view can be encoded against the base view by replaying it.
    ///
    /// The scheme: one encoder takes the base view alone and its output is layer 0. For each
    /// access unit k, a second encoder is fed the base pictures up to k and then the dependent
    /// picture, and only that last picture is kept. If the encoder is deterministic and causal,
    /// the second encoder's reconstruction of base picture k is bit identical to the first
    /// encoder's, so the dependent picture is predicted from exactly the picture the decoder will
    /// have in layer 0 - which is what makes the two streams mergeable.
    ///
    /// Three things have to hold, and each is checked here rather than assumed:
    ///   1. the same input encodes to the same bytes twice,
    ///   2. a prefix of a sequence encodes to the same bytes as the whole sequence,
    ///   3. appending a dependent picture does not change the base pictures before it.
    /// </summary>
    public sealed class PrefixProbe
    {
        private static readonly HashSet<Guid> _reported = new HashSet<Guid>();

        private readonly MvHevcTrack _track;
        private readonly SingleLayerRewriter _rewriter;

        public PrefixProbe(MvHevcTrack track, SingleLayerRewriter rewriter)
        {
            _track = track;
            _rewriter = rewriter;
        }

        public void Run(int accessUnits, uint bitrate, uint gopSize, uint quality)
        {
            var sps = _rewriter.ParserContext.SeqParameterSets[0];
            uint codedWidth = (uint)sps.PicWidthInLumaSamples;
            uint codedHeight = (uint)sps.PicHeightInLumaSamples;

            var (baseView, dependentView) = DecodeSource(accessUnits, codedWidth, codedHeight);
            Console.WriteLine($"  {baseView.Count} stereo pairs at {codedWidth}x{codedHeight}, " +
                $"{(quality > 0 ? $"quality {quality}" : $"{bitrate / 1_000_000} Mbit/s CBR")}, " +
                $"GOP {(gopSize == 0 ? "default" : gopSize.ToString())}");
            Console.WriteLine();

            byte[][] Encode(IEnumerable<byte[]> frames) =>
                EncodeSequence(frames.ToList(), codedWidth, codedHeight,
                    _track.FpsNom, _track.FpsDenom, bitrate, gopSize, quality);

            // 1. Determinism. Everything else is meaningless without it.
            var master = Encode(baseView);
            var again = Encode(baseView);
            Console.WriteLine($"  1. same input twice:      {Describe(master, again)}");

            // 2. Causality. Does a shorter run agree with the start of a longer one?
            int half = baseView.Count / 2;
            var prefix = Encode(baseView.Take(half));
            Console.WriteLine($"  2. prefix of {half} vs full {baseView.Count}:  {Describe(master.Take(half).ToArray(), prefix)}");

            // 3. The scheme itself: base pictures up to k, then the dependent picture of k.
            foreach (int k in new[] { 1, half / 2, half - 1 }.Distinct().Where(k => k > 0))
            {
                var replayed = Encode(baseView.Take(k + 1).Append(dependentView[k]));
                var basePart = replayed.Take(k + 1).ToArray();

                Console.WriteLine($"  3. replay to k={k,-3} base part: {Describe(master.Take(k + 1).ToArray(), basePart)}" +
                    $"   dependent picture {replayed.Length - 1 == k + 1} kept, " +
                    $"{replayed.Last().Length / 1024.0:F1} KB");

                ReportReferences(replayed, k);
            }

            // 4. Replaying from frame zero for every access unit is quadratic. If a run started at
            // a GOP boundary agrees with the master from that point, the replay only has to go back
            // to the last key frame, which makes the cost linear in the GOP length instead.
            if (gopSize > 1 && baseView.Count > gopSize)
            {
                int boundary = (int)gopSize;
                var fromBoundary = Encode(baseView.Skip(boundary).Take(boundary));
                Console.WriteLine();
                Console.WriteLine($"  4. restart at GOP boundary {boundary}: " +
                    $"{Describe(master.Skip(boundary).Take(boundary).ToArray(), fromBoundary)}");
            }

            // What the cross view prediction is worth: the dependent pictures against the base ones.
            // 5. Cloning the encoder is not possible, so the replay cost is bounded instead by
            // chunking: the master is encoded one GOP at a time, each chunk a fresh encoder run,
            // and every replay starts at its own chunk rather than at the first picture. Test 2
            // is what makes this work - a prefix agrees with the start of a longer run - so the
            // agreement holds by construction rather than by luck with rate control.
            int chunk = gopSize > 1 ? (int)gopSize : 4;
            if (baseView.Count >= chunk * 2)
            {
                int second = chunk;   // the first picture of the second chunk
                var chunkMaster = Encode(baseView.Skip(second).Take(chunk));

                int k = second + chunk / 2;
                var chunkReplay = Encode(baseView.Skip(second).Take(k - second + 1).Append(dependentView[k]));
                var basePart = chunkReplay.Take(k - second + 1).ToArray();

                Console.WriteLine();
                Console.WriteLine($"  5. chunked master, chunk of {chunk} starting at {second}:");
                Console.WriteLine($"       replay for k={k} vs that chunk: " +
                    $"{Describe(chunkMaster.Take(k - second + 1).ToArray(), basePart)}");

                long full = (long)baseView.Count * (baseView.Count + 3) / 2;
                long chunked = (long)baseView.Count * (chunk + 3) / 2;
                Console.WriteLine($"       picture encodes for {baseView.Count} access units: " +
                    $"{full} replaying from the start, {chunked} chunked");
            }

            // The dependent picture against the base picture of the same access unit, which is the
            // comparison that means anything - averaging the base view in would fold in the key
            // frame, which is many times the size of the pictures around it.
            Console.WriteLine();
            // And against the alternative: the dependent view encoded on its own, predicting from
            // its own previous picture rather than across the views.
            var alone = Encode(dependentView);

            Console.WriteLine("   k   base picture   dependent, cross view   dependent, on its own");
            var ratios = new List<double>();
            for (int k = 0; k < Math.Min(baseView.Count, 8); k++)
            {
                var replayed = Encode(baseView.Take(k + 1).Append(dependentView[k]));
                var dependent = replayed.Last();
                bool intra = IsIntra(dependent);
                double ratio = 100.0 * dependent.Length / master[k].Length;
                if (!intra) ratios.Add(ratio);

                Console.WriteLine($"  {k,2}   {master[k].Length / 1024.0,9:F1} KB   {dependent.Length / 1024.0,17:F1} KB   " +
                    $"{alone[k].Length / 1024.0,17:F1} KB{(intra ? "  (cross view coded intra)" : "")}");
            }

            if (ratios.Count > 0)
                Console.WriteLine($"  the dependent view averages {ratios.Average():F0}% of the base picture beside it");

            long perAccessUnitCross = 0, perAccessUnitAlone = 0;
            for (int k = 1; k < Math.Min(baseView.Count, 8); k++)   // skipping the key frame
            {
                perAccessUnitCross += master[k].Length +
                    Encode(baseView.Take(k + 1).Append(dependentView[k])).Last().Length;
                perAccessUnitAlone += master[k].Length + alone[k].Length;
            }

            Console.WriteLine();
            Console.WriteLine($"  per access unit, excluding the key frame:");
            Console.WriteLine($"    cross view predicted dependent: {perAccessUnitCross / 7 / 1024.0:F1} KB");
            Console.WriteLine($"    dependent on its own (simulcast): {perAccessUnitAlone / 7 / 1024.0:F1} KB");
        }

        /// <summary>True when the picture carries an IRAP NAL unit, so it was coded without prediction.</summary>
        private static bool IsIntra(byte[] sample)
        {
            foreach (var nalu in AnnexB.Nalus(sample))
            {
                uint type = (uint)((nalu[0] >> 1) & 0x3F);
                if (type >= 16 && type <= 23)
                    return true;
            }
            return false;
        }

        /// <summary>Compares two coded sequences picture by picture.</summary>
        private static string Describe(byte[][] left, byte[][] right)
        {
            if (left.Length != right.Length)
                return $"different length ({left.Length} vs {right.Length})";

            for (int i = 0; i < left.Length; i++)
                if (!left[i].SequenceEqual(right[i]))
                    return $"DIFFER at picture {i} ({left[i].Length} vs {right[i].Length} bytes)";

            return $"identical ({left.Length} pictures)";
        }

        /// <summary>Prints what the last picture of the replay references, which should be the base picture of k.</summary>
        private static void ReportReferences(byte[][] coded, int k)
        {
            var parser = new MvHevcParser();
            var parameterSets = new List<byte[]>();
            foreach (var sample in coded)
                foreach (var nalu in AnnexB.Nalus(sample))
                {
                    uint type = (uint)((nalu[0] >> 1) & 0x3F);
                    if (type is 32 or 33 or 34)
                        parameterSets.Add(nalu);
                }
            parser.ParseParameterSets(parameterSets);

            foreach (var nalu in AnnexB.Nalus(coded.Last()))
            {
                uint type = (uint)((nalu[0] >> 1) & 0x3F);
                if (type > 21 || (type > 9 && type < 16))
                    continue;

                var parsed = parser.ParseSlice(new Nalu { Data = nalu });
                int poc = parser.DerivePoc(parsed);
                string sliceType = parsed.Header.SliceType switch { 0 => "B", 1 => "P", 2 => "I", _ => "?" };
                Console.WriteLine($"       the kept picture is a {sliceType} slice at poc {poc}");
            }
        }

        private (List<byte[]> BaseView, List<byte[]> DependentView) DecodeSource(
            int accessUnits, uint codedWidth, uint codedHeight)
        {
            int dpbSize = Math.Max(_rewriter.MaxDpbOccupancy + 1, 8);
            var parameterSets = _rewriter.BuildParameterSets(dpbSize, _rewriter.MaxReorder);
            long step = 10_000_000L * _track.FpsDenom / _track.FpsNom / SingleLayerRewriter.PocScale;

            var frames = new SortedDictionary<int, byte[]>();
            int pictures = Math.Min(_rewriter.Pictures.Count, accessUnits * SingleLayerRewriter.PocScale + 64);
            int limit = accessUnits * SingleLayerRewriter.PocScale;

            using (var decoder = new SpatialDecoder(codedWidth, codedHeight, _track.FpsNom, _track.FpsDenom))
            {
                decoder.SendParameterSets(parameterSets);
                foreach (var picture in _rewriter.Pictures.Take(pictures))
                    foreach (var frame in decoder.Decode(_rewriter.RewriteSlice(picture), picture.Poc * step))
                        if (frame.Timestamp / step < limit)
                            frames[(int)(frame.Timestamp / step)] = frame.Nv12;
                foreach (var frame in decoder.Flush())
                    if (frame.Timestamp / step < limit)
                        frames[(int)(frame.Timestamp / step)] = frame.Nv12;
            }

            var baseView = new List<byte[]>();
            var dependentView = new List<byte[]>();
            for (int poc = 0; poc < limit; poc += 2)
            {
                if (!frames.TryGetValue(poc, out var b) || !frames.TryGetValue(poc + 1, out var d))
                    break;
                baseView.Add(b);
                dependentView.Add(d);
            }

            return (baseView, dependentView);
        }

        private static byte[][] EncodeSequence(List<byte[]> frames, uint width, uint height,
            uint fpsNom, uint fpsDenom, uint bitrate, uint gopSize, uint quality)
        {
            using var encoder = new H265Encoder(width, height, fpsNom, fpsDenom, bitrate);
            if (gopSize > 0)
                encoder.CodecProperties[CodecApiProperties.GopSize] = gopSize;

            // Constant bitrate carries buffer state from picture to picture, so a run that starts
            // part way through the sequence cannot agree with one that started at the beginning.
            // Quality mode has no such state.
            if (quality > 0)
            {
                encoder.CodecProperties[CodecApiProperties.RateControlMode] =
                    CodecApiProperties.RateControlModes.Quality;
                encoder.CodecProperties[CodecApiProperties.Quality] = quality;
            }
            encoder.Initialize();

            foreach (var result in encoder.CodecPropertyResults)
                if (!result.Applied && !_reported.Contains(result.Property))
                {
                    _reported.Add(result.Property);
                    Console.WriteLine($"  [not applied: {result.Property}, supported={result.Supported}]");
                }

            var buffer = new byte[encoder.OutputSize];
            var output = new List<byte[]>();
            long frameDuration = 10_000_000L * fpsDenom / fpsNom;

            void Drain()
            {
                while (encoder.ProcessOutput(ref buffer, out uint length, out long _) && length > 0)
                    output.Add(buffer.Take((int)length).ToArray());
            }

            for (int i = 0; i < frames.Count; i++)
            {
                encoder.ProcessInput(frames[i], i * frameDuration);
                Drain();
            }

            encoder.BeginDrain();
            for (int quiet = 0; quiet < 4;)
            {
                int before = output.Count;
                Drain();
                if (output.Count > before) quiet = 0; else quiet++;
            }
            encoder.EndDrain();

            return output.ToArray();
        }

    }
}
