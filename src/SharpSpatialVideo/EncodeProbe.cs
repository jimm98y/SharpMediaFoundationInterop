using SharpMediaFoundationInterop.Transforms;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;
using SharpMediaFoundationInterop.Transforms.H265;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>
    /// Encodes the two views interleaved - L, R, L, R - through the Media Foundation HEVC encoder
    /// and reports the reference structure it chose.
    ///
    /// The idea being tested is the inverse of the single layer rewrite: if the encoder predicts
    /// each R picture from the L picture beside it, that prediction is disparity prediction, and
    /// the result could be patched into a two layer MV-HEVC stream that carries real cross view
    /// compression without an MV-HEVC encoder. Whether the patch can be lossless depends entirely
    /// on what the encoder references, which is what this prints.
    /// </summary>
    public sealed class EncodeProbe
    {
        private readonly MvHevcTrack _track;
        private readonly SingleLayerRewriter _rewriter;

        public EncodeProbe(MvHevcTrack track, SingleLayerRewriter rewriter)
        {
            _track = track;
            _rewriter = rewriter;
        }

        public void Run(int accessUnits, uint bitrate, int temporalLayers, uint gopSize)
        {
            var sps = _rewriter.ParserContext.SeqParameterSets[0];
            int codedWidth = (int)sps.PicWidthInLumaSamples;
            uint codedHeight = (uint)sps.PicHeightInLumaSamples;

            int dpbSize = Math.Max(_rewriter.MaxDpbOccupancy + 1, 8);
            var parameterSets = _rewriter.BuildParameterSets(dpbSize, _rewriter.MaxReorder);
            long step = 10_000_000L * _track.FpsDenom / _track.FpsNom / SingleLayerRewriter.PocScale;
            long frameDuration = 10_000_000L * _track.FpsDenom / _track.FpsNom / SingleLayerRewriter.PocScale;

            using var encoder = new H265Encoder((uint)codedWidth, codedHeight,
                _track.FpsNom * SingleLayerRewriter.PocScale, _track.FpsDenom, bitrate);

            // Two temporal sub-layers would put the base view at temporal id 0 and the dependent
            // view at 1. A picture may only reference pictures whose temporal id is at most its
            // own, so the base view could no longer reference the dependent one - which is the
            // single thing that stopped the interleaved encode from being splittable.
            if (temporalLayers > 1)
                encoder.CodecProperties[CodecApiProperties.TemporalLayerCount] = (uint)temporalLayers;

            // A GOP of 2 over an interleaved feed would make every base picture an I picture, with
            // no references at all, and leave each dependent picture predicting from the base
            // picture beside it. That is the MV-HEVC dependency graph, at the cost of coding the
            // base view intra only.
            if (gopSize > 0)
                encoder.CodecProperties[CodecApiProperties.GopSize] = gopSize;

            encoder.Initialize();
            ReportCodecSupport(encoder, temporalLayers);

            var encoded = new byte[encoder.OutputSize];
            var output = new List<byte[]>();
            var decoded = new SortedDictionary<int, byte[]>();
            int fed = 0;

            void Drain()
            {
                while (encoder.ProcessOutput(ref encoded, out uint length, out long _) && length > 0)
                    output.Add(encoded.Take((int)length).ToArray());
            }

            // Decode far enough to have both views of every access unit asked for, then feed the
            // pictures in presentation order: base, dependent, base, dependent.
            int pictures = Math.Min(_rewriter.Pictures.Count, accessUnits * SingleLayerRewriter.PocScale + 64);
            using (var decoder = new SpatialDecoder((uint)codedWidth, codedHeight, _track.FpsNom, _track.FpsDenom))
            {
                decoder.SendParameterSets(parameterSets);
                foreach (var picture in _rewriter.Pictures.Take(pictures))
                    foreach (var frame in decoder.Decode(_rewriter.RewriteSlice(picture), picture.Poc * step))
                        decoded[(int)(frame.Timestamp / step)] = frame.Nv12;
                foreach (var frame in decoder.Flush())
                    decoded[(int)(frame.Timestamp / step)] = frame.Nv12;
            }

            int wanted = accessUnits * SingleLayerRewriter.PocScale;
            for (int poc = 0; poc < wanted; poc++)
            {
                if (!decoded.TryGetValue(poc, out var frame))
                    break;

                encoder.ProcessInput(frame, fed * frameDuration);
                fed++;
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

            long bytes = output.Sum(s => (long)s.Length);
            Console.WriteLine($"  fed {fed} pictures (L, R interleaved), got {output.Count} samples back, " +
                $"{bytes / 1024} KB coded");

            // Which view the bits went to. The encoder hands samples back in coding order, which
            // for this structure is also the order they were fed, so they alternate.
            long baseBytes = 0, dependentBytes = 0;
            int baseCount = 0, dependentCount = 0;
            for (int i = 0; i < output.Count; i++)
            {
                if (i % 2 == 0) { baseBytes += output[i].Length; baseCount++; }
                else { dependentBytes += output[i].Length; dependentCount++; }
            }

            if (baseCount > 0 && dependentCount > 0)
            {
                Console.WriteLine($"  base view:      {baseBytes / 1024,6} KB over {baseCount} pictures " +
                    $"({baseBytes / baseCount / 1024.0:F1} KB each)");
                Console.WriteLine($"  dependent view: {dependentBytes / 1024,6} KB over {dependentCount} pictures " +
                    $"({dependentBytes / dependentCount / 1024.0:F1} KB each)");
                Console.WriteLine($"  the dependent view costs {100.0 * dependentBytes / baseBytes:F1}% of the base view");
            }
            Report(output);
        }

        /// <summary>
        /// Lists every HEVC encoder installed and what each one lets us control. The reference
        /// structure can only be steered through properties the encoder actually implements, and
        /// hardware encoders generally implement more of them than the software one.
        /// </summary>
        public static void ReportEncoders()
        {
            var input = new MFT_REGISTER_TYPE_INFO
            {
                guidMajorType = PInvoke.MFMediaType_Video,
                guidSubtype = PInvoke.MFVideoFormat_NV12,
            };
            var output = new MFT_REGISTER_TYPE_INFO
            {
                guidMajorType = PInvoke.MFMediaType_Video,
                guidSubtype = PInvoke.MFVideoFormat_HEVC,
            };

            foreach (var (flags, label) in new[]
            {
                (MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE, "hardware"),
                (MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT, "software (sync)"),
                (MFT_ENUM_FLAG.MFT_ENUM_FLAG_ASYNCMFT, "software (async)"),
            })
            {
                var transforms = MediaTransformBase.EnumerateTransforms(
                    PInvoke.MFT_CATEGORY_VIDEO_ENCODER, flags | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER,
                    input, output);

                Console.WriteLine($"  --- {label}: {transforms.Count} HEVC encoder(s) ---");
                foreach (var (name, transform) in transforms)
                {
                    Console.WriteLine($"  {name}");
                    PrintSupport(transform as ICodecApi, "    ");
                    MediaTransformBase.DestroyTransform(transform);
                }
                Console.WriteLine();
            }
        }

        private static string Describe(Guid property)
        {
            foreach (var (candidate, name) in Interesting)
                if (candidate == property)
                    return name;
            return property.ToString();
        }

        private static void PrintSupport(ICodecApi codec, string indent)
        {
            if (codec == null)
            {
                Console.WriteLine($"{indent}no ICodecAPI");
                return;
            }

            // Every property the codec API defines, not just the ones we had in mind. An encoder
            // implements an arbitrary subset, and asking about all of them is the only way to know
            // what is really on offer.
            var supported = new List<string>();
            foreach (var (name, property) in CodecApiCatalogue.All)
                if (codec.IsPropertySupported(property))
                    supported.Add(name);

            Console.WriteLine($"{indent}{supported.Count} of {CodecApiCatalogue.All.Length} properties supported");
            foreach (var name in supported)
                Console.WriteLine($"{indent}  {name}");
        }

        private static readonly (Guid Property, string Name)[] Interesting =
        {
            (CodecApiProperties.TemporalLayerCount, "TemporalLayerCount"),
            (CodecApiProperties.LtrBufferControl, "LTRBufferControl"),
            (CodecApiProperties.MarkLtrFrame, "MarkLTRFrame"),
            (CodecApiProperties.UseLtrFrame, "UseLTRFrame"),
            (CodecApiProperties.GopSize, "GOPSize"),
            (CodecApiProperties.RateControlMode, "RateControlMode"),
            (CodecApiProperties.MaxNumRefFrame, "MaxNumRefFrame"),
            (CodecApiProperties.BPictureCount, "BPictureCount"),
        };

        /// <summary>Prints what this encoder lets us ask of it, and what it accepted.</summary>
        private static void ReportCodecSupport(H265Encoder encoder, int temporalLayers)
        {
            var codec = encoder.CodecApi;
            if (codec == null)
            {
                Console.WriteLine("  the encoder does not expose ICodecAPI at all");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("  encoder property        supported  modifiable  value");
            foreach (var (property, name) in Interesting)
            {
                bool supported = codec.IsPropertySupported(property);
                bool modifiable = codec.IsPropertyModifiable(property);
                string value = codec.TryGetProperty(property, out object v) ? $"{v}" : "-";
                Console.WriteLine($"  {name,-22}  {(supported ? "yes" : "no"),9}  {(modifiable ? "yes" : "no"),10}  {value}");
            }

            foreach (var result in encoder.CodecPropertyResults)
                Console.WriteLine($"  requested {Describe(result.Property)}: " +
                    $"supported={result.Supported}, applied={result.Applied}");

            Console.WriteLine();
        }

        /// <summary>
        /// Parses the encoded stream back and prints, per coded picture, what it references. Even
        /// POCs are the base view and odd POCs the dependent one, so the question the table answers
        /// is whether the encoder kept the two apart.
        /// </summary>
        private static void Report(List<byte[]> samples)
        {
            var parser = new MvHevcParser();
            var parameterSets = new List<byte[]>();

            foreach (var sample in samples)
                foreach (var nalu in AnnexBNalus(sample))
                {
                    uint type = (uint)((nalu[0] >> 1) & 0x3F);
                    if (type is 32 or 33 or 34)
                        parameterSets.Add(nalu);
                }

            parser.ParseParameterSets(parameterSets);
            Console.WriteLine($"  {parameterSets.Count} parameter set NAL units");

            Console.WriteLine();
            Console.WriteLine("   idx  poc  view  nal  tid  slice  refs (poc)                  notes");

            int index = 0;
            int crossParity = 0, bSlices = 0, longTerm = 0;

            foreach (var sample in samples)
                foreach (var nalu in AnnexBNalus(sample))
                {
                    uint type = (uint)((nalu[0] >> 1) & 0x3F);
                    if (type > 21 || (type > 9 && type < 16))
                        continue;   // not a slice

                    int temporalId = (nalu[1] & 0x07) - 1;
                    var parsed = parser.ParseSlice(new Nalu { Data = nalu });
                    int poc = parser.DerivePoc(parsed);
                    var header = parsed.Header;

                    var refs = ReferencedPocs(parser, parsed, poc, out string note);
                    string sliceType = header.SliceType switch { 0 => "B", 1 => "P", 2 => "I", _ => "?" };

                    if (sliceType == "B") bSlices++;
                    if (header.NumLongTermPics + header.NumLongTermSps > 0) longTerm++;
                    if (refs != null && refs.Any(r => (r & 1) != (poc & 1)))
                        crossParity++;

                    Console.WriteLine($"  {index,4} {poc,4}  {(poc % 2 == 0 ? "base" : "dep ")}  " +
                        $"{type,3}  {temporalId,3}  {sliceType,5}  " +
                        $"{(refs == null ? "?" : string.Join(",", refs)),-26}  {note}");
                    index++;
                }

            Console.WriteLine();
            Console.WriteLine($"  pictures referencing the other view: {crossParity}");
            Console.WriteLine($"  B slices: {bSlices}, pictures using long term references: {longTerm}");
        }

        /// <summary>
        /// The picture order counts this slice references, from its short term reference picture
        /// set. A set coded as a difference from another one is reported rather than derived - what
        /// matters here is which pictures are referenced, and the encoders worth probing write the
        /// set out in full.
        /// </summary>
        private static List<int> ReferencedPocs(MvHevcParser parser, ParsedSlice parsed, int poc, out string note)
        {
            note = "";
            var header = parsed.Header;

            var rps = header.ShortTermRefPicSetSpsFlag == 0
                ? header.StRefPicSet
                : parser.Context.SeqParameterSets[0].StRefPicSet?.ElementAtOrDefault((int)header.ShortTermRefPicSetIdx);

            if (rps == null)
            {
                note = "no short term set";
                return new List<int>();
            }

            if (rps.InterRefPicSetPredictionFlag != 0)
            {
                note = "set coded as a difference from another";
                return null;
            }

            var result = new List<int>();
            int delta = 0;
            for (int i = 0; i < (int)rps.NumNegativePics; i++)
            {
                delta -= (int)(rps.DeltaPocS0Minus1[i] + 1);
                if (rps.UsedByCurrPicS0Flag[i] != 0)
                    result.Add(poc + delta);
            }

            delta = 0;
            for (int i = 0; i < (int)rps.NumPositivePics; i++)
            {
                delta += (int)(rps.DeltaPocS1Minus1[i] + 1);
                if (rps.UsedByCurrPicS1Flag[i] != 0)
                    result.Add(poc + delta);
            }

            if (header.NumLongTermPics + header.NumLongTermSps > 0)
                note = $"{header.NumLongTermPics + header.NumLongTermSps} long term";

            return result;
        }

        /// <summary>Splits an Annex B buffer into its NAL units, dropping the start codes.</summary>
        private static IEnumerable<byte[]> AnnexBNalus(byte[] data)
        {
            var starts = new List<int>();
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                    starts.Add(i + 3);
                else if (i + 4 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
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
