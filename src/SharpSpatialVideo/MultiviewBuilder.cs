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
            BaseParameterSets.Add(WriteParameterSet(H265NALTypes.VPS_NUT, 0,
                s => Vps.Write(_templateContext, s)));

            // Read the base encoder's own sequence and picture parameter sets, keep them verbatim
            // for layer 0, and re-emit copies for layer 1.
            _parser.ParseParameterSets(baseParameterSets);

            foreach (var nalu in baseParameterSets)
            {
                uint type = (uint)((nalu[0] >> 1) & 0x3F);
                if (type == H265NALTypes.SPS_NUT || type == H265NALTypes.PPS_NUT)
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

        /// <summary>The ids layer 1's parameter sets take, so both layers can be active together.</summary>
        public const ulong LayerSpsId = 1;
        public const ulong LayerPpsId = 1;

        private void PatchVps(int width, int height)
        {
            var extension = Vps.VpsExtension
                ?? throw new InvalidOperationException("the template video parameter set has no multiview extension");

            // Whether layer 1 is allowed to predict from layer 0. With this clear the two layers
            // are independent and the file is simulcast; with it set the dependent view's slices
            // carry inter-layer references.
            if (extension.DirectDependencyFlag != null && extension.DirectDependencyFlag.Length > 1
                && extension.DirectDependencyFlag[1] != null && extension.DirectDependencyFlag[1].Length > 0)
            {
                extension.DirectDependencyFlag[1][0] = (byte)(InterLayerPrediction ? 1 : 0);
            }

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
