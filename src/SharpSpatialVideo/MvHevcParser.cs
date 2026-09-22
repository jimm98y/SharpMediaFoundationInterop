using SharpH265;
using SharpH26X;
using System;
using System.Collections.Generic;
using System.IO;

namespace SharpSpatialVideo
{
    /// <summary>A parsed slice segment header plus the offsets needed to rewrite it.</summary>
    public sealed class ParsedSlice
    {
        public Nalu Nalu { get; set; }
        public NalUnit NalUnit { get; set; }
        public SliceSegmentLayerRbsp Slice { get; set; }
        public SliceSegmentHeader Header => Slice.SliceSegmentHeader;

        /// <summary>The whole NAL unit with emulation prevention removed.</summary>
        public byte[] Rbsp { get; set; }

        /// <summary>Bits consumed by the NAL unit header plus the slice segment header.</summary>
        public ulong HeaderBits { get; set; }

        /// <summary>
        /// Index into <see cref="Rbsp"/> of the first slice segment data byte. The slice segment
        /// header ends with byte_alignment(), so the payload always starts on a byte boundary.
        /// </summary>
        public int PayloadOffset => (int)((HeaderBits + 7) / 8);

        /// <summary>Picture order count derived for this picture (8.3.1).</summary>
        public int Poc { get; set; }
    }

    /// <summary>
    /// Wraps SharpH265 to parse MV-HEVC parameter sets and slice headers, and to derive picture
    /// order counts across the stream.
    /// </summary>
    public sealed class MvHevcParser
    {
        public H265Context Context { get; } = new H265Context();

        /// <summary>Where each parameter set's fields are logged as they are read, if anywhere.</summary>
        public SharpMP4.Common.IMp4Logger Logger { get; set; }

        private int _prevTid0PocMsb;
        private int _prevTid0PocLsb;
        private bool _seenFirstPicture;

        public void ParseParameterSets(IEnumerable<byte[]> nalus)
        {
            foreach (var nalu in nalus)
                ParseParameterSet(nalu);
        }

        private void ParseParameterSet(byte[] ebsp)
        {
            var rbsp = RbspUtils.ToRbsp(ebsp);
            using var stream = Logger == null
                ? new ItuStream(new MemoryStream(rbsp))
                : new ItuStream(new MemoryStream(rbsp), Logger);
            // The payload splice needs byte offsets in the RBSP domain, so emulation prevention
            // is handled here rather than inside the bit reader.
            stream.Bitstream.SkipPreventionBytes = false;

            var nalUnit = new NalUnit((uint)rbsp.Length);
            Context.NalHeader = nalUnit;
            nalUnit.Read(Context, stream);

            switch (nalUnit.NalUnitHeader.NalUnitType)
            {
                case H265NALTypes.VPS_NUT:
                    var vps = new VideoParameterSetRbsp();
                    Context.VideoParameterSetRbsp = vps;
                    vps.Read(Context, stream);
                    break;

                case H265NALTypes.SPS_NUT:
                    var sps = new SeqParameterSetRbsp();
                    Context.SeqParameterSetRbsp = sps;
                    sps.Read(Context, stream);
                    break;

                case H265NALTypes.PPS_NUT:
                    var pps = new PicParameterSetRbsp();
                    Context.PicParameterSetRbsp = pps;
                    pps.Read(Context, stream);
                    break;
            }
        }

        /// <summary>Parses a slice segment header, leaving the payload untouched.</summary>
        public ParsedSlice ParseSlice(Nalu nalu)
        {
            var rbsp = RbspUtils.ToRbsp(nalu.Data);
            using var stream = new ItuStream(new MemoryStream(rbsp));
            stream.Bitstream.SkipPreventionBytes = false;

            var nalUnit = new NalUnit((uint)rbsp.Length);
            Context.NalHeader = nalUnit;
            ulong bits = nalUnit.Read(Context, stream);

            var slice = new SliceSegmentLayerRbsp();
            Context.SliceSegmentLayerRbsp = slice;
            bits += slice.Read(Context, stream);

            return new ParsedSlice
            {
                Nalu = nalu,
                NalUnit = nalUnit,
                Slice = slice,
                Rbsp = rbsp,
                HeaderBits = bits,
            };
        }

        /// <summary>
        /// Picture order count derivation (8.3.1), tracked over the base view in decode order.
        /// </summary>
        public int DerivePoc(ParsedSlice slice)
        {
            var sps = Context.SeqParameterSets.TryGetValue(
                Context.PicParameterSetRbsp.PpsSeqParameterSetId, out var activeSps)
                ? activeSps
                : Context.SeqParameterSetRbsp;

            int maxPocLsb = 1 << (int)(sps.Log2MaxPicOrderCntLsbMinus4 + 4);
            var nalu = slice.Nalu;

            if (nalu.IsIdr)
            {
                _prevTid0PocMsb = 0;
                _prevTid0PocLsb = 0;
                _seenFirstPicture = true;
                return 0;
            }

            int pocLsb = (int)slice.Header.SlicePicOrderCntLsb;
            int pocMsb;

            // The first picture of the stream, and any IRAP that starts a new sequence, has
            // NoRaslOutputFlag equal to 1 and restarts the count.
            bool startsSequence = !_seenFirstPicture;

            if (startsSequence)
            {
                pocMsb = 0;
            }
            else
            {
                int prevPocMsb = _prevTid0PocMsb;
                int prevPocLsb = _prevTid0PocLsb;

                if (pocLsb < prevPocLsb && (prevPocLsb - pocLsb) >= maxPocLsb / 2)
                    pocMsb = prevPocMsb + maxPocLsb;
                else if (pocLsb > prevPocLsb && (pocLsb - prevPocLsb) > maxPocLsb / 2)
                    pocMsb = prevPocMsb - maxPocLsb;
                else
                    pocMsb = prevPocMsb;
            }

            _seenFirstPicture = true;
            int poc = pocMsb + pocLsb;

            // prevTid0Pic is the previous picture with TemporalId 0 that is not a RASL, RADL or
            // sub-layer non-reference picture.
            if (nalu.TemporalId == 0 && !nalu.IsRasl && nalu.Type != 6 && nalu.Type != 7)
            {
                _prevTid0PocMsb = pocMsb;
                _prevTid0PocLsb = pocLsb;
            }

            return poc;
        }

        public void ResetPocState()
        {
            _prevTid0PocMsb = 0;
            _prevTid0PocLsb = 0;
            _seenFirstPicture = false;
        }
    }
}
