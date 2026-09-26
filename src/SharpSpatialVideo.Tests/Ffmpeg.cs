using System.Diagnostics;
using System.Text;

namespace SharpSpatialVideo.Tests;

/// <summary>
/// Runs ffmpeg, which is the only independent answer on Windows to what a spatial video's two
/// views should contain: nothing else here decodes both of them. What the conversions produce is
/// checked against it rather than against themselves.
/// </summary>
internal static class Ffmpeg
{
    /// <summary>
    /// An ffmpeg to use: the SHARPSPATIAL_FFMPEG environment variable first, then PATH, then the
    /// build this was developed against. Null when there is none, which skips the tests that need
    /// it rather than failing them.
    /// </summary>
    public static string Locate()
    {
        var candidates = new List<string>();

        string configured = Environment.GetEnvironmentVariable("SHARPSPATIAL_FFMPEG");
        if (!string.IsNullOrEmpty(configured))
            candidates.Add(Directory.Exists(configured) ? Path.Combine(configured, "ffmpeg.exe") : configured);

        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(directory))
                candidates.Add(Path.Combine(directory.Trim(), "ffmpeg.exe"));

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "ffmpeg-master-latest-winarm64-gpl", "bin", "ffmpeg.exe"));

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // a malformed PATH entry is not worth failing over
            }
        }

        return null;
    }

    /// <summary>
    /// A hash of each decoded picture. The view is chosen with -view_ids, which is what an ffmpeg
    /// without MV-HEVC support rejects; for an ordinary file it is left out. yuv420p matters:
    /// -pix_fmt gray silently converts limited range to full, and every frame then differs by a
    /// few levels for no reason.
    /// </summary>
    public static List<string> FrameHashes(string input, int? viewId = null, int? frames = null)
    {
        string view = viewId.HasValue ? $"-view_ids {viewId} " : "";
        string limit = frames.HasValue ? $"-frames:v {frames} " : "";
        string output = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.framemd5");

        try
        {
            Run($"-y -v error {view}-i \"{input}\" {limit}-map 0:v:0 -pix_fmt yuv420p -f framemd5 \"{output}\"");

            return File.ReadAllLines(output)
                .Where(line => !line.StartsWith("#") && line.Length > 0)
                .Select(line => line.Split(',').Last().Trim())
                .ToList();
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
        }
    }

    /// <summary>
    /// How close two sets of pictures are, in dB. The frames are paired by index rather than by
    /// timestamp - a recording's timing is its own business, and two files that hold the same
    /// pictures may still stamp them differently.
    /// </summary>
    public static double Psnr(string first, string second, string firstFilter = null,
        string secondFilter = null, int? viewId = null, int? frames = null)
    {
        string view = viewId.HasValue ? $"-view_ids {viewId} " : "";
        string trim = frames.HasValue ? $",trim=end_frame={frames}" : "";
        string a = $"[0:v:0]{Prefix(firstFilter)}setpts=N/TB{trim}[a];";
        string b = $"[1:v:0]{Prefix(secondFilter)}setpts=N/TB{trim}[b];";

        string output = Run($"-v info {view}-i \"{first}\" -i \"{second}\" " +
            $"-filter_complex \"{a}{b}[a][b]psnr\" -f null -");

        // The line ends with: PSNR y:.. u:.. v:.. average:37.01 min:.. max:..
        var match = System.Text.RegularExpressions.Regex.Match(output, @"average:([0-9.]+)");
        Assert.IsTrue(match.Success, $"ffmpeg reported no PSNR:\n{output}");
        return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Encodes one view of a spatial video as an ordinary HEVC file - what an editor would hand
    /// back after working on one eye.
    /// </summary>
    /// <param name="slices">How many slices each picture is coded as.</param>
    public static void EncodeView(string input, int viewId, string output, int frames, int slices = 1)
    {
        Run($"-y -v error -view_ids {viewId} -i \"{input}\" -frames:v {frames} " +
            $"-c:v libx265 -preset ultrafast -x265-params log-level=none:keyint=30:min-keyint=30:slices={slices} " +
            $"-tag:v hvc1 \"{output}\"");
    }

    private static string Prefix(string filter) => string.IsNullOrEmpty(filter) ? "" : filter + ",";

    private static string Run(string arguments)
    {
        string ffmpeg = Locate();
        Assert.IsNotNull(ffmpeg, "no ffmpeg found");

        var info = new ProcessStartInfo(ffmpeg, arguments)
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

        Assert.AreEqual(0, process.ExitCode,
            $"ffmpeg {arguments}\n{captured}");

        return captured.ToString();
    }
}
