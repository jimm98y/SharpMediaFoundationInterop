using SharpMediaFoundationInterop.Transforms.H265;
using System;
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
            long dts = 0;

            // Each coded picture is written the moment the encoder hands it back, so nothing waits
            // for the end: the encoder returns them in decode order, stamped with their
            // presentation time, and the composition offset of each comes straight from that.
            void Write(int length, long timestamp)
            {
                // The encoder hands its output back in Annex B, one access unit at a time, so it is
                // split where it lies and fed to the track, and the access unit is then flushed out
                // of it. The track holds one back until it sees the next one start, which would put
                // it a sample behind the timing worked out here; flushing keeps the two together. It
                // also takes the parameter sets out into the sample entry as it goes.
                foreach (var nalu in muxTrack.ParseSample(new MemoryStream(encoded, 0, length)))
                    muxTrack.ProcessSample(nalu.Array, nalu.Offset, nalu.Count, out _, out _);

                muxTrack.ProcessSample(null, out var accessUnit, out bool isRandomAccessPoint);
                if (accessUnit.Array == null)
                    return;

                long cts = timestamp * _track.FpsNom / 10_000_000L;
                builder.ProcessRawSample(muxTrack.TrackID, accessUnit,
                    (int)_track.FpsDenom, isRandomAccessPoint, (int)(cts - dts));
                dts += _track.FpsDenom;
            }

            bool DrainEncoder()
            {
                bool any = false;
                while (encoder.ProcessOutput(ref encoded, out uint length, out long timestamp) && length > 0)
                {
                    Write((int)length, timestamp);
                    any = true;
                }
                return any;
            }

            // The two eyes are put side by side in this one picture, straight out of the decoder's
            // own buffer: each half is written as its eye arrives, so no decoded frame is ever held
            // or copied. The encoder copies what it is given, so the picture is free again once it
            // has been fed.
            var composed = new byte[width * 2 * height * 3 / 2];
            int waitingForPartnerOf = -1;
            int frames = 0;

            void Take(byte[] frame, long timestamp)
            {
                int poc = (int)(timestamp / step);

                // The decoder hands frames back in picture order, so a dependent picture arrives
                // just after the base picture it pairs with. One whose partner never came - the
                // decoder can drop a frame - is left out rather than paired with the wrong one.
                if (poc % SingleLayerRewriter.PocScale == 0)
                {
                    SbsComposer.PlaceEye(frame, codedWidth, (int)codedHeight, width, height, composed,
                        rightHalf: !baseLayerIsLeftEye);
                    waitingForPartnerOf = poc;
                    return;
                }

                if (poc - 1 != waitingForPartnerOf)
                    return;

                SbsComposer.PlaceEye(frame, codedWidth, (int)codedHeight, width, height, composed,
                    rightHalf: baseLayerIsLeftEye);
                waitingForPartnerOf = -1;

                encoder.ProcessInput(composed, frames * frameDuration);
                frames++;
                DrainEncoder();
            }

            // Low latency releases each frame as soon as it can rather than holding a full reorder
            // window. It hands back the same frames in the same order - all 760 pairs of the sample
            // come out matched to their eyes - holds 53 MB less, and takes no longer.
            using (var decoder = new SpatialDecoder((uint)codedWidth, codedHeight, _track.FpsNom, _track.FpsDenom,
                lowLatency: true))
            {
                decoder.SendParameterSets(parameterSets);
                foreach (var picture in pictures.Take(count))
                    decoder.DecodeInto(new[] { _rewriter.RewriteSlice(picture) }, picture.Poc * step, Take);
                decoder.FlushInto(Take);
            }

            encoder.BeginDrain();
            for (int quiet = 0; quiet < 4;)
                quiet = DrainEncoder() ? 0 : quiet + 1;
            encoder.EndDrain();

            if (audioTrack != null)
            {
                foreach (var (data, duration) in MvHevcReader.StreamAudioSamples(_track))
                {
                    builder.ProcessRawSample(audioTrack.TrackID, data, duration, true);
                    AudioSamplesWritten++;
                }
            }

            builder.FinalizeMedia();
            FramesWritten = frames;
        }
    }
}
