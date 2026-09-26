using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>
    /// Puts a spatial video back together out of the pair the split made, exactly as it was.
    ///
    /// Nothing is rebuilt by working out what it should have been: the split carries the parts it
    /// had to rewrite in the dependent file - see <see cref="RoundTripSei"/> - so this puts each
    /// original slice header back in front of the coded slice data it belonged to, and writes the
    /// parameter sets it was given. The pictures were never decoded on either leg, so what comes
    /// out is what went in.
    /// </summary>
    public static class SpatialVideoRebuilder
    {
        /// <summary>
        /// Whether this file came from the split and can be put back exactly. Only its first
        /// picture is read: that is where the split writes what it put aside.
        /// </summary>
        public static bool CanRebuild(string dependentPath)
        {
            var track = MvHevcReader.Read(dependentPath, loadSamples: false);
            var carried = new RoundTripSei.Content();

            foreach (var accessUnit in MvHevcReader.StreamAccessUnits(track, limit: 1))
                foreach (var nalu in accessUnit.Nalus)
                    if (!nalu.IsSlice)
                        RoundTripSei.ReadInto(nalu.Data, carried);

            return !carried.IsEmpty;
        }

        public static LeftRightTranscoder.Result Write(string basePath, string dependentPath,
            string outputPath, StereoMetadata stereo)
        {
            // Only the indexes are read: both files are worked through a sample at a time.
            var baseTrack = MvHevcReader.Read(basePath, loadSamples: false);
            var dependentTrack = MvHevcReader.Read(dependentPath, loadSamples: false);

            using var dependent = new DependentView(dependentTrack);
            var carried = dependent.Carried;

            if (carried.IsEmpty)
                throw new InvalidOperationException(
                    $"{dependentPath} does not carry what the split put aside, so it cannot be put back exactly");

            // The parameter sets as they were: the multiview video parameter set and the eye
            // mapping belong to the base layer, layer 1's own go in beside them.
            var baseParameterSets = carried.ParameterSets.Where(n => Layer(n) == 0)
                .Concat(baseTrack.BaseParameterSets.Where(n => Type(n) == 33 || Type(n) == 34))
                .OrderBy(n => Order(Type(n)))
                .ToList();

            var layerParameterSets = carried.ParameterSets.Where(n => Layer(n) == 1).ToList();

            var counted = new Counter();

            if (carried.Container != null)
            {
                // The file as it was, with each sample put back where it sat.
                carried.Container.Write(outputPath,
                    Samples(baseTrack, carried, dependent, counted),
                    MvHevcReader.StreamAudioSamples(baseTrack).Select(sample => sample.Data));
            }
            else
            {
                MvHevcWriter.Write(outputPath, baseParameterSets, layerParameterSets,
                    AccessUnits(baseTrack, carried, dependent, counted),
                    baseTrack.Timescale, stereo, baseTrack.HasAudio ? baseTrack : null);
            }

            return new LeftRightTranscoder.Result
            {
                Path = outputPath,
                AccessUnits = counted.AccessUnits,
                Bytes = counted.Bytes,
            };
        }

        /// <summary>What was written, which is only known once the samples have been asked for.</summary>
        private sealed class Counter
        {
            public int AccessUnits { get; set; }
            public long Bytes { get; set; }
        }

        /// <summary>
        /// The access units, one at a time: the base view's NAL units as they are, and layer 1's
        /// put back together out of the header the split kept and the slice data it belongs to.
        /// </summary>
        private static IEnumerable<MultiviewAccessUnit> AccessUnits(MvHevcTrack baseTrack,
            RoundTripSei.Content carried, DependentView dependent, Counter counted)
        {
            // Layer 1's pictures are put back together in this, which is emptied for each access
            // unit: the writer has written the last one out before it asks for the next.
            var rejoined = new MemoryStream(1 << 20);

            foreach (var source in MvHevcReader.StreamAccessUnits(baseTrack))
            {
                var baseSlice = source.Nalus.FirstOrDefault(n => n.IsSlice);
                if (baseSlice == null)
                    continue;

                rejoined.SetLength(0);

                var accessUnit = new MultiviewAccessUnit
                {
                    Duration = (int)source.Duration,
                    CompositionOffset = source.CompositionOffset,
                    IsRandomAccessPoint = source.Nalus.Any(n => n.IsIrap),
                };

                carried.Extra.TryGetValue(source.Index, out var extra);

                // What the access unit held in front of its base picture, then the picture.
                if (extra != null)
                    foreach (var nalu in extra)
                        if (nalu.BeforeBasePicture)
                            accessUnit.BaseNalus.Add(nalu.Data);

                foreach (var nalu in source.Nalus)
                    if (nalu.IsSlice)
                        accessUnit.BaseNalus.Add(nalu.Data);

                // Then whatever sat in front of layer 1's picture, and that picture.
                if (extra != null)
                    foreach (var nalu in extra)
                        if (!nalu.BeforeBasePicture)
                            accessUnit.LayerNalus.Add(nalu.Data);

                if (carried.SliceHeaders.TryGetValue(source.Index, out var header))
                {
                    var payload = dependent.Payload(source.Index);
                    if (payload.Array != null)
                        accessUnit.LayerNalus.Add(Rejoin(header, payload, rejoined));
                }

                counted.AccessUnits++;
                counted.Bytes += accessUnit.BaseNalus.Sum(n => (long)n.Count)
                    + accessUnit.LayerNalus.Sum(n => (long)n.Count);

                yield return accessUnit;
            }
        }

        /// <summary>
        /// The same, as the samples a file holds. They are assembled in one buffer that grows to
        /// the largest of them and is then written over, because each is written before the next
        /// one is asked for.
        /// </summary>
        private static IEnumerable<ArraySegment<byte>> Samples(MvHevcTrack baseTrack,
            RoundTripSei.Content carried, DependentView dependent, Counter counted)
        {
            var buffer = new byte[1 << 20];

            foreach (var accessUnit in AccessUnits(baseTrack, carried, dependent, counted))
            {
                int length = 0;
                foreach (var nalu in accessUnit.BaseNalus.Concat(accessUnit.LayerNalus))
                {
                    if (length + 4 + nalu.Count > buffer.Length)
                        Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + 4 + nalu.Count));

                    buffer[length++] = (byte)(nalu.Count >> 24);
                    buffer[length++] = (byte)(nalu.Count >> 16);
                    buffer[length++] = (byte)(nalu.Count >> 8);
                    buffer[length++] = (byte)nalu.Count;

                    Buffer.BlockCopy(nalu.Array, nalu.Offset, buffer, length, nalu.Count);
                    length += nalu.Count;
                }

                yield return new ArraySegment<byte>(buffer, 0, length);
            }
        }

        /// <summary>
        /// The pictures the dependent file presents, by the access unit each came from. The
        /// pictures it carries only so those can be decoded are left out: they are the base view,
        /// which comes from the other file.
        /// </summary>
        /// <remarks>
        /// What is kept is which slice belongs to which access unit, not the slice data: that is
        /// already in hand - the file was read - and is taken apart only when it is written, so
        /// nothing holds a second copy of the pictures.
        /// </remarks>
        private sealed class DependentView : IDisposable
        {
            private readonly Dictionary<int, (long Offset, int Length)> _slices =
                new Dictionary<int, (long, int)>();

            private readonly MvHevcParser _parser = new MvHevcParser();
            private readonly FileStream _file;

            /// <summary>What the split put aside, which the same pass reads.</summary>
            public RoundTripSei.Content Carried { get; } = new RoundTripSei.Content();

            public DependentView(MvHevcTrack track)
            {
                _parser.ParseParameterSets(track.BaseParameterSets);
                _file = new FileStream(track.Path, FileMode.Open, FileAccess.Read, FileShare.Read);

                // One pass over the file: what it carries, and where each picture of the dependent
                // view lies. The pictures themselves stay in the file until they are written.
                foreach (var accessUnit in MvHevcReader.StreamAccessUnits(track))
                {
                    var here = new RoundTripSei.Content();
                    foreach (var nalu in accessUnit.Nalus)
                    {
                        if (nalu.IsSlice)
                            continue;

                        RoundTripSei.ReadInto(nalu.Data, Carried);
                        RoundTripSei.ReadInto(nalu.Data, here);
                    }

                    if (here.SliceHeaders.Count == 0)
                        continue;

                    var slice = accessUnit.Nalus.FirstOrDefault(n => n.IsSlice);
                    if (slice != null)
                        _slices[here.SliceHeaders.Keys.Single()] = (slice.Offset, slice.Data.Count);
                }
            }

            /// <summary>
            /// The coded slice data of one access unit's dependent picture, read out of the file as
            /// it is asked for, or an empty segment if the file has no such picture. It lies in the
            /// parser's buffer - see <see cref="ParsedSlice.Payload"/> - until the next is asked for.
            /// </summary>
            public ArraySegment<byte> Payload(int accessUnitIndex)
            {
                if (!_slices.TryGetValue(accessUnitIndex, out var at))
                    return default;

                var nalu = new Nalu
                {
                    Data = MvHevcReader.ReadNalu(_file, at.Offset, at.Length, ref _readBuffer),
                    Offset = at.Offset,
                };

                return _parser.ParseSlice(nalu).Payload;
            }

            /// <summary>What each picture is read into; the next one goes over it.</summary>
            private byte[] _readBuffer;

            public void Dispose() => _file.Dispose();
        }

        /// <summary>
        /// One NAL unit out of the header the split kept and the slice data it belongs to, with
        /// the emulation prevention bytes put back over the join.
        /// </summary>
        private static ArraySegment<byte> Rejoin(byte[] header, ArraySegment<byte> payload, MemoryStream into)
        {
            return MvHevcParser.ToEbsp(header, payload, into);
        }

        private static uint Type(byte[] nalu) => (uint)((nalu[0] >> 1) & 0x3F);

        private static uint Layer(byte[] nalu) => (uint)(((nalu[0] & 1) << 5) | (nalu[1] >> 3));

        /// <summary>Video parameter set, then sequence, then picture, then the SEI - as hvcC holds them.</summary>
        private static int Order(uint type) => type switch
        {
            32 => 0,
            33 => 1,
            34 => 2,
            _ => 3,
        };
    }
}
