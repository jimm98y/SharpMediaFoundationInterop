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

            // Read both encodes' own parameter sets first: the video parameter set's buffer table
            // is sized from them.
            _parser.ParseParameterSets(baseParameterSets);
            var layerParser = new MvHevcParser();
            layerParser.ParseParameterSets(dependentParameterSets);

            PatchVps(width, height,
                _context.SeqParameterSets.Values.First(),
                layerParser.Context.SeqParameterSets.Values.First());

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

            // Layer 0's sequence and picture parameter sets go through verbatim. Its slices are not
            // touched, and nothing about them needs to change: with more than one layer, the
            // buffer a decoder keeps is sized by the video parameter set, not by these.
            foreach (var nalu in baseParameterSets)
            {
                uint type = (uint)((nalu[0] >> 1) & 0x3F);
                if (type == H265NALTypes.SPS_NUT || type == H265NALTypes.PPS_NUT)
                    BaseParameterSets.Add(nalu);
            }

            // Layer 1's parameter sets come from the dependent view's own encode, not from copies
            // of the base view's - the two views were coded separately and need not agree.
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

        private static bool IsVps(byte[] nalu) => ((nalu[0] >> 1) & 0x3F) == 32;

        /// <summary>
        /// The decoded picture buffer the video parameter set declares for each output layer set.
        /// With more than one layer this, not the sequence parameter set, is what a decoder sizes
        /// its buffer by.
        /// </summary>
        public string DescribeDpbSize()
        {
            var ext = Vps?.VpsExtension;
            var dpb = ext?.DpbSize;
            if (dpb == null)
                return "no dpb_size in the extension";

            string Show(object value)
            {
                if (value == null) return "null";
                if (value is System.Collections.IEnumerable items && !(value is string))
                    return "[" + string.Join(",", items.Cast<object>().Select(Show)) + "]";
                return value.ToString();
            }

            return string.Join("|", new[]
            {
                $"NumOutputLayerSets = {ext.NumOutputLayerSets}",
                $"sub_layer_flag_info_present_flag = {Show(dpb.SubLayerFlagInfoPresentFlag)}",
                $"sub_layer_dpb_info_present_flag = {Show(dpb.SubLayerDpbInfoPresentFlag)}",
                $"max_vps_dec_pic_buffering_minus1 = {Show(dpb.MaxVpsDecPicBufferingMinus1)}",
                $"max_vps_num_reorder_pics = {Show(dpb.MaxVpsNumReorderPics)}",
                $"max_vps_latency_increase_plus1 = {Show(dpb.MaxVpsLatencyIncreasePlus1)}",
                $"vps_max_dec_pic_buffering_minus1 (base) = {Show(Vps.VpsMaxDecPicBufferingMinus1)}",
            });
        }

        /// <summary>The value for the highest sub-layer, which is the one that governs.</summary>
        private static ulong LastOf(ulong[] values) =>
            values == null || values.Length == 0 ? 0 : values[values.Length - 1];

        /// <summary>The ids layer 1's parameter sets take, so both layers can be active together.</summary>
        public const ulong LayerSpsId = 1;
        public const ulong LayerPpsId = 1;

        private void PatchVps(int width, int height, SeqParameterSetRbsp baseSps, SeqParameterSetRbsp layerSps)
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

            // With more than one layer a decoder sizes each layer's share of the decoded picture
            // buffer from this table, not from the sequence parameter sets. The template's was
            // written for Apple's dependent layer, which only ever references the base picture of
            // its own access unit - a picture that sits in layer 0's share - so it gives layer 1
            // room for one picture. A dependent view with temporal references of its own needs
            // what its encoder asked for, or each reference is gone by the time it is used.
            var dpb = extension.DpbSize;
            if (dpb?.MaxVpsDecPicBufferingMinus1 != null)
            {
                // Indexed [ output layer set ][ layer ][ sub-layer ].
                for (int i = 0; i < dpb.MaxVpsDecPicBufferingMinus1.Length; i++)
                {
                    var perLayer = dpb.MaxVpsDecPicBufferingMinus1[i];
                    if (perLayer == null) continue;

                    for (int k = 0; k < perLayer.Length && k < 2; k++)
                    {
                        var perSubLayer = perLayer[k];
                        if (perSubLayer == null) continue;

                        var sps = k == 0 ? baseSps : layerSps;
                        for (int j = 0; j < perSubLayer.Length; j++)
                            perSubLayer[j] = LastOf(sps.SpsMaxDecPicBufferingMinus1);
                    }

                    if (dpb.MaxVpsNumReorderPics?[i] != null)
                        for (int j = 0; j < dpb.MaxVpsNumReorderPics[i].Length; j++)
                            dpb.MaxVpsNumReorderPics[i][j] = Math.Max(
                                LastOf(baseSps.SpsMaxNumReorderPics), LastOf(layerSps.SpsMaxNumReorderPics));
                }
            }

            // The base layer's own entry, which a single layer decoder reads.
            if (Vps.VpsMaxDecPicBufferingMinus1 != null)
                for (int i = 0; i < Vps.VpsMaxDecPicBufferingMinus1.Length; i++)
                    Vps.VpsMaxDecPicBufferingMinus1[i] = LastOf(baseSps.SpsMaxDecPicBufferingMinus1);
            if (Vps.VpsMaxNumReorderPics != null)
                for (int i = 0; i < Vps.VpsMaxNumReorderPics.Length; i++)
                    Vps.VpsMaxNumReorderPics[i] = LastOf(baseSps.SpsMaxNumReorderPics);

            // The representation format says what a layer's pictures look like, and a decoder
            // checks the layers' sequence parameter sets against it. Both layers share it here, so
            // it is taken whole from the base encode: coded size, chroma format, bit depth and the
            // conformance window that crops the coded size to the displayed one.
            if (extension.RepFormat != null)
            {
                foreach (var format in extension.RepFormat)
                {
                    format.PicWidthVpsInLumaSamples = (uint)baseSps.PicWidthInLumaSamples;
                    format.PicHeightVpsInLumaSamples = (uint)baseSps.PicHeightInLumaSamples;
                    format.ChromaAndBitDepthVpsPresentFlag = 1;
                    format.ChromaFormatVpsIdc = (uint)baseSps.ChromaFormatIdc;
                    format.SeparateColourPlaneVpsFlag = baseSps.SeparateColourPlaneFlag;
                    format.BitDepthVpsLumaMinus8 = (uint)baseSps.BitDepthLumaMinus8;
                    format.BitDepthVpsChromaMinus8 = (uint)baseSps.BitDepthChromaMinus8;
                    format.ConformanceWindowVpsFlag = baseSps.ConformanceWindowFlag;
                    format.ConfWinVpsLeftOffset = baseSps.ConfWinLeftOffset;
                    format.ConfWinVpsRightOffset = baseSps.ConfWinRightOffset;
                    format.ConfWinVpsTopOffset = baseSps.ConfWinTopOffset;
                    format.ConfWinVpsBottomOffset = baseSps.ConfWinBottomOffset;
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
