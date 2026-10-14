using System;

namespace SharpMediaFoundationInterop.Output
{
    /// <summary>
    /// The speakers of so many channels, in WAVE's order - as WAVEFORMATEXTENSIBLE's dwChannelMask has them, as Windows'
    /// decoders hand them out - and where each is around the listener.
    /// </summary>
    public static class SpeakerLayout
    {
        /// <summary>
        /// The speakers of so many channels: mono the front centre; stereo the front left and right; 3.0, quad, 5.0, 5.1 and
        /// 6.1 of the back speakers, as Windows' 5.1 is; 7.1 with the side ones too. More than 8, the first so many of the
        /// mask's bits.
        /// </summary>
        public static uint ChannelMask(uint channels) => channels switch
        {
            1 => 0x4,          // FC
            2 => 0x3,          // FL FR
            3 => 0x7,          // FL FR FC
            4 => 0x33,         // FL FR BL BR
            5 => 0x37,         // FL FR FC BL BR
            6 => 0x3F,         // FL FR FC LFE BL BR
            7 => 0x13F,        // FL FR FC LFE BL BR BC
            8 => 0x63F,        // FL FR FC LFE BL BR SL SR
            _ => channels >= 32 ? uint.MaxValue : (1u << (int)channels) - 1,
        };

        /// <summary>
        /// Where each speaker of the mask is, in its order: degrees to the left of straight ahead, and up; null of the LFE,
        /// which is of no direction. The front ones at 30 degrees, as ITU-R BS.775 has them; the surrounds at 110 where
        /// there is one pair of them, else the side ones at 90 and the back ones at 150; the top ones at 45 up.
        /// </summary>
        public static (double Azimuth, double Elevation)?[] Directions(uint mask)
        {
            bool sides = (mask & 0x600) != 0, backs = (mask & 0x30) != 0;
            double side = backs ? 90 : 110, back = sides ? 150 : 110;
            var directions = new (double, double)?[System.Numerics.BitOperations.PopCount(mask)];
            int i = 0;
            for (int bit = 0; bit < 32 && i < directions.Length; bit++)
            {
                if ((mask & (1u << bit)) == 0)
                    continue;
                directions[i++] = bit switch
                {
                    0 => (30, 0),       // front left
                    1 => (-30, 0),      // front right
                    2 => (0, 0),        // front centre
                    3 => null,          // LFE
                    4 => (back, 0),     // back left
                    5 => (-back, 0),    // back right
                    6 => (15, 0),       // front left of centre
                    7 => (-15, 0),      // front right of centre
                    8 => (180, 0),      // back centre
                    9 => (side, 0),     // side left
                    10 => (-side, 0),   // side right
                    11 => (0, 90),      // top centre
                    12 => (30, 45),     // top front left
                    13 => (0, 45),      // top front centre
                    14 => (-30, 45),    // top front right
                    15 => (back, 45),   // top back left
                    16 => (180, 45),    // top back centre
                    17 => (-back, 45),  // top back right
                    _ => (0, 0),
                };
            }
            return directions;
        }
    }
}
