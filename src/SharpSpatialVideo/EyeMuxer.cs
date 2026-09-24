using SharpMP4.Builders;
using SharpMP4.Tracks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>One picture ready to be written to an MP4 sample.</summary>
    public sealed class MuxPicture
    {
        public byte[] Nalu { get; set; }
        public int Poc { get; set; }
        public bool IsRandomAccessPoint { get; set; }

        /// <summary>Decode duration in track timescale units.</summary>
        public int Duration { get; set; }
    }

    /// <summary>
    /// Writes HEVC pictures into an MP4 without re-encoding. Pictures are stored in decode order
    /// and their composition times come from the picture order count, so the reordering that the
    /// B pictures rely on survives into the container.
    /// </summary>
    public static class EyeMuxer
    {
        public static void Write(
            string path,
            IEnumerable<byte[]> parameterSets,
            IReadOnlyList<MuxPicture> pictures,
            uint timescale,
            int pocUnitTicks,
            MvHevcTrack audioSource = null)
        {
            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            var builder = new Mp4Builder(new SingleStreamOutput(output));

            var defaultDuration = pictures.Count > 0 ? pictures[0].Duration : 1;
            var track = new H265Track(timescale, defaultDuration);
            builder.AddTrack(track);

            // Audio is unrelated to the views, so it is copied across untouched.
            AACTrack audioTrack = null;
            if (audioSource != null && audioSource.HasAudio)
            {
                audioTrack = new AACTrack(audioSource.AudioConfig, audioSource.AudioTimescale);
                builder.AddTrack(audioTrack);
            }

            // Parameter sets are collected into the sample entry rather than emitted as samples.
            foreach (var parameterSet in parameterSets)
                builder.ProcessTrackSample(track.TrackID, parameterSet, defaultDuration);

            // Samples are written directly instead of through ProcessTrackSample, because that
            // buffers NAL units and only emits a sample once the *next* picture starts - which
            // would attach each picture's composition offset to the one before it.
            int basePoc = pictures.Count > 0 ? pictures.Min(p => p.Poc) : 0;
            long decodeTime = 0;
            for (int i = 0; i < pictures.Count; i++)
            {
                var picture = pictures[i];
                long compositionTime = (long)(picture.Poc - basePoc) * pocUnitTicks;

                builder.ProcessRawSample(
                    track.TrackID,
                    LengthPrefixed(picture.Nalu),
                    picture.Duration,
                    picture.IsRandomAccessPoint,
                    (int)(compositionTime - decodeTime));

                decodeTime += picture.Duration;
            }

            if (audioTrack != null)
            {
                for (int i = 0; i < audioSource.AudioSamples.Count; i++)
                    builder.ProcessRawSample(audioTrack.TrackID, audioSource.AudioSamples[i],
                        (int)audioSource.AudioSampleDurations[i], true);
            }

            builder.FinalizeMedia();
        }

        /// <summary>Wraps a NAL unit in the 4-byte length prefix an MP4 sample uses.</summary>
        private static byte[] LengthPrefixed(byte[] nalu)
        {
            var sample = new byte[nalu.Length + 4];
            sample[0] = (byte)(nalu.Length >> 24);
            sample[1] = (byte)(nalu.Length >> 16);
            sample[2] = (byte)(nalu.Length >> 8);
            sample[3] = (byte)nalu.Length;
            Buffer.BlockCopy(nalu, 0, sample, 4, nalu.Length);
            return sample;
        }
    }
}
