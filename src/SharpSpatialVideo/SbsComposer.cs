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

        /// <summary>
        /// Takes the left half of a side by side frame, as an NV12 picture of the coded size the
        /// encoder wants. The source rows are the coded width; the half that is wanted is the
        /// first <paramref name="width"/> samples of each.
        /// </summary>
        public static byte[] CropLeft(byte[] frame, int codedWidth, int codedHeight,
            int width, int height, int outCodedWidth, int outCodedHeight, byte[] output = null) =>
            Crop(frame, 0, codedWidth, codedHeight, width, height, outCodedWidth, outCodedHeight, output);

        /// <summary>The right half of a side by side frame.</summary>
        public static byte[] CropRight(byte[] frame, int codedWidth, int codedHeight,
            int width, int height, int outCodedWidth, int outCodedHeight, byte[] output = null) =>
            Crop(frame, width, codedWidth, codedHeight, width, height, outCodedWidth, outCodedHeight, output);

        /// <remarks>
        /// Pass <paramref name="output"/> to crop into an existing buffer instead of a new one. A
        /// crop is 3 MB, and a conversion makes two per frame; reusing them keeps that off the
        /// large object heap, which is only collected on a full collection.
        /// </remarks>
        private static byte[] Crop(byte[] frame, int x, int codedWidth, int codedHeight,
            int width, int height, int outCodedWidth, int outCodedHeight, byte[] output)
        {
            output ??= new byte[outCodedWidth * outCodedHeight * 3 / 2];

            for (int y = 0; y < height; y++)
                Buffer.BlockCopy(frame, y * codedWidth + x, output, y * outCodedWidth, width);

            // The rows past the picture repeat the last one, so the padding the encoder codes is
            // cheap rather than an edge it has to spend bits on.
            for (int y = height; y < outCodedHeight; y++)
                Buffer.BlockCopy(output, (height - 1) * outCodedWidth, output, y * outCodedWidth, width);

            int sourceChroma = codedWidth * codedHeight;
            int outputChroma = outCodedWidth * outCodedHeight;
            for (int y = 0; y < height / 2; y++)
                Buffer.BlockCopy(frame, sourceChroma + y * codedWidth + x,
                    output, outputChroma + y * outCodedWidth, width);

            for (int y = height / 2; y < outCodedHeight / 2; y++)
                Buffer.BlockCopy(output, outputChroma + (height / 2 - 1) * outCodedWidth,
                    output, outputChroma + y * outCodedWidth, width);

            return output;
        }
    }
}
