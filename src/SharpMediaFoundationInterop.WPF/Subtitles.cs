using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>A track of subtitles a source has: what a viewer chooses it by.</summary>
    public sealed class SubtitleTrackInfo
    {
        public SubtitleTrackInfo(string name, string language, bool forced)
        {
            Name = name;
            Language = string.IsNullOrEmpty(language) || language == "und" ? null : language;
            Forced = forced;
        }

        /// <summary>What it is called: the track's name, or the file it is of.</summary>
        public string Name { get; }

        /// <summary>Its language, as the source says it - an ISO 639 code - or null where it does not.</summary>
        public string Language { get; }

        /// <summary>Whether it is shown whether subtitles are on or not: a translation of what is said in another language.</summary>
        public bool Forced { get; }

        public override string ToString()
        {
            string name = Name;
            if (Language != null && (name == null || !name.Contains(Language, StringComparison.OrdinalIgnoreCase)))
                name = name == null ? Language : $"{name} ({Language})";
            return Forced ? $"{name ?? "Subtitles"}, forced" : name ?? "Subtitles";
        }
    }

    /// <summary>A subtitle: its text, shown from <see cref="Start"/> until <see cref="End"/>, on the clock of the video's frames.</summary>
    public readonly struct Subtitle
    {
        public Subtitle(long start, long end, string text)
        {
            Start = start;
            End = end;
            Text = text;
        }

        /// <summary>When it is shown, in 100 ns units, of the clock the source's video frames are timed by.</summary>
        public long Start { get; }

        /// <summary>When it is no longer shown.</summary>
        public long End { get; }

        /// <summary>Its text, plain: lines apart by line breaks.</summary>
        public string Text { get; }

        public override string ToString() => $"{TimeSpan.FromTicks(Start)}-{TimeSpan.FromTicks(End)}: {Text}";
    }

    /// <summary>A source with subtitles: of a file, its subtitle tracks, and files of subtitles beside it.</summary>
    public interface ISubtitleSource
    {
        /// <summary>The tracks of subtitles there are to choose from, once the source is initialized.</summary>
        IReadOnlyList<SubtitleTrackInfo> SubtitleTracks { get; }

        /// <summary>The subtitles of a track, in the order they are shown.</summary>
        IReadOnlyList<Subtitle> GetSubtitles(int track);
    }

    /// <summary>
    /// Subtitles read from text: SubRip (.srt) and WebVTT (.vtt) files, and TTML documents; and the text of a cue made plain,
    /// its markup - WebVTT's and SubRip's tags, entities, SSA override codes - taken out.
    /// </summary>
    public static class SubtitleParser
    {
        // 00:01:02,345 or 00:01:02.345, the hours left out of a WebVTT time where they are 0
        private static readonly Regex Timing = new Regex(
            @"^\s*(?:(\d+):)?(\d{1,2}):(\d{1,2})[,.](\d{1,3})\s*-->\s*(?:(\d+):)?(\d{1,2}):(\d{1,2})[,.](\d{1,3})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex Tags = new Regex(@"<[^>]*>|\{\\[^}]*\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// The cues of a SubRip or WebVTT file: each a line of its times, then its text up to a blank line; WebVTT's header,
        /// notes, styles and regions passed over. The text made plain - see <see cref="ToPlainText"/>.
        /// </summary>
        public static List<Subtitle> ParseSrtOrWebVtt(string text)
        {
            var subtitles = new List<Subtitle>();
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var match = Timing.Match(lines[i]);
                if (!match.Success)
                    continue;

                long start = Time(match, 1), end = Time(match, 5);
                var cue = new StringBuilder();
                for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++)
                {
                    if (cue.Length > 0)
                        cue.Append('\n');
                    cue.Append(lines[i]);
                }
                string plain = ToPlainText(cue.ToString());
                if (plain.Length > 0 && end > start)
                    subtitles.Add(new Subtitle(start, end, plain));
            }
            subtitles.Sort((a, b) => a.Start.CompareTo(b.Start));
            return subtitles;
        }

        private static long Time(Match match, int group)
        {
            long hours = match.Groups[group].Success ? long.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) : 0;
            long minutes = long.Parse(match.Groups[group + 1].Value, CultureInfo.InvariantCulture);
            long seconds = long.Parse(match.Groups[group + 2].Value, CultureInfo.InvariantCulture);
            string fraction = match.Groups[group + 3].Value.PadRight(3, '0');
            long milliseconds = long.Parse(fraction, CultureInfo.InvariantCulture);
            return ((hours * 60 + minutes) * 60 + seconds) * TimeSpan.TicksPerSecond + milliseconds * TimeSpan.TicksPerMillisecond;
        }

        /// <summary>
        /// A cue's text made plain: tags - WebVTT's &lt;v Speaker&gt;, &lt;i&gt;, timestamps; SubRip's &lt;i&gt;,
        /// &lt;font&gt; - and SSA override codes such as {\an8} taken out, entities decoded, lines trimmed and blank ones
        /// dropped.
        /// </summary>
        public static string ToPlainText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            text = Tags.Replace(text, string.Empty);
            text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
            var lines = text.Replace("\r\n", "\n").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0);
            return string.Join("\n", lines);
        }

        private static readonly XNamespace TtmlParameter = "http://www.w3.org/ns/ttml#parameter";

        /// <summary>
        /// The subtitles of a TTML document - each paragraph with its own times, or, without, the times of the sample the
        /// document is - as an MP4 track carries them, a document a sample, shown from <paramref name="sampleStart"/> to
        /// <paramref name="sampleEnd"/>. A paragraph's times are taken as the track's, or where they fall outside the
        /// sample, as of its start.
        /// </summary>
        public static IEnumerable<Subtitle> ParseTtml(string document, long sampleStart, long sampleEnd)
        {
            XDocument xml;
            try
            {
                xml = XDocument.Parse(document);
            }
            catch (System.Xml.XmlException)
            {
                yield break;
            }

            var root = xml.Root;
            double tickRate = ParseDouble(root?.Attribute(TtmlParameter + "tickRate")?.Value, 0);
            double frameRate = ParseDouble(root?.Attribute(TtmlParameter + "frameRate")?.Value, 30);
            if (tickRate <= 0)
                tickRate = frameRate > 0 ? frameRate : 1;

            foreach (var paragraph in xml.Descendants().Where(e => e.Name.LocalName == "p"))
            {
                string text = ToPlainText(TextOf(paragraph));
                if (text.Length == 0)
                    continue;

                long? begin = ParseTtmlTime(Attribute(paragraph, "begin"), tickRate, frameRate);
                long? end = ParseTtmlTime(Attribute(paragraph, "end"), tickRate, frameRate);
                long? duration = ParseTtmlTime(Attribute(paragraph, "dur"), tickRate, frameRate);
                long start = begin ?? 0, stop = end ?? (duration != null ? start + duration.Value : long.MaxValue);
                if (begin == null && end == null && duration == null)
                {
                    start = sampleStart;
                    stop = sampleEnd;
                }
                else if (start < sampleStart - TimeSpan.TicksPerMillisecond || start > sampleEnd)
                {
                    // of the sample's start
                    start += sampleStart;
                    stop = stop == long.MaxValue ? sampleEnd : stop + sampleStart;
                }
                stop = Math.Min(stop == long.MaxValue ? sampleEnd : stop, sampleEnd);
                if (stop > start)
                    yield return new Subtitle(start, stop, text);
            }
        }

        private static string Attribute(XElement element, string name) =>
            element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

        /// <summary>A paragraph's text, a &lt;br/&gt; a line break; of nested spans, their text.</summary>
        private static string TextOf(XElement element)
        {
            var text = new StringBuilder();
            foreach (var node in element.Nodes())
            {
                if (node is XText value)
                    text.Append(value.Value);
                else if (node is XElement child)
                    text.Append(child.Name.LocalName == "br" ? "\n" : TextOf(child));
            }
            return text.ToString();
        }

        /// <summary>
        /// A TTML time: a clock time, hh:mm:ss, hh:mm:ss.fff or hh:mm:ss:frames; or an offset, a number of h, m, s, ms, f
        /// (frames) or t (ticks).
        /// </summary>
        private static long? ParseTtmlTime(string value, double tickRate, double frameRate)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            value = value.Trim();

            var parts = value.Split(':');
            if (parts.Length >= 3)
            {
                double seconds = ParseDouble(parts[0], 0) * 3600 + ParseDouble(parts[1], 0) * 60 + ParseDouble(parts[2], 0);
                if (parts.Length > 3 && frameRate > 0)
                    seconds += ParseDouble(parts[3], 0) / frameRate;
                return (long)Math.Round(seconds * TimeSpan.TicksPerSecond);
            }

            int unit = value.Length;
            while (unit > 0 && char.IsLetter(value[unit - 1]))
                unit--;
            double number = ParseDouble(value.Substring(0, unit), double.NaN);
            if (double.IsNaN(number))
                return null;
            double factor = value.Substring(unit) switch
            {
                "h" => 3600,
                "m" => 60,
                "s" => 1,
                "ms" => 0.001,
                "f" => frameRate > 0 ? 1 / frameRate : 0,
                "t" => 1 / tickRate,
                _ => double.NaN,
            };
            if (double.IsNaN(factor))
                return null;
            return (long)Math.Round(number * factor * TimeSpan.TicksPerSecond);
        }

        private static double ParseDouble(string value, double fallback) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : fallback;
    }
}
