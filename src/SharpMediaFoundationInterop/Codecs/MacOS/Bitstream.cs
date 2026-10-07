using System;
using System.Collections.Generic;
using System.IO;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// Reads bits, most significant first. Of a NAL unit's payload it skips the emulation prevention bytes - the 3 of every
    /// 0, 0, 3 - as it goes, reading the RBSP.
    /// </summary>
    internal ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly bool _rbsp;
        private int _byte;
        private int _bit;
        private int _zeros;

        public BitReader(ReadOnlySpan<byte> data, bool rbsp = false)
        {
            _data = data;
            _rbsp = rbsp;
            _byte = 0;
            _bit = 0;
            _zeros = 0;
        }

        public bool AtEnd => _byte >= _data.Length;

        public uint U(int bits)
        {
            uint value = 0;
            for (int i = 0; i < bits; i++)
            {
                if (_byte >= _data.Length)
                    throw new EndOfStreamException("The header ends early.");
                if (_bit == 0 && _rbsp && _zeros >= 2 && _data[_byte] == 3)
                {
                    _byte++;
                    _zeros = 0;
                    if (_byte >= _data.Length)
                        throw new EndOfStreamException("The header ends early.");
                }
                byte b = _data[_byte];
                value = (value << 1) | (uint)((b >> (7 - _bit)) & 1);
                if (++_bit == 8)
                {
                    _bit = 0;
                    _zeros = b == 0 ? _zeros + 1 : 0;
                    _byte++;
                }
            }
            return value;
        }

        public bool Flag() => U(1) != 0;

        public void Skip(int bits)
        {
            while (bits > 24)
            {
                U(24);
                bits -= 24;
            }
            U(bits);
        }

        /// <summary>An Exp-Golomb code, unsigned.</summary>
        public uint Ue()
        {
            int zeros = 0;
            while (U(1) == 0)
            {
                if (++zeros > 31)
                    throw new InvalidDataException("An Exp-Golomb code of more than 32 bits.");
            }
            return zeros == 0 ? 0 : (uint)((1UL << zeros) - 1 + U(zeros));
        }

        /// <summary>An Exp-Golomb code, signed.</summary>
        public int Se()
        {
            uint k = Ue();
            return (k & 1) != 0 ? (int)((k + 1) / 2) : -(int)(k / 2);
        }

        /// <summary>AV1's uvlc().</summary>
        public uint Uvlc()
        {
            int zeros = 0;
            while (U(1) == 0)
            {
                if (++zeros >= 32)
                    return uint.MaxValue;
            }
            return zeros == 0 ? 0 : U(zeros) + (uint)((1UL << zeros) - 1);
        }
    }

    /// <summary>The NAL units of H.264 and H.265 as VideoToolbox needs them: their parameter sets, read for their ids.</summary>
    internal static class NalUnits
    {
        /// <summary>
        /// Each NAL unit of the data, without its start code: of Annex B, every unit between start codes; of anything else
        /// the data whole, as one unit.
        /// </summary>
        public static void Split(ReadOnlySpan<byte> data, List<Range> units)
        {
            units.Clear();
            int start = StartCodeEnd(data, 0);
            if (start < 0)
            {
                if (data.Length > 0)
                    units.Add(new Range(0, data.Length));
                return;
            }

            while (start >= 0)
            {
                int next = FindStartCode(data, start, out int codeLength);
                int end = next < 0 ? data.Length : next;
                // a start code of four bytes is the three before it, and a zero
                while (end > start && data[end - 1] == 0 && next >= 0)
                    end--;
                if (end > start)
                    units.Add(new Range(start, end));
                start = next < 0 ? -1 : next + codeLength;
            }
        }

        /// <summary>Where the data's first unit starts, past its start code; -1 where it does not start with one.</summary>
        private static int StartCodeEnd(ReadOnlySpan<byte> data, int offset)
        {
            if (data.Length >= offset + 3 && data[offset] == 0 && data[offset + 1] == 0 && data[offset + 2] == 1)
                return offset + 3;
            if (data.Length >= offset + 4 && data[offset] == 0 && data[offset + 1] == 0 && data[offset + 2] == 0 && data[offset + 3] == 1)
                return offset + 4;
            return -1;
        }

        private static int FindStartCode(ReadOnlySpan<byte> data, int offset, out int length)
        {
            length = 3;
            for (int i = offset; i + 2 < data.Length; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Of an H.264 SPS: its id, and how many frames may come before one shown ahead of them - the frames a decoder
        /// holds to hand them out in the order they are shown. Where the SPS does not say (its VUI's bitstream restriction),
        /// the most its level's picture buffer holds, but of the baseline profile, which has no B-frames, none.
        /// </summary>
        public static (uint Id, int ReorderDepth) ParseH264Sps(ReadOnlySpan<byte> nal)
        {
            var r = new BitReader(nal.Slice(1), rbsp: true);
            uint profile = r.U(8);
            uint constraints = r.U(8);
            uint level = r.U(8);
            uint id = r.Ue();
            try
            {
                if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
                {
                    uint chroma = r.Ue();
                    if (chroma == 3)
                        r.U(1);
                    r.Ue(); // bit depth of luma
                    r.Ue(); // of chroma
                    r.U(1);
                    if (r.Flag())
                    {
                        for (int i = 0; i < (chroma != 3 ? 8 : 12); i++)
                        {
                            if (r.Flag())
                                SkipScalingList(ref r, i < 6 ? 16 : 64);
                        }
                    }
                }
                r.Ue(); // log2_max_frame_num_minus4
                uint pocType = r.Ue();
                if (pocType == 0)
                {
                    r.Ue();
                }
                else if (pocType == 1)
                {
                    r.U(1);
                    r.Se();
                    r.Se();
                    uint cycle = r.Ue();
                    for (uint i = 0; i < cycle; i++)
                        r.Se();
                }
                r.Ue(); // max_num_ref_frames
                r.U(1);
                uint widthMbs = r.Ue() + 1;
                uint heightMapUnits = r.Ue() + 1;
                bool frameMbsOnly = r.Flag();
                uint heightMbs = heightMapUnits * (frameMbsOnly ? 1u : 2u);
                if (!frameMbsOnly)
                    r.U(1);
                r.U(1);
                if (r.Flag())
                {
                    r.Ue(); r.Ue(); r.Ue(); r.Ue();
                }

                int fallback = profile == 66 ? 0 : MaxDpbFrames(level, (constraints & 0x10) != 0, widthMbs * heightMbs);
                if (!r.Flag()) // no VUI
                    return (id, fallback);

                if (r.Flag())
                {
                    if (r.U(8) == 255)
                        r.Skip(32);
                }
                if (r.Flag())
                    r.U(1);
                if (r.Flag())
                {
                    r.U(4);
                    if (r.Flag())
                        r.Skip(24);
                }
                if (r.Flag())
                {
                    r.Ue(); r.Ue();
                }
                if (r.Flag())
                    r.Skip(65);
                bool nalHrd = r.Flag();
                if (nalHrd)
                    SkipHrd(ref r);
                bool vclHrd = r.Flag();
                if (vclHrd)
                    SkipHrd(ref r);
                if (nalHrd || vclHrd)
                    r.U(1);
                r.U(1); // pic_struct_present_flag
                if (!r.Flag()) // no bitstream restriction
                    return (id, fallback);
                r.U(1);
                r.Ue(); r.Ue(); r.Ue(); r.Ue();
                return (id, (int)Math.Min(r.Ue(), 16));
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
            {
                // an SPS cut short: the most a picture buffer holds
                return (id, profile == 66 ? 0 : 16);
            }
        }

        private static void SkipScalingList(ref BitReader r, int size)
        {
            int last = 8, next = 8;
            for (int j = 0; j < size; j++)
            {
                if (next != 0)
                    next = (last + r.Se() + 256) % 256;
                last = next == 0 ? last : next;
            }
        }

        private static void SkipHrd(ref BitReader r)
        {
            uint count = r.Ue() + 1;
            r.U(8);
            for (uint i = 0; i < count; i++)
            {
                r.Ue(); r.Ue(); r.U(1);
            }
            r.U(20);
        }

        /// <summary>The frames a picture buffer of the level holds, of pictures of the size given: H.264's table A-1.</summary>
        private static int MaxDpbFrames(uint level, bool level1b, uint frameMbs)
        {
            uint maxDpbMbs = level switch
            {
                9 => 396,
                10 => 396,
                11 => level1b ? 396u : 900u,
                12 or 13 or 20 => 2376,
                21 => 4752,
                22 or 30 => 8100,
                31 => 18000,
                32 => 20480,
                40 or 41 => 32768,
                42 => 34816,
                50 => 110400,
                51 or 52 => 184320,
                _ => 696320
            };
            return (int)Math.Clamp(maxDpbMbs / Math.Max(frameMbs, 1), 1, 16);
        }

        /// <summary>The id of an H.264 PPS, its first field.</summary>
        public static uint ParseH264PpsId(ReadOnlySpan<byte> nal) => new BitReader(nal.Slice(1), rbsp: true).Ue();

        /// <summary>Whether an H.264 slice is a picture's first: its first macroblock is 0.</summary>
        public static bool IsFirstH264Slice(ReadOnlySpan<byte> nal) => nal.Length > 1 && (nal[1] & 0x80) != 0;

        /// <summary>The id of an H.265 VPS, its first 4 bits.</summary>
        public static uint ParseHevcVpsId(ReadOnlySpan<byte> nal) => nal.Length > 2 ? (uint)(nal[2] >> 4) : 0;

        /// <summary>The id of an H.265 PPS, its first field.</summary>
        public static uint ParseHevcPpsId(ReadOnlySpan<byte> nal) => new BitReader(nal.Slice(2), rbsp: true).Ue();

        /// <summary>Whether an H.265 slice segment is a picture's first: its first_slice_segment_in_pic_flag.</summary>
        public static bool IsFirstHevcSlice(ReadOnlySpan<byte> nal) => nal.Length > 2 && (nal[2] & 0x80) != 0;

        /// <summary>Of an H.265 SPS: its id, and sps_max_num_reorder_pics of its highest sub-layer.</summary>
        public static (uint Id, int ReorderDepth) ParseHevcSps(ReadOnlySpan<byte> nal)
        {
            var r = new BitReader(nal.Slice(2), rbsp: true);
            r.U(4); // vps id
            int maxSubLayersMinus1 = (int)r.U(3);
            r.U(1);

            // profile_tier_level
            r.Skip(88);
            r.U(8);
            Span<bool> profilePresent = stackalloc bool[8];
            Span<bool> levelPresent = stackalloc bool[8];
            for (int i = 0; i < maxSubLayersMinus1; i++)
            {
                profilePresent[i] = r.Flag();
                levelPresent[i] = r.Flag();
            }
            if (maxSubLayersMinus1 > 0)
            {
                for (int i = maxSubLayersMinus1; i < 8; i++)
                    r.U(2);
            }
            for (int i = 0; i < maxSubLayersMinus1; i++)
            {
                if (profilePresent[i])
                    r.Skip(88);
                if (levelPresent[i])
                    r.U(8);
            }

            uint id = r.Ue();
            try
            {
                if (r.Ue() == 3)
                    r.U(1);
                r.Ue(); r.Ue(); // the size
                if (r.Flag())
                {
                    r.Ue(); r.Ue(); r.Ue(); r.Ue();
                }
                r.Ue(); r.Ue(); // bit depths
                r.Ue(); // log2_max_pic_order_cnt_lsb_minus4
                bool orderingForEach = r.Flag();
                int reorder = 0;
                for (int i = orderingForEach ? 0 : maxSubLayersMinus1; i <= maxSubLayersMinus1; i++)
                {
                    r.Ue();
                    reorder = (int)r.Ue();
                    r.Ue();
                }
                return (id, Math.Min(reorder, 16));
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
            {
                return (id, 16);
            }
        }
    }

    /// <summary>The sample description atoms of the codecs whose configuration VideoToolbox takes as an MP4 file does.</summary>
    internal static class SampleDescriptions
    {
        /// <summary>
        /// An ES_Descriptor of MPEG-4 Systems, ISO/IEC 14496-1 7.2.6.5, of the stream and its decoder specific information:
        /// what an 'esds' box holds after its version and flags, and the magic cookie AudioToolbox takes of AAC.
        /// </summary>
        public static byte[] CreateEsDescriptor(byte objectType, byte streamType, ReadOnlySpan<byte> decoderSpecificInfo)
        {
            var decoderConfig = new List<byte>
            {
                objectType,
                (byte)((streamType << 2) | 1), // not upstream, and the reserved bit
                0, 0, 0, // bufferSizeDB
                0, 0, 0, 0, // maxBitrate
                0, 0, 0, 0 // avgBitrate
            };
            decoderConfig.AddRange(Descriptor(0x05, decoderSpecificInfo));

            var es = new List<byte> { 0, 1, 0 }; // ES_ID 1, as an MP4 file's own; no dependence, URL or OCR stream
            es.AddRange(Descriptor(0x04, decoderConfig.ToArray()));
            es.AddRange(Descriptor(0x06, [0x02])); // SLConfigDescriptor, predefined for MP4
            return Descriptor(0x03, es.ToArray());
        }

        /// <summary>
        /// The decoder specific information of an ES_Descriptor - or of an 'esds' box's content, of its version and flags
        /// first: of AAC, the AudioSpecificConfig. Null where it has none.
        /// </summary>
        public static byte[] ReadDecoderSpecificInfo(ReadOnlySpan<byte> descriptor)
        {
            if (descriptor.Length > 4 && descriptor[0] != 0x03 && descriptor[4] == 0x03)
                descriptor = descriptor.Slice(4);
            int i = 0;
            if (!ReadDescriptor(descriptor, ref i, 0x03, out int esEnd))
                return null;
            byte flags = descriptor[i + 2];
            i += 3;
            if ((flags & 0x80) != 0)
                i += 2; // dependsOn_ES_ID
            if ((flags & 0x40) != 0 && i < descriptor.Length)
                i += 1 + descriptor[i]; // the URL, of its length
            if ((flags & 0x20) != 0)
                i += 2; // OCR_ES_Id
            if (!ReadDescriptor(descriptor, ref i, 0x04, out _))
                return null;
            i += 13; // objectTypeIndication, streamType, bufferSizeDB, maxBitrate, avgBitrate
            if (!ReadDescriptor(descriptor, ref i, 0x05, out int end) || end > descriptor.Length || end > esEnd)
                return null;
            return descriptor[i..end].ToArray();
        }

        /// <summary>A descriptor's tag and size, of up to four bytes of seven bits each: past them, and where its content ends.</summary>
        private static bool ReadDescriptor(ReadOnlySpan<byte> data, ref int i, byte tag, out int end)
        {
            end = 0;
            if (i >= data.Length || data[i] != tag)
                return false;
            i++;
            int size = 0;
            for (int n = 0; n < 4 && i < data.Length; n++)
            {
                byte b = data[i++];
                size = (size << 7) | (b & 0x7F);
                if ((b & 0x80) == 0)
                    break;
            }
            end = i + size;
            return end <= data.Length;
        }

        /// <summary>An 'esds' box's content: its version and flags, and the ES_Descriptor.</summary>
        public static byte[] CreateEsds(byte objectType, byte streamType, ReadOnlySpan<byte> decoderSpecificInfo)
        {
            var descriptor = CreateEsDescriptor(objectType, streamType, decoderSpecificInfo);
            var esds = new byte[4 + descriptor.Length];
            descriptor.CopyTo(esds, 4);
            return esds;
        }

        private static byte[] Descriptor(byte tag, ReadOnlySpan<byte> content)
        {
            // the size in four bytes of seven bits each, as Apple's own are
            int size = content.Length;
            var result = new byte[5 + size];
            result[0] = tag;
            result[1] = (byte)(0x80 | ((size >> 21) & 0x7F));
            result[2] = (byte)(0x80 | ((size >> 14) & 0x7F));
            result[3] = (byte)(0x80 | ((size >> 7) & 0x7F));
            result[4] = (byte)(size & 0x7F);
            content.CopyTo(result.AsSpan(5));
            return result;
        }

        /// <summary>
        /// Of an MPEG-4 Part 2 sample that starts with its headers - the visual object sequence, object and object layer -
        /// those headers, up to its first group of VOPs or VOP: the decoder specific information of its 'esds'. Null where it
        /// has none.
        /// </summary>
        public static byte[] ReadMpeg4Headers(ReadOnlySpan<byte> sample)
        {
            bool hasVol = false;
            for (int i = 0; i + 3 < sample.Length; i++)
            {
                if (sample[i] != 0 || sample[i + 1] != 0 || sample[i + 2] != 1)
                    continue;
                byte code = sample[i + 3];
                if (code >= 0x20 && code <= 0x2F)
                    hasVol = true;
                else if (code == 0xB3 || code == 0xB6)
                    return hasVol ? sample.Slice(0, i).ToArray() : null;
            }
            return hasVol ? sample.ToArray() : null;
        }

        /// <summary>
        /// Of an MPEG-1 or MPEG-2 sample with a sequence header: its size, and whether it is MPEG-1's - a sequence header
        /// with no sequence extension after it. False where the sample has no sequence header.
        /// </summary>
        public static bool ReadMpegSequenceHeader(ReadOnlySpan<byte> sample, out int width, out int height, out bool mpeg1)
        {
            width = height = 0;
            mpeg1 = true;
            bool found = false;
            for (int i = 0; i + 3 < sample.Length; i++)
            {
                if (sample[i] != 0 || sample[i + 1] != 0 || sample[i + 2] != 1)
                    continue;
                byte code = sample[i + 3];
                if (code == 0xB3 && !found && i + 6 < sample.Length)
                {
                    found = true;
                    width = (sample[i + 4] << 4) | (sample[i + 5] >> 4);
                    height = ((sample[i + 5] & 0x0F) << 8) | sample[i + 6];
                }
                else if (code == 0xB5 && found && i + 4 < sample.Length && (sample[i + 4] >> 4) == 1)
                {
                    mpeg1 = false;
                }
                else if (code == 0x00 && found)
                {
                    break; // the first picture: no extension before it
                }
            }
            return found;
        }

        /// <summary>
        /// Of a VP9 key frame - of the first frame of the sample, superframe or not - its 'vpcC' (VP Codec ISO Media File
        /// Format Binding, 2.2) and its size. Null of any other frame.
        /// </summary>
        public static byte[] CreateVpcC(ReadOnlySpan<byte> frame, out int width, out int height)
        {
            width = height = 0;
            try
            {
                var r = new BitReader(frame);
                if (r.U(2) != 2) // frame_marker
                    return null;
                int profile = (int)(r.U(1) | (r.U(1) << 1));
                if (profile == 3)
                    r.U(1);
                if (r.Flag()) // show_existing_frame
                    return null;
                if (r.U(1) != 0) // not a key frame
                    return null;
                r.U(1); // show_frame
                r.U(1); // error_resilient_mode
                if (r.U(24) != 0x498342)
                    return null;

                int bitDepth = 8;
                if (profile >= 2)
                    bitDepth = r.Flag() ? 12 : 10;
                int colorSpace = (int)r.U(3);
                bool fullRange;
                int subsampling; // vpcC's: 1 is 4:2:0, 2 is 4:2:2, 3 is 4:4:4
                if (colorSpace != 7) // not sRGB
                {
                    fullRange = r.Flag();
                    if (profile == 1 || profile == 3)
                    {
                        bool x = r.Flag(), y = r.Flag();
                        r.U(1);
                        subsampling = x && y ? 1 : x ? 2 : 3;
                    }
                    else
                    {
                        subsampling = 1;
                    }
                }
                else
                {
                    fullRange = true;
                    if (profile == 1 || profile == 3)
                        r.U(1);
                    subsampling = 3;
                }
                width = (int)r.U(16) + 1;
                height = (int)r.U(16) + 1;

                // the colour description of the colour space: ISO/IEC 23091-2's codes
                (byte primaries, byte transfer, byte matrix) = colorSpace switch
                {
                    1 => ((byte)6, (byte)6, (byte)6), // BT.601
                    2 => ((byte)1, (byte)1, (byte)1), // BT.709
                    3 => ((byte)6, (byte)6, (byte)6), // SMPTE-170
                    4 => ((byte)7, (byte)7, (byte)7), // SMPTE-240
                    5 => ((byte)9, (byte)(bitDepth == 12 ? 15 : 14), (byte)9), // BT.2020
                    7 => ((byte)1, (byte)13, (byte)0), // sRGB
                    _ => ((byte)2, (byte)2, (byte)2) // unspecified
                };

                return
                [
                    1, 0, 0, 0, // version 1, and flags
                    (byte)profile,
                    Vp9Level(width, height),
                    (byte)((bitDepth << 4) | (subsampling << 1) | (fullRange ? 1 : 0)),
                    primaries, transfer, matrix,
                    0, 0 // no codec initialization data
                ];
            }
            catch (EndOfStreamException)
            {
                return null;
            }
        }

        /// <summary>The least VP9 level whose pictures are of the size: of the size alone, as nothing else is known.</summary>
        private static byte Vp9Level(int width, int height)
        {
            long samples = (long)width * height;
            return samples switch
            {
                <= 36864 => 10,
                <= 73728 => 11,
                <= 122880 => 20,
                <= 245760 => 21,
                <= 552960 => 30,
                <= 983040 => 31,
                <= 2228224 => 41,
                <= 8912896 => 51,
                _ => 61
            };
        }
    }

    /// <summary>The OBUs of AV1, as its low overhead bitstream format has them: each with its size.</summary>
    internal static class Obus
    {
        public const int SequenceHeader = 1;
        public const int TemporalDelimiter = 2;
        public const int FrameHeader = 3;
        public const int TileGroup = 4;
        public const int Frame = 6;
        public const int RedundantFrameHeader = 7;
        public const int Padding = 15;

        /// <summary>An OBU of the data: where it starts, its type, and where its payload starts and ends.</summary>
        public readonly record struct Obu(int Start, int Type, int HeaderLength, bool HasSize, int PayloadStart, int End);

        /// <summary>
        /// Each OBU of the data. One without a size field runs to the end of the data, so is the data's last; where an OBU's
        /// size runs past the data's end, it is cut there.
        /// </summary>
        public static void Split(ReadOnlySpan<byte> data, List<Obu> obus)
        {
            obus.Clear();
            int i = 0;
            while (i < data.Length)
            {
                byte header = data[i];
                int type = (header >> 3) & 0xF;
                bool extension = (header & 0x04) != 0;
                bool hasSize = (header & 0x02) != 0;
                int headerLength = extension ? 2 : 1;
                int p = i + headerLength;
                long size;
                if (hasSize)
                {
                    size = ReadLeb128(data, ref p);
                    if (size < 0)
                        break;
                }
                else
                {
                    size = data.Length - p;
                }
                int end = (int)Math.Min(data.Length, p + size);
                if (p > data.Length)
                    break;
                obus.Add(new Obu(i, type, headerLength, hasSize, p, end));
                i = end;
            }
        }

        private static long ReadLeb128(ReadOnlySpan<byte> data, ref int position)
        {
            long value = 0;
            for (int i = 0; i < 8; i++)
            {
                if (position >= data.Length)
                    return -1;
                byte b = data[position++];
                value |= (long)(b & 0x7F) << (i * 7);
                if ((b & 0x80) == 0)
                    return value;
            }
            return -1;
        }

        public static void WriteLeb128(List<byte> output, long value)
        {
            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0)
                    b |= 0x80;
                output.Add(b);
            }
            while (value != 0);
        }

        /// <summary>
        /// The OBU as the low overhead format has it: with its size field, which is given one where it has none. Its
        /// header's extension byte stays as it is.
        /// </summary>
        public static void WriteWithSize(List<byte> output, ReadOnlySpan<byte> data, Obu obu)
        {
            if (obu.HasSize)
            {
                for (int i = obu.Start; i < obu.End; i++)
                    output.Add(data[i]);
                return;
            }
            output.Add((byte)(data[obu.Start] | 0x02));
            if (obu.HeaderLength == 2)
                output.Add(data[obu.Start + 1]);
            WriteLeb128(output, obu.End - obu.PayloadStart);
            for (int i = obu.PayloadStart; i < obu.End; i++)
                output.Add(data[i]);
        }

        /// <summary>
        /// Of a sequence header OBU's payload: the 'av1C' (AV1 Codec ISO Media File Format Binding, 2.3) of the OBU whole -
        /// the configuration and then the OBU itself, as its configOBUs - and whether its frames' headers are reduced, as a
        /// still picture's are, and its size.
        /// </summary>
        public static byte[] CreateAv1C(ReadOnlySpan<byte> sequenceHeaderObu, ReadOnlySpan<byte> payload,
            out bool reducedStillPictureHeader, out int width, out int height)
        {
            var r = new BitReader(payload);
            uint profile = r.U(3);
            r.U(1); // still_picture
            reducedStillPictureHeader = r.Flag();
            uint level0, tier0 = 0;
            if (reducedStillPictureHeader)
            {
                level0 = r.U(5);
            }
            else
            {
                bool decoderModelInfo = false;
                int bufferDelayLength = 0;
                if (r.Flag()) // timing_info_present_flag
                {
                    r.Skip(64);
                    if (r.Flag())
                        r.Uvlc();
                    decoderModelInfo = r.Flag();
                    if (decoderModelInfo)
                    {
                        bufferDelayLength = (int)r.U(5) + 1;
                        r.Skip(32);
                        r.U(5);
                        r.U(5);
                    }
                }
                bool initialDisplayDelay = r.Flag();
                int operatingPoints = (int)r.U(5) + 1;
                level0 = 0;
                for (int i = 0; i < operatingPoints; i++)
                {
                    r.U(12);
                    uint level = r.U(5);
                    uint tier = level > 7 ? r.U(1) : 0;
                    if (i == 0)
                    {
                        level0 = level;
                        tier0 = tier;
                    }
                    if (decoderModelInfo && r.Flag())
                    {
                        r.Skip(bufferDelayLength * 2);
                        r.U(1);
                    }
                    if (initialDisplayDelay && r.Flag())
                        r.U(4);
                }
            }

            int widthBits = (int)r.U(4) + 1;
            int heightBits = (int)r.U(4) + 1;
            width = (int)r.U(widthBits) + 1;
            height = (int)r.U(heightBits) + 1;
            bool enableOrderHint = false;
            if (!reducedStillPictureHeader)
            {
                if (r.Flag()) // frame_id_numbers_present_flag
                {
                    r.U(4);
                    r.U(3);
                }
            }
            r.U(3); // use_128x128_superblock, enable_filter_intra, enable_intra_edge_filter
            if (!reducedStillPictureHeader)
            {
                r.U(4); // interintra, masked compound, warped motion, dual filter
                enableOrderHint = r.Flag();
                if (enableOrderHint)
                    r.U(2);
                uint forceScreenContentTools = r.Flag() ? 2u : r.U(1);
                if (forceScreenContentTools > 0)
                {
                    if (!r.Flag())
                        r.U(1);
                }
                if (enableOrderHint)
                    r.U(3);
            }
            r.U(3); // superres, cdef, restoration

            // color_config
            bool highBitDepth = r.Flag();
            bool twelveBit = profile == 2 && highBitDepth && r.Flag();
            bool monochrome = profile != 1 && r.Flag();
            uint primaries = 2, transfer = 2, matrix = 2;
            if (r.Flag())
            {
                primaries = r.U(8);
                transfer = r.U(8);
                matrix = r.U(8);
            }
            bool subsamplingX, subsamplingY;
            uint chromaSamplePosition = 0;
            if (monochrome)
            {
                r.U(1);
                subsamplingX = subsamplingY = true;
            }
            else if (primaries == 1 && transfer == 13 && matrix == 0)
            {
                subsamplingX = subsamplingY = false;
            }
            else
            {
                r.U(1); // color_range
                if (profile == 0)
                {
                    subsamplingX = subsamplingY = true;
                }
                else if (profile == 1)
                {
                    subsamplingX = subsamplingY = false;
                }
                else if (twelveBit)
                {
                    subsamplingX = r.Flag();
                    subsamplingY = subsamplingX && r.Flag();
                }
                else
                {
                    subsamplingX = true;
                    subsamplingY = false;
                }
                if (subsamplingX && subsamplingY)
                    chromaSamplePosition = r.U(2);
            }

            var av1C = new byte[4 + sequenceHeaderObu.Length];
            av1C[0] = 0x81; // marker and version 1
            av1C[1] = (byte)((profile << 5) | level0);
            av1C[2] = (byte)((tier0 << 7) | (highBitDepth ? 0x40u : 0) | (twelveBit ? 0x20u : 0) | (monochrome ? 0x10u : 0) |
                (subsamplingX ? 0x08u : 0) | (subsamplingY ? 0x04u : 0) | chromaSamplePosition);
            av1C[3] = 0; // no initial presentation delay
            sequenceHeaderObu.CopyTo(av1C.AsSpan(4));
            return av1C;
        }

        /// <summary>
        /// Whether a frame header - of an OBU_FRAME_HEADER or an OBU_FRAME - is of a frame that is shown: one shown now, or
        /// one decoded before and shown now, its show_existing_frame. A frame of a reduced header is a still picture's,
        /// shown.
        /// </summary>
        public static bool IsShown(ReadOnlySpan<byte> frameHeader, bool reducedStillPictureHeader, out bool showExisting)
        {
            showExisting = false;
            if (reducedStillPictureHeader)
                return true;
            if (frameHeader.Length == 0)
                return false;
            var r = new BitReader(frameHeader);
            showExisting = r.Flag();
            if (showExisting)
                return true;
            r.U(2); // frame_type
            return r.Flag();
        }
    }
}
