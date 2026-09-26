using SharpH265;
using SharpH26X;
using System;
using System.Collections.Generic;
using System.IO;

namespace SharpSpatialVideo
{
    /// <summary>
    /// What a one-eye file cannot say, carried in it anyway: the parts of the spatial video that
    /// the split has to rewrite, kept so that <see cref="LeftRightTranscoder"/> can put the
    /// original back exactly rather than build something equivalent.
    ///
    /// It rides along as a user data SEI, which every decoder passes over, so both eye files stay
    /// ordinary videos that play anywhere.
    /// </summary>
    /// <remarks>
    /// Two things are kept. The parameter sets, because the split writes single layer ones in
    /// their place: the video parameter set with its multiview extension, the eye mapping SEI, and
    /// layer 1's sequence and picture parameter sets. And each of layer 1's slice headers, because
    /// the split rebuilds every reference set out of what the interleaved stream must retain - a
    /// set that decodes to the same pictures, but is not the one the camera wrote, and cannot be
    /// turned back into it. The coded slice data is never touched, so only the headers are kept.
    /// </remarks>
    public static class RoundTripSei
    {
        /// <summary>What marks a user data SEI as one of these: "SpatialSplit01" and two zeros.</summary>
        private static readonly byte[] Uuid =
        {
            0x53, 0x70, 0x61, 0x74, 0x69, 0x61, 0x6C, 0x53,
            0x70, 0x6C, 0x69, 0x74, 0x30, 0x31, 0x00, 0x00,
        };

        private const byte ParameterSetsKind = 1;
        private const byte SliceHeaderKind = 2;
        private const byte ExtraNalusKind = 3;
        private const byte ContainerKind = 4;

        /// <summary>The parameter sets the split writes over, to be put back on the way in.</summary>
        public sealed class Content
        {
            public List<byte[]> ParameterSets { get; } = new List<byte[]>();

            /// <summary>
            /// Each layer 1 slice header, by the access unit it belonged to. Counted in decode
            /// order rather than by picture order count, because a stream with a key frame every
            /// so often starts its count again at each one, and two pictures a second apart would
            /// then be filed under the same number.
            /// </summary>
            public Dictionary<int, byte[]> SliceHeaders { get; } = new Dictionary<int, byte[]>();

            /// <summary>
            /// What else an access unit held - the camera's own SEIs, which sit in front of one
            /// picture or the other - by that access unit, counted as above.
            /// </summary>
            public Dictionary<int, List<ExtraNalu>> Extra { get; } = new Dictionary<int, List<ExtraNalu>>();

            /// <summary>The file the split read, with its coded samples taken out.</summary>
            public ContainerLayout Container { get; set; }

            public bool IsEmpty => ParameterSets.Count == 0 && SliceHeaders.Count == 0;
        }

        /// <summary>A NAL unit an access unit held besides its two pictures, and where it sat.</summary>
        public sealed class ExtraNalu
        {
            public byte[] Data { get; set; }

            /// <summary>True when it came in front of the base picture, false in front of layer 1's.</summary>
            public bool BeforeBasePicture { get; set; }
        }

        /// <summary>The SEI carrying the parameter sets, which goes on the first picture.</summary>
        public static byte[] ForParameterSets(IEnumerable<byte[]> parameterSets)
        {
            using var body = new MemoryStream();
            body.WriteByte(ParameterSetsKind);

            var sets = new List<byte[]>(parameterSets);
            WriteUInt16(body, sets.Count);
            foreach (var set in sets)
            {
                WriteUInt16(body, set.Length);
                body.Write(set, 0, set.Length);
            }

            return Wrap(body.ToArray());
        }

        /// <summary>The SEI carrying one layer 1 slice header, which goes on that picture.</summary>
        public static byte[] ForSliceHeader(int accessUnitIndex, byte[] header)
        {
            using var body = new MemoryStream();
            body.WriteByte(SliceHeaderKind);
            WriteInt32(body, accessUnitIndex);
            WriteUInt16(body, header.Length);
            body.Write(header, 0, header.Length);

            return Wrap(body.ToArray());
        }

        /// <summary>The SEI carrying what else an access unit held, which goes on that picture.</summary>
        public static byte[] ForExtraNalus(int accessUnitIndex, IEnumerable<ExtraNalu> extra)
        {
            using var body = new MemoryStream();
            body.WriteByte(ExtraNalusKind);
            WriteInt32(body, accessUnitIndex);

            var nalus = new List<ExtraNalu>(extra);
            WriteUInt16(body, nalus.Count);
            foreach (var nalu in nalus)
            {
                body.WriteByte((byte)(nalu.BeforeBasePicture ? 1 : 0));
                WriteUInt16(body, nalu.Data.Length);
                body.Write(nalu.Data, 0, nalu.Data.Length);
            }

            return Wrap(body.ToArray());
        }

