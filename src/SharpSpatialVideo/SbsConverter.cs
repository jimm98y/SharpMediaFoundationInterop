using SharpMediaFoundationInterop.Transforms.H265;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>
    /// Produces a single side-by-side MP4 from an MV-HEVC source. Unlike the lossless split, this
    /// has to decode and re-encode: putting both eyes in one picture is new pixel data.
    /// </summary>
    public sealed class SbsConverter
    {
        private readonly MvHevcTrack _track;
        private readonly SingleLayerRewriter _rewriter;

        public SbsConverter(MvHevcTrack track, SingleLayerRewriter rewriter)
        {
            _track = track;
            _rewriter = rewriter;
        }

        public int FramesWritten { get; private set; }
        public int AudioSamplesWritten { get; private set; }

        /// <summary>
        /// How the encoder controls its rate - see <see cref="EncoderRateControl"/>. The output is
        /// usually an intermediate that gets encoded again, and whatever this encode adds is
        /// inherited by everything made from it, so it defaults a notch finer than the final one.
        /// </summary>
        public string RateControl { get; set; } = "qp:22";

        public void Convert(string outputPath, uint bitrate, bool baseLayerIsLeftEye, int limit = int.MaxValue)
        {
            var sps = _rewriter.ParserContext.SeqParameterSets[0];
            int codedWidth = (int)sps.PicWidthInLumaSamples;
            uint codedHeight = (uint)sps.PicHeightInLumaSamples;
            int width = (int)_track.DisplayWidth;
            int height = (int)_track.DisplayHeight;

            int dpbSize = Math.Max(_rewriter.MaxDpbOccupancy + 1, 8);
            var parameterSets = _rewriter.BuildParameterSets(dpbSize, _rewriter.MaxReorder);
            long step = 10_000_000L * _track.FpsDenom / _track.FpsNom / SingleLayerRewriter.PocScale;

            var pictures = _rewriter.Pictures;
            int count = Math.Min(limit, pictures.Count);

            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            var builder = new SharpMP4.Builders.Mp4Builder(new SharpMP4.Builders.SingleStreamOutput(output));
            var muxTrack = new SharpMP4.Tracks.H265Track(_track.Timescale, (int)_track.FpsDenom);
            builder.AddTrack(muxTrack);

            // Audio is copied across untouched - it has nothing to do with the views.
            SharpMP4.Tracks.AACTrack audioTrack = null;
            if (_track.HasAudio)
            {
                audioTrack = new SharpMP4.Tracks.AACTrack(_track.AudioConfig, _track.AudioTimescale);
                builder.AddTrack(audioTrack);
            }

            using var encoder = new H265Encoder((uint)(width * 2), (uint)height,
                _track.FpsNom, _track.FpsDenom, bitrate);
            EncoderRateControl.Apply(encoder, RateControl);
            encoder.Initialize();
            EncoderRateControl.ReportRejected(encoder);

            var encoded = new byte[encoder.OutputSize];
            long frameDuration = 10_000_000L * _track.FpsDenom / _track.FpsNom;
            var samples = new List<(long Timestamp, byte[] Data)>();

            // The decoder emits in picture order, so a dependent picture always arrives just after
            // the base picture it pairs with. Composing on the fly keeps one frame in hand rather
            // than the whole clip, which at this resolution would run to gigabytes.
            var pending = new Dictionary<int, byte[]>();
            int composed = 0;

            void Take(DecodedFrame frame)
            {
                int poc = (int)(frame.Timestamp / step);
                pending[poc] = frame.Nv12;

                int basePoc = poc % SingleLayerRewriter.PocScale == 0 ? poc : poc - 1;
                if (!pending.TryGetValue(basePoc, out var baseView)) return;
                if (!pending.TryGetValue(basePoc + 1, out var dependentView)) return;

                var left = baseLayerIsLeftEye ? baseView : dependentView;
                var right = baseLayerIsLeftEye ? dependentView : baseView;
                var composedFrame = SbsComposer.Compose(left, right, codedWidth, (int)codedHeight, width, height);
                pending.Remove(basePoc);
                pending.Remove(basePoc + 1);

                encoder.ProcessInput(composedFrame, composed * frameDuration);
                composed++;

                while (encoder.ProcessOutput(ref encoded, out uint length, out long timestamp) && length > 0)
                    samples.Add((timestamp, encoded.Take((int)length).ToArray()));
            }

            using (var decoder = new SpatialDecoder((uint)codedWidth, codedHeight, _track.FpsNom, _track.FpsDenom))
            {
                decoder.SendParameterSets(parameterSets);
                foreach (var picture in pictures.Take(count))
                    foreach (var frame in decoder.Decode(_rewriter.RewriteSlice(picture), picture.Poc * step))
                        Take(frame);
                foreach (var frame in decoder.Flush())
                    Take(frame);
            }

            encoder.BeginDrain();
            for (int quiet = 0; quiet < 4; )
            {
                bool any = false;
                while (encoder.ProcessOutput(ref encoded, out uint length, out long timestamp) && length > 0)
                {
                    samples.Add((timestamp, encoded.Take((int)length).ToArray()));
                    any = true;
                }
                if (any) quiet = 0; else quiet++;
            }
            encoder.EndDrain();

            // The encoder hands samples back in decode order and stamps each with its presentation
            // time, so the composition offsets come straight from those.
            long dts = 0;
            foreach (var sample in samples)
            {
                foreach (var nalu in AnnexBNalus(sample.Data))
                    muxTrack.ProcessSample(nalu, out _, out _);

                long cts = sample.Timestamp * _track.FpsNom / 10_000_000L;
                builder.ProcessRawSample(muxTrack.TrackID, LengthPrefixed(sample.Data),
                    (int)_track.FpsDenom, IsIrap(sample.Data), (int)(cts - dts));
                dts += _track.FpsDenom;
            }

            if (audioTrack != null)
            {
                for (int i = 0; i < _track.AudioSamples.Count; i++)
                    builder.ProcessRawSample(audioTrack.TrackID, _track.AudioSamples[i],
                        (int)_track.AudioSampleDurations[i], true);
                AudioSamplesWritten = _track.AudioSamples.Count;
            }

            builder.FinalizeMedia();
            FramesWritten = composed;
        }

        private static bool IsIrap(byte[] annexB)
        {
            foreach (var nalu in AnnexBNalus(annexB))
            {
                uint type = (uint)((nalu[0] >> 1) & 0x3F);
                if (type >= 16 && type <= 23) return true;
            }
            return false;
        }

        /// <summary>Splits an Annex B buffer into its NAL units, dropping the start codes.</summary>
        private static IEnumerable<byte[]> AnnexBNalus(byte[] data)
        {
            var starts = new List<int>();
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                    starts.Add(i + 3);
                else if (i + 4 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
                {
                    starts.Add(i + 4);
                    i++;
                }
            }

            for (int i = 0; i < starts.Count; i++)
            {
                int end = i + 1 < starts.Count ? starts[i + 1] : data.Length;
                while (end > starts[i] && end - 1 > 0 && data[end - 1] == 0) end--;
                if (end <= starts[i]) continue;
                var nalu = new byte[end - starts[i]];
                Buffer.BlockCopy(data, starts[i], nalu, 0, nalu.Length);
                yield return nalu;
            }
        }

        /// <summary>Rewrites an Annex B access unit into the length prefixed form an MP4 sample uses.</summary>
        private static byte[] LengthPrefixed(byte[] annexB)
        {
            using var memory = new MemoryStream();
            foreach (var nalu in AnnexBNalus(annexB))
            {
                memory.WriteByte((byte)(nalu.Length >> 24));
                memory.WriteByte((byte)(nalu.Length >> 16));
                memory.WriteByte((byte)(nalu.Length >> 8));
                memory.WriteByte((byte)nalu.Length);
                memory.Write(nalu, 0, nalu.Length);
            }
            return memory.ToArray();
        }
    }
}
