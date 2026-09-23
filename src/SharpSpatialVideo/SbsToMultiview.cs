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
    /// Each view is encoded on its own, with its own temporal prediction and no prediction between
    /// the views: simulcast. A cross view version was built and measured too - both views through
    /// one encoder, interleaved with a GOP of two, so that every dependent picture predicted from
    /// the base picture beside it - and then dropped. The encoder keeps one reference picture, so
    /// in an interleaved feed a base picture's only candidate is the dependent picture before it,
    /// which MV-HEVC forbids; every base picture therefore had to be coded intra, and the file came
    /// out at 300 MB against simulcast's 172 for the same footage at the same quantiser. A Mac
    /// takes either as spatial video, so the disparity prediction bought nothing.
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
            int limit = int.MaxValue)
        {
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
            using var baseEncoder = new StreamingEncoder(CodedWidth, CodedHeight,
                track.FpsNom, track.FpsDenom, bitrate / 2, SimulcastGopSize, EncoderThreads, RateControl);
            using var dependentEncoder = new StreamingEncoder(CodedWidth, CodedHeight,
                track.FpsNom, track.FpsDenom, bitrate / 2, SimulcastGopSize, EncoderThreads, RateControl);
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
                baseEncoder.Feed(right);
                dependentEncoder.Feed(left);

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

            var results = new List<Result>
            {
                Assemble(outputStem + "_simulcast.mov", baseEncoder.Finish(), dependentEncoder.Finish(), track),
            };
            Memory("simulcast written");

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
                foreach (var nalu in SharpMP4.AnnexB.ParseNalUnits(sample))
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
            MvHevcTrack track)
        {
            var builder = new MultiviewBuilder();
            builder.LoadTemplate(_templateParameterSets);
            builder.Build(baseView.ParameterSets, dependentView.ParameterSets, CodedWidth, CodedHeight);

            if (!builder.VpsIsSelfConsistent)
                Console.WriteLine("  the patched video parameter set does not write the same bytes " +
                    "once its derived values are worked out again");

            var multiviewVps = builder.BaseParameterSets.Where(IsVps).ToList();
            var restamper = new LayerRestamper(multiviewVps
                .Concat(dependentView.ParameterSets.Where(n => !IsVps(n)))
                .Concat(builder.LayerParameterSets.Where(n => !IsVps(n))));

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

                    // The base view is already layer 0, so its slices go through as they are.
                    foreach (var nalu in basePicture)
                    {
                        accessUnit.BaseNalus.Add(nalu);
                        baseBytes += nalu.Length;
                    }

                    foreach (var nalu in dependentView.Read(i))
                    {
                        // The dependent encoder's own picture order count is kept, and already
                        // agrees with the base encoder's: the two share a fixed key frame spacing.
                        var restamped = restamper.ToLayerOne(nalu, MultiviewBuilder.LayerPpsId);

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
        private static void WriteSingleView(string path, IPictures view, MvHevcTrack track)
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
                        Duration = (int)track.FpsDenom,
                    };
                })
                .ToList();

            EyeMuxer.Write(path, view.ParameterSets, pictures, track.Timescale,
                (int)track.FpsDenom);
        }

        private static bool IsVps(byte[] nalu) => ((nalu[0] >> 1) & 0x3F) == 32;

    }
}
