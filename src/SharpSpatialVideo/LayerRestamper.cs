using SharpH265;
using SharpH26X;
using System;
using System.Collections.Generic;
using System.IO;

namespace SharpSpatialVideo
{
    /// <summary>
    /// Moves a single layer encode's coded slices into layer 1 of a multi-layer stream.
    ///
    /// The NAL unit header carries the layer, so this cannot be done by editing bytes in place:
    /// the slice header's own syntax depends on the layer. An IDR slice at layer 0 leaves its
    /// picture order count out, because it is known to be zero, while one at a layer above carries
    /// it - so the header has to be parsed and written again rather than patched. The coded data
    /// after the header is untouched, which the byte alignment at the end of every slice header
    /// makes safe.
    /// </summary>
    public sealed class LayerRestamper
    {
        private readonly MvHevcParser _parser = new MvHevcParser();

        public LayerRestamper(IEnumerable<byte[]> parameterSets)
        {
            _parser.ParseParameterSets(parameterSets);
            InferLayerMapping();
        }

        /// <summary>
        /// Fills in what the video parameter set leaves to be inferred. When layer ids are not
        /// coded explicitly, each layer's id is its index, and the parser has nothing to record -
        /// but writing a slice above layer 0 reads the mapping back, so it has to exist.
        /// </summary>
        private void InferLayerMapping()
        {
            var context = _parser.Context;
            var vps = context.VideoParameterSetRbsp;
            if (vps?.VpsExtension == null)
                return;

            int layers = (int)Math.Min(62, vps.VpsMaxLayersMinus1) + 1;

            if (context.LayerIdxInVps == null || context.LayerIdxInVps.Length < layers)
            {
                var mapping = new uint[layers];
                for (uint i = 0; i < layers; i++)
                    mapping[i] = i;
                context.LayerIdxInVps = mapping;
            }

            vps.VpsExtension.PocLsbNotPresentFlag ??= new byte[layers];
        }

        public H265Context Context => _parser.Context;

        /// <summary>Re-emits one coded slice at layer 1, pointing at layer 1's picture parameter set.</summary>
        public byte[] ToLayerOne(byte[] nalu, ulong picParameterSetId)
        {
            var parsed = _parser.ParseSlice(new Nalu { Data = nalu });
            var context = _parser.Context;
            var header = parsed.Header;

            // Re-activate the parameter sets this slice was coded against so the writer takes the
            // same branches through the syntax that the reader took.
            context.PicParameterSetRbsp = context.PicParameterSets[header.SlicePicParameterSetId];
            context.SeqParameterSetRbsp = context.SeqParameterSets[
                context.PicParameterSetRbsp.PpsSeqParameterSetId];
            context.SliceSegmentLayerRbsp = parsed.Slice;

            var nalUnit = new NalUnit(0);
            nalUnit.NalUnitHeader = new NalUnitHeader
            {
                ForbiddenZeroBit = 0,
                NalUnitType = parsed.NalUnit.NalUnitHeader.NalUnitType,
                NuhLayerId = 1,
                NuhTemporalIdPlus1 = parsed.NalUnit.NalUnitHeader.NuhTemporalIdPlus1,
            };
            context.NalHeader = nalUnit;

            // An IDR at layer 0 has no picture order count in its header, so when the same slice
            // moves up a layer the value has to be supplied.
            bool isIdr = nalUnit.NalUnitHeader.NalUnitType == 19 || nalUnit.NalUnitHeader.NalUnitType == 20;
            if (isIdr)
                header.SlicePicOrderCntLsb = 0;

            ulong originalPpsId = header.SlicePicParameterSetId;
            header.SlicePicParameterSetId = picParameterSetId;

            try
            {
                using var memory = new MemoryStream();
                using (var stream = new ItuStream(memory))
                {
                    stream.Bitstream.InsertPreventionBytes = false;
                    nalUnit.Write(context, stream);
                    parsed.Slice.Write(context, stream);
                }

                var rewritten = memory.ToArray();
                var payload = parsed.Rbsp;
                var output = new byte[rewritten.Length + payload.Length - parsed.PayloadOffset];
                Buffer.BlockCopy(rewritten, 0, output, 0, rewritten.Length);
                Buffer.BlockCopy(payload, parsed.PayloadOffset, output,
                    rewritten.Length, payload.Length - parsed.PayloadOffset);

                return RbspUtils.ToEbsp(output);
            }
            finally
            {
                header.SlicePicParameterSetId = originalPpsId;
            }
        }

        /// <summary>True when the NAL unit is a coded slice rather than a parameter set or message.</summary>
        public static bool IsSlice(byte[] nalu)
        {
            uint type = (uint)((nalu[0] >> 1) & 0x3F);
            return type <= 21 && !(type > 9 && type < 16);
        }

        /// <summary>True when the NAL unit starts a random access point.</summary>
        public static bool IsIrap(byte[] nalu)
        {
            uint type = (uint)((nalu[0] >> 1) & 0x3F);
            return type >= 16 && type <= 23;
        }

        /// <summary>True when the NAL unit carries a parameter set.</summary>
        public static bool IsParameterSet(byte[] nalu)
        {
            uint type = (uint)((nalu[0] >> 1) & 0x3F);
            return type is 32 or 33 or 34;
        }
    }
}
