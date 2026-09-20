using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpSpatialVideo
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  SharpSpatialVideo analyze <input.mov> [accessUnits]");
                Console.WriteLine("  SharpSpatialVideo truth <input.mov> [truthDir] [frames]   verify against ffmpeg");
                Console.WriteLine("  SharpSpatialVideo reference <input.mov> [truthDir]        regenerate the ffmpeg reference");
                return 1;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "analyze":
                    return Analyze(args);

                case "plan":
                    return Plan(args);

                case "decode":
                    return Decode(args);

                case "verify":
                    return Verify(args);

                case "sbs":
                    return Sbs(args);

                case "split":
                    return Split(args);

                case "check":
                    return Check(args);

                case "fidelity":
                    return Fidelity(args);

                case "truth":
                    return Truth(args);

                case "reference":
                    return Reference(args);

                case "convert":
                    return ConvertSbs(args);

                default:
                    Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                    return 1;
            }
        }

        private static int Analyze(string[] args)
        {
            string path = args.Length > 1 ? args[1] : @"C:\Temp\IMG_7881.MOV";
            int limit = args.Length > 2 ? int.Parse(args[2]) : int.MaxValue;

            var track = MvHevcReader.Read(path);
            Console.WriteLine($"{path}");
            Console.WriteLine($"  display {track.DisplayWidth}x{track.DisplayHeight}  " +
                $"nalLengthSize={track.NalLengthSize}  timescale={track.Timescale}  " +
                $"fps={track.FpsNom}/{track.FpsDenom}  multiview={track.IsMultiview}");
            Console.WriteLine($"  access units: {track.AccessUnits.Count}");

            var parser = new MvHevcParser();
            parser.ParseParameterSets(track.BaseParameterSets);
            parser.ParseParameterSets(track.LayerParameterSets);

            var sps = parser.Context.SeqParameterSets[0];
            Console.WriteLine($"  coded {sps.PicWidthInLumaSamples}x{sps.PicHeightInLumaSamples}  " +
                $"log2MaxPocLsb={sps.Log2MaxPicOrderCntLsbMinus4 + 4}  " +
                $"maxDecPicBuffering={sps.SpsMaxDecPicBufferingMinus1[0] + 1}  " +
                $"maxNumReorder={sps.SpsMaxNumReorderPics[0]}  " +
                $"longTermRefPics={sps.LongTermRefPicsPresentFlag}");
            Console.WriteLine();
            Console.WriteLine("  AU |  view 0            |  view 1");
            Console.WriteLine("     | type poc_lsb  poc  | type poc_lsb  poc  refs");

            int count = Math.Min(limit, track.AccessUnits.Count);
            int maxPoc = 0;
            for (int i = 0; i < count; i++)
            {
                var au = track.AccessUnits[i];
                var base0 = au.SliceOfLayer(0);
                var dep1 = au.SliceOfLayer(1);
                if (base0 == null)
                    continue;

                var parsed0 = parser.ParseSlice(base0);
                parsed0.Poc = parser.DerivePoc(parsed0);
                maxPoc = Math.Max(maxPoc, parsed0.Poc);

                string line = $"  {i,3} | {NalName(base0.Type),-5}{parsed0.Header.SlicePicOrderCntLsb,6} " +
                    $"{parsed0.Poc,5}  |";

                if (dep1 != null)
                {
                    var parsed1 = parser.ParseSlice(dep1);
                    line += $" {NalName(dep1.Type),-5}{parsed1.Header.SlicePicOrderCntLsb,6} " +
                        $"{"",5}  nRefIdx={parsed1.Header.NumRefIdxL0ActiveMinus1 + 1}" +
                        $"/{parsed1.Header.NumRefIdxL1ActiveMinus1 + 1} " +
                        $"total={parser.Context.NumPicTotalCurr} {Refs(parsed1)}";
                }

                Console.WriteLine(line);
            }

            Console.WriteLine();
            Console.WriteLine($"  highest base-view POC seen: {maxPoc}");
            return 0;
        }

        /// <summary>Decodes both views, composes them side by side and re-encodes to one MP4.</summary>
        private static int ConvertSbs(string[] args)
        {
            string path = args.Length > 1 ? args[1] : @"C:\Temp\IMG_7881.MOV";
            string outPath = args.Length > 2 ? args[2]
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)),
                    Path.GetFileNameWithoutExtension(path) + "_sbs.mp4");
            uint bitrate = args.Length > 3 ? uint.Parse(args[3]) : 30_000_000;
            int limit = args.Length > 4 ? int.Parse(args[4]) : int.MaxValue;

            SharpMediaFoundationInterop.Log.SinkError = (m, ex) => Console.WriteLine($"  [mf error] {m}");

            var track = MvHevcReader.Read(path);
            var rewriter = new SingleLayerRewriter(track);
            rewriter.Plan();

            var converter = new SbsConverter(track, rewriter);
            converter.Convert(outPath, bitrate, BaseLayerIsLeftEye, limit);

            Console.WriteLine($"  wrote {outPath}: {converter.FramesWritten} frames at " +
                $"{track.DisplayWidth * 2}x{track.DisplayHeight}, {bitrate / 1_000_000} Mbit/s, " +
                $"{converter.AudioSamplesWritten} audio samples");
            return converter.FramesWritten > 0 ? 0 : 2;
        }

        /// <summary>
        /// Regenerates the per frame reference hashes with ffmpeg, overwriting what is there. The
        /// 'truth' command generates them on demand, so this is only needed after changing the
        /// input or the ffmpeg build.
        /// </summary>
        private static int Reference(string[] args)
        {
            string path = args.Length > 1 ? args[1] : @"C:\Temp\IMG_7881.MOV";
            string truthDir = args.Length > 2 ? args[2] : @"C:\Temp\truth";

            if (!FfmpegReference.Ensure(path, truthDir, force: true, out string reason))
            {
                Console.Error.WriteLine($"  {reason}");
                return 2;
            }

            Console.WriteLine($"  {reason}");
            foreach (var file in Directory.GetFiles(truthDir, "v*.md5"))
                Console.WriteLine($"  {Path.GetFileName(file)}: {new FileInfo(file).Length} bytes");

            return 0;
        }

        /// <summary>
        /// Compares both decoded views against reference frames produced by an MV-HEVC capable
        /// decoder. This is the only external check available on the dependent view, which cannot
        /// otherwise be verified because nothing else on this machine can decode it.
        /// </summary>
        private static int Truth(string[] args)
        {
            string path = args.Length > 1 ? args[1] : @"C:\Temp\IMG_7881.MOV";
            string truthDir = args.Length > 2 ? args[2] : @"C:\Temp\truth";
            int frames = args.Length > 3 ? int.Parse(args[3]) : 40;

            // The reference comes from ffmpeg, the only MV-HEVC decoder to hand. Without it there
            // is nothing to compare against, so say why and stop rather than report a pass.
            if (!FfmpegReference.Ensure(path, truthDir, force: false, out string reason))
            {
                Console.WriteLine($"  no reference available: {reason}");
                return 2;
            }
            Console.WriteLine($"  reference: {reason}");

            SharpMediaFoundationInterop.Log.SinkError = (m, ex) => Console.WriteLine($"  [mf error] {m}");

            var track = MvHevcReader.Read(path);
            var rewriter = new SingleLayerRewriter(track);
            rewriter.Plan();

            var sps = rewriter.ParserContext.SeqParameterSets[0];
            int codedWidth = (int)sps.PicWidthInLumaSamples;
            int width = (int)track.DisplayWidth;
            int height = (int)track.DisplayHeight;
            int frameBytes = width * height;

            // Decode enough of the rewritten stream to cover the reference frames. The dependent
            // view lags the base view, so feed generously.
            var decodedByPoc = new Dictionary<int, byte[]>();
            int dpbSize = Math.Max(rewriter.MaxDpbOccupancy + 1, 8);
            var parameterSets = rewriter.BuildParameterSets(dpbSize, rewriter.MaxReorder);
            long step = 10_000_000L * track.FpsDenom / track.FpsNom / SingleLayerRewriter.PocScale;

            int feed = Math.Min(rewriter.Pictures.Count, frames * SingleLayerRewriter.PocScale + 64);
            using (var decoder = new SpatialDecoder((uint)codedWidth, (uint)sps.PicHeightInLumaSamples,
                track.FpsNom, track.FpsDenom))
            {
                decoder.SendParameterSets(parameterSets);
                foreach (var picture in rewriter.Pictures.Take(feed))
                    foreach (var frame in decoder.Decode(rewriter.RewriteSlice(picture), picture.Poc * step))
                        decodedByPoc[(int)(frame.Timestamp / step)] = frame.Nv12;
                foreach (var frame in decoder.Flush())
                    decodedByPoc[(int)(frame.Timestamp / step)] = frame.Nv12;
            }

            foreach (var (viewId, slot, eye) in new[] { (0, 0, "base"), (1, 1, "dependent") })
            {
                string hashFile = Path.Combine(truthDir, $"v{viewId}.md5");
                if (File.Exists(hashFile))
                {
                    CompareHashes(hashFile, viewId, slot, eye, decodedByPoc, codedWidth, width, height);
                    continue;
                }

                string file = Path.Combine(truthDir, $"v{viewId}.yuv");
                if (!File.Exists(file)) { Console.WriteLine($"  {file} missing"); continue; }

                var truth = File.ReadAllBytes(file);
                int truthStride = frameBytes * 3 / 2;   // yuv420p: luma plane then two chroma planes
                int available = truth.Length / truthStride;
                int compared = 0, exact = 0;
                double worstMean = 0; int worstPixel = 0;

                for (int p = 0; p < Math.Min(available, frames); p++)
                {
                    if (!decodedByPoc.TryGetValue(p * SingleLayerRewriter.PocScale + slot, out var mine))
                        continue;
                    compared++;

                    long sum = 0; int worst = 0;
                    for (int y = 0; y < height; y++)
                    {
                        int mineRow = y * codedWidth;
                        int truthRow = p * truthStride + y * width;
                        for (int x = 0; x < width; x++)
                        {
                            int d = Math.Abs(mine[mineRow + x] - truth[truthRow + x]);
                            sum += d;
                            if (d > worst) worst = d;
                        }
                    }
                    if (worst == 0) exact++;
                    else
                    {
                        worstMean = Math.Max(worstMean, sum / (double)frameBytes);
                        worstPixel = Math.Max(worstPixel, worst);
                    }
                }

                Console.WriteLine($"  view {viewId} ({eye}): {exact}/{compared} frames bit identical to the reference" +
                    (exact == compared ? "" : $"; worst mean {worstMean:F4}, worst pixel {worstPixel}"));
            }

            return 0;
        }

        /// <summary>
        /// Compares every decoded frame of one view against per-frame hashes from the reference
        /// decoder, which covers the whole clip without writing gigabytes of raw video.
        /// </summary>
        private static void CompareHashes(
            string hashFile, int viewId, int slot, string eye,
            Dictionary<int, byte[]> decodedByPoc, int codedWidth, int width, int height)
        {
            // framemd5 lines are "stream, dts, pts, duration, size, hash"; stream 0 is the video.
            var expected = File.ReadLines(hashFile)
                .Where(line => line.Length > 0 && line[0] != '#')
                .Select(line => line.Split(','))
                .Where(f => f.Length >= 6 && f[0].Trim() == "0")
                .Select(f => f[5].Trim())
                .ToList();

            using var md5 = System.Security.Cryptography.MD5.Create();
            int compared = 0, exact = 0;
            var mismatches = new List<int>();

            for (int p = 0; p < expected.Count; p++)
            {
                if (!decodedByPoc.TryGetValue(p * SingleLayerRewriter.PocScale + slot, out var nv12))
                    continue;
                compared++;

                var planar = ToPlanarYuv420(nv12, codedWidth, width, height);
                string hash = Convert.ToHexString(md5.ComputeHash(planar)).ToLowerInvariant();
                if (hash == expected[p]) exact++;
                else if (mismatches.Count < 5) mismatches.Add(p);
            }

            var missing = Enumerable.Range(0, expected.Count)
                .Where(p => !decodedByPoc.ContainsKey(p * SingleLayerRewriter.PocScale + slot))
                .ToList();
            Console.WriteLine($"  view {viewId} ({eye}): {exact}/{compared} frames match the reference hash" +
                (mismatches.Count == 0 ? "" : $"; first mismatches at frames {string.Join(",", mismatches)}"));
            if (missing.Count > 0)
                Console.WriteLine($"     {missing.Count} never decoded: {string.Join(",", missing.Take(12))}" +
                    (missing.Count > 12 ? " ..." : ""));
        }

        /// <summary>Converts a decoded NV12 frame to the planar layout the reference decoder hashes.</summary>
        private static byte[] ToPlanarYuv420(byte[] nv12, int codedWidth, int width, int height)
        {
            var output = new byte[width * height * 3 / 2];
            for (int y = 0; y < height; y++)
                Buffer.BlockCopy(nv12, y * codedWidth, output, y * width, width);

            int chromaStart = codedWidth * (nv12.Length / (codedWidth * 3 / 2));
            int u = width * height;
            int v = u + width * height / 4;
            for (int y = 0; y < height / 2; y++)
            {
                int row = chromaStart + y * codedWidth;
                for (int x = 0; x < width / 2; x++)
                {
                    output[u + y * width / 2 + x] = nv12[row + x * 2];
                    output[v + y * width / 2 + x] = nv12[row + x * 2 + 1];
                }
            }
            return output;
        }

        /// <summary>
        /// Compares the base view decoded from its untouched stream against the same pictures
        /// decoded out of the rewritten stream. They should be identical: the coded slice data is
        /// the same and only the picture order counts and reference picture sets were rewritten.
        /// Any difference means the rewrite is not bit exact, and the dependent view predicts from
        /// a reference that is already wrong.
        /// </summary>
        private static int Fidelity(string[] args)
        {
            string path = args.Length > 1 ? args[1] : @"C:\Temp\IMG_7881.MOV";
            int limit = args.Length > 2 ? int.Parse(args[2]) : 120;

            SharpMediaFoundationInterop.Log.SinkError = (m, ex) => Console.WriteLine($"  [mf error] {m}");

            var track = MvHevcReader.Read(path);
            var rewriter = new SingleLayerRewriter(track);
            rewriter.Plan();

            var sps = rewriter.ParserContext.SeqParameterSets[0];
            uint codedWidth = (uint)sps.PicWidthInLumaSamples;
            uint codedHeight = (uint)sps.PicHeightInLumaSamples;
            int lumaSize = (int)(codedWidth * track.DisplayHeight);

            var pictures = rewriter.Pictures;
            int count = Math.Min(limit, pictures.Count);
            var fed = pictures.Take(count).ToList();

            // Reference decode: the base view exactly as it was coded, original picture order
            // counts and all.
            var reference = new Dictionary<int, byte[]>();
            long refStep = 10_000_000L * track.FpsDenom / track.FpsNom;
            using (var decoder = new SpatialDecoder(codedWidth, codedHeight, track.FpsNom, track.FpsDenom))
            {
                decoder.SendParameterSets(rewriter.BuildBaseViewParameterSets(track.BaseParameterSets));
                foreach (var picture in fed.Where(p => p.View == 0).OrderBy(p => p.DecodeIndex))
                    foreach (var frame in decoder.Decode(picture.Source.Nalu.Data, picture.Source.Poc * refStep))
                        reference[(int)(frame.Timestamp / refStep)] = frame.Nv12;
                foreach (var frame in decoder.Flush())
                    reference[(int)(frame.Timestamp / refStep)] = frame.Nv12;
            }

            // Rewritten decode.
            int dpbSize = Math.Max(rewriter.MaxDpbOccupancy + 1, 8);
            var parameterSets = rewriter.BuildParameterSets(dpbSize, rewriter.MaxReorder);
            var rewritten = new Dictionary<int, byte[]>();
            long step = 10_000_000L * track.FpsDenom / track.FpsNom / SingleLayerRewriter.PocScale;
            using (var decoder = new SpatialDecoder(codedWidth, codedHeight, track.FpsNom, track.FpsDenom))
            {
                decoder.SendParameterSets(parameterSets);
                foreach (var picture in fed)
                    foreach (var frame in decoder.Decode(rewriter.RewriteSlice(picture), picture.Poc * step))
                        rewritten[(int)(frame.Timestamp / step)] = frame.Nv12;
                foreach (var frame in decoder.Flush())
                    rewritten[(int)(frame.Timestamp / step)] = frame.Nv12;
            }

            int compared = 0, same = 0;
            double worstMean = 0;
            int worstPixel = 0;
            foreach (var picture in fed.Where(p => p.View == 0).OrderBy(p => p.Source.Poc))
            {
                if (!reference.TryGetValue(picture.Source.Poc, out var a)) continue;
                if (!rewritten.TryGetValue(picture.Poc, out var b)) continue;
                compared++;
                if (a.AsSpan(0, lumaSize).SequenceEqual(b.AsSpan(0, lumaSize))) { same++; continue; }

                long sum = 0; int worst = 0;
                for (int i = 0; i < lumaSize; i++)
                {
                    int d = Math.Abs(a[i] - b[i]);
                    sum += d;
                    if (d > worst) worst = d;
                }
                worstMean = Math.Max(worstMean, sum / (double)lumaSize);
                worstPixel = Math.Max(worstPixel, worst);
            }

            Console.WriteLine($"  base view: {same}/{compared} pictures bit identical between the " +
                $"untouched and rewritten streams");
            if (same != compared)
                Console.WriteLine($"  worst picture: mean abs luma diff {worstMean:F3}, worst pixel {worstPixel}");
            return 0;
        }

        /// <summary>Decodes a produced MP4 back through Media Foundation to prove it is playable.</summary>
        private static int Check(string[] args)
        {
            string path = args[1];
            int limit = args.Length > 2 ? int.Parse(args[2]) : int.MaxValue;
            string dump = args.Length > 3 ? args[3] : null;

            SharpMediaFoundationInterop.Log.SinkError = (m, ex) => Console.WriteLine($"  [mf error] {m}");

            var track = MvHevcReader.Read(path);
            Console.WriteLine($"{path}: {track.AccessUnits.Count} samples, " +
                $"{track.DisplayWidth}x{track.DisplayHeight}, multiview={track.IsMultiview}");

            // Read back what was actually coded, rather than trusting what we meant to write.
            var inspector = new MvHevcParser();
            inspector.ParseParameterSets(track.BaseParameterSets);
            var inspectPps = inspector.Context.PicParameterSets;
            foreach (var kv in inspectPps)
                Console.WriteLine($"  pps {kv.Key}: output_flag_present={kv.Value.OutputFlagPresentFlag}");
            foreach (var au in track.AccessUnits.Take(6))
            {
                var slice = au.Nalus.FirstOrDefault(n => n.IsSlice);
                if (slice == null) continue;
                var parsed = inspector.ParseSlice(slice);
                Console.WriteLine($"  sample {au.Index}: t={slice.Type} poc_lsb={parsed.Header.SlicePicOrderCntLsb} " +
                    $"pic_output_flag={parsed.Header.PicOutputFlag}");
            }

            uint codedHeight = (track.DisplayHeight + 7) / 8 * 8;
            if (codedHeight % 64 != 0 && codedHeight < 1088) codedHeight = 1088;

            using var decoder = new SpatialDecoder(track.DisplayWidth, codedHeight, track.FpsNom, track.FpsDenom);
            decoder.SendParameterSets(track.BaseParameterSets);

            int fed = 0, got = 0;
            long timestamp = 0;
            long step = 10_000_000L * track.FpsDenom / track.FpsNom;

            foreach (var au in track.AccessUnits.Take(limit))
            {
                foreach (var frame in decoder.Decode(SpatialDecoder.ToAnnexB(au.Nalus.Select(n => n.Data)), timestamp))
                {
                    if (dump != null && got < 4)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dump)));
                        string name = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dump)),
                            Path.GetFileNameWithoutExtension(dump) + got.ToString("D3") + ".bmp");
                        BmpWriter.Save(frame.Nv12, (int)track.DisplayWidth, (int)track.DisplayHeight, name);
                        Console.WriteLine($"  wrote {name}");
                    }
                    got++;
                }
                fed++;
                timestamp += step;
            }
            foreach (var frame in decoder.Flush())
                got++;

            Console.WriteLine($"  fed {fed} samples -> decoded {got} frames");
            return got > 0 ? 0 : 2;
        }

        /// <summary>
        /// Writes one MP4 per eye without decoding or re-encoding. The left file is the base view
        /// exactly as it was coded. The right file is the rewritten single-layer stream: it has to
        /// carry the base pictures too, because the right eye is predicted from them, but they are
        /// marked not to be output so only the right eye is presented.
        /// </summary>
        /// <summary>
        /// Whether the MV-HEVC base layer carries the left eye. Measured from disparity rather
        /// than taken from the file: a feature sits further right in the left eye, and here it
        /// sits further right in the dependent view.
        /// </summary>
        private const bool BaseLayerIsLeftEye = false;

        private static int Split(string[] args)
        {
            string path = args.Length > 1 ? args[1] : @"C:\Temp\IMG_7881.MOV";
            string outDir = args.Length > 2 ? args[2] : Path.GetDirectoryName(Path.GetFullPath(path));
            string stem = Path.GetFileNameWithoutExtension(path);

            var track = MvHevcReader.Read(path);
            var rewriter = new SingleLayerRewriter(track);
            rewriter.Plan();

            Directory.CreateDirectory(outDir);
            int framePeriod = (int)track.FpsDenom;   // one access unit, in source timescale units

            // Left eye: the base view is already an ordinary single-layer stream, so its NAL
            // units and its original picture order counts go through untouched.
            // Base pictures follow the source access units one for one and in the same order, so
            // each keeps its original sample duration.
            var leftPictures = rewriter.Pictures
                .Where(p => p.View == 0)
                .OrderBy(p => p.DecodeIndex)
                .Select((p, index) => new MuxPicture
                {
                    Nalu = p.Source.Nalu.Data,
                    Poc = p.Source.Poc,
                    IsRandomAccessPoint = p.Source.Nalu.IsIrap,
                    Duration = (int)track.AccessUnits[index].Duration,
                })
                .ToList();

            // Which layer holds which eye is measured from the disparity between the decoded
            // views: a feature sits further right in the left eye, and in this footage it sits
            // further right in the dependent view. So the base layer is the RIGHT eye. Note the
            // stri box claims otherwise - it is reported below so the two can be compared.
            string baseEye = BaseLayerIsLeftEye ? "left" : "right";
            string dependentEye = BaseLayerIsLeftEye ? "right" : "left";
            Console.WriteLine($"  stri box: has_left={track.HasLeftEyeView} has_right={track.HasRightEyeView} " +
                $"reversed={track.EyeViewsReversed} (raw 0x{track.StereoViewInfo:X2})");
            Console.WriteLine($"  base layer treated as the {baseEye} eye, dependent as the {dependentEye}");

            string leftPath = Path.Combine(outDir, $"{stem}_{baseEye}.mp4");
            var leftParameterSets = rewriter.BuildBaseViewParameterSets(track.BaseParameterSets);
            EyeMuxer.Write(leftPath, leftParameterSets, leftPictures, track.Timescale, framePeriod, track);
            Console.WriteLine($"  wrote {leftPath} ({leftPictures.Count} pictures, base view verbatim)");

            // Right eye: the whole rewritten stream, on a timescale three times finer so the
            // three picture order count slots of an access unit each land on a whole tick.
            int dpbSize = Math.Max(rewriter.MaxDpbOccupancy + 1, 8);
            var parameterSets = rewriter.BuildParameterSets(dpbSize, rewriter.MaxReorder);

            // Only the dependent view is presented; the base pictures are carried solely so the
            // dependent view has something to predict from.
            foreach (var picture in rewriter.Pictures)
                picture.Output = picture.View == 1;

            // An access unit holds two or three pictures depending on whether it needed a
            // duplicate. Splitting its frame period between them keeps every access unit exactly
            // one frame long, so the track duration matches the source.
            var picturesPerAccessUnit = rewriter.Pictures
                .GroupBy(p => p.Poc / SingleLayerRewriter.PocScale)
                .ToDictionary(g => g.Key, g => g.Count());

            int rightPeriod = framePeriod * SingleLayerRewriter.PocScale;
            var rightPictures = rewriter.Pictures
                .OrderBy(p => p.DecodeIndex)
                .Select(p => new MuxPicture
                {
                    Nalu = rewriter.RewriteSlice(p),
                    Poc = p.Poc,
                    IsRandomAccessPoint = p.OutputNalType >= 16 && p.OutputNalType <= 23,
                    Duration = rightPeriod / picturesPerAccessUnit[p.Poc / SingleLayerRewriter.PocScale],
                })
                .ToList();

            string rightPath = Path.Combine(outDir, $"{stem}_{dependentEye}.mp4");
            EyeMuxer.Write(rightPath, parameterSets, rightPictures,
                track.Timescale * (uint)SingleLayerRewriter.PocScale, framePeriod, track);
            Console.WriteLine($"  wrote {rightPath} ({rightPictures.Count} pictures, " +
                $"{rightPictures.Count - leftPictures.Count} of them reference-only)");

            return 0;
        }

        /// <summary>Rewrites, decodes and pairs the two eyes into side-by-side frames.</summary>
        private static int Sbs(string[] args)
        {
            string path = args.Length > 1 ? args[1] : @"C:\Temp\IMG_7881.MOV";
            int limit = args.Length > 2 ? int.Parse(args[2]) : 60;
            string dumpDir = args.Length > 3 ? args[3] : null;

            SharpMediaFoundationInterop.Log.SinkError = (m, ex) => Console.WriteLine($"  [mf error] {m}");

            var track = MvHevcReader.Read(path);
            var rewriter = new SingleLayerRewriter(track);
            rewriter.Plan();

            int dpbSize = Math.Max(rewriter.MaxDpbOccupancy + 1, 8);
            var parameterSets = rewriter.BuildParameterSets(dpbSize, rewriter.MaxReorder);

            var sps = rewriter.ParserContext.SeqParameterSets[0];
            uint codedWidth = (uint)sps.PicWidthInLumaSamples;
            uint codedHeight = (uint)sps.PicHeightInLumaSamples;

            var pictures = rewriter.Pictures;
            int count = Math.Min(limit, pictures.Count);

            using var decoder = new SpatialDecoder(codedWidth, codedHeight, track.FpsNom, track.FpsDenom);
            decoder.SendParameterSets(parameterSets);

            var decoded = new List<DecodedFrame>();
            long step = 10_000_000L * track.FpsDenom / track.FpsNom / SingleLayerRewriter.PocScale;
            for (int i = 0; i < count; i++)
            {
                var nalu = rewriter.RewriteSlice(pictures[i]);
                foreach (var frame in decoder.Decode(nalu, pictures[i].Poc * step))
                    decoded.Add(frame);
            }
            foreach (var frame in decoder.Flush())
                decoded.Add(frame);

            // Each picture is fed with a timestamp derived from its picture order count, and the
            // decoder carries that timestamp onto the frame it produces. Matching on it is exact,
            // unlike matching on output position: the decoder drops frames at the tail, and any
            // such gap silently shifts an index-based mapping - which would transpose the views.
            var fed = pictures.Take(count).ToList();
            var byPoc = new Dictionary<int, byte[]>();
            int unmatched = 0;
            foreach (var frame in decoded)
            {
                long poc = step == 0 ? 0 : frame.Timestamp / step;
                if (poc >= 0 && poc <= int.MaxValue && !byPoc.ContainsKey((int)poc))
                    byPoc[(int)poc] = frame.Nv12;
                else
                    unmatched++;
            }
            Console.WriteLine($"  fed {count} pictures -> {decoded.Count} frames, " +
                $"{byPoc.Count} matched to a picture order count, {unmatched} not");


            int width = (int)track.DisplayWidth;
            int height = (int)track.DisplayHeight;
            int pairs = 0;

            if (dumpDir != null)
                Directory.CreateDirectory(dumpDir);

            foreach (var picture in fed.Where(p => p.View == 0).OrderBy(p => p.Poc))
            {
                if (!byPoc.TryGetValue(picture.Poc, out var left))
                    continue;
                if (!byPoc.TryGetValue(picture.Poc + 1, out var right))
                    continue;

                if (dumpDir != null && pairs < 3)
                {
                    var sbs = SbsComposer.Compose(left, right, (int)codedWidth, (int)codedHeight, width, height);
                    string name = Path.Combine(dumpDir, $"sbs{pairs:D3}.bmp");
                    BmpWriter.Save(sbs, width * 2, height, name);
                    Console.WriteLine($"  wrote {name}");
                }
                pairs++;
            }

            Console.WriteLine($"  composed {pairs} side-by-side frames at {width * 2}x{height}");
            return pairs > 0 ? 0 : 2;
        }

        /// <summary>Re-parses a rewritten elementary stream, to catch structural errors without a decoder.</summary>
        private static int Verify(string[] args)
        {
            string path = args.Length > 1 ? args[1]
                : Path.Combine(Path.GetTempPath(), "spatial_singlelayer.h265");
            int show = args.Length > 2 ? int.Parse(args[2]) : 12;

            var data = File.ReadAllBytes(path);
            var nalus = SplitAnnexB(data);
            Console.WriteLine($"{path}: {nalus.Count} NAL units");

            var parser = new MvHevcParser();
            int failures = 0;

            for (int i = 0; i < nalus.Count; i++)
            {
                var nalu = new Nalu { Data = nalus[i] };
                string tag = $"  [{i,3}] t={nalu.Type,-2} L{nalu.LayerId} {nalus[i].Length,8}B";
                try
                {
                    if (nalu.Type >= 32 && nalu.Type <= 34)
                    {
                        parser.ParseParameterSets(new[] { nalus[i] });
                        if (i < show) Console.WriteLine($"{tag} parameter set OK");
                        continue;
                    }

                    var parsed = parser.ParseSlice(nalu);
                    var header = parsed.Header;
                    var rps = header.StRefPicSet;
                    string refs = "";
                    if (rps != null)
                    {
                        long acc = 0;
                        for (int k = 0; k < (int)rps.NumNegativePics; k++)
                        {
                            acc -= (long)(rps.DeltaPocS0Minus1[k] + 1);
                            refs += $" {acc}{(rps.UsedByCurrPicS0Flag[k] != 0 ? "*" : "")}";
                        }
                        acc = 0;
                        for (int k = 0; k < (int)rps.NumPositivePics; k++)
                        {
                            acc += (long)(rps.DeltaPocS1Minus1[k] + 1);
                            refs += $" +{acc}{(rps.UsedByCurrPicS1Flag[k] != 0 ? "*" : "")}";
                        }
                    }
                    string lt = header.NumLongTermPics > 0 ? $" lt=[{string.Join(",", header.PocLsbLt)}]" : "";

                    // A header that desynchronised almost always shows up as an illegal merge
                    // candidate count or a nonsense quantiser delta.
                    bool sane = header.FiveMinusMaxNumMergeCand <= 4 &&
                                header.SliceQpDelta > -40 && header.SliceQpDelta < 40;
                    if (!sane) failures++;

                    if (i < show || !sane)
                        Console.WriteLine($"{tag} poc_lsb={header.SlicePicOrderCntLsb,4} type={header.SliceType} " +
                            $"rps=[{refs}]{lt} merge={5 - (int)header.FiveMinusMaxNumMergeCand} " +
                            $"qpd={header.SliceQpDelta} {(sane ? "" : "<<< SUSPECT")}");
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.WriteLine($"{tag} PARSE FAILED: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Console.WriteLine($"  suspect/failed NAL units: {failures}");
            return failures == 0 ? 0 : 2;
        }

        private static List<byte[]> SplitAnnexB(byte[] data)
        {
            var starts = new List<int>();
            for (int i = 0; i + 3 < data.Length; i++)
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
                    starts.Add(i + 4);

            var result = new List<byte[]>();
            for (int i = 0; i < starts.Count; i++)
            {
                int end = i + 1 < starts.Count ? starts[i + 1] - 4 : data.Length;
                var nalu = new byte[end - starts[i]];
                Buffer.BlockCopy(data, starts[i], nalu, 0, nalu.Length);
                result.Add(nalu);
            }
            return result;
        }

        private static int Decode(string[] args)
        {
            string path = args.Length > 1 ? args[1] : @"C:\Temp\IMG_7881.MOV";
            int limit = args.Length > 2 ? int.Parse(args[2]) : 24;
            string dumpDir = args.Length > 3 && args[3] != "-" ? args[3] : null;
            // orig = untouched base view, psonly = new parameter sets + untouched base view,
            // full = the complete rewrite.
            string mode = args.Length > 4 ? args[4] : "full";

            SharpMediaFoundationInterop.Log.SinkError = (m, ex) => Console.WriteLine($"  [mf error] {m}");
            SharpMediaFoundationInterop.Log.SinkWarn = (m, ex) => Console.WriteLine($"  [mf warn ] {m}");

            var track = MvHevcReader.Read(path);
            var rewriter = new SingleLayerRewriter(track);
            rewriter.Plan(baseViewOnly: mode == "v0");

            int dpbSize = Math.Max(rewriter.MaxDpbOccupancy + 1, 8);
            var parameterSets = mode == "orig"
                ? new List<byte[]>(track.BaseParameterSets)
                : rewriter.BuildParameterSets(dpbSize, rewriter.MaxReorder);
            Console.WriteLine($"  mode={mode} dpb={dpbSize} reorder={rewriter.MaxReorder} " +
                $"retained={rewriter.MaxRetained} occupancy={rewriter.MaxDpbOccupancy}");
            Console.WriteLine($"  rewritten parameter sets: {parameterSets.Count} " +
                $"({string.Join(", ", parameterSets.ConvertAll(p => $"t={(p[0] >> 1) & 0x3F}:{p.Length}B"))})");

            var pictures = rewriter.Pictures;
            int count = Math.Min(limit, pictures.Count);

            // Dump the rewritten elementary stream so it can be inspected independently.
            string elementary = Path.Combine(Path.GetTempPath(), "spatial_singlelayer.h265");
            using (var output = File.Create(elementary))
            {
                var bytes = SpatialDecoder.ToAnnexB(parameterSets);
                output.Write(bytes, 0, bytes.Length);
                for (int i = 0; i < count; i++)
                {
                    var nalu = rewriter.RewriteSlice(pictures[i]);
                    bytes = SpatialDecoder.ToAnnexB(new[] { nalu });
                    output.Write(bytes, 0, bytes.Length);
                }
            }
            Console.WriteLine($"  wrote {elementary}");

            uint codedWidth = 1920, codedHeight = 1088;
            var sps = rewriter.ParserContext.SeqParameterSets[0];
            codedWidth = (uint)sps.PicWidthInLumaSamples;
            codedHeight = (uint)sps.PicHeightInLumaSamples;

            using var decoder = new SpatialDecoder(codedWidth, codedHeight, track.FpsNom, track.FpsDenom);
            decoder.SendParameterSets(parameterSets);

            var decoded = new List<DecodedFrame>();
            long half = 10_000_000L * track.FpsDenom / track.FpsNom / 2;

            for (int i = 0; i < count; i++)
            {
                var picture = pictures[i];
                // In the bisecting modes only the untouched base view is fed, so any failure is
                // attributable to the parameter sets rather than the slice rewrite.
                if (mode != "full" && mode != "v0")
                {
                    if (picture.View != 0)
                        continue;
                    foreach (var frame in decoder.Decode(picture.Source.Nalu.Data, picture.Poc * half))
                        decoded.Add(frame);
                    continue;
                }

                var nalu = rewriter.RewriteSlice(picture);
                foreach (var frame in decoder.Decode(nalu, picture.Poc * half))
                    decoded.Add(frame);
            }
            foreach (var frame in decoder.Flush())
                decoded.Add(frame);

            Console.WriteLine($"  fed {count} pictures -> decoded {decoded.Count} frames " +
                $"(rejected inputs: {decoder.RejectedInputs})");

            if (dumpDir != null && decoded.Count > 0)
            {
                Directory.CreateDirectory(dumpDir);
                int dump = Math.Min(4, decoded.Count);
                for (int i = 0; i < dump; i++)
                {
                    // Output is in picture order, so even indices are the left eye.
                    string name = Path.Combine(dumpDir, $"frame{i:D3}_{(i % 2 == 0 ? "left" : "right")}.bmp");
                    BmpWriter.Save(decoded[i].Nv12, (int)codedWidth, (int)codedHeight, name);
                    Console.WriteLine($"  wrote {name}");
                }
            }

            return decoded.Count > 0 ? 0 : 2;
        }

        private static int Plan(string[] args)
        {
            string path = args.Length > 1 ? args[1] : @"C:\Temp\IMG_7881.MOV";
            int show = args.Length > 2 ? int.Parse(args[2]) : 12;

            var track = MvHevcReader.Read(path);
            var rewriter = new SingleLayerRewriter(track);
            rewriter.Plan();

            Console.WriteLine($"{path}");
            Console.WriteLine($"  {track.AccessUnits.Count} access units -> {rewriter.Pictures.Count} single-layer pictures");
            Console.WriteLine($"  max retained references: {rewriter.MaxRetained}");
            Console.WriteLine($"  max dpb occupancy: {rewriter.MaxDpbOccupancy}, reorder depth: {rewriter.MaxReorder}");
            Console.WriteLine($"  pictures using temporal MVP: {rewriter.TemporalMvpPictures}");
            Console.WriteLine();
            Console.WriteLine("  idx view  poc  type  used                 retained");

            for (int i = 0; i < Math.Min(show, rewriter.Pictures.Count); i++)
            {
                var picture = rewriter.Pictures[i];
                string used = string.Join(",", picture.UsedShortTerm);
                if (picture.CrossViewPoc >= 0)
                    used += (used.Length > 0 ? " " : "") + $"lt:{picture.CrossViewPoc}";
                Console.WriteLine($"  {i,3}   V{picture.View}  {picture.Poc,4}  {NalName(picture.OutputNalType),-5} " +
                    $"{used,-20} [{string.Join(",", picture.Retained)}]");
            }

            return 0;
        }

        private static string Refs(ParsedSlice slice)
        {
            var header = slice.Header;
            var rps = header.StRefPicSet;
            var parts = new System.Text.StringBuilder();

            if (rps != null)
            {
                long acc = 0;
                for (int i = 0; i < (int)rps.NumNegativePics; i++)
                {
                    acc -= (long)(rps.DeltaPocS0Minus1[i] + 1);
                    parts.Append($" {acc}{(rps.UsedByCurrPicS0Flag[i] != 0 ? "*" : "")}");
                }
                acc = 0;
                for (int i = 0; i < (int)rps.NumPositivePics; i++)
                {
                    acc += (long)(rps.DeltaPocS1Minus1[i] + 1);
                    parts.Append($" +{acc}{(rps.UsedByCurrPicS1Flag[i] != 0 ? "*" : "")}");
                }
            }

            var mod = header.RefPicListsModification;
            string modText = mod != null && mod.RefPicListModificationFlagL0 != 0
                ? $" mod_l0=[{string.Join(",", mod.ListEntryL0)}]"
                : "";

            return $"rps=[{parts}]{modText}";
        }

        private static string NalName(uint type) => type switch
        {
            0 => "TR_N",
            1 => "TR_R",
            8 => "RASL",
            9 => "RASL",
            19 => "IDR",
            20 => "IDR",
            21 => "CRA",
            _ => type.ToString(),
        };
    }
}
