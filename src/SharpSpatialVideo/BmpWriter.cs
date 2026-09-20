using System;
using System.IO;

namespace SharpSpatialVideo
{
    /// <summary>Writes an NV12 frame out as a 24bpp bitmap, for eyeballing intermediate results.</summary>
    public static class BmpWriter
    {
        public static void Save(byte[] nv12, int width, int height, string path)
        {
            int rowBytes = (width * 3 + 3) / 4 * 4;
            var pixels = new byte[rowBytes * height];

            for (int y = 0; y < height; y++)
            {
                int row = (height - 1 - y) * rowBytes;   // bitmaps are bottom-up
                for (int x = 0; x < width; x++)
                {
                    int luma = nv12[y * width + x] - 16;
                    int chroma = width * height + y / 2 * width + (x & ~1);
                    int u = nv12[chroma] - 128;
                    int v = nv12[chroma + 1] - 128;

                    pixels[row + x * 3 + 0] = Clamp((298 * luma + 516 * u + 128) >> 8);
                    pixels[row + x * 3 + 1] = Clamp((298 * luma - 100 * u - 208 * v + 128) >> 8);
                    pixels[row + x * 3 + 2] = Clamp((298 * luma + 409 * v + 128) >> 8);
                }
            }

            using var writer = new BinaryWriter(File.Create(path));
            writer.Write((ushort)0x4D42);
            writer.Write(54 + pixels.Length);
            writer.Write(0);
            writer.Write(54);
            writer.Write(40);
            writer.Write(width);
            writer.Write(height);
            writer.Write((ushort)1);
            writer.Write((ushort)24);
            writer.Write(0);
            writer.Write(pixels.Length);
            writer.Write(2835);
            writer.Write(2835);
            writer.Write(0);
            writer.Write(0);
            writer.Write(pixels);
        }

        private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
    }
}
