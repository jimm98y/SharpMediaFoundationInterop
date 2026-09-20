using SharpMediaFoundationInterop.Transforms;
using SharpMediaFoundationInterop.Transforms.H265;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>
    /// Compares the two ways of coding a stereo pair with a single layer encoder, at the same total
    /// bitrate, and measures what each one costs in quality.
    ///
    /// Interleaved: the two views are fed as one sequence with a GOP of 2, so every base picture is
    /// intra and every dependent picture predicts from the base picture beside it. That is the
    /// MV-HEVC dependency graph, and the stream can be split into two layers.
    ///
    /// Separate: each view is encoded on its own with the encoder's own GOP, giving both views
    /// ordinary temporal prediction but no prediction between them. That is the simulcast file.
    ///
    /// Both are decoded back and compared against the source, because at a fixed bitrate the
    /// difference between them is quality, not size.
    /// </summary>
    public sealed class SchemeComparison
    {
        private readonly MvHevcTrack _track;
        private readonly SingleLayerRewriter _rewriter;

        public SchemeComparison(MvHevcTrack track, SingleLayerRewriter rewriter)
        {
            _track = track;
            _rewriter = rewriter;
        }

        public void Run(int accessUnits, uint totalBitrate)
        {
            var sps = _rewriter.ParserContext.SeqParameterSets[0];
            uint codedWidth = (uint)sps.PicWidthInLumaSamples;
            uint codedHeight = (uint)sps.PicHeightInLumaSamples;
            int lumaBytes = (int)(codedWidth * codedHeight);

            var source = DecodeSource(accessUnits, codedWidth, codedHeight);
            var baseView = new List<byte[]>();
            var dependentView = new List<byte[]>();
            for (int poc = 0; poc + 1 < source.Count * 2; poc += 2)
            {
                if (!source.TryGetValue(poc, out var b) || !source.TryGetValue(poc + 1, out var d))
                    break;
                baseView.Add(b);
                dependentView.Add(d);
            }

            Console.WriteLine($"  {baseView.Count} stereo pairs at {codedWidth}x{codedHeight}, " +
                $"{totalBitrate / 1_000_000} Mbit/s total");
            Console.WriteLine();

            // Interleaved, one encoder, both views, GOP of 2.
            var interleaved = new List<byte[]>();
            for (int i = 0; i < baseView.Count; i++)
            {
                interleaved.Add(baseView[i]);
                interleaved.Add(dependentView[i]);
            }

            var interleavedOut = Encode(interleaved, codedWidth, codedHeight,
                _track.FpsNom * 2, _track.FpsDenom, totalBitrate, gopSize: 2);

            // Separate, one encoder per view, half the bitrate each, encoder's own GOP.
            var baseOut = Encode(baseView, codedWidth, codedHeight,
                _track.FpsNom, _track.FpsDenom, totalBitrate / 2, gopSize: 0);
            var dependentOut = Encode(dependentView, codedWidth, codedHeight,
                _track.FpsNom, _track.FpsDenom, totalBitrate / 2, gopSize: 0);

            var interleavedBack = DecodeBack(interleavedOut, codedWidth, codedHeight,
                _track.FpsNom * 2, _track.FpsDenom);
            var baseBack = DecodeBack(baseOut, codedWidth, codedHeight, _track.FpsNom, _track.FpsDenom);
            var dependentBack = DecodeBack(dependentOut, codedWidth, codedHeight, _track.FpsNom, _track.FpsDenom);

            var interleavedBaseBack = interleavedBack.Where((_, i) => i % 2 == 0).ToList();
            var interleavedDependentBack = interleavedBack.Where((_, i) => i % 2 == 1).ToList();

            long interleavedBytes = interleavedOut.Sum(s => (long)s.Length);
            long separateBytes = baseOut.Sum(s => (long)s.Length) + dependentOut.Sum(s => (long)s.Length);

            Console.WriteLine($"  frames: source {baseView.Count}/{dependentView.Count}, " +
                $"interleaved back {interleavedBaseBack.Count}/{interleavedDependentBack.Count}, " +
                $"separate back {baseBack.Count}/{dependentBack.Count}");
            Console.WriteLine($"  sanity: base vs dependent source PSNR = " +
                $"{Psnr(baseView, dependentView, lumaBytes):F2} (the two eyes against each other)");
            Console.WriteLine();
            Console.WriteLine("  scheme        coded      base view PSNR   dependent view PSNR");
            Console.WriteLine($"  interleaved  {interleavedBytes / 1024,6} KB   " +
                $"{Psnr(baseView, interleavedBaseBack, lumaBytes),14:F2}   " +
                $"{Psnr(dependentView, interleavedDependentBack, lumaBytes),19:F2}");
            Console.WriteLine($"  separate     {separateBytes / 1024,6} KB   " +
                $"{Psnr(baseView, baseBack, lumaBytes),14:F2}   " +
                $"{Psnr(dependentView, dependentBack, lumaBytes),19:F2}");
            Console.WriteLine();
            Console.WriteLine($"  the interleaved scheme spent {100.0 * interleavedBytes / separateBytes - 100:F0}% " +
                "more bits; rate control does not land both schemes on the same size, so the " +
                "quality figures have to be read against that.");
        }

        private SortedDictionary<int, byte[]> DecodeSource(int accessUnits, uint codedWidth, uint codedHeight)
        {
            int dpbSize = Math.Max(_rewriter.MaxDpbOccupancy + 1, 8);
            var parameterSets = _rewriter.BuildParameterSets(dpbSize, _rewriter.MaxReorder);
            long step = 10_000_000L * _track.FpsDenom / _track.FpsNom / SingleLayerRewriter.PocScale;

            var frames = new SortedDictionary<int, byte[]>();
            int pictures = Math.Min(_rewriter.Pictures.Count, accessUnits * SingleLayerRewriter.PocScale + 64);

            using var decoder = new SpatialDecoder(codedWidth, codedHeight, _track.FpsNom, _track.FpsDenom);
            decoder.SendParameterSets(parameterSets);
            foreach (var picture in _rewriter.Pictures.Take(pictures))
                foreach (var frame in decoder.Decode(_rewriter.RewriteSlice(picture), picture.Poc * step))
                    if (frame.Timestamp / step < accessUnits * SingleLayerRewriter.PocScale)
                        frames[(int)(frame.Timestamp / step)] = frame.Nv12;
            foreach (var frame in decoder.Flush())
                if (frame.Timestamp / step < accessUnits * SingleLayerRewriter.PocScale)
                    frames[(int)(frame.Timestamp / step)] = frame.Nv12;

            return frames;
        }

        private static List<byte[]> Encode(List<byte[]> frames, uint width, uint height,
            uint fpsNom, uint fpsDenom, uint bitrate, uint gopSize)
        {
            using var encoder = new H265Encoder(width, height, fpsNom, fpsDenom, bitrate);
            if (gopSize > 0)
                encoder.CodecProperties[CodecApiProperties.GopSize] = gopSize;
            encoder.Initialize();

            var buffer = new byte[encoder.OutputSize];
            var output = new List<byte[]>();
            long frameDuration = 10_000_000L * fpsDenom / fpsNom;

            void Drain()
            {
                while (encoder.ProcessOutput(ref buffer, out uint length, out long _) && length > 0)
                    output.Add(buffer.Take((int)length).ToArray());
            }

            for (int i = 0; i < frames.Count; i++)
            {
                encoder.ProcessInput(frames[i], i * frameDuration);
                Drain();
            }

            encoder.BeginDrain();
            for (int quiet = 0; quiet < 4;)
            {
                int before = output.Count;
                Drain();
                if (output.Count > before) quiet = 0; else quiet++;
            }
            encoder.EndDrain();

            return output;
        }

        private static List<byte[]> DecodeBack(List<byte[]> samples, uint width, uint height,
            uint fpsNom, uint fpsDenom)
        {
            var frames = new List<(long Timestamp, byte[] Nv12)>();
            long frameDuration = 10_000_000L * fpsDenom / fpsNom;

            using var decoder = new SpatialDecoder(width, height, fpsNom, fpsDenom);
            for (int i = 0; i < samples.Count; i++)
                foreach (var frame in decoder.Decode(samples[i], i * frameDuration))
                    frames.Add((frame.Timestamp, frame.Nv12));
            foreach (var frame in decoder.Flush())
                frames.Add((frame.Timestamp, frame.Nv12));

            return frames.OrderBy(f => f.Timestamp).Select(f => f.Nv12).ToList();
        }

        /// <summary>
        /// Peak signal to noise ratio over the luma plane, in dB. Above roughly 40 dB the
        /// difference is not visible; a gap of 1 dB between two codings of the same content is
        /// a difference worth having.
        /// </summary>
        private static double Psnr(List<byte[]> source, List<byte[]> coded, int lumaBytes)
        {
            int count = Math.Min(source.Count, coded.Count);
            if (count == 0)
                return double.NaN;

            double total = 0;
            for (int i = 0; i < count; i++)
            {
                long squaredError = 0;
                for (int p = 0; p < lumaBytes; p++)
                {
                    int difference = source[i][p] - coded[i][p];
                    squaredError += (long)difference * difference;
                }

                double mse = squaredError / (double)lumaBytes;
                total += mse == 0 ? 100 : 10 * Math.Log10(255.0 * 255.0 / mse);
            }

            return total / count;
        }
    }
}
