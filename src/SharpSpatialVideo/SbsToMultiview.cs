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
            var baseView = Encode(left, track, bitrate / 2, gopSize: 0);
            var dependentView = Encode(right, track, bitrate / 2, gopSize: 0);

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

            var multiviewVps = builder.BaseParameterSets.Where(IsVps);
            var restamper = new LayerRestamper(multiviewVps
                .Concat(dependentView.ParameterSets.Where(n => !IsVps(n)))
                .Concat(builder.LayerParameterSets.Where(n => !IsVps(n))));

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
                    accessUnit.BaseNalus.Add(nalu);
                    baseBytes += nalu.Length;
                }

                foreach (var nalu in dependentView.Pictures[i])
                {
                    var restamped = restamper.ToLayerOne(nalu, MultiviewBuilder.LayerPpsId);
                    accessUnit.LayerNalus.Add(restamped);
                    dependentBytes += restamped.Length;
                }

                accessUnits.Add(accessUnit);
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
