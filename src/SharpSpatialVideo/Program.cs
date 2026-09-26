using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>
    /// Converts Apple spatial video (MV-HEVC) to and from the two shapes an ordinary tool can
    /// handle: one file per eye, or one file with the eyes side by side.
    ///
    ///   split    spatial video out to one file per eye, without decoding a single picture
    ///   mvmux    the two eyes back into spatial video, equally untouched
    ///   sbs      spatial video out to a side by side file, which has to be re-encoded
    ///   sbs2mv   a side by side file back into spatial video, likewise
    ///
    /// The lossless pair is exact: what comes back out of mvmux is what went into split, byte for
    /// byte. The side by side pair is not, and cannot be - the two eyes share one picture there,
    /// so both directions decode and encode.
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// Which eye the MV-HEVC base layer carries. Measured from the disparity between the
        /// decoded views rather than taken from the file: a feature sits further right in the left
        /// eye, and in Apple's footage it sits further right in the dependent view. The stri box
        /// claims otherwise, and split reports both so they can be compared.
        /// </summary>
        private const bool BaseLayerIsLeftEye = false;

        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  SharpSpatialVideo split  <spatial.mov> [outDir]");
                Console.WriteLine("        one MP4 per eye, without decoding");
                Console.WriteLine("  SharpSpatialVideo mvmux  <left.mp4> <right.mp4> <out.mov> [template=spatial.MOV]");
                Console.WriteLine("        the two eyes back into spatial video, without decoding");
                Console.WriteLine("  SharpSpatialVideo sbs    <spatial.mov> [out.mp4] [bitrate] [frames] [rc=qp:22]");
                Console.WriteLine("        one side by side MP4, re-encoded");
                Console.WriteLine("  SharpSpatialVideo sbs2mv <sbs.mp4> <out-stem> [bitrate] [frames] [rc=qp:26] [template=spatial.MOV]");
                Console.WriteLine("        a side by side MP4 back into spatial video, re-encoded");
                return 1;
            }

            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "split":
                        return Split(args);

                    case "mvmux":
                        return MvMux(args);

                    case "sbs":
                        return Sbs(args);

                    case "sbs2mv":
                        return Sbs2Mv(args);

                    default:
                        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                        return 1;
                }
            }
            catch (NotSupportedException unsupported)
            {
                // A file this cannot handle, which is said plainly; anything else is a fault, and
                // keeps its stack trace.
                Console.Error.WriteLine(unsupported.Message);
                return 3;
            }
        }

        /// <summary>
        /// What the stereo pair is described with. Built in, unless "template=&lt;file&gt;" names a
        /// spatial video to take it from instead - say one from a camera that writes it
        /// differently. See <see cref="MultiviewTemplate"/>.
        /// </summary>
        /// <summary>
        /// Makes the folder an output goes in, if it is not there. The file is only opened once the
        /// work is under way, and a long conversion should not fall over for want of a folder.
        /// </summary>
        private static void CreateFolderOf(string outputPath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
        }

        private static List<byte[]> Template(string[] args)
        {
            var named = args.FirstOrDefault(a => a.StartsWith("template="));
            return named == null ? null : MultiviewTemplate.ParameterSets(named.Substring("template=".Length));
        }

        /// <summary>One MP4 per eye - see <see cref="EyeSplitter"/>.</summary>
        private static int Split(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: SharpSpatialVideo split <spatial.mov> [outDir]");
                return 1;
            }

            string path = args[1];
            string outDir = args.Length > 2 ? args[2] : Path.GetDirectoryName(Path.GetFullPath(path));

            var result = EyeSplitter.Write(path, outDir, BaseLayerIsLeftEye);

            Console.WriteLine($"  stri box: {EyeSplitter.StereoViewInfo(MvHevcReader.Read(path, loadSamples: false))}");
            Console.WriteLine($"  base layer treated as the {result.BaseEye} eye, dependent as the {result.DependentEye}");
            Console.WriteLine($"  wrote {result.BasePath} ({result.BasePictures} pictures, base view verbatim)");
            Console.WriteLine($"  wrote {result.DependentPath} ({result.DependentPictures} pictures, " +
                $"{result.DependentPictures - result.BasePictures} of them reference-only)");
            return 0;
        }

        /// <summary>
        /// The reverse of split: one ordinary HEVC file per eye in, one spatial video out, without
        /// touching a pixel.
        /// </summary>
        private static int MvMux(string[] args)
        {
            if (args.Length < 4)
            {
                Console.Error.WriteLine("Usage: SharpSpatialVideo mvmux <left.mp4> <right.mp4> <out.mov> [template=spatial.MOV]");
                return 1;
            }

            string leftPath = args[1];
            string rightPath = args[2];
            string outputPath = args[3];
            CreateFolderOf(outputPath);

            // A pair that came from the split carries what the split had to rewrite, and goes back
            // together exactly. Any other pair - two eyes out of an editor, say - is built into a
            // new spatial video instead.
            string basePath = BaseLayerIsLeftEye ? leftPath : rightPath;
            string dependentPath = BaseLayerIsLeftEye ? rightPath : leftPath;
            bool fromSplit = SpatialVideoRebuilder.CanRebuild(dependentPath);

            var result = fromSplit
                ? SpatialVideoRebuilder.Write(basePath, dependentPath, outputPath, new StereoMetadata())
                : LeftRightTranscoder.Write(leftPath, rightPath, outputPath, new StereoMetadata(), Template(args));

            Console.WriteLine(fromSplit
                ? "  put back from what the split carried, so this is the file it was split from"
                : "  built from two single view files");

            Console.WriteLine($"  wrote {result.Path}: {result.AccessUnits} access units, " +
                $"{result.Bytes / (1024 * 1024)} MB");
            return 0;
        }

        /// <summary>Decodes both views, composes them side by side and re-encodes to one MP4.</summary>
        private static int Sbs(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: SharpSpatialVideo sbs <spatial.mov> [out.mp4] [bitrate] [frames] [rc=qp:22]");
                return 1;
            }

            string path = args[1];
            string outPath = args.Length > 2 ? args[2]
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)),
                    Path.GetFileNameWithoutExtension(path) + "_sbs.mp4");
            uint bitrate = args.Length > 3 ? uint.Parse(args[3]) : 30_000_000;
            int limit = args.Length > 4 ? int.Parse(args[4]) : int.MaxValue;

            SharpMediaFoundationInterop.Log.SinkError = (m, ex) => Console.WriteLine($"  [mf error] {m}");

            // Only the index is read: the rewriter reads each picture out of the file as it is
            // decoded, so a recording of any length costs the same.
            var track = MvHevcReader.Read(path, loadSamples: false);
            using var rewriter = new SingleLayerRewriter(track);
            rewriter.Plan();

            var converter = new SbsConverter(track, rewriter);
            var rateControl = args.FirstOrDefault(a => a.StartsWith("rc="));
            if (rateControl != null)
                converter.RateControl = rateControl.Substring("rc=".Length);
            CreateFolderOf(outPath);
            converter.Convert(outPath, bitrate, BaseLayerIsLeftEye, limit);

            Console.WriteLine($"  wrote {outPath}: {converter.FramesWritten} frames at " +
                $"{track.DisplayWidth * 2}x{track.DisplayHeight}, rate control {converter.RateControl}, " +
                $"{converter.AudioSamplesWritten} audio samples");
            return converter.FramesWritten > 0 ? 0 : 2;
        }

        /// <summary>
        /// Side by side in, spatial video out, with the two views coded independently.
        /// </summary>
        private static int Sbs2Mv(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: SharpSpatialVideo sbs2mv <sbs.mp4> <out-stem> " +
                    "[bitrate] [frames] [rc=qp:26] [threads=N] [dthreads=N] [template=spatial.MOV] [dump] [mem]");
                return 1;
            }

            string sourcePath = args[1];
            string stem = args[2];
            uint bitrate = args.Length > 3 ? uint.Parse(args[3]) : 40_000_000;
            int limit = args.Length > 4 ? int.Parse(args[4]) : int.MaxValue;

            SharpMediaFoundationInterop.Log.SinkError = (m, ex) => Console.WriteLine($"  [mf error] {m}");

            CreateFolderOf(stem);

            var converter = new SbsToMultiview(Template(args))
            {
                DumpViews = args.Contains("dump"),
                ReportMemory = args.Contains("mem"),
            };

            var threads = args.FirstOrDefault(a => a.StartsWith("threads="));
            if (threads != null)
                converter.EncoderThreads = uint.Parse(threads.Substring("threads=".Length));
            var decoderThreads = args.FirstOrDefault(a => a.StartsWith("dthreads="));
            if (decoderThreads != null)
                converter.DecoderThreads = uint.Parse(decoderThreads.Substring("dthreads=".Length));
            var rateControl = args.FirstOrDefault(a => a.StartsWith("rc="));
            if (rateControl != null)
                converter.RateControl = rateControl.Substring("rc=".Length);

            var results = converter.Write(sourcePath, stem, bitrate, limit);

            foreach (var result in results)
                Console.WriteLine($"  wrote {result.Path}: {result.AccessUnits} access units, " +
                    $"{result.Bytes / (1024 * 1024)} MB " +
                    $"(base {result.BaseBytes / 1024} KB, dependent {result.DependentBytes / 1024} KB, " +
                    $"dependent is {100.0 * result.DependentBytes / Math.Max(1, result.BaseBytes):F0}% of base)");

            return 0;
        }
    }
}
