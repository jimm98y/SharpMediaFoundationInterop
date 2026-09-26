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
        private readonly ItuStream _stream;
        private readonly MvHevcParser _parser;
        private ArraySegment<byte>? _payload;

        /// <param name="stream">The reader the header came out of, left just behind it.</param>
        internal ParsedSlice(ItuStream stream, MvHevcParser parser)
        {
            _stream = stream;
            _parser = parser;
        }

        public Nalu Nalu { get; set; }
        public NalUnit NalUnit { get; set; }
        public SliceSegmentLayerRbsp Slice { get; set; }
        public SliceSegmentHeader Header => Slice.SliceSegmentHeader;

        /// <summary>
        /// The coded slice data that follows the header, read out through the same stream the
        /// header was, so it comes without emulation prevention bytes and can be written back
        /// through a stream that puts them in again. The header ends with byte_alignment(), so it
        /// starts on a byte boundary.
        /// </summary>
        /// <remarks>
        /// Read only when it is asked for: most slices are parsed for their header alone. And read
        /// into the parser's own buffer, which the next payload it reads goes over, so it has to be
        /// used - or copied - before another slice's is asked for. Every caller writes it at once.
        /// </remarks>
        public ArraySegment<byte> Payload => _payload ??= _parser.ReadPayload(_stream, Nalu.Data.Count);

        /// <summary>
        /// The slice segment header as it was stored, without emulation prevention bytes: the
        /// bytes in front of <see cref="Payload"/>. Keeping it lets a caller put the picture back
        /// together exactly as it came, rather than writing the header again from what was parsed.
        /// </summary>
        public byte[] HeaderBytes { get; set; }
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
            // The stream takes the NAL unit as stored and drops emulation prevention bytes as it
            // reads, so nothing has to be stripped first.
            using var stream = Logger == null
                ? new ItuStream(new MemoryStream(ebsp))
                : new ItuStream(new MemoryStream(ebsp), Logger);

            var nalUnit = new NalUnit((uint)ebsp.Length);
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
            // Not disposed: the slice keeps it, to read its payload from where the header ended if
            // it is asked for. It holds nothing but a view of the NAL unit.
            var stream = new ItuStream(new MemoryStream(nalu.Data.Array, nalu.Data.Offset, nalu.Data.Count));

            var nalUnit = new NalUnit((uint)nalu.Data.Count);
            Context.NalHeader = nalUnit;
            nalUnit.Read(Context, stream);

            var slice = new SliceSegmentLayerRbsp();
            Context.SliceSegmentLayerRbsp = slice;
            slice.Read(Context, stream);

            // Where the header ends, in the bytes as stored: the stream counts the emulation
            // prevention bytes it skipped, and a header ends on a byte boundary.
            int headerLength = (int)((stream.Bitstream.BitsPosition + 7) / 8);

            return new ParsedSlice(stream, this)
            {
                Nalu = nalu,
                NalUnit = nalUnit,
                Slice = slice,
                HeaderBytes = ToRbsp(nalu.Data, headerLength),
            };
        }

        /// <summary>What <see cref="ParsedSlice.Payload"/> is read into; see the lifetime it has there.</summary>
        private byte[] _payloadBuffer = new byte[1 << 16];

        /// <summary>
        /// The rest of a NAL unit, emulation prevention bytes dropped, into the parser's buffer.
        /// The stream counts the ones it skipped in its position, so what is left is measured
        /// against the stored length - and there can be no more than that.
        /// </summary>
        internal ArraySegment<byte> ReadPayload(ItuStream stream, int storedLength)
        {
            int left = storedLength - (int)(stream.Bitstream.BitsPosition / 8);
            if (left > _payloadBuffer.Length)
                _payloadBuffer = new byte[Math.Max(left, _payloadBuffer.Length * 2)];

            int read = stream.ReadBytes(_payloadBuffer, 0, left);
            return new ArraySegment<byte>(_payloadBuffer, 0, read);
        }

        /// <summary>
        /// A NAL unit as it is stored, out of the two pieces it comes in - a header and the slice
        /// data it belongs to - with the emulation prevention bytes put in, including over the
        /// join. Written through a stream, so the rule is the one the library applies everywhere
        /// else.
        /// </summary>
        /// <param name="into">
        /// What the caller collects the access unit in: the unit is appended to it and handed back
        /// as a segment of its buffer, valid until the caller empties it for the next access unit.
        /// </param>
        public static ArraySegment<byte> ToEbsp(byte[] first, ArraySegment<byte> second, MemoryStream into)
        {
            // Not disposed, because that would dispose what it writes into; it holds nothing else.
            int start = (int)into.Length;
            into.Position = start;
            var stream = new ItuStream(into);

            stream.WriteBytes(first, 0, first.Length);
            stream.WriteBytes(second.Array, second.Offset, second.Count);

            return new ArraySegment<byte>(into.GetBuffer(), start, (int)into.Length - start);
        }

        /// <summary>
        /// The first <paramref name="length"/> stored bytes of a NAL unit, without their emulation
        /// prevention bytes. Read through a stream, so the rule is the one the library applies
        /// everywhere else.
        /// </summary>
        public static byte[] ToRbsp(ArraySegment<byte> nalu, int length)
        {
            using var stream = new ItuStream(new MemoryStream(nalu.Array, nalu.Offset, length));

            // Dropping emulation prevention bytes only ever shortens it.
            var rbsp = new byte[length];
            int read = stream.ReadBytes(rbsp, 0, length);
            if (read < length)
                Array.Resize(ref rbsp, read);

            return rbsp;
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
