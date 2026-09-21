using SharpH265;
using SharpH26X;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>
    /// Builds the parameter sets for a two layer MV-HEVC stream out of two ordinary single layer
    /// encodes, and re-stamps the dependent view's coded slices into layer 1.
    ///
    /// The video parameter set is taken from an existing MV-HEVC file and patched, rather than
    /// built from nothing. Its extension carries layer sets, output layer sets, representation
    /// formats, profile tier levels and a DPB size table, all cross referenced; starting from one
    /// a decoder already accepts is far more likely to produce a file that plays than assembling
    /// those by hand. What gets patched is the picture size, the profile and level, and whether
    /// layer 1 is allowed to predict from layer 0.
    /// </summary>
    public sealed class MultiviewBuilder
    {
        private H265Context _templateContext;
        private readonly MvHevcParser _parser = new MvHevcParser();

        private H265Context _context => _parser.Context;

        /// <summary>Whether layer 1 predicts from layer 0, which the video parameter set declares.</summary>
        public bool InterLayerPrediction { get; set; }

        public VideoParameterSetRbsp Vps { get; private set; }

        /// <summary>Whether the template's video parameter set was reproduced exactly.</summary>
        public bool RoundTripsCleanly { get; private set; }

        /// <summary>Whether the patched set writes the same bytes once its own values are re-derived.</summary>
        public bool VpsIsSelfConsistent { get; private set; }

        /// <summary>Layer 0's parameter sets, as they will be written into hvcC.</summary>
        public List<byte[]> BaseParameterSets { get; } = new List<byte[]>();

        /// <summary>Layer 1's parameter sets, as they will be written into lhvC.</summary>
        public List<byte[]> LayerParameterSets { get; } = new List<byte[]>();

        /// <summary>
        /// Takes the video parameter set from an MV-HEVC file to use as the template.
        /// </summary>
        /// <summary>The template's video parameter set exactly as it was read, for comparison.</summary>
        public byte[] TemplateVpsBytes { get; private set; }

        public void LoadTemplate(IEnumerable<byte[]> templateParameterSets)
        {
            TemplateVpsBytes = templateParameterSets.FirstOrDefault(
                n => ((n[0] >> 1) & 0x3F) == H265NALTypes.VPS_NUT);

            // Parsed through MvHevcParser rather than by hand: the video parameter set's syntax
            // has callbacks that read back what has been parsed so far, so the context has to be
            // wired up before the read starts, not after.
            var parser = new MvHevcParser();
            parser.ParseParameterSets(templateParameterSets.Where(
                n => ((n[0] >> 1) & 0x3F) == H265NALTypes.VPS_NUT));

            Vps = parser.Context.VideoParameterSetRbsp
                ?? throw new InvalidOperationException("the template carries no video parameter set");
            _templateContext = parser.Context;
        }

        /// <summary>
        /// Builds both layers' parameter sets. The base view's are used as they are - its slices go
        /// through untouched, so its sequence and picture parameter sets have to match them
        /// exactly. Layer 1 gets copies at nuh_layer_id 1, with their own ids so both can be
        /// active at once.
        /// </summary>
        public void Build(IEnumerable<byte[]> baseParameterSets,
            IEnumerable<byte[]> dependentParameterSets, int width, int height)
        {
            BaseParameterSets.Clear();
            LayerParameterSets.Clear();

            // Check the template survives a read and a write before anything is changed. A video
            // parameter set extension is deep enough that a write which does not reproduce its
            // input is a sign the branches taken differ, and anything built on it is suspect.
            var unpatched = WriteParameterSet(H265NALTypes.VPS_NUT, 0,
                s => Vps.Write(_templateContext, s));
            RoundTripsCleanly = TemplateVpsBytes != null && unpatched.SequenceEqual(TemplateVpsBytes);

            PatchVps(width, height);

            // Values derived from the extension - how many reference layers each layer has, and so
            // on - were worked out while reading the template, and some of them decide whether a
            // field is present at all. Changing the layer dependency changes those, so the set is
            // written, read back with the derived values worked out afresh, and written again.
            // Two writes that agree mean the result is self consistent; if they do not, what would
            // go in the file is a set that describes something other than what it contains.
            var once = WriteParameterSet(H265NALTypes.VPS_NUT, 0, s => Vps.Write(_templateContext, s));

            var reparse = new MvHevcParser();
            reparse.ParseParameterSets(new[] { once });
            Vps = reparse.Context.VideoParameterSetRbsp;
            _templateContext = reparse.Context;

            var twice = WriteParameterSet(H265NALTypes.VPS_NUT, 0, s => Vps.Write(_templateContext, s));
            VpsIsSelfConsistent = once.SequenceEqual(twice);

            BaseParameterSets.Add(twice);

            // Read the base encoder's own sequence and picture parameter sets, keep them verbatim
            // for layer 0, and re-emit copies for layer 1.
            _parser.ParseParameterSets(baseParameterSets);

            // Both layers share one decoded picture buffer, and the encoder sized its sequence
            // parameter set for one view. Left alone, layer 0's pictures crowd out layer 1's and
            // the dependent view loses references it was coded against. Only the buffer sizes
            // change, which nothing in the coded slices depends on.
            //
            // A sequence parameter set has to be written before the picture parameter sets that
            // point at it, or reading them back finds nothing to attach them to.
            foreach (var parameterSet in _context.SeqParameterSets.Values.ToList())
            {
                GrowDecodedPictureBuffer(parameterSet);
                _context.SeqParameterSetRbsp = parameterSet;
                BaseParameterSets.Add(WriteParameterSet(H265NALTypes.SPS_NUT, 0,
                    s => parameterSet.Write(_context, s)));
            }

            foreach (var nalu in baseParameterSets)
            {
                uint type = (uint)((nalu[0] >> 1) & 0x3F);
                if (type == H265NALTypes.PPS_NUT)
                    BaseParameterSets.Add(nalu);
            }

            // Layer 1's parameter sets come from the dependent view's own encode, not from copies
            // of the base view's - the two views were coded separately and need not agree.
            var layerParser = new MvHevcParser();
            layerParser.ParseParameterSets(dependentParameterSets);

            var sps = layerParser.Context.SeqParameterSets.Values.ToList();
            var pps = layerParser.Context.PicParameterSets.Values.ToList();

            foreach (var parameterSet in sps)
            {
                GrowDecodedPictureBuffer(parameterSet);
                // A sequence parameter set at nuh_layer_id 1 may inherit its format from the video
                // parameter set instead of carrying it. Writing it out in full keeps layer 1
                // self describing, which is one less thing that has to agree.
                parameterSet.SpsSeqParameterSetId = LayerSpsId;
                layerParser.Context.SeqParameterSetRbsp = parameterSet;
                LayerParameterSets.Add(WriteParameterSet(H265NALTypes.SPS_NUT, 1,
                    s => parameterSet.Write(layerParser.Context, s), layerParser.Context));
            }

            foreach (var parameterSet in pps)
            {
                parameterSet.PpsPicParameterSetId = LayerPpsId;
                parameterSet.PpsSeqParameterSetId = LayerSpsId;
                layerParser.Context.PicParameterSetRbsp = parameterSet;
                LayerParameterSets.Add(WriteParameterSet(H265NALTypes.PPS_NUT, 1,
                    s => parameterSet.Write(layerParser.Context, s), layerParser.Context));
            }
        }

        /// <summary>
        /// Reads layer 1's parameter sets back the way a decoder would - at layer 1 - and reports
        /// any field that does not come back as the encoder wrote it. The syntax above layer 0 is
        /// not the same syntax, so a set written there can lose values silently.
        /// </summary>
        public List<string> CheckLayerParameterSets(IEnumerable<byte[]> dependentParameterSets)
        {
            var problems = new List<string>();

            var original = new MvHevcParser();
            original.ParseParameterSets(dependentParameterSets);

            var written = new MvHevcParser();
            written.ParseParameterSets(BaseParameterSets.Where(IsVps).Concat(LayerParameterSets));

            foreach (var expected in original.Context.SeqParameterSets.Values)
            {
                if (!written.Context.SeqParameterSets.TryGetValue(LayerSpsId, out var actual))
                {
                    problems.Add("layer 1 sequence parameter set did not read back at all");
                    continue;
                }

                foreach (var difference in ParameterSetDiff.Compare(expected, actual))
                    if (!difference.StartsWith("SpsSeqParameterSetId"))
                        problems.Add($"sequence parameter set {difference}");
                break;
            }

            foreach (var expected in original.Context.PicParameterSets.Values)
            {
                if (!written.Context.PicParameterSets.TryGetValue(LayerPpsId, out var actual))
                {
                    problems.Add("layer 1 picture parameter set did not read back at all");
                    continue;
                }

                foreach (var difference in ParameterSetDiff.Compare(expected, actual))
                    if (!difference.StartsWith("PpsPicParameterSetId")
                        && !difference.StartsWith("PpsSeqParameterSetId"))
                        problems.Add($"picture parameter set {difference}");
                break;
            }

            return problems;
        }

        private static bool IsVps(byte[] nalu) => ((nalu[0] >> 1) & 0x3F) == 32;

        /// <summary>
        /// Room for both layers' pictures rather than one layer's. The spec caps the buffer at 16
        /// however high the level goes, so doubling is bounded by that.
        /// </summary>
        private static void GrowDecodedPictureBuffer(SeqParameterSetRbsp parameterSet)
        {
            if (parameterSet.SpsMaxDecPicBufferingMinus1 == null)
                return;

            for (int i = 0; i < parameterSet.SpsMaxDecPicBufferingMinus1.Length; i++)
            {
                ulong pictures = parameterSet.SpsMaxDecPicBufferingMinus1[i] + 1;
                parameterSet.SpsMaxDecPicBufferingMinus1[i] = Math.Min(pictures * 2, 16) - 1;
            }
        }

        /// <summary>The ids layer 1's parameter sets take, so both layers can be active together.</summary>
        public const ulong LayerSpsId = 1;
        public const ulong LayerPpsId = 1;

        private void PatchVps(int width, int height)
        {
            var extension = Vps.VpsExtension
                ?? throw new InvalidOperationException("the template video parameter set has no multiview extension");

            // Layer 1 stays declared as depending on layer 0 either way, and whether it actually
            // predicts is left to each slice. Clearing the dependency instead would be the more
            // obvious move, but it changes what the extension itself contains - a layer with no
            // reference layers carries a poc_lsb_not_present_flag that one with a reference layer
            // does not - and the set then no longer matches the template that was known to work.
            //
            // So: keep the dependency, and turn off the inference that would otherwise force every
            // dependent slice to use it. With this clear each slice carries the flag itself.
            extension.DefaultRefLayersActiveFlag = (byte)(InterLayerPrediction ? 1 : 0);

            // The representation format says what a layer's pictures look like. It is shared by
            // both layers here, so one patch covers the pair.
            if (extension.RepFormat != null)
            {
                foreach (var format in extension.RepFormat)
                {
                    format.PicWidthVpsInLumaSamples = (ushort)width;
                    format.PicHeightVpsInLumaSamples = (ushort)height;
                }
            }
        }

        private byte[] WriteParameterSet(uint nalType, uint layerId, Action<ItuStream> write,
            H265Context context = null)
        {
            context ??= _context;
            using var memory = new MemoryStream();
            using (var stream = new ItuStream(memory))
            {
                // Emulation prevention is applied once, over the finished NAL unit.
                stream.Bitstream.InsertPreventionBytes = false;

                var nalUnit = new NalUnit(0);
                nalUnit.NalUnitHeader = new NalUnitHeader
                {
                    ForbiddenZeroBit = 0,
                    NalUnitType = nalType,
                    NuhLayerId = layerId,
                    NuhTemporalIdPlus1 = 1,
                };
                context.NalHeader = nalUnit;
                nalUnit.Write(context, stream);
                write(stream);
            }

            return RbspUtils.ToEbsp(memory.ToArray());
        }
    }
}
