using System;

namespace SharpSpatialVideo
{
    /// <summary>Places the two eyes next to each other in a single NV12 frame.</summary>
    public static class SbsComposer
    {
        /// <summary>
        /// Builds a <paramref name="width"/>*2 by <paramref name="height"/> NV12 frame from two
        /// decoded eyes. The decoded pictures are the coded size, so <paramref name="height"/> can
        /// be the display height to drop the conformance window padding at the bottom.
        /// </summary>
        public static byte[] Compose(byte[] left, byte[] right, int codedWidth, int codedHeight, int width, int height)
        {
            int outWidth = width * 2;
            var output = new byte[outWidth * height * 3 / 2];

            // Luma: each output row is the left row followed by the right row.
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(left, y * codedWidth, output, y * outWidth, width);
                Buffer.BlockCopy(right, y * codedWidth, output, y * outWidth + width, width);
            }

            // Chroma is interleaved and half height, so it splits at the same byte offset.
            int srcChroma = codedWidth * codedHeight;
            int dstChroma = outWidth * height;
            for (int y = 0; y < height / 2; y++)
            {
                Buffer.BlockCopy(left, srcChroma + y * codedWidth, output, dstChroma + y * outWidth, width);
                Buffer.BlockCopy(right, srcChroma + y * codedWidth, output, dstChroma + y * outWidth + width, width);
            }

            return output;
        }
    }
}
