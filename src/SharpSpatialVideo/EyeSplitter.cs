using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>
    /// Splits Apple spatial video into one ordinary MP4 per eye without decoding or re-encoding.
    ///
    /// The base view is already an ordinary single layer stream and goes through untouched. The
    /// dependent view cannot: it is predicted from the base pictures, so its file carries those
    /// too, marked not to be output, and a decoder presents only the dependent eye.
    ///
    /// <see cref="LeftRightTranscoder"/> is the way back, and the pair is exact: what comes out of
    /// it is what went into this, byte for byte.
    /// </summary>
    public static class EyeSplitter
    {
        public sealed class Result
        {
            /// <summary>The file holding the base view, and which eye it turned out to be.</summary>
            public string BasePath { get; set; }
            public string BaseEye { get; set; }
            public int BasePictures { get; set; }

            /// <summary>The file holding the dependent view, which also carries the base pictures.</summary>
            public string DependentPath { get; set; }
            public string DependentEye { get; set; }
            public int DependentPictures { get; set; }
        }

        /// <summary>
        /// Writes both eyes beside each other in <paramref name="outputDirectory"/>, named after
        /// the input and the eye each holds.
        /// </summary>
        /// <param name="baseLayerIsLeftEye">
        /// Which eye the base layer carries. Measured from the disparity between the decoded views
        /// rather than taken from the file: a feature sits further right in the left eye, and in
        /// Apple's footage it sits further right in the dependent view. The stri box claims
        /// otherwise, which is why this is a decision the caller makes.
        /// </param>
        public static Result Write(string path, string outputDirectory, bool baseLayerIsLeftEye)
        {
            // Only the index is read here; the pictures are read one at a time as they are
            // written, so a recording of any length costs the same.
            var track = MvHevcReader.Read(path, loadSamples: false);
            using var rewriter = new SingleLayerRewriter(track);
            rewriter.Plan();

            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            Directory.CreateDirectory(outputDirectory);
            string stem = Path.GetFileNameWithoutExtension(path);
            int framePeriod = (int)track.FpsDenom;   // one access unit, in source timescale units

            // The base view's NAL units and its original picture order counts go through as they
            // are, each read out of the source as it is written.
            var basePlan = rewriter.Pictures
                .Where(p => p.View == 0)
                .OrderBy(p => p.DecodeIndex)
                .ToList();

            var result = new Result
            {
                BaseEye = baseLayerIsLeftEye ? "left" : "right",
                DependentEye = baseLayerIsLeftEye ? "right" : "left",
                BasePictures = basePlan.Count,
            };

            byte[] readBuffer = null;

            IEnumerable<MuxPicture> BasePictures()
            {
                foreach (var picture in basePlan)
                {
                    yield return new MuxPicture
                    {
                        Nalu = MvHevcReader.ReadNalu(source, picture.SourceOffset, picture.SourceLength, ref readBuffer),
                        Poc = picture.SourcePoc,
                        IsRandomAccessPoint = picture.SourceIsIrap,
                        Duration = (int)picture.AccessUnitDuration,
                    };
                }
            }

            result.BasePath = Path.Combine(outputDirectory, $"{stem}_{result.BaseEye}.mp4");
            var baseParameterSets = rewriter.BuildBaseViewParameterSets(track.BaseParameterSets);
            EyeMuxer.Write(result.BasePath, baseParameterSets, BasePictures(), track.Timescale, framePeriod,
                track, basePlan.Count > 0 ? basePlan.Min(p => p.SourcePoc) : 0,
                basePlan.Count > 0 ? (int)basePlan[0].AccessUnitDuration : 1);

            // The dependent view: the whole rewritten stream, on a timescale PocScale times finer
            // so the picture order count slots of an access unit - one a view - each land on a
            // whole tick.
            int dpbSize = Math.Max(rewriter.MaxDpbOccupancy + 1, 8);
            var parameterSets = rewriter.BuildParameterSets(dpbSize, rewriter.MaxReorder);

            foreach (var picture in rewriter.Pictures)
                picture.Output = picture.View == 1;

            // An access unit holds a picture a view, or only the base one where the source has
            // no dependent picture. Splitting its frame period between them keeps every access unit exactly
            // one frame long, so the track duration matches the source.
            var picturesPerAccessUnit = rewriter.Pictures
                .GroupBy(p => p.AccessUnitIndex)
                .ToDictionary(g => g.Key, g => g.Count());

            int dependentPeriod = framePeriod * SingleLayerRewriter.PocScale;
            var dependentPlan = rewriter.Pictures.OrderBy(p => p.DecodeIndex).ToList();

            // What the split has to rewrite, kept so the way back can put the original together
            // again - see RoundTripSei. It is worked out from the plan, before any picture is read.
            var carried = WhatWasRewritten(path, rewriter);

            IEnumerable<MuxPicture> DependentPictures()
            {
                foreach (var picture in dependentPlan)
                {
                    var muxPicture = new MuxPicture
                    {
                        Nalu = rewriter.RewriteSlice(picture),
                        Poc = picture.Poc,
                        IsRandomAccessPoint = picture.OutputNalType >= 16 && picture.OutputNalType <= 23,
                        Duration = dependentPeriod / picturesPerAccessUnit[picture.AccessUnitIndex],
                    };

                    if (carried.TryGetValue(picture.DecodeIndex, out var prefix))
                        muxPicture.Prefix.AddRange(prefix);

                    yield return muxPicture;
                }
            }

            result.DependentPath = Path.Combine(outputDirectory, $"{stem}_{result.DependentEye}.mp4");
            result.DependentPictures = dependentPlan.Count;
            EyeMuxer.Write(result.DependentPath, parameterSets, DependentPictures(),
                track.Timescale * (uint)SingleLayerRewriter.PocScale, framePeriod, track,
                dependentPlan.Count > 0 ? dependentPlan.Min(p => p.Poc) : 0,
                dependentPeriod);

            return result;
        }

        /// <summary>
        /// What the split has to rewrite, as the user data SEIs that carry it - see
        /// <see cref="RoundTripSei"/> - against the picture each goes in front of, by that
        /// picture's place in decode order. Without them the way back builds a file that decodes to
        /// the same pictures; with them it builds the same file. All of it comes from the plan and
        /// the boxes, so no picture has to be read.
        /// </summary>
        /// <remarks>
        /// Everything is filed by where it belongs in decode order rather than by picture order
        /// count. A recording whose key frames are IDR pictures - which is what this tool's own
        /// side by side conversion writes - starts the count again at every one of them, so one
        /// picture order count names one picture per key frame, and filing by it silently piles
        /// every key frame's worth of headers onto the same picture.
        /// </remarks>
        private static Dictionary<int, List<byte[]>> WhatWasRewritten(string path, SingleLayerRewriter rewriter)
        {
            var carried = new Dictionary<int, List<byte[]>>();

            void Add(int decodeIndex, byte[] sei)
            {
                if (!carried.TryGetValue(decodeIndex, out var list))
                    carried[decodeIndex] = list = new List<byte[]>();

                list.Add(sei);
            }

            var first = rewriter.Pictures.OrderBy(p => p.DecodeIndex).First();

            // Which picture carries an access unit's own SEIs: the dependent one, which is where
            // the way back looks for them.
            var dependentPictures = rewriter.Pictures
                .Where(p => p.View == 1)
                .ToDictionary(p => p.AccessUnitIndex);

            // The file itself, with its coded samples taken out: the boxes, the sample tables and
            // the tracks that are carried along untouched, so the way back writes the same file
            // rather than an equivalent one.
            Add(first.DecodeIndex, RoundTripSei.ForContainer(ContainerLayout.Capture(path)));

            // The multiview video parameter set and the eye mapping, which the split replaces with
            // single layer ones, and layer 1's own parameter sets, which it drops.
            var track = MvHevcReader.Read(path, loadSamples: false);
            Add(first.DecodeIndex, RoundTripSei.ForParameterSets(track.BaseParameterSets
                .Where(n => ((n[0] >> 1) & 0x3F) is 32 or 39)
                .Concat(track.LayerParameterSets)
                .ToList()));

            // Each of layer 1's slice headers, against the access unit it came from.
            foreach (var picture in dependentPictures.Values)
                Add(picture.DecodeIndex,
                    RoundTripSei.ForSliceHeader(picture.AccessUnitIndex, picture.SourceHeaderBytes));

            // And whatever else an access unit held besides its two pictures, which the plan
            // collected as it went: where each sat matters, so that is kept with it.
            foreach (var entry in rewriter.OtherNalus)
            {
                var extra = entry.Value
                    .Select(x => new RoundTripSei.ExtraNalu { Data = x.Data, BeforeBasePicture = x.BeforeBasePicture })
                    .ToList();

                // Against the dependent picture of that access unit, which is where the way back
                // looks for it.
                if (dependentPictures.TryGetValue(entry.Key, out var picture))
                    Add(picture.DecodeIndex, RoundTripSei.ForExtraNalus(entry.Key, extra));
            }

            return carried;
        }

        /// <summary>What the stri box says about the eyes, which split reports for comparison.</summary>
        public static string StereoViewInfo(MvHevcTrack track) =>
            $"has_left={track.HasLeftEyeView} has_right={track.HasRightEyeView} " +
            $"reversed={track.EyeViewsReversed} (raw 0x{track.StereoViewInfo:X2})";
    }
}
