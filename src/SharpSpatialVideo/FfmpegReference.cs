using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SharpSpatialVideo
{
    /// <summary>
    /// Produces the reference the rewritten stream is checked against, by decoding each view of the
    /// original MV-HEVC with ffmpeg. Nothing else on Windows decodes both views, so this is the only
    /// independent answer to what the pictures should contain - the comparison itself lives in the
    /// 'truth' command.
    /// </summary>
    internal static class FfmpegReference
    {
        /// <summary>
        /// Finds an ffmpeg to use: the SHARPSPATIAL_FFMPEG environment variable first, then PATH,
        /// then the build this was developed against. Returns null when there is none, which is not
        /// an error - the harness is skipped rather than failed.
        /// </summary>
        public static string Locate()
        {
            var candidates = new List<string>();

            string configured = Environment.GetEnvironmentVariable("SHARPSPATIAL_FFMPEG");
            if (!string.IsNullOrEmpty(configured))
                candidates.Add(Directory.Exists(configured) ? Path.Combine(configured, "ffmpeg.exe") : configured);

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
                if (!string.IsNullOrWhiteSpace(dir))
                    candidates.Add(Path.Combine(dir.Trim(), "ffmpeg.exe"));

            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "ffmpeg-master-latest-winarm64-gpl", "bin", "ffmpeg.exe"));

            foreach (var candidate in candidates)
            {
                try { if (File.Exists(candidate)) return candidate; }
                catch (ArgumentException) { }   // a malformed PATH entry is not worth failing over
            }

            return null;
        }

        /// <summary>
        /// Writes v0.md5 and v1.md5 into <paramref name="truthDir"/>, one per view, unless they are
        /// already there. Each holds a per frame MD5 of the decoded picture in yuv420p.
        /// </summary>
        /// <returns>True when both files are present afterwards.</returns>
        public static bool Ensure(string input, string truthDir, bool force, out string reason)
        {
            bool haveBoth = File.Exists(Path.Combine(truthDir, "v0.md5"))
                && File.Exists(Path.Combine(truthDir, "v1.md5"));
            if (haveBoth && !force)
            {
                reason = "already generated";
                return true;
            }

            if (!File.Exists(input))
            {
                reason = $"{input} not found";
                return false;
            }

            string ffmpeg = Locate();
            if (ffmpeg == null)
            {
                reason = "no ffmpeg found; set SHARPSPATIAL_FFMPEG to one built with MV-HEVC support";
                return false;
            }

            Directory.CreateDirectory(truthDir);

            for (int viewId = 0; viewId <= 1; viewId++)
            {
                string output = Path.Combine(truthDir, $"v{viewId}.md5");

                // -view_ids picks the single view to decode, and is what an ffmpeg without MV-HEVC
                // support rejects. yuv420p matters: -pix_fmt gray silently converts limited range
                // to full, and every frame then differs by a few levels for no reason.
                int exitCode = Run(ffmpeg,
                    $"-y -v error -view_ids {viewId} -i \"{input}\" -pix_fmt yuv420p -f framemd5 \"{output}\"",
                    out string errors);

                if (exitCode != 0)
                {
                    reason = $"ffmpeg failed on view {viewId}: {FirstLine(errors)}";
                    return false;
                }
            }

            reason = $"generated with {ffmpeg}";
            return true;
        }

        private static int Run(string executable, string arguments, out string errors)
        {
            var info = new ProcessStartInfo(executable, arguments)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(info);
            var captured = new StringBuilder();
            captured.Append(process.StandardError.ReadToEnd());
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            errors = captured.ToString();
            return process.ExitCode;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "no output";

            foreach (var line in text.Split('\n'))
                if (line.Trim().Length > 0)
                    return line.Trim();

            return "no output";
        }
    }
}
