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
        public ParsedSlice Source { get; set; }

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
    public sealed class SingleLayerRewriter
    {
        // Level 5.1. Interleaving multiplies the picture rate and the DPB, which overflows the
        // original level 4.1 (its MaxDpbSize is 6 at this picture size).
        private const uint OutputLevelIdc = 153;

        /// <summary>Picture order count slots per access unit: base then dependent.</summary>
        public const int PocScale = 2;

        // 3 x 760 access units overflows the source's 11-bit picture order count, so widen it.
        private const ulong OutputLog2MaxPocLsbMinus4 = 9;

        private readonly MvHevcTrack _track;
        private readonly MvHevcParser _parser = new MvHevcParser();

        public H265Context ParserContext => _parser.Context;

        public List<OutputPicture> Pictures { get; } = new List<OutputPicture>();
        public int MaxRetained { get; private set; }
        public int TemporalMvpPictures { get; private set; }

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
        public void Plan(bool baseViewOnly = false)
        {
            _parser.ParseParameterSets(_track.BaseParameterSets);
            _parser.ParseParameterSets(_track.LayerParameterSets);
            _parser.ResetPocState();

            foreach (var au in _track.AccessUnits)
            {
                var baseSlice = au.SliceOfLayer(0);
                var depSlice = au.SliceOfLayer(1);
                if (baseSlice == null)
                    continue;

                var parsedBase = _parser.ParseSlice(baseSlice);
                int poc = _parser.DerivePoc(parsedBase);
                parsedBase.Poc = poc;

                var basePicture = new OutputPicture
                {
                    View = 0,
                    Poc = poc * PocScale,
                    Source = parsedBase,
                    OutputNalType = baseSlice.Type,
                };
                CollectUsedReferences(parsedBase, poc, 0, basePicture);
                Pictures.Add(basePicture);

                if (depSlice == null || baseViewOnly)
                    continue;

                var parsedDep = _parser.ParseSlice(depSlice);
                parsedDep.Poc = poc;

                var depPicture = new OutputPicture
                {
                    View = 1,
                    Poc = poc * PocScale + 1,
                    Source = parsedDep,
                    // The dependent view predicts from the base view, so it can no longer be an
                    // IRAP: a CRA there becomes an ordinary trailing picture.
                    OutputNalType = depSlice.IsCra ? 1u : depSlice.Type,
                };
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
            {
                Pictures[i].DecodeIndex = i;
                if (Pictures[i].Source.Header.SliceTemporalMvpEnabledFlag != 0)
                    TemporalMvpPictures++;
            }

            ComputeRetention();
            ComputeDpbRequirements();
        }

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
            var decoded = new List<int>();

            for (int i = 0; i < Pictures.Count; i++)
            {
                decoded.Add(Pictures[i].Poc);
                remainingPocs.Remove(Pictures[i].Poc);

                int lowestUndecoded = remainingPocs.Count > 0 ? remainingPocs.Min : int.MaxValue;
                var waiting = decoded.Where(poc => poc > lowestUndecoded).ToList();
                MaxReorder = Math.Max(MaxReorder, waiting.Count);

                var occupancy = new HashSet<int>(waiting);
                foreach (var poc in Pictures[i].Retained)
                    occupancy.Add(poc);
                occupancy.Remove(Pictures[i].Poc);

                MaxDpbOccupancy = Math.Max(MaxDpbOccupancy, occupancy.Count + 1);
            }
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
        private void ComputeRetention()
        {
            var decodedBefore = new HashSet<int>();
            var positionOf = new Dictionary<int, int>();
            for (int i = 0; i < Pictures.Count; i++)
                positionOf[Pictures[i].Poc] = i;

            // futureNeeds[i] = every POC referenced by picture i or anything after it.
            var futureNeeds = new HashSet<int>[Pictures.Count];
            var running = new HashSet<int>();
            for (int i = Pictures.Count - 1; i >= 0; i--)
            {
                foreach (var poc in Pictures[i].UsedShortTerm)
                    running.Add(poc);
                if (Pictures[i].CrossViewPoc >= 0)
                    running.Add(Pictures[i].CrossViewPoc);

                futureNeeds[i] = new HashSet<int>(running);
            }

            for (int i = 0; i < Pictures.Count; i++)
            {
                var picture = Pictures[i];

                foreach (var poc in futureNeeds[i])
                {
                    if (poc == picture.Poc)
                        continue;
                    if (decodedBefore.Contains(poc))
                        picture.Retained.Add(poc);
                }

                picture.Retained.Sort();
                MaxRetained = Math.Max(MaxRetained, picture.Retained.Count);

                decodedBefore.Add(picture.Poc);
            }
        }

        /// <summary>
        /// Rewrites the video parameter set to describe a single layer. Every field naming the
        /// layer structure has to agree, or the result claims one layer in one place and two in
        /// another.
        /// </summary>
        private void CollapseVpsToSingleLayer()
        {
            var vps = _parser.Context.VideoParameterSetRbsp;
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
            var context = _parser.Context;
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
            var context = _parser.Context;
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
            sps.Log2MaxPicOrderCntLsbMinus4 = OutputLog2MaxPocLsbMinus4;
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
            var context = _parser.Context;
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

        /// <summary>Rewrites one picture's slice into a single-layer NAL unit.</summary>
        public byte[] RewriteSlice(OutputPicture picture)
        {
            var context = _parser.Context;
            var source = picture.Source;
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
                NuhTemporalIdPlus1 = source.NalUnit.NalUnitHeader.NuhTemporalIdPlus1,
            };
            context.NalHeader = nalUnit;

            ApplyReferenceSet(picture);

            bool isIdr = picture.OutputNalType == 19 || picture.OutputNalType == 20;
            if (!isIdr)
                header.SlicePicOrderCntLsb = (ulong)(picture.Poc & (MaxPocLsb(context) - 1));

            header.PicOutputFlag = (byte)(picture.Output ? 1 : 0);

            using var memory = new MemoryStream();
            using (var stream = new ItuStream(memory))
            {
                nalUnit.Write(context, stream);
                source.Slice.Write(context, stream);

                foreach (byte value in source.Payload)
                    stream.WriteUnsignedInt(8, value, null);
            }

            return memory.ToArray();
        }

        private static int MaxPocLsb(H265Context context) =>
            1 << (int)(context.SeqParameterSetRbsp.Log2MaxPicOrderCntLsbMinus4 + 4);

        /// <summary>
        /// Rebuilds the slice's reference picture set over the doubled picture order counts, and
        /// moves the cross-view reference into the long-term set.
        /// </summary>
        private void ApplyReferenceSet(OutputPicture picture)
        {
            var header = picture.Source.Header;
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
                header.PocLsbLt = new ulong[] { (ulong)(picture.CrossViewPoc & (MaxPocLsb(_parser.Context) - 1)) };
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