        /// <summary>The SEI carrying the file the split read, which goes on the first picture.</summary>
        public static byte[] ForContainer(ContainerLayout layout)
        {
            var bytes = layout.ToBytes();

            using var body = new MemoryStream();
            body.WriteByte(ContainerKind);
            WriteInt32(body, bytes.Length);
            body.Write(bytes, 0, bytes.Length);

            return Wrap(body.ToArray());
        }

        /// <summary>Adds what one NAL unit carries, if it is one of these, to what has been read.</summary>
        public static void ReadInto(ArraySegment<byte> nalu, Content content)
        {
            if (((nalu[0] >> 1) & 0x3F) != H265NALTypes.PREFIX_SEI_NUT)
                return;

            var rbsp = MvHevcParser.ToRbsp(nalu, nalu.Count);

            // nal header, payload type, payload size, then the uuid this is known by.
            int at = 2;
            int payloadType = ReadFfCoded(rbsp, ref at);
            int payloadSize = ReadFfCoded(rbsp, ref at);
            if (payloadType != 5 || at + payloadSize > rbsp.Length)
                return;

            for (int i = 0; i < Uuid.Length; i++)
                if (rbsp[at + i] != Uuid[i])
                    return;

            int position = at + Uuid.Length;
            byte kind = rbsp[position++];

            if (kind == ParameterSetsKind)
            {
                int count = ReadUInt16(rbsp, ref position);
                for (int i = 0; i < count; i++)
                {
                    int length = ReadUInt16(rbsp, ref position);
                    content.ParameterSets.Add(Slice(rbsp, position, length));
                    position += length;
                }
            }
            else if (kind == SliceHeaderKind)
            {
                int accessUnit = ReadInt32(rbsp, ref position);
                int length = ReadUInt16(rbsp, ref position);
                content.SliceHeaders[accessUnit] = Slice(rbsp, position, length);
            }
            else if (kind == ContainerKind)
            {
                int length = ReadInt32(rbsp, ref position);
                content.Container = ContainerLayout.FromBytes(Slice(rbsp, position, length));
            }
            else if (kind == ExtraNalusKind)
            {
                int accessUnit = ReadInt32(rbsp, ref position);
                int count = ReadUInt16(rbsp, ref position);

                var extra = new List<ExtraNalu>(count);
                for (int i = 0; i < count; i++)
                {
                    bool beforeBase = rbsp[position++] != 0;
                    int length = ReadUInt16(rbsp, ref position);
                    extra.Add(new ExtraNalu { BeforeBasePicture = beforeBase, Data = Slice(rbsp, position, length) });
                    position += length;
                }

                content.Extra[accessUnit] = extra;
            }
        }

        /// <summary>Wraps a body as a user data SEI NAL unit, at layer 0 where a prefix SEI belongs.</summary>
        private static byte[] Wrap(byte[] body)
        {
            using var memory = new MemoryStream();
            using (var stream = new ItuStream(memory))
            {
                var nalUnit = new NalUnit(0);
                nalUnit.NalUnitHeader = new NalUnitHeader
                {
                    ForbiddenZeroBit = 0,
                    NalUnitType = H265NALTypes.PREFIX_SEI_NUT,
                    NuhLayerId = 0,
                    NuhTemporalIdPlus1 = 1,
                };
                var context = new H265Context { NalHeader = nalUnit };
                nalUnit.Write(context, stream);

                WriteFfCoded(stream, 5);                            // user_data_unregistered
                WriteFfCoded(stream, Uuid.Length + body.Length);

                foreach (byte value in Uuid)
                    stream.WriteUnsignedInt(8, value, null);
                foreach (byte value in body)
                    stream.WriteUnsignedInt(8, value, null);

                stream.WriteUnsignedInt(8, 0x80, null);             // rbsp_trailing_bits
            }

            return memory.ToArray();
        }

        private static void WriteFfCoded(ItuStream stream, int value)
        {
            while (value >= 0xFF)
            {
                stream.WriteUnsignedInt(8, (byte)0xFF, null);
                value -= 0xFF;
            }

            stream.WriteUnsignedInt(8, (byte)value, null);
        }

        private static int ReadFfCoded(byte[] bytes, ref int at)
        {
            int value = 0;
            while (at < bytes.Length && bytes[at] == 0xFF)
            {
                value += 0xFF;
                at++;
            }

            return at < bytes.Length ? value + bytes[at++] : value;
        }

        private static void WriteUInt16(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }


        private static void WriteInt32(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static int ReadUInt16(byte[] bytes, ref int at)
        {
            int value = (bytes[at] << 8) | bytes[at + 1];
            at += 2;
            return value;
        }

        private static int ReadInt32(byte[] bytes, ref int at)
        {
            int value = (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];
            at += 4;
            return value;
        }

        private static byte[] Slice(byte[] bytes, int at, int length)
        {
            var result = new byte[length];
            Buffer.BlockCopy(bytes, at, result, 0, length);
            return result;
        }
    }
}
