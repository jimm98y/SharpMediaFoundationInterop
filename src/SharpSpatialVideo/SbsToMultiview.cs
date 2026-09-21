using SharpMediaFoundationInterop.Transforms;
using SharpMediaFoundationInterop.Transforms.H265;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>
    /// Turns a side by side file into MV-HEVC. Unlike the left/right transcode this has to decode
    /// and encode, because the two eyes share a picture in the source and have to be separated.
    ///
    /// Two ways of coding the pair are offered, because on this machine neither is obviously
    /// better:
    ///
    ///   simulcast - each view encoded on its own, with its own temporal prediction and no
    ///   prediction between the views. The layers are independent and the file declares as much.
    ///
    ///   cross view - the two views encoded as one interleaved sequence with a GOP of two, which
    ///   makes every base picture an IDR and every dependent picture predict from the base picture
    ///   beside it. That is real disparity prediction, at the cost of coding the base view intra
    ///   only. Measured on this footage it spends more bits than simulcast does, so it is offered
    ///   rather than chosen.
    /// </summary>
    public sealed class SbsToMultiview
    {
        private readonly string _templatePath;

        public SbsToMultiview(string templatePath)
        {
            _templatePath = templatePath;
        }

        public sealed class Result
        {
            public string Path { get; set; }
            public int AccessUnits { get; set; }
            public long Bytes { get; set; }
            public long BaseBytes { get; set; }
            public long DependentBytes { get; set; }
        }

        /// <summary>Decodes the source once and writes whichever versions were asked for.</summary>
        public List<Result> Write(string sourcePath, string outputStem, uint bitrate,
            bool simulcast, bool crossView, int limit = int.MaxValue)
        {
            var (left, right, track) = DecodeViews(sourcePath, limit);
            Console.WriteLine($"  {left.Count} stereo pairs at {Width}x{Height} " +
                $"(coded {CodedWidth}x{CodedHeight})");

            var results = new List<Result>();

            if (simulcast)
                results.Add(WriteSimulcast(left, right, track, outputStem + "_simulcast.mov", bitrate));

            if (crossView)
                results.Add(WriteCrossView(left, right, track, outputStem + "_crossview.mov", bitrate));

            return results;
        }

        /// <summary>Key frame spacing for the simulcast pair, which both views share.</summary>
        public uint SimulcastGopSize { get; set; } = 30;

        private int Width { get; set; }
        private int Height { get; set; }
        private int CodedWidth { get; set; }
        private int CodedHeight { get; set; }

        /// <summary>
        /// Splits every frame of the side by side source down the middle. The halves are the two
        /// eyes, each at half the source width.
        /// </summary>
        private (List<byte[]> Left, List<byte[]> Right, MvHevcTrack Track) DecodeViews(
            string path, int limit)
        {
            var track = MvHevcReader.Read(path);

            // The source is an ordinary single layer file, so its own parameter sets drive the
            // decoder and its samples go in as they are.
            var rewriter = new SingleLayerRewriter(track);
            rewriter.Plan(baseViewOnly: true);

            var sps = rewriter.ParserContext.SeqParameterSets[0];
            int sourceCodedWidth = (int)sps.PicWidthInLumaSamples;
            int sourceCodedHeight = (int)sps.PicHeightInLumaSamples;
            int sourceWidth = (int)track.DisplayWidth;
            int sourceHeight = (int)track.DisplayHeight;

            Width = sourceWidth / 2;
            Height = sourceHeight;
            CodedWidth = Align(Width, 8);
            CodedHeight = Align(Height, 8);

            var left = new List<byte[]>();
            var right = new List<byte[]>();

            long frameDuration = 10_000_000L * track.FpsDenom / track.FpsNom;
            var pictures = rewriter.Pictures.Take(limit).ToList();

            using var decoder = new SpatialDecoder((uint)sourceCodedWidth, (uint)sourceCodedHeight,
                track.FpsNom, track.FpsDenom);
            decoder.SendParameterSets(track.BaseParameterSets);

            void Take(DecodedFrame frame)
            {
                left.Add(SbsComposer.CropLeft(frame.Nv12, sourceCodedWidth, sourceCodedHeight,
                    Width, Height, CodedWidth, CodedHeight));
                right.Add(SbsComposer.CropRight(frame.Nv12, sourceCodedWidth, sourceCodedHeight,
                    Width, Height, CodedWidth, CodedHeight));
            }

            for (int i = 0; i < pictures.Count; i++)
                foreach (var frame in decoder.Decode(pictures[i].Source.Nalu.Data, i * frameDuration))
                    Take(frame);
            foreach (var frame in decoder.Flush())
                Take(frame);

            return (left, right, track);
        }

        private static int Align(int value, int multiple) =>
            (value + multiple - 1) / multiple * multiple;

        /// <summary>Each view encoded on its own, with no prediction between them.</summary>
        private Result WriteSimulcast(List<byte[]> left, List<byte[]> right, MvHevcTrack track,
            string path, uint bitrate)
        {
            // Both views take the same fixed GOP. Left to itself the encoder picks key frames to
            // suit each view's content, and the two views then disagree about where a sequence
            // starts: a key frame in the base view resets the picture order count and empties the
            // buffer, while the dependent view carries on referencing pictures that are no longer
            // there. Every picture of an access unit has to share a count, so both views have to
            // reset in the same places.
            var baseView = Encode(left, track, bitrate / 2, SimulcastGopSize);
            var dependentView = Encode(right, track, bitrate / 2, SimulcastGopSize);

            return Assemble(path, baseView, dependentView, track, interLayerPrediction: false);
        }

        /// <summary>
        /// Both views through one encoder as an interleaved sequence with a GOP of two, so the
        /// dependent pictures predict from the base picture beside them.
        /// </summary>
        private Result WriteCrossView(List<byte[]> left, List<byte[]> right, MvHevcTrack track,
            string path, uint bitrate)
        {
            var interleaved = new List<byte[]>(left.Count * 2);
            for (int i = 0; i < Math.Min(left.Count, right.Count); i++)
            {
                interleaved.Add(left[i]);
                interleaved.Add(right[i]);
            }

            var coded = Encode(interleaved, track, bitrate, gopSize: 2, doubleRate: true);

            // The interleaved stream is itself ordinary single layer HEVC, and decoded as such it
            // is what the two layers have to reproduce - the check that the reference set rewrite
            // is exact rather than merely plausible.
            if (DumpViews)
                WriteSingleView(path + ".interleaved.mp4", coded, track, doubleRate: true);

            // Even pictures are the base view, odd ones the dependent view.
            var baseView = new EncodedStream { ParameterSets = coded.ParameterSets };
            var dependentView = new EncodedStream { ParameterSets = coded.ParameterSets };
            for (int i = 0; i < coded.Pictures.Count; i++)
                (i % 2 == 0 ? baseView.Pictures : dependentView.Pictures).Add(coded.Pictures[i]);

            return Assemble(path, baseView, dependentView, track, interLayerPrediction: true);
        }

        private sealed class EncodedStream
        {
            public List<byte[]> ParameterSets { get; set; } = new List<byte[]>();
            public List<List<byte[]>> Pictures { get; } = new List<List<byte[]>>();
        }

        private EncodedStream Encode(List<byte[]> frames, MvHevcTrack track, uint bitrate,
            uint gopSize, bool doubleRate = false)
        {
            using var encoder = new H265Encoder((uint)CodedWidth, (uint)CodedHeight,
                doubleRate ? track.FpsNom * 2 : track.FpsNom, track.FpsDenom, bitrate);
            if (gopSize > 0)
                encoder.CodecProperties[CodecApiProperties.GopSize] = gopSize;
            encoder.Initialize();

            var buffer = new byte[encoder.OutputSize];
            var samples = new List<byte[]>();
            long frameDuration = 10_000_000L * track.FpsDenom / track.FpsNom / (doubleRate ? 2 : 1);

            void Drain()
            {
                while (encoder.ProcessOutput(ref buffer, out uint length, out long _) && length > 0)
                    samples.Add(buffer.Take((int)length).ToArray());
            }

            for (int i = 0; i < frames.Count; i++)
            {
                encoder.ProcessInput(frames[i], i * frameDuration);
                Drain();
            }

            encoder.BeginDrain();
            for (int quiet = 0; quiet < 4;)
            {
                int before = samples.Count;
                Drain();
                if (samples.Count > before) quiet = 0; else quiet++;
            }
            encoder.EndDrain();

            // Parameter sets are collected once; the coded pictures keep only their slices.
            var result = new EncodedStream();
            foreach (var sample in samples)
            {
                var picture = new List<byte[]>();
                foreach (var nalu in AnnexBNalus(sample))
                {
                    if (LayerRestamper.IsParameterSet(nalu))
                    {
                        if (!result.ParameterSets.Any(p => p.SequenceEqual(nalu)))
                            result.ParameterSets.Add(nalu);
                    }
                    else if (LayerRestamper.IsSlice(nalu))
                    {
                        picture.Add(nalu);
                    }
                }

                if (picture.Count > 0)
                    result.Pictures.Add(picture);
            }

            return result;
        }

        private Result Assemble(string path, EncodedStream baseView, EncodedStream dependentView,
            MvHevcTrack track, bool interLayerPrediction)
        {
            var template = MvHevcReader.Read(_templatePath);
            var builder = new MultiviewBuilder { InterLayerPrediction = interLayerPrediction };
            builder.LoadTemplate(template.BaseParameterSets);
            builder.Build(baseView.ParameterSets, dependentView.ParameterSets, CodedWidth, CodedHeight);

            if (!builder.VpsIsSelfConsistent)
                Console.WriteLine("  the patched video parameter set does not write the same bytes " +
                    "once its derived values are worked out again");

            var multiviewVps = builder.BaseParameterSets.Where(IsVps);
            var restamper = new LayerRestamper(multiviewVps
                .Concat(dependentView.ParameterSets.Where(n => !IsVps(n)))
                .Concat(builder.LayerParameterSets.Where(n => !IsVps(n))))
            {
                CrossView = interLayerPrediction,
            };

            // With a GOP of two every base picture came out an IDR, which resets the count, so
            // every access unit would carry picture order count zero and a decoder drops all but
            // the first. Written as CRA pictures instead they keep counting, which costs nothing -
            // they are still intra coded and still random access points.
            var baseRestamper = interLayerPrediction
                ? new LayerRestamper(multiviewVps.Concat(baseView.ParameterSets.Where(n => !IsVps(n))))
                : null;

            int count = Math.Min(baseView.Pictures.Count, dependentView.Pictures.Count);
            int duration = (int)track.FpsDenom;

            var accessUnits = new List<MultiviewAccessUnit>(count);
            long baseBytes = 0, dependentBytes = 0;

            for (int i = 0; i < count; i++)
            {
                var accessUnit = new MultiviewAccessUnit
                {
                    Duration = duration,
                    IsRandomAccessPoint = baseView.Pictures[i].Any(LayerRestamper.IsIrap),
                };

                foreach (var nalu in baseView.Pictures[i])
                {
                    // The first picture stays an IDR so the stream still opens with one.
                    var written = baseRestamper == null || i == 0
                        ? nalu
                        : baseRestamper.Restamp(nalu, 0, PpsIdOf(nalu, baseRestamper), i, CraNalType);

                    accessUnit.BaseNalus.Add(written);
                    baseBytes += written.Length;
                }

                foreach (var nalu in dependentView.Pictures[i])
                {
                    // Simulcast keeps the dependent encoder's own count, which already matches the
                    // base encoder's; cross view renumbers, because the interleaved encode counted
                    // both views in one sequence.
                    var restamped = restamper.ToLayerOne(nalu, MultiviewBuilder.LayerPpsId,
                        interLayerPrediction ? i : (int?)null);

                    accessUnit.LayerNalus.Add(restamped);
                    dependentBytes += restamped.Length;
                }

                accessUnits.Add(accessUnit);
            }

            // Each view is also written on its own, so a fault in the encode can be told apart
            // from a fault in putting the two together.
            if (DumpViews)
            {
                WriteSingleView(path + ".base.mp4", baseView, track);
                WriteSingleView(path + ".dependent.mp4", dependentView, track);
            }

            if (DumpViews)
            {
                ReportAlignment(baseView, dependentView);
                Console.WriteLine("    template dpb_size:");
                foreach (var line in builder.DescribeDpbSize().Split('|'))
                    Console.WriteLine($"      {line}");
                var spsParser = new MvHevcParser();
                spsParser.ParseParameterSets(dependentView.ParameterSets);
                foreach (var sps in spsParser.Context.SeqParameterSets.Values)
                    Console.WriteLine($"    dependent encoder sps_max_dec_pic_buffering_minus1 = " +
                        $"[{string.Join(",", sps.SpsMaxDecPicBufferingMinus1)}], " +
                        $"num_reorder = [{string.Join(",", sps.SpsMaxNumReorderPics)}]");
            }

            var stereo = new StereoMetadata { BaseLayerIsLeftEye = true };
            MvHevcWriter.Write(path, builder.BaseParameterSets, builder.LayerParameterSets,
                accessUnits, track.Timescale, stereo, track.HasAudio ? track : null);

            return new Result
            {
                Path = path,
                AccessUnits = accessUnits.Count,
                Bytes = new FileInfo(path).Length,
                BaseBytes = baseBytes,
                DependentBytes = dependentBytes,
            };
        }

        /// <summary>Clean random access, the intra picture type that does not reset the count.</summary>
        private const uint CraNalType = 21;

        /// <summary>The picture parameter set the slice already points at, which does not change.</summary>
        private static ulong PpsIdOf(byte[] nalu, LayerRestamper restamper) =>
            restamper.PicParameterSetIdOf(nalu);

        /// <summary>
        /// Prints what each view coded per access unit. Every picture of an access unit has to
        /// carry the same picture order count, so the two columns have to agree.
        /// </summary>
        private static void ReportAlignment(EncodedStream baseView, EncodedStream dependentView)
        {
            var baseParser = new MvHevcParser();
            baseParser.ParseParameterSets(baseView.ParameterSets);
            var dependentParser = new MvHevcParser();
            dependentParser.ParseParameterSets(dependentView.ParameterSets);

            Console.WriteLine("    au   base type/poc   dependent type/poc");
            int disagreements = 0;
            int count = Math.Min(baseView.Pictures.Count, dependentView.Pictures.Count);

            for (int i = 0; i < count; i++)
            {
                var b = baseParser.ParseSlice(new Nalu { Data = baseView.Pictures[i][0] });
                var d = dependentParser.ParseSlice(new Nalu { Data = dependentView.Pictures[i][0] });
                int basePoc = baseParser.DerivePoc(b);
                int dependentPoc = dependentParser.DerivePoc(d);

                bool agrees = basePoc == dependentPoc
                    && b.NalUnit.NalUnitHeader.NalUnitType == d.NalUnit.NalUnitHeader.NalUnitType;
                if (!agrees)
                    disagreements++;

                if (i < 8 || !agrees && disagreements < 8)
                    Console.WriteLine($"    {i,3}   {b.NalUnit.NalUnitHeader.NalUnitType,4}/{basePoc,-6} " +
                        $"{d.NalUnit.NalUnitHeader.NalUnitType,9}/{dependentPoc,-6} {(agrees ? "" : "  <-- differ")}");
            }

            Console.WriteLine($"    {disagreements} of {count} access units disagree");
        }

        /// <summary>Writes one view as an ordinary single layer file, for comparison.</summary>
        public bool DumpViews { get; set; }

        private static void WriteSingleView(string path, EncodedStream view, MvHevcTrack track,
            bool doubleRate = false)
        {
            var pictures = view.Pictures
                .Select((picture, index) => new MuxPicture
                {
                    Nalu = picture[0],
                    Poc = index,
                    IsRandomAccessPoint = picture.Any(LayerRestamper.IsIrap),
                    Duration = (int)track.FpsDenom / (doubleRate ? 2 : 1),
                })
                .ToList();

            EyeMuxer.Write(path, view.ParameterSets, pictures, track.Timescale,
                (int)track.FpsDenom / (doubleRate ? 2 : 1));
        }

        private static bool IsVps(byte[] nalu) => ((nalu[0] >> 1) & 0x3F) == 32;

        private static IEnumerable<byte[]> AnnexBNalus(byte[] data)
        {
            var starts = new List<int>();
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                    starts.Add(i + 3);
                else if (i + 4 < data.Length && data[i] == 0 && data[i + 1] == 0
                    && data[i + 2] == 0 && data[i + 3] == 1)
                {
                    starts.Add(i + 4);
                    i++;
                }
            }

            for (int i = 0; i < starts.Count; i++)
            {
                int start = starts[i];
                int end = i + 1 < starts.Count ? starts[i + 1] : data.Length;
                while (end > start && data[end - 1] == 0)
                    end--;
                if (end > start)
                    yield return data.AsSpan(start, end - start).ToArray();
            }
        }
    }
}
