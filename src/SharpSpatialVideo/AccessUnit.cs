using System.Collections.Generic;

namespace SharpSpatialVideo
{
    /// <summary>A single NAL unit as stored in the MP4 sample (EBSP, no start code or length prefix).</summary>
    public sealed class Nalu
    {
        public byte[] Data { get; set; }

        public uint Type => (uint)((Data[0] >> 1) & 0x3F);
        public uint LayerId => (uint)(((Data[0] & 1) << 5) | (Data[1] >> 3));
        public uint TemporalId => (uint)((Data[1] & 0x07) - 1);

        /// <summary>VCL NAL unit, i.e. a coded slice segment.</summary>
        public bool IsSlice => Type <= 31;

        public bool IsIrap => Type >= 16 && Type <= 23;
        public bool IsIdr => Type == 19 || Type == 20;
        public bool IsCra => Type == 21;
        public bool IsRasl => Type == 8 || Type == 9;

        public override string ToString() => $"t={Type} L{LayerId} {Data.Length}B";
    }

    /// <summary>One MP4 sample: in an MV-HEVC track this is one access unit holding both views.</summary>
    public sealed class AccessUnit
    {
        public int Index { get; set; }
        public List<Nalu> Nalus { get; } = new List<Nalu>();

        /// <summary>Composition time offset from the track's ctts, in media timescale units.</summary>
        public int CompositionOffset { get; set; }
        public uint Duration { get; set; }

        public Nalu SliceOfLayer(uint layerId)
        {
            foreach (var nalu in Nalus)
                if (nalu.IsSlice && nalu.LayerId == layerId)
                    return nalu;
            return null;
        }
    }
}
