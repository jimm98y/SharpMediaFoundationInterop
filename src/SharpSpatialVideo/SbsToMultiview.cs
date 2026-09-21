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
    ///   makes every base picture an intra picture and every dependent picture predict from the
    ///   base picture beside it. That is real disparity prediction, at the cost of coding the base
    ///   view intra only. Measured on this footage it spends more bits than simulcast does, so it
    ///   is offered rather than chosen.
    ///
    /// Memory stays flat however long the clip is. The source is read one access unit at a time,
    /// each decoded frame is split into two reused buffers and fed to the encoders straight away,
    /// the encoders spool what they code to disk, and the output is assembled from the spools one
    /// access unit at a time. What is held is a few frames and the encoders' own working memory.
    /// </summary>
    public sealed class SbsToMultiview
    {
        private readonly string _templatePath;
        private List<byte[]> _templateParameterSets;

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

        /// <summary>Threads for the decoder; 0 leaves it to the decoder.</summary>
        public uint DecoderThreads { get; set; }

        /// <summary>
        /// Whether the decoder runs in low latency mode, releasing each frame as soon as it can
        /// rather than holding a full reorder window. It hands back the same frames in the same
        /// order - checked against a stream with a B picture pyramid - and holds 86 MB less.
        /// </summary>
        public bool DecoderLowLatency { get; set; } = true;

        /// <summary>
        /// Writes both versions from one decode, with all three encoders running at once, instead
        /// of one version per pass. It is no faster - the encoders are the work, and the passes
        /// share it out - and it needs a third encoder's memory on top.
        /// </summary>
        public bool OnePass { get; set; }

        /// <summary>
        /// How the encoders control their rate - see <see cref="EncoderRateControl"/>. A constant
        /// quantiser by default: constant bitrate made fine detail pulse on every key frame.
        /// </summary>
        public string RateControl { get; set; } = "qp:26";

        /// <summary>Threads per encoder; 0 leaves it to the encoder.</summary>
        public uint EncoderThreads { get; set; }

        /// <summary>Key frame spacing for the simulcast pair, which both views share.</summary>
        public uint SimulcastGopSize { get; set; } = 30;

        /// <summary>Writes each view as an ordinary single layer file too, for comparison.</summary>
        public bool DumpViews { get; set; }

        /// <summary>Prints memory at each stage: what is live, and what the process holds in all.</summary>
        public bool ReportMemory { get; set; }

        private int Width { get; set; }
        private int Height { get; set; }
        private int CodedWidth { get; set; }
        private int CodedHeight { get; set; }

        /// <summary>Decodes the source and writes whichever versions were asked for.</summary>
        public List<Result> Write(string sourcePath, string outputStem, uint bitrate,
            bool simulcast, bool crossView, int limit = int.MaxValue)
        {
            // One version per pass unless asked otherwise. Decoding the source twice costs far less
            // than an encoder, and running the encoders one version at a time means only one
            // version's encoders are in memory at once.
            if (simulcast && crossView && !OnePass)
            {
                var passes = Write(sourcePath, outputStem, bitrate, simulcast: true, crossView: false, limit);
                passes.AddRange(Write(sourcePath, outputStem, bitrate, simulcast: false, crossView: true, limit));
                return passes;
            }

            // Only the sample index is read here; the samples themselves are streamed below. The
            // template is needed only for its video parameter set.
            var track = MvHevcReader.Read(sourcePath, loadSamples: false);
            _templateParameterSets = MvHevcReader.Read(_templatePath, loadSamples: false).BaseParameterSets;
            Memory("source indexed");

            // The source is an ordinary single layer file, so its sequence parameter set gives the
            // coded size, and its samples go to the decoder as they are.
            var parser = new MvHevcParser();
            parser.ParseParameterSets(track.BaseParameterSets);
            var sps = parser.Context.SeqParameterSets.Values.First();
            int sourceCodedWidth = (int)sps.PicWidthInLumaSamples;
            int sourceCodedHeight = (int)sps.PicHeightInLumaSamples;

            Width = (int)track.DisplayWidth / 2;
            Height = (int)track.DisplayHeight;
            CodedWidth = Align(Width, 8);
            CodedHeight = Align(Height, 8);

            // Simulcast: one encoder per view, sharing a fixed GOP. Left to itself the encoder
            // picks key frames to suit each view's content, and the two views then disagree about
            // where a sequence starts - a key frame in one resets the picture order count while the
            // other carries on referencing pictures the reset threw away.
            using var baseEncoder = simulcast
                ? new StreamingEncoder(CodedWidth, CodedHeight, track.FpsNom, track.FpsDenom, bitrate / 2, SimulcastGopSize, EncoderThreads, RateControl)
                : null;
            using var dependentEncoder = simulcast
                ? new StreamingEncoder(CodedWidth, CodedHeight, track.FpsNom, track.FpsDenom, bitrate / 2, SimulcastGopSize, EncoderThreads, RateControl)
                : null;

            // Cross view: both views through one encoder, interleaved, at twice the rate and with a
            // GOP of two, so each dependent picture predicts from the base picture beside it.
            using var interleavedEncoder = crossView
                ? new StreamingEncoder(CodedWidth, CodedHeight, track.FpsNom * 2, track.FpsDenom, bitrate, 2, EncoderThreads, RateControl)
                : null;
            Memory("encoders created");

            // The two eyes are cropped into the same two buffers every frame. The encoders copy
            // what they are given, so the buffers are free again as soon as Feed returns.
            int cropSize = CodedWidth * CodedHeight * 3 / 2;
            var left = new byte[cropSize];
            var right = new byte[cropSize];
            int pairs = 0;

            void Take(byte[] frame, long timestamp)
            {
                SbsComposer.CropLeft(frame, sourceCodedWidth, sourceCodedHeight,
                    Width, Height, CodedWidth, CodedHeight, left);
                SbsComposer.CropRight(frame, sourceCodedWidth, sourceCodedHeight,
                    Width, Height, CodedWidth, CodedHeight, right);

                // The right eye goes in the base layer: that is what the eye mapping SEI carried
                // over from the template says, and what Apple writes. See MultiviewBuilder.
                baseEncoder?.Feed(right);
                dependentEncoder?.Feed(left);
                interleavedEncoder?.Feed(right);
                interleavedEncoder?.Feed(left);

                if (++pairs % 150 == 0)
                    Memory($"{pairs} pairs encoded");
            }

            long frameDuration = 10_000_000L * track.FpsDenom / track.FpsNom;
            using (var decoder = new SpatialDecoder((uint)sourceCodedWidth, (uint)sourceCodedHeight,
                track.FpsNom, track.FpsDenom, DecoderLowLatency, DecoderThreads))
            {
                decoder.SendParameterSets(track.BaseParameterSets);

                foreach (var accessUnit in MvHevcReader.StreamAccessUnits(track, limit))
                    decoder.DecodeInto(accessUnit.Nalus.Select(n => n.Data),
                        accessUnit.Index * frameDuration, Take);

                decoder.FlushInto(Take);
            }

            Console.WriteLine($"  {pairs} stereo pairs at {Width}x{Height} (coded {CodedWidth}x{CodedHeight})");
            Memory("decode finished");

            var results = new List<Result>();

            if (simulcast)
            {
                results.Add(Assemble(outputStem + "_simulcast.mov", baseEncoder.Finish(),
                    dependentEncoder.Finish(), track, interLayerPrediction: false));
                Memory("simulcast written");
            }

            if (crossView)
            {
                string path = outputStem + "_crossview.mov";
                var coded = interleavedEncoder.Finish();

                // The interleaved stream is itself ordinary single layer HEVC, and decoded as such
                // it is what the two layers have to reproduce - the check that the reference set
                // rewrite is exact rather than merely plausible.
                if (DumpViews)
                    WriteSingleView(path + ".interleaved.mp4", coded, track, doubleRate: true);

                // Even pictures are the base view, odd ones the dependent view.
                results.Add(Assemble(path, new EveryOther(coded, 0), new EveryOther(coded, 1),
                    track, interLayerPrediction: true));
                Memory("cross view written");
            }

            return results;
        }

        private void Memory(string stage)
        {
            if (!ReportMemory)
                return;

            long working = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
            long heapBefore = GC.GetTotalMemory(false);
            long live = GC.GetTotalMemory(true);
            Console.WriteLine($"    [mem] {stage,-22} working set {working / (1 << 20),5} MB, " +
                $"managed heap {heapBefore / (1 << 20),5} MB, of which live {live / (1 << 20),5} MB");
        }

        private static int Align(int value, int multiple) =>
            (value + multiple - 1) / multiple * multiple;

        /// <summary>A coded stream's parameter sets and its pictures, in coding order.</summary>
        private interface IPictures
        {
            List<byte[]> ParameterSets { get; }
            int Count { get; }

            /// <summary>One picture's NAL units, read back from wherever they are kept.</summary>
            List<byte[]> Read(int index);
        }

        /// <summary>
        /// A stream's coded pictures, kept in a temporary file rather than in memory. A 25 second
        /// clip at these rates is a few hundred megabytes of coded data, and the output is written
        /// one access unit at a time, so there is no reason to hold it.
        /// </summary>
        private sealed class PictureSpool : IPictures, IDisposable
        {
            private readonly FileStream _file = new FileStream(
                Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                1 << 16, FileOptions.DeleteOnClose);

            private readonly List<(long Offset, int[] Lengths)> _index = new List<(long, int[])>();

            public List<byte[]> ParameterSets { get; } = new List<byte[]>();
            public int Count => _index.Count;

            public void Add(List<byte[]> picture)
            {
                _file.Seek(0, SeekOrigin.End);
                long offset = _file.Position;
                foreach (var nalu in picture)
                    _file.Write(nalu, 0, nalu.Length);
                _index.Add((offset, picture.Select(n => n.Length).ToArray()));
            }

            public List<byte[]> Read(int index)
            {
                var (offset, lengths) = _index[index];
                _file.Seek(offset, SeekOrigin.Begin);

                var picture = new List<byte[]>(lengths.Length);
                foreach (int length in lengths)
                {
                    var nalu = new byte[length];
                    _file.ReadExactly(nalu);
                    picture.Add(nalu);
                }
                return picture;
            }

            public void Dispose() => _file.Dispose();
        }

        /// <summary>Every other picture of an interleaved stream: one of its two views.</summary>
        private sealed class EveryOther : IPictures
        {
            private readonly IPictures _source;
            private readonly int _first;

            public EveryOther(IPictures source, int first)
            {
                _source = source;
                _first = first;
            }

            public List<byte[]> ParameterSets => _source.ParameterSets;
            public int Count => (_source.Count - _first + 1) / 2;
            public List<byte[]> Read(int index) => _source.Read(_first + index * 2);
        }

        /// <summary>
        /// An encoder fed one frame at a time, which spools what it codes to disk - parameter
        /// sets once, and each picture's slices.
        /// </summary>
        private sealed class StreamingEncoder : IDisposable
        {
            private readonly H265Encoder _encoder;
            private readonly long _frameDuration;
            private byte[] _buffer;
            private int _fed;

            public PictureSpool Output { get; } = new PictureSpool();

            public StreamingEncoder(int width, int height, uint fpsNom, uint fpsDenom, uint bitrate,
                uint gopSize, uint threads, string rateControl)
            {
                _encoder = new H265Encoder((uint)width, (uint)height, fpsNom, fpsDenom, bitrate);
                if (gopSize > 0)
                    _encoder.CodecProperties[CodecApiProperties.GopSize] = gopSize;
                if (threads > 0)
                    _encoder.CodecProperties[CodecApiProperties.NumWorkerThreads] = threads;

                EncoderRateControl.Apply(_encoder, rateControl);
                _encoder.Initialize();

                EncoderRateControl.ReportRejected(_encoder);

                _buffer = new byte[_encoder.OutputSize];
                _frameDuration = 10_000_000L * fpsDenom / fpsNom;
            }

            public void Feed(byte[] frame)
            {
                _encoder.ProcessInput(frame, _fed++ * _frameDuration);
                Drain();
            }

            public PictureSpool Finish()
            {
                _encoder.BeginDrain();
                for (int quiet = 0; quiet < 4;)
                    quiet = Drain() ? 0 : quiet + 1;
                _encoder.EndDrain();
                return Output;
            }

            private bool Drain()
            {
                bool any = false;
                while (_encoder.ProcessOutput(ref _buffer, out uint length, out long _) && length > 0)
                {
                    Collect(_buffer.AsSpan(0, (int)length).ToArray());
                    any = true;
                }
                return any;
            }

            private void Collect(byte[] sample)
            {
                var picture = new List<byte[]>();
                foreach (var nalu in AnnexBNalus(sample))
                {
                    if (LayerRestamper.IsParameterSet(nalu))
                    {
                        if (!Output.ParameterSets.Any(p => p.SequenceEqual(nalu)))
                            Output.ParameterSets.Add(nalu);
                    }
                    else if (LayerRestamper.IsSlice(nalu))
                    {
                        picture.Add(nalu);
                    }
                }

                if (picture.Count > 0)
                    Output.Add(picture);
            }

            public void Dispose()
            {
                _encoder.Dispose();
                Output.Dispose();
            }
        }

        private Result Assemble(string path, IPictures baseView, IPictures dependentView,
            MvHevcTrack track, bool interLayerPrediction)
        {
            var builder = new MultiviewBuilder { InterLayerPrediction = interLayerPrediction };
            builder.LoadTemplate(_templateParameterSets);
            builder.Build(baseView.ParameterSets, dependentView.ParameterSets, CodedWidth, CodedHeight);

            if (!builder.VpsIsSelfConsistent)
                Console.WriteLine("  the patched video parameter set does not write the same bytes " +
                    "once its derived values are worked out again");

            var multiviewVps = builder.BaseParameterSets.Where(IsVps).ToList();
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

            if (DumpViews)
            {
                WriteSingleView(path + ".base.mp4", baseView, track);
                WriteSingleView(path + ".dependent.mp4", dependentView, track);
                ReportAlignment(baseView, dependentView);
            }

            int count = Math.Min(baseView.Count, dependentView.Count);
            int duration = (int)track.FpsDenom;
            long baseBytes = 0, dependentBytes = 0;

            // Built one at a time as the writer asks for them, so only the access unit being
            // written is ever in memory.
            IEnumerable<MultiviewAccessUnit> AccessUnits()
            {
                for (int i = 0; i < count; i++)
                {
                    var basePicture = baseView.Read(i);
                    var accessUnit = new MultiviewAccessUnit
                    {
                        Duration = duration,
                        IsRandomAccessPoint = basePicture.Any(LayerRestamper.IsIrap),
                    };

                    foreach (var nalu in basePicture)
                    {
                        // The first picture stays an IDR so the stream still opens with one.
                        var written = baseRestamper == null || i == 0
                            ? nalu
                            : baseRestamper.Restamp(nalu, 0, baseRestamper.PicParameterSetIdOf(nalu), i, CraNalType);

                        accessUnit.BaseNalus.Add(written);
                        baseBytes += written.Length;
                    }

                    foreach (var nalu in dependentView.Read(i))
                    {
                        // Simulcast keeps the dependent encoder's own count, which already matches
                        // the base encoder's; cross view renumbers, because the interleaved encode
                        // counted both views in one sequence.
                        var restamped = restamper.ToLayerOne(nalu, MultiviewBuilder.LayerPpsId,
                            interLayerPrediction ? i : (int?)null);

                        accessUnit.LayerNalus.Add(restamped);
                        dependentBytes += restamped.Length;
                    }

                    yield return accessUnit;
                }
            }

            var stereo = new StereoMetadata();
            MvHevcWriter.Write(path, builder.BaseParameterSets, builder.LayerParameterSets,
                AccessUnits(), track.Timescale, stereo, track.HasAudio ? track : null);

            return new Result
            {
                Path = path,
                AccessUnits = count,
                Bytes = new FileInfo(path).Length,
                BaseBytes = baseBytes,
                DependentBytes = dependentBytes,
            };
        }

        /// <summary>Clean random access, the intra picture type that does not reset the count.</summary>
        private const uint CraNalType = 21;

        /// <summary>
        /// Prints what each view coded per access unit. Every picture of an access unit has to
        /// carry the same picture order count, so the two columns have to agree.
        /// </summary>
        private static void ReportAlignment(IPictures baseView, IPictures dependentView)
        {
            var baseParser = new MvHevcParser();
            baseParser.ParseParameterSets(baseView.ParameterSets);
            var dependentParser = new MvHevcParser();
            dependentParser.ParseParameterSets(dependentView.ParameterSets);

            Console.WriteLine("    au   base type/poc   dependent type/poc");
            int disagreements = 0;
            int count = Math.Min(baseView.Count, dependentView.Count);

            for (int i = 0; i < count; i++)
            {
                var b = baseParser.ParseSlice(new Nalu { Data = baseView.Read(i)[0] });
                var d = dependentParser.ParseSlice(new Nalu { Data = dependentView.Read(i)[0] });
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

        /// <summary>
        /// Writes one view as an ordinary single layer file. A diagnostic, and not a streaming one:
        /// the muxer it uses takes the pictures as a list.
        /// </summary>
        private static void WriteSingleView(string path, IPictures view, MvHevcTrack track,
            bool doubleRate = false)
        {
            var pictures = Enumerable.Range(0, view.Count)
                .Select(index =>
                {
                    var picture = view.Read(index);
                    return new MuxPicture
                    {
                        Nalu = picture[0],
                        Poc = index,
                        IsRandomAccessPoint = picture.Any(LayerRestamper.IsIrap),
                        Duration = (int)track.FpsDenom / (doubleRate ? 2 : 1),
                    };
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
