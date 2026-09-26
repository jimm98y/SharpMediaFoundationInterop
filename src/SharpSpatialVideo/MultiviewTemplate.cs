using System;
using System.Collections.Generic;

namespace SharpSpatialVideo
{
    /// <summary>
    /// The two parameter sets a spatial video has to be built around, taken from an iPhone
    /// recording and kept here so nothing has to be supplied to build one.
    ///
    /// They are the parts that describe the stereo pair rather than the pictures, so they do not
    /// depend on the footage: what the encoder produces supplies the rest.
    /// </summary>
    public static class MultiviewTemplate
    {
        /// <summary>
        /// The video parameter set, with the multiview extension that declares the two layers,
        /// their dependency, the representation formats, the output layer sets and the buffer
        /// sizes - all cross referenced. <see cref="MultiviewBuilder"/> patches it rather than
        /// assembling one, because a set a decoder already accepts is far likelier to play than
        /// one built from the specification by hand.
        /// </summary>
        public const string VideoParameterSetHex =
            "40010C11FFFF016000000300B0000003000003007B15C15B7B200028245970602000000BF8000003000007B8D07800440A01E5C52BF708500808080080";

        /// <summary>
        /// The three dimensional reference displays information SEI, which says which layer is
        /// which eye: left_view_id 1 and right_view_id 0, so the base layer is the right eye.
        /// Without it a Mac plays the file as one eye, at half the frame rate, for twice as long -
        /// it takes the two views of an access unit for two frames.
        /// </summary>
        public const string EyeMappingSeiHex = "4E01B004040A802080";

        /// <summary>The parameter sets to build from, in the order hvcC holds them.</summary>
        public static List<byte[]> ParameterSets() => new List<byte[]>
        {
            FromHex(VideoParameterSetHex),
            FromHex(EyeMappingSeiHex),
        };

        /// <summary>
        /// The same, out of an existing spatial video. Only needed to build against a different
        /// recording's parameter sets - say one from a camera that writes them differently.
        /// </summary>
        public static List<byte[]> ParameterSets(string spatialVideoPath) =>
            MvHevcReader.Read(spatialVideoPath, loadSamples: false).BaseParameterSets;

        private static byte[] FromHex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}
