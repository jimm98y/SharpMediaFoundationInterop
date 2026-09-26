using SharpMP4.Builders;
using SharpMP4.Tracks;
using System;
using System.Collections.Generic;
using System.IO;

namespace SharpSpatialVideo
{
    /// <summary>One picture ready to be written to an MP4 sample.</summary>
    public sealed class MuxPicture
    {
        /// <summary>
        /// The coded picture. It may be a slice of a buffer the caller fills again for the next
        /// picture: it is written before the next one is asked for.
        /// </summary>
        public ArraySegment<byte> Nalu { get; set; }

        /// <summary>
        /// NAL units that go in the same sample, in front of the picture - the user data SEIs the
        /// split carries what it had to rewrite in. Empty for everything else.
        /// </summary>
        public List<byte[]> Prefix { get; } = new List<byte[]>();
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
        /// <param name="pictures">
        /// The pictures, taken one at a time and written as they come: a caller is free to read
        /// each from its source only when it is asked for, so a recording of any length costs the
        /// same. What is held is the sample being written.
        /// </param>
        /// <param name="lowestPoc">
        /// The lowest picture order count among them, which composition times are measured from.
        /// It is a parameter because the pictures are not looked through twice. Where a key frame
        /// starts the count again, each sequence is measured from its own lowest count instead.
        /// </param>
        public static void Write(
            string path,
            IEnumerable<byte[]> parameterSets,
            IEnumerable<MuxPicture> pictures,
            uint timescale,
            int pocUnitTicks,
            MvHevcTrack audioSource = null,
            int lowestPoc = 0,
            int defaultDuration = 1)
        {
            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            var builder = new Mp4Builder(new SingleStreamOutput(output));
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
            long decodeTime = 0;

            // An IDR picture starts the picture order count again at zero, so the count alone does
            // not say when a picture is shown: every key frame would send the clock backwards. Each
            // such sequence is therefore placed after the last picture of the one before it.
            long sequenceStart = 0;
            int highestPocInSequence = int.MinValue;
            bool anyWritten = false;

            // Every sample is assembled in this, which grows to the largest of them and is then
            // written over: the builder writes each one out before the next is assembled.
            var sample = new MemoryStream(1 << 20);
            foreach (var picture in pictures)
            {
                if (anyWritten && IsIdr(picture))
                {
                    sequenceStart += highestPocInSequence - lowestPoc + 1;
                    highestPocInSequence = int.MinValue;
                    lowestPoc = picture.Poc;
                }

                highestPocInSequence = Math.Max(highestPocInSequence, picture.Poc);
                anyWritten = true;

                long compositionTime = (sequenceStart + picture.Poc - lowestPoc) * pocUnitTicks;

                builder.ProcessRawSample(
                    track.TrackID,
                    LengthPrefixed(picture, sample),
                    picture.Duration,
                    picture.IsRandomAccessPoint,
                    (int)(compositionTime - decodeTime));

                decodeTime += picture.Duration;
            }

            if (audioTrack != null)
            {
                foreach (var (data, duration) in MvHevcReader.StreamAudioSamples(audioSource))
                    builder.ProcessRawSample(audioTrack.TrackID, data, duration, true);
            }

            builder.FinalizeMedia();
        }

        /// <summary>
        /// Whether this picture starts the picture order count again: only an IDR does, which is
        /// why the random access point flag is not enough - a CRA is one too and carries on counting.
        /// </summary>
        private static bool IsIdr(MuxPicture picture)
        {
            uint type = (uint)((picture.Nalu.Array[picture.Nalu.Offset] >> 1) & 0x3F);
            return type == 19 || type == 20;
        }

        /// <summary>One sample: the picture and whatever goes in front of it, each behind its length.</summary>
        private static ArraySegment<byte> LengthPrefixed(MuxPicture picture, MemoryStream sample)
        {
            sample.SetLength(0);

            foreach (var nalu in picture.Prefix)
                Append(sample, nalu);

            Append(sample, picture.Nalu.Array, picture.Nalu.Offset, picture.Nalu.Count);
            return new ArraySegment<byte>(sample.GetBuffer(), 0, (int)sample.Length);
        }

        private static void Append(Stream sample, byte[] nalu) => Append(sample, nalu, 0, nalu.Length);

        private static void Append(Stream sample, byte[] nalu, int offset, int length)
        {
            sample.WriteByte((byte)(length >> 24));
            sample.WriteByte((byte)(length >> 16));
            sample.WriteByte((byte)(length >> 8));
            sample.WriteByte((byte)length);
            sample.Write(nalu, offset, length);
        }
    }
}
