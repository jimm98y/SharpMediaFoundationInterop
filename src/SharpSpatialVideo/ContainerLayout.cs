using SharpISOBMFF;
using SharpMP4.Readers;
using System;
using System.Collections.Generic;
using System.IO;

namespace SharpSpatialVideo
{
    /// <summary>
    /// A spatial video's file with the coded data taken out of it: every byte that is not a video
    /// or audio sample, and where each of those samples sat.
    ///
    /// The split restores the coded data exactly, so this is the rest of what it takes to write
    /// the file back as it was - the boxes, the sample tables, whatever padding the camera left
    /// between chunks, and the tracks that are carried along without being touched. It is small:
    /// a few tens of kilobytes beside a few hundred megabytes of pictures.
    /// </summary>
    public sealed class ContainerLayout
    {
        /// <summary>Stretches of the file that are not sample data, each where it belongs.</summary>
        public List<(long Offset, byte[] Bytes)> Ranges { get; } = new List<(long, byte[])>();

        /// <summary>Where each video sample sat, in the order the track holds them.</summary>
        public List<long> VideoSampleOffsets { get; } = new List<long>();

        /// <summary>The same for the audio, which the split carries into both eye files.</summary>
        public List<long> AudioSampleOffsets { get; } = new List<long>();

        /// <summary>Reads a file and keeps everything about it but the coded samples.</summary>
        public static ContainerLayout Capture(string path)
        {
            var layout = new ContainerLayout();

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var container = new Container();
            container.Read(new IsoStream(new StreamWrapper(file)));

            var reader = new VideoReader();
            reader.Parse(container);

            // What the rebuild can put back by itself, and so does not need carrying.
            var covered = new List<(long Offset, long Length)>();
            foreach (var entry in reader.Tracks)
            {
                string handler = entry.Value.Track.HandlerType;
                if (handler != HandlerTypes.Video && handler != HandlerTypes.Sound)
                    continue;

                var offsets = handler == HandlerTypes.Video ? layout.VideoSampleOffsets : layout.AudioSampleOffsets;
                foreach (var (offset, length) in SampleRanges(entry.Value))
                {
                    covered.Add((offset, length));
                    offsets.Add(offset);
                }
            }

            covered.Sort((a, b) => a.Offset.CompareTo(b.Offset));

            // Everything else, in the order it lies in the file.
            long position = 0;
            file.Seek(0, SeekOrigin.Begin);

            foreach (var (offset, length) in covered)
            {
                if (offset > position)
                    layout.Ranges.Add((position, Read(file, position, offset - position)));

                position = Math.Max(position, offset + length);
            }

            if (position < file.Length)
                layout.Ranges.Add((position, Read(file, position, file.Length - position)));

            return layout;
        }

        /// <summary>
        /// Writes the file back: what was carried, and the samples that were not. The samples are
        /// taken one at a time and written where they sat, so nothing holds more than one of them -
        /// and a caller is free to hand out the same buffer each time.
        /// </summary>
        public void Write(string path, IEnumerable<ArraySegment<byte>> videoSamples,
            IEnumerable<ArraySegment<byte>> audioSamples)
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

            foreach (var (offset, bytes) in Ranges)
            {
                file.Seek(offset, SeekOrigin.Begin);
                file.Write(bytes, 0, bytes.Length);
            }

            Place(file, "video", VideoSampleOffsets, videoSamples);
            Place(file, "audio", AudioSampleOffsets, audioSamples);
        }

        private static void Place(FileStream file, string what, List<long> offsets,
            IEnumerable<ArraySegment<byte>> samples)
        {
            int index = 0;
            foreach (var sample in samples)
            {
                if (index == offsets.Count)
                    throw new InvalidOperationException(
                        $"the file held {offsets.Count} {what} samples and more than that were offered");

                file.Seek(offsets[index++], SeekOrigin.Begin);
                file.Write(sample.Array, sample.Offset, sample.Count);
            }

            if (index != offsets.Count)
                throw new InvalidOperationException(
                    $"the file held {offsets.Count} {what} samples and {index} were offered");
        }

        public byte[] ToBytes()
        {
            using var memory = new MemoryStream();

            WriteInt32(memory, Ranges.Count);
            foreach (var (offset, bytes) in Ranges)
            {
                WriteInt64(memory, offset);
                WriteInt32(memory, bytes.Length);
                memory.Write(bytes, 0, bytes.Length);
            }

            WriteOffsets(memory, VideoSampleOffsets);
            WriteOffsets(memory, AudioSampleOffsets);

            return memory.ToArray();
        }

        public static ContainerLayout FromBytes(byte[] bytes)
        {
            var layout = new ContainerLayout();
            int at = 0;

            int ranges = ReadInt32(bytes, ref at);
            for (int i = 0; i < ranges; i++)
            {
                long offset = ReadInt64(bytes, ref at);
                int length = ReadInt32(bytes, ref at);

                var data = new byte[length];
                Buffer.BlockCopy(bytes, at, data, 0, length);
                at += length;

                layout.Ranges.Add((offset, data));
            }

            ReadOffsets(bytes, ref at, layout.VideoSampleOffsets);
            ReadOffsets(bytes, ref at, layout.AudioSampleOffsets);

            return layout;
        }

        /// <summary>Where each of a track's samples lies, worked out from its chunks and sizes.</summary>
        private static IEnumerable<(long Offset, long Length)> SampleRanges(TrackContext track)
        {
            int sample = 0;
            for (int chunk = 0; chunk < track.ChunkAddressList.Length && sample < track.SizesList.Length; chunk++)
            {
                long offset = (long)track.ChunkAddressList[chunk];

                for (int i = 0; i < track.FramesInChunkList[chunk] && sample < track.SizesList.Length; i++)
                {
                    uint size = track.SizesList[sample++];
                    yield return (offset, size);
                    offset += size;
                }
            }
        }

        private static byte[] Read(FileStream file, long offset, long length)
        {
            var bytes = new byte[length];
            file.Seek(offset, SeekOrigin.Begin);
            file.ReadExactly(bytes);
            return bytes;
        }

        private static void WriteOffsets(Stream stream, List<long> offsets)
        {
            WriteInt32(stream, offsets.Count);
            foreach (long offset in offsets)
                WriteInt64(stream, offset);
        }

        private static void ReadOffsets(byte[] bytes, ref int at, List<long> offsets)
        {
            int count = ReadInt32(bytes, ref at);
            for (int i = 0; i < count; i++)
                offsets.Add(ReadInt64(bytes, ref at));
        }

        private static void WriteInt32(Stream stream, int value)
        {
            for (int shift = 24; shift >= 0; shift -= 8)
                stream.WriteByte((byte)(value >> shift));
        }

        private static void WriteInt64(Stream stream, long value)
        {
            for (int shift = 56; shift >= 0; shift -= 8)
                stream.WriteByte((byte)(value >> shift));
        }

        private static int ReadInt32(byte[] bytes, ref int at)
        {
            int value = 0;
            for (int i = 0; i < 4; i++)
                value = (value << 8) | bytes[at++];
            return value;
        }

        private static long ReadInt64(byte[] bytes, ref int at)
        {
            long value = 0;
            for (int i = 0; i < 8; i++)
                value = (value << 8) | bytes[at++];
            return value;
        }
    }
}
