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

        /// <summary>Re-emits one coded slice at layer 1, pointing at layer 1's picture parameter set.</summary>
        /// <param name="into">
        /// What the caller collects the access unit in: the slice is appended to it and handed back
        /// as a segment of its buffer. The caller empties it for each access unit, and has written
        /// the unit out by then - a picture may have several slices, so it is not emptied per slice.
        /// </param>
        public ArraySegment<byte> ToLayerOne(ArraySegment<byte> nalu, ulong picParameterSetId, MemoryStream into,
            int? pictureOrderCount = null) =>
            Restamp(nalu, 1, picParameterSetId, pictureOrderCount, null, into);

        /// <summary>
        /// Re-emits a coded slice into a given layer, optionally as a different picture type and at
        /// a given picture order count. Changing an IDR into a CRA is what lets a run of intra
        /// pictures keep counting rather than resetting to zero at every one of them.
        /// </summary>
        /// <param name="into">What the slice is appended to - see <see cref="ToLayerOne"/>.</param>
        public ArraySegment<byte> Restamp(ArraySegment<byte> nalu, uint layerId, ulong picParameterSetId,
            int? pictureOrderCount, uint? newNalType, MemoryStream into)
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
                NalUnitType = newNalType ?? parsed.NalUnit.NalUnitHeader.NalUnitType,
                NuhLayerId = layerId,
                NuhTemporalIdPlus1 = parsed.NalUnit.NalUnitHeader.NuhTemporalIdPlus1,
            };
            context.NalHeader = nalUnit;

            // An IDR at layer 0 has no picture order count in its header, so when the same slice
            // moves up a layer the value has to be supplied.
            bool isIdr = nalUnit.NalUnitHeader.NalUnitType == 19 || nalUnit.NalUnitHeader.NalUnitType == 20;
            int maxPocLsb = 1 << (int)(context.SeqParameterSetRbsp.Log2MaxPicOrderCntLsbMinus4 + 4);
            // A slice keeps the picture order count it was coded with unless a new one is given.
            // Renumbering is only wanted when the pictures of two encodes are being made to share
            // a count they did not have; overwriting it otherwise throws away the encoder's own
            // numbering, which every reference in the stream is expressed against.
            if (isIdr)
                header.SlicePicOrderCntLsb = 0;
            else if (pictureOrderCount.HasValue)
                header.SlicePicOrderCntLsb = (ulong)(pictureOrderCount.Value & (maxPocLsb - 1));

            // Above layer 0 a slice says whether it predicts from the layer below. The views here
            // are coded on their own and do not, and each slice has to say so, or the decoder adds
            // a picture to its reference list that the encoder never used.
            header.InterLayerPredEnabledFlag = 0;

            StRefPicSet savedSet = null;
            byte savedSpsFlag = 0;
            bool wasIdr = parsed.NalUnit.NalUnitHeader.NalUnitType == 19
                || parsed.NalUnit.NalUnitHeader.NalUnitType == 20;

            // An intra picture written as a CRA rather than an IDR keeps the count running, and
            // a picture with no references needs an empty set to say so.
            if (wasIdr && !isIdr)
            {
                savedSet = header.StRefPicSet;
                savedSpsFlag = header.ShortTermRefPicSetSpsFlag;

                header.ShortTermRefPicSetSpsFlag = 0;
                header.StRefPicSet = new StRefPicSet(0)
                {
                    InterRefPicSetPredictionFlag = 0,
                    NumNegativePics = 0,
                    NumPositivePics = 0,
                    DeltaPocS0Minus1 = new ulong[0],
                    UsedByCurrPicS0Flag = new byte[0],
                    DeltaPocS1Minus1 = new ulong[0],
                    UsedByCurrPicS1Flag = new byte[0],
                };
            }

            ulong originalPpsId = header.SlicePicParameterSetId;
            header.SlicePicParameterSetId = picParameterSetId;

            try
            {
                // The payload goes back through the stream rather than being appended to what
                // it writes: emulation prevention then covers the join between the new header and
                // the old payload, where a run of zeros can straddle the two.
                //
                // The stream is not disposed, because that would dispose what it writes into. It
                // holds nothing else: bytes go through as each is completed, and the write ends on
                // a byte boundary. Should the buffer grow, the slices already in it stay where they
                // were, in the array they were written to, which nothing writes to again.
                int start = (int)into.Length;
                into.Position = start;
                var stream = new ItuStream(into);

                nalUnit.Write(context, stream);
                parsed.Slice.Write(context, stream);

                var payload = parsed.Payload;
                stream.WriteBytes(payload.Array, payload.Offset, payload.Count);

                return new ArraySegment<byte>(into.GetBuffer(), start, (int)into.Length - start);
            }
            finally
            {
                header.SlicePicParameterSetId = originalPpsId;
                if (savedSet != null)
                {
                    header.StRefPicSet = savedSet;
                    header.ShortTermRefPicSetSpsFlag = savedSpsFlag;
                }
            }
        }

        /// <summary>True when the NAL unit is a coded slice rather than a parameter set or message.</summary>
        public static bool IsSlice(ArraySegment<byte> nalu)
        {
            uint type = (uint)((nalu[0] >> 1) & 0x3F);
            return type <= 21 && !(type > 9 && type < 16);
        }

        /// <summary>True when the NAL unit starts a random access point.</summary>
        public static bool IsIrap(ArraySegment<byte> nalu)
        {
            uint type = (uint)((nalu[0] >> 1) & 0x3F);
            return type >= 16 && type <= 23;
        }

        /// <summary>True when the NAL unit carries a parameter set.</summary>
        public static bool IsParameterSet(ArraySegment<byte> nalu)
        {
            uint type = (uint)((nalu[0] >> 1) & 0x3F);
            return type is 32 or 33 or 34;
        }
    }
}
