using SharpH265;
using SharpH26X;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>One picture of the rewritten single-layer stream.</summary>
    public sealed class OutputPicture
    {
        public int DecodeIndex { get; set; }
        public int View { get; set; }
        public int Poc { get; set; }
        /// <summary>Where the picture lies in the source file, so it can be read when it is written.</summary>
        public long SourceOffset { get; set; }
        public int SourceLength { get; set; }

        /// <summary>What the source said about it, kept because the bytes are not.</summary>
        public uint SourceTemporalIdPlus1 { get; set; }
        /// <summary>
        /// The count the source gave this picture. It is not unique: an IDR picture starts the
        /// count again, so a recording with a key frame every second names a picture per second
        /// with the same number. <see cref="Poc"/> is the one that names a picture in the stream.
        /// </summary>
        public int SourcePoc { get; set; }
        public bool SourceIsIrap { get; set; }

        /// <summary>
        /// The slice segment header as the source stored it, which is small and worth keeping: it
        /// is what the split carries so the way back can put the picture together again.
        /// </summary>
        public byte[] SourceHeaderBytes { get; set; }

        /// <summary>The access unit it came from, and how long that was.</summary>
        public int AccessUnitIndex { get; set; }
        public uint AccessUnitDuration { get; set; }

        /// <summary>Long-term cross-view reference for this picture; -1 when it has none.</summary>
        public int CrossViewPoc { get; set; } = -1;

        /// <summary>
        /// Whether this picture is presented. Cleared for pictures that only exist so others can
        /// reference them, which is every base-view picture in a right-eye-only stream.
        /// </summary>
        public bool Output { get; set; } = true;

        /// <summary>POCs this picture predicts from, excluding the cross-view reference.</summary>
        public List<int> UsedShortTerm { get; } = new List<int>();

        /// <summary>Everything that must stay marked as a reference, this picture aside.</summary>
        public List<int> Retained { get; } = new List<int>();

        public uint OutputNalType { get; set; }
    }

    /// <summary>
    /// Converts an MV-HEVC access unit stream into a conformant single-layer HEVC stream holding
    /// both views, so one ordinary HEVC decoder produces every view.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Views are interleaved and each access unit gets two picture order count slots: 2k for the
    /// base view and 2k+1 for the dependent view (k being the original POC). Scaling every count
    /// by the same factor leaves motion vector scaling untouched, which was confirmed by decoding
    /// the base view out of the rewritten stream and comparing it against the untouched one.
    /// </para>
    /// <para>
    /// The count never starts again. A source whose key frames are IDR pictures restarts its count
    /// at each one, but the interleave names pictures from both sides of a key frame, and every
    /// map here uses the count as a picture's identity. So k carries on across key frames, the base
    /// view's later IDR pictures are written as CRA pictures - intra all the same, and the slice
    /// data does not name its own NAL type - and the count is made wide enough never to wrap.
    /// </para>
    /// <para>
    /// The inter-layer reference is re-expressed as a <em>long-term</em> reference picture. In
    /// reference list construction (8.3.4) LtCurr is appended after StCurrBefore and StCurrAfter,
    /// which is exactly where MV-HEVC puts RefPicSetInterLayer0 (F.8.3.4), and MV-HEVC already
    /// marks inter-layer references as long-term (F.8.3.2). So candidate indices are preserved and
    /// every ref_pic_lists_modification carries over untouched.
    /// </para>
    /// <para>
    /// Naming a picture long-term marks it so, and 8.3.2 resolves short-term entries only against
    /// pictures that are currently short-term - a base picture can never go back. Rather than
    /// decode a second copy of it under another picture order count, which would change its
    /// distance to its own references and so decode differently, the dependent view is simply held
    /// back until nothing needs its cross-view partner short-term any more. See
    /// <see cref="ScheduleDecodeOrder"/>.
    /// </para>
    /// </remarks>
    public sealed class SingleLayerRewriter : IDisposable
    {
        // Level 5.1. Interleaving multiplies the picture rate and the DPB, which overflows the
        // original level 4.1 (its MaxDpbSize is 6 at this picture size).
        private const uint OutputLevelIdc = 153;

        /// <summary>Picture order count slots per access unit: base then dependent.</summary>
        public const int PocScale = 2;

        // The rewritten stream holds two pictures per access unit and keeps counting across key
        // frames, so its counts run far past the source's 11 bits. The width is chosen to hold the
        // whole stream without wrapping, because a long term reference is named by its low bits
        // alone; 16 bits is as wide as HEVC allows, which is 32768 access units.
        private const ulong MaxLog2MaxPocLsbMinus4 = 12;
        private ulong _log2MaxPocLsbMinus4 = 9;

        private readonly MvHevcTrack _track;
        /// <summary>Reads the source: its parameter sets as the camera wrote them, never changed.</summary>
        private readonly MvHevcParser _parser = new MvHevcParser();

        /// <summary>
        /// Writes the rewritten stream, against its own copies of the parameter sets - the ones
        /// this widens the picture order count in, and allows long-term references in. A slice of
        /// the source has to be read against the sets it was written with and written against
        /// these, so the two cannot be the same objects.
        /// </summary>
        private readonly MvHevcParser _writer = new MvHevcParser();

        public H265Context ParserContext => _parser.Context;

        public List<OutputPicture> Pictures { get; } = new List<OutputPicture>();

        /// <summary>
        /// What each access unit held besides its two pictures, by access unit: the camera writes
        /// an SEI of its own in front of each picture of a key frame. They are collected while
        /// planning, which is the one pass over the file, and are small enough to keep.
        /// </summary>
        /// <remarks>
        /// Counted in decode order, not by picture order count: a stream whose key frames are IDR
        /// pictures starts its count again at each one, so picture order counts repeat.
        /// </remarks>
        public Dictionary<int, List<(byte[] Data, bool BeforeBasePicture)>> OtherNalus { get; } =
            new Dictionary<int, List<(byte[], bool)>>();

        /// <summary>
        /// Largest number of pictures that precede a picture in decode order but follow it in
        /// output order, i.e. the value sps_max_num_reorder_pics has to carry.
        /// </summary>
        public int MaxReorder { get; private set; }

        public SingleLayerRewriter(MvHevcTrack track)
        {
            _track = track;
        }

        /// <summary>Parses the source and works out the rewritten picture plan.</summary>
        /// <param name="baseViewOnly">
        /// Drops the dependent view, leaving only the picture order count rescaling. Useful to
        /// separate a problem in the rescaling from one in the cross-view reference.
        /// </param>
        /// <summary>
        /// Reads the file through once and works out what the rewritten stream will hold: which
        /// picture goes where, what each predicts from, and what has to stay in the buffer for it.
        /// </summary>
        /// <remarks>
        /// Only what is said about each picture is kept - a few dozen bytes - not the picture. Each
        /// is read again, out of the file, when it comes to be written, so a recording of any
        /// length costs the same.
        /// </remarks>
        public void Plan(bool baseViewOnly = false)
        {
            _parser.ParseParameterSets(_track.BaseParameterSets);
            _parser.ParseParameterSets(_track.LayerParameterSets);
            _parser.ResetPocState();

            _writer.ParseParameterSets(_track.BaseParameterSets);
            _writer.ParseParameterSets(_track.LayerParameterSets);

            // Where the rewritten stream's count has reached, and the highest count the source has
            // given in the sequence being read. An IDR picture starts the source's count again;
            // this one carries on, because the interleave has to name pictures from before it - see
            // StreamPoc.
            int carriedOver = 0;
            int highestInSequence = -1;

            foreach (var au in MvHevcReader.StreamAccessUnits(_track))
            {
                var baseSlice = au.SliceOfLayer(0);
                var depSlice = au.SliceOfLayer(1);
                if (baseSlice == null)
                    continue;

                // One slice a picture is what this rewrites: Apple writes that, and so does every
                // encoder this has been used with. Of a picture coded as several, all but the first
                // would be left out without a word, so such a file is refused rather than turned
                // into a broken one.
                int mostSlices = Math.Max(
                    au.Nalus.Count(n => n.IsSlice && n.LayerId == 0),
                    au.Nalus.Count(n => n.IsSlice && n.LayerId == 1));
                if (mostSlices > 1)
                    throw new NotSupportedException(
                        $"Access unit {au.Index} codes a picture as {mostSlices} slices, and only pictures " +
                        "of a single slice can be taken apart into their views.");

                var parsedBase = _parser.ParseSlice(baseSlice);
                int sourcePoc = _parser.DerivePoc(parsedBase);

                if (baseSlice.IsIdr && Pictures.Count > 0)
                {
                    carriedOver += highestInSequence + 1;
                    highestInSequence = -1;
                }

                highestInSequence = Math.Max(highestInSequence, sourcePoc);
                int poc = sourcePoc + carriedOver;

                foreach (var nalu in au.Nalus)
                {
                    if (nalu.IsSlice)
                        continue;

                    if (!OtherNalus.TryGetValue(au.Index, out var others))
                        OtherNalus[au.Index] = others = new List<(byte[], bool)>();

                    // Kept past this access unit, so copied out of the reader's buffer.
                    others.Add((nalu.Data.ToArray(), nalu.Offset < baseSlice.Offset));
                }

                var basePicture = Describe(baseSlice, parsedBase, au, view: 0, poc: poc, sourcePoc: sourcePoc);

                // An IDR would start the count again, which the interleave cannot have: a CRA is a
                // key frame too and carries on counting, and the slice data is untouched either way
                // - both are intra pictures, and nothing in the payload names its own NAL type. The
                // first picture stays an IDR, because a stream has to open with one.
                basePicture.OutputNalType = baseSlice.IsIdr && Pictures.Count > 0
                    ? H265NALTypes.CRA_NUT
                    : baseSlice.Type;
                CollectUsedReferences(parsedBase, poc, 0, basePicture);
                Pictures.Add(basePicture);

                if (depSlice == null || baseViewOnly)
                    continue;

                var parsedDep = _parser.ParseSlice(depSlice);

                var depPicture = Describe(depSlice, parsedDep, au, view: 1, poc: poc, sourcePoc: sourcePoc);

                // The dependent view predicts from the base view, so it can no longer be an IRAP at
                // all: a key frame there becomes an ordinary trailing picture, whichever kind it was.
                depPicture.OutputNalType = depSlice.IsIrap ? 1u : depSlice.Type;
                CollectUsedReferences(parsedDep, poc, 1, depPicture);

                // In the source the inter-layer picture is always a candidate, but only some
                // pictures select it; the rest truncate it away via num_ref_idx. Only name it
                // where it is really used, because naming it marks the base picture long-term.
                if (UsesInterLayerReference(parsedDep, depPicture.UsedShortTerm.Count))
                    depPicture.CrossViewPoc = basePicture.Poc;

                Pictures.Add(depPicture);
            }

            ScheduleDecodeOrder();

            for (int i = 0; i < Pictures.Count; i++)
                Pictures[i].DecodeIndex = i;

            ComputeRetention();
            ComputeDpbRequirements();
            ChoosePocWidth();
        }

        /// <summary>What is worth keeping about a picture, once its bytes are let go of.</summary>
        private static OutputPicture Describe(Nalu slice, ParsedSlice parsed, AccessUnit accessUnit,
            int view, int poc, int sourcePoc) =>
            new OutputPicture
            {
                View = view,
                Poc = poc * PocScale + view,
                SourceOffset = slice.Offset,
                SourceLength = slice.Data.Count,
                SourceTemporalIdPlus1 = parsed.NalUnit.NalUnitHeader.NuhTemporalIdPlus1,
                SourcePoc = sourcePoc,
                SourceIsIrap = slice.IsIrap,
                SourceHeaderBytes = parsed.HeaderBytes,
                AccessUnitIndex = accessUnit.Index,
                AccessUnitDuration = accessUnit.Duration,
            };

        /// <summary>
        /// Orders the interleaved pictures so that a base picture is only named as a long-term
        /// cross-view reference once nothing needs it short-term any more.
        /// </summary>
        /// <remarks>
        /// Naming a picture long-term marks it so, and 8.3.2 resolves short-term entries only
        /// against pictures that are currently short-term - a base picture cannot go back. The
        /// base view is therefore left in its original order, and each dependent picture is held
        /// back until the last base picture that still predicts from its cross-view partner has
        /// been decoded. In practice this lags the dependent view about one group of pictures
        /// behind the base view.
        /// </remarks>
        private void ScheduleDecodeOrder()
        {
            var basePictures = Pictures.Where(p => p.View == 0).ToList();
            var dependentPictures = Pictures.Where(p => p.View == 1).ToList();
            if (dependentPictures.Count == 0)
                return;

            // For each base picture, the last position in base decode order that predicts from it.
            var positionOfBase = new Dictionary<int, int>();
            for (int i = 0; i < basePictures.Count; i++)
                positionOfBase[basePictures[i].Poc] = i;

            var lastNeededAt = new Dictionary<int, int>();
            for (int i = 0; i < basePictures.Count; i++)
            {
                lastNeededAt[basePictures[i].Poc] = i;
                foreach (var reference in basePictures[i].UsedShortTerm)
                    lastNeededAt[reference] = i;
            }

            // A dependent picture may be emitted once its cross-view partner is free and all the
            // dependent pictures it predicts from are out.
            var emitted = new HashSet<int>();
            var schedule = new List<OutputPicture>(Pictures.Count);
            int next = 0;

            void DrainDependents(int basePosition)
            {
                bool progress = true;
                while (progress)
                {
                    progress = false;
                    while (next < dependentPictures.Count)
                    {
                        var candidate = dependentPictures[next];

                        int freeAt = candidate.CrossViewPoc >= 0 && lastNeededAt.TryGetValue(candidate.CrossViewPoc, out int at)
                            ? at
                            : positionOfBase.TryGetValue(candidate.Poc - 1, out int own) ? own : 0;
                        if (freeAt > basePosition)
                            break;

                        if (!candidate.UsedShortTerm.All(emitted.Contains))
                            break;

                        schedule.Add(candidate);
                        emitted.Add(candidate.Poc);
                        next++;
                        progress = true;
                    }
                }
            }

            for (int i = 0; i < basePictures.Count; i++)
            {
                schedule.Add(basePictures[i]);
                emitted.Add(basePictures[i].Poc);
                DrainDependents(i);
            }

            // Anything still held back goes out at the end, in order.
            while (next < dependentPictures.Count)
            {
                schedule.Add(dependentPictures[next]);
                emitted.Add(dependentPictures[next].Poc);
                next++;
            }

            Pictures.Clear();
            Pictures.AddRange(schedule);
        }

        /// <summary>
        /// Simulates the decoded picture buffer to size it. Occupancy is the union of the pictures
        /// still held as references and those decoded but not yet output - counting them separately
        /// double counts the overlap, and HEVC caps the buffer at 16 pictures whatever the level.
        /// </summary>
        private void ComputeDpbRequirements()
        {
            // Output happens in picture order count order, so a picture can leave once every
            // lower numbered picture has been decoded.
            var remainingPocs = new SortedSet<int>(Pictures.Select(p => p.Poc));

            // Decoded but not yet output. Once the lowest undecoded count passes a picture it is
            // output and never waits again, so the set only holds the reorder window rather than
            // being worked out afresh from everything decoded so far.
            var waiting = new SortedSet<int>();

            for (int i = 0; i < Pictures.Count; i++)
            {
                waiting.Add(Pictures[i].Poc);
                remainingPocs.Remove(Pictures[i].Poc);

                int lowestUndecoded = remainingPocs.Count > 0 ? remainingPocs.Min : int.MaxValue;
                while (waiting.Count > 0 && waiting.Min < lowestUndecoded)
                    waiting.Remove(waiting.Min);
                MaxReorder = Math.Max(MaxReorder, waiting.Count);

                var occupancy = new HashSet<int>(waiting);
                foreach (var poc in Pictures[i].Retained)
                    occupancy.Add(poc);
                occupancy.Remove(Pictures[i].Poc);

                MaxDpbOccupancy = Math.Max(MaxDpbOccupancy, occupancy.Count + 1);
            }
        }

        /// <summary>
        /// Widens the picture order count until the whole stream fits in it without wrapping. A long
        /// term reference is named by the low bits of its count alone, so two pictures sharing them
        /// would be indistinguishable.
        /// </summary>
        private void ChoosePocWidth()
        {
            int highest = Pictures.Count > 0 ? Pictures.Max(p => p.Poc) : 0;

            _log2MaxPocLsbMinus4 = 9;
            while (_log2MaxPocLsbMinus4 < MaxLog2MaxPocLsbMinus4 && 1 << (int)(_log2MaxPocLsbMinus4 + 4) <= highest)
                _log2MaxPocLsbMinus4++;
        }

        /// <summary>Largest number of pictures the decoded picture buffer has to hold at once.</summary>
        public int MaxDpbOccupancy { get; private set; }

        /// <summary>
        /// Whether this dependent-view slice really predicts from the inter-layer picture. It sits
        /// at index <paramref name="usedShortTermCount"/> of the candidate list (F.8.3.4 appends
        /// RefPicSetInterLayer0 after the short-term sets), so it is used either when the list is
        /// modified to select that index, or when it is the only candidate there is.
        /// </summary>
        private static bool UsesInterLayerReference(ParsedSlice slice, int usedShortTermCount)
        {
            // An intra slice predicts from nothing at all, whatever the candidate list holds. This
            // is what a key frame of the dependent view looks like in a stream whose views are coded
            // independently; naming the base picture there would mark it long term for nothing, and
            // hold the dependent view back behind it.
            if (slice.Header.SliceType == 2)
                return false;

            if (usedShortTermCount == 0)
                return true;

            var modification = slice.Header.RefPicListsModification;
            if (modification == null)
                return false;

            if (modification.RefPicListModificationFlagL0 != 0 && modification.ListEntryL0 != null &&
                modification.ListEntryL0.Any(e => (int)e == usedShortTermCount))
                return true;

            return modification.RefPicListModificationFlagL1 != 0 && modification.ListEntryL1 != null &&
                modification.ListEntryL1.Any(e => (int)e == usedShortTermCount);
        }

        /// <summary>Maps the source short-term reference set onto the doubled picture order count.</summary>
        private static void CollectUsedReferences(ParsedSlice slice, int poc, int slot, OutputPicture picture)
        {
            var rps = slice.Header.StRefPicSet;
            if (rps == null || slice.Header.ShortTermRefPicSetSpsFlag != 0)
                return;

            long delta = 0;
            for (int i = 0; i < (int)rps.NumNegativePics; i++)
            {
                delta -= (long)(rps.DeltaPocS0Minus1[i] + 1);
                if (rps.UsedByCurrPicS0Flag[i] != 0)
                    picture.UsedShortTerm.Add((int)((poc + delta) * PocScale + slot));
            }

            delta = 0;
            for (int i = 0; i < (int)rps.NumPositivePics; i++)
            {
                delta += (long)(rps.DeltaPocS1Minus1[i] + 1);
                if (rps.UsedByCurrPicS1Flag[i] != 0)
                    picture.UsedShortTerm.Add((int)((poc + delta) * PocScale + slot));
            }
        }

        /// <summary>
        /// Works out, for each picture, everything decoded before it that it or any later picture
        /// still needs. A picture's reference picture set has to name all of them, otherwise the
        /// decoder marks them unused and a later picture loses its reference.
        /// </summary>
        /// <remarks>
        /// A picture is still needed at position i when something at i or after references it, so
        /// all that has to be known of the future is where each picture is referenced for the last
        /// time. What is retained is then a set that only ever holds what the buffer holds - a
        /// dozen pictures - rather than, for every picture, everything the rest of the stream
        /// references, which grows with the square of the length: three gigabytes for six
        /// thousand access units.
        /// </remarks>
        private void ComputeRetention()
        {
            var lastReferencedAt = new Dictionary<int, int>();
            for (int i = 0; i < Pictures.Count; i++)
            {
                foreach (var poc in Pictures[i].UsedShortTerm)
                    lastReferencedAt[poc] = i;
                if (Pictures[i].CrossViewPoc >= 0)
                    lastReferencedAt[Pictures[i].CrossViewPoc] = i;
            }

            // Decoded, and referenced again at or after the picture being looked at.
            var live = new HashSet<int>();

            for (int i = 0; i < Pictures.Count; i++)
            {
                var picture = Pictures[i];
                live.RemoveWhere(poc => lastReferencedAt[poc] < i);

                foreach (var poc in live)
                    if (poc != picture.Poc)
                        picture.Retained.Add(poc);

                picture.Retained.Sort();

                if (lastReferencedAt.TryGetValue(picture.Poc, out int last) && last > i)
                    live.Add(picture.Poc);
            }
        }

        /// <summary>
        /// Rewrites the video parameter set to describe a single layer. Every field naming the
        /// layer structure has to agree, or the result claims one layer in one place and two in
        /// another.
        /// </summary>
        private void CollapseVpsToSingleLayer()
        {
            var vps = _writer.Context.VideoParameterSetRbsp;
            vps.VpsMaxLayersMinus1 = 0;
            vps.VpsMaxLayerId = 0;
            vps.VpsNumLayerSetsMinus1 = 0;
            vps.LayerIdIncludedFlag = null;
            vps.VpsExtensionFlag = 0;
            vps.VpsExtension = null;
            vps.VpsExtension2Flag = 0;
            vps.Vps3dExtensionFlag = 0;
            vps.Vps3dExtension = null;
            vps.VpsExtension3Flag = 0;
        }

        /// <summary>
        /// Parameter sets for a base-view-only stream: the original sequence and picture parameter
        /// sets untouched, alongside a video parameter set with the multiview extension removed.
        /// </summary>
        /// <remarks>
        /// The base view's coded slices are emitted verbatim, so its sequence and picture
        /// parameter sets must stay exactly as they were. Only the video parameter set changes -
        /// left as it is, it still advertises two layers, and a player that acts on that goes
        /// looking for a dependent layer that is no longer in the file.
        /// </remarks>
        public List<byte[]> BuildBaseViewParameterSets(IEnumerable<byte[]> originalParameterSets)
        {
            var context = _writer.Context;
            CollapseVpsToSingleLayer();

            var result = new List<byte[]>
            {
                WriteParameterSet(H265NALTypes.VPS_NUT,
                    s => context.VideoParameterSetRbsp.Write(context, s))
            };

            foreach (var nalu in originalParameterSets)
            {
                uint type = (uint)((nalu[0] >> 1) & 0x3F);
                if (type == H265NALTypes.SPS_NUT || type == H265NALTypes.PPS_NUT)
                    result.Add(nalu);
            }

            return result;
        }

        /// <summary>Emits the rewritten parameter sets, in the order they should be fed to a decoder.</summary>
        public List<byte[]> BuildParameterSets(int dpbSize, int maxReorder)
        {
            var context = _writer.Context;
            var result = new List<byte[]>();

            var vps = context.VideoParameterSetRbsp;
            CollapseVpsToSingleLayer();
            if (vps.ProfileTierLevel != null)
                vps.ProfileTierLevel.GeneralLevelIdc = OutputLevelIdc;
            for (int i = 0; vps.VpsMaxDecPicBufferingMinus1 != null && i < vps.VpsMaxDecPicBufferingMinus1.Length; i++)
            {
                vps.VpsMaxDecPicBufferingMinus1[i] = (ulong)(dpbSize - 1);
                vps.VpsMaxNumReorderPics[i] = (ulong)maxReorder;
            }
            result.Add(WriteParameterSet(H265NALTypes.VPS_NUT, s => vps.Write(context, s)));

            var sps = context.SeqParameterSets[0];
            context.SeqParameterSetRbsp = sps;
            if (sps.ProfileTierLevel != null)
                sps.ProfileTierLevel.GeneralLevelIdc = OutputLevelIdc;
            for (int i = 0; i < sps.SpsMaxDecPicBufferingMinus1.Length; i++)
            {
                sps.SpsMaxDecPicBufferingMinus1[i] = (ulong)(dpbSize - 1);
                sps.SpsMaxNumReorderPics[i] = (ulong)maxReorder;
            }
            // The cross-view reference is carried as a long-term picture, so slice headers must be
            // allowed to signal long-term references.
            sps.LongTermRefPicsPresentFlag = 1;
            sps.NumLongTermRefPicsSps = 0;
            sps.Log2MaxPicOrderCntLsbMinus4 = _log2MaxPocLsbMinus4;
            result.Add(WriteParameterSet(H265NALTypes.SPS_NUT, s => sps.Write(context, s)));

            // Both views now share sequence parameter set 0; only one can be active in a sequence.
            foreach (var id in context.PicParameterSets.Keys.OrderBy(k => k))
            {
                var pps = context.PicParameterSets[id];
                pps.PpsSeqParameterSetId = 0;
                // Duplicated base pictures must decode without being output, which needs
                // pic_output_flag in the slice header.
                pps.OutputFlagPresentFlag = 1;
                context.PicParameterSetRbsp = pps;
                result.Add(WriteParameterSet(H265NALTypes.PPS_NUT, s => pps.Write(context, s)));
            }

            return result;
        }

        private byte[] WriteParameterSet(uint nalType, Action<ItuStream> write)
        {
            var context = _writer.Context;
            using var memory = new MemoryStream();
            using (var stream = new ItuStream(memory))
            {
                var nalUnit = new NalUnit(0);
                nalUnit.NalUnitHeader = new NalUnitHeader
                {
                    ForbiddenZeroBit = 0,
                    NalUnitType = nalType,
                    NuhLayerId = 0,
                    NuhTemporalIdPlus1 = 1,
                };
                context.NalHeader = nalUnit;
                nalUnit.Write(context, stream);
                write(stream);
            }

            return memory.ToArray();
        }

        /// <summary>
        /// Rewrites one picture's slice into a single-layer NAL unit, reading it back out of the
        /// source file: what the plan kept is where it lies, not what it holds.
        /// </summary>
        /// <remarks>
        /// The NAL unit is written into a buffer the next picture is written over, so it has to be
        /// used - or copied - before another is asked for. Every caller writes it out at once.
        /// </remarks>
        public ArraySegment<byte> RewriteSlice(OutputPicture picture)
        {
            var context = _writer.Context;
            var source = ReadSource(picture);
            var header = source.Header;

            // Re-activate the parameter sets this slice uses so the writer takes the same branches.
            context.PicParameterSetRbsp = context.PicParameterSets[header.SlicePicParameterSetId];
            context.SeqParameterSetRbsp = context.SeqParameterSets[0];
            context.SliceSegmentLayerRbsp = source.Slice;

            var nalUnit = new NalUnit(0);
            nalUnit.NalUnitHeader = new NalUnitHeader
            {
                ForbiddenZeroBit = 0,
                NalUnitType = picture.OutputNalType,
                NuhLayerId = 0,
                NuhTemporalIdPlus1 = picture.SourceTemporalIdPlus1,
            };
            context.NalHeader = nalUnit;

            ApplyReferenceSet(picture, source);

            bool isIdr = picture.OutputNalType == 19 || picture.OutputNalType == 20;
            if (!isIdr)
                header.SlicePicOrderCntLsb = (ulong)(picture.Poc & (MaxPocLsb(context) - 1));

            header.PicOutputFlag = (byte)(picture.Output ? 1 : 0);

            // The stream is not disposed, because that would dispose the buffer under it. It holds
            // nothing else: bytes go through as each is completed, and every write here ends on a
            // byte boundary.
            _rewritten.SetLength(0);
            var stream = new ItuStream(_rewritten);

            nalUnit.Write(context, stream);
            source.Slice.Write(context, stream);

            var payload = source.Payload;
            stream.WriteBytes(payload.Array, payload.Offset, payload.Count);

            return new ArraySegment<byte>(_rewritten.GetBuffer(), 0, (int)_rewritten.Length);
        }

        /// <summary>What <see cref="RewriteSlice"/> writes into, one picture after another.</summary>
        private readonly MemoryStream _rewritten = new MemoryStream(1 << 20);

        /// <summary>The picture as the source holds it, read again and parsed again.</summary>
        private ParsedSlice ReadSource(OutputPicture picture)
        {
            _source ??= new FileStream(_track.Path, FileMode.Open, FileAccess.Read, FileShare.Read);

            var nalu = new Nalu
            {
                Data = MvHevcReader.ReadNalu(_source, picture.SourceOffset, picture.SourceLength, ref _readBuffer),
                Offset = picture.SourceOffset,
            };

            return _parser.ParseSlice(nalu);
        }

        /// <summary>
        /// What each picture is read into. The next one goes over it, so a picture is written out
        /// - payload and all - before the next is read; see <see cref="RewriteSlice"/>.
        /// </summary>
        private byte[] _readBuffer;

        public void Dispose()
        {
            _source?.Dispose();
            _source = null;
        }

        private FileStream _source;

        private static int MaxPocLsb(H265Context context) =>
            1 << (int)(context.SeqParameterSetRbsp.Log2MaxPicOrderCntLsbMinus4 + 4);

        /// <summary>
        /// Rebuilds the slice's reference picture set over the doubled picture order counts, and
        /// moves the cross-view reference into the long-term set.
        /// </summary>
        private void ApplyReferenceSet(OutputPicture picture, ParsedSlice source)
        {
            var header = source.Header;
            bool isIdr = picture.OutputNalType == 19 || picture.OutputNalType == 20;

            if (isIdr)
            {
                header.StRefPicSet = null;
                header.NumLongTermSps = 0;
                header.NumLongTermPics = 0;
                return;
            }

            var used = new HashSet<int>(picture.UsedShortTerm);

            var negatives = picture.Retained
                .Where(p => p < picture.Poc && p != picture.CrossViewPoc)
                .OrderByDescending(p => p)
                .ToList();
            var positives = picture.Retained
                .Where(p => p > picture.Poc)
                .OrderBy(p => p)
                .ToList();

            var rps = new StRefPicSet(0)
            {
                InterRefPicSetPredictionFlag = 0,
                NumNegativePics = (ulong)negatives.Count,
                NumPositivePics = (ulong)positives.Count,
                DeltaPocS0Minus1 = new ulong[negatives.Count],
                UsedByCurrPicS0Flag = new byte[negatives.Count],
                DeltaPocS1Minus1 = new ulong[positives.Count],
                UsedByCurrPicS1Flag = new byte[positives.Count],
            };

            int previous = picture.Poc;
            for (int i = 0; i < negatives.Count; i++)
            {
                rps.DeltaPocS0Minus1[i] = (ulong)(previous - negatives[i] - 1);
                rps.UsedByCurrPicS0Flag[i] = (byte)(used.Contains(negatives[i]) ? 1 : 0);
                previous = negatives[i];
            }

            previous = picture.Poc;
            for (int i = 0; i < positives.Count; i++)
            {
                rps.DeltaPocS1Minus1[i] = (ulong)(positives[i] - previous - 1);
                rps.UsedByCurrPicS1Flag[i] = (byte)(used.Contains(positives[i]) ? 1 : 0);
                previous = positives[i];
            }

            header.ShortTermRefPicSetSpsFlag = 0;
            header.StRefPicSet = rps;

            header.NumLongTermSps = 0;
            if (picture.CrossViewPoc >= 0)
            {
                header.NumLongTermPics = 1;
                header.LtIdxSps = new ulong[1];
                header.PocLsbLt = new ulong[] { (ulong)(picture.CrossViewPoc & (MaxPocLsb(_writer.Context) - 1)) };
                header.UsedByCurrPicLtFlag = new byte[] { 1 };
                // Every picture order count in the stream is unique modulo MaxPicOrderCntLsb, so
                // the least significant bits identify the picture on their own.
                header.DeltaPocMsbPresentFlag = new byte[] { 0 };
                header.DeltaPocMsbCycleLt = new ulong[] { 0 };
            }
            else
            {
                header.NumLongTermPics = 0;
                header.PocLsbLt = new ulong[0];
                header.UsedByCurrPicLtFlag = new byte[0];
                header.DeltaPocMsbPresentFlag = new byte[0];
                header.DeltaPocMsbCycleLt = new ulong[0];
            }
        }
    }
}
