using SharpH265;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>
    /// Remuxes an MV-HEVC file with one thing about it changed, to find out which differences
    /// between Apple's files and ours decide whether a Mac calls a file spatial video. The coded
    /// pictures are not touched, so each variant differs from its source in exactly what was asked
    /// for and nothing else.
    /// </summary>
    public static class SpatialVariants
    {
        public sealed class Options
        {
            /// <summary>Drops the user data SEI (payload type 5) Apple writes before each key frame.</summary>
            public bool DropUserDataSei { get; set; }

            /// <summary>Puts a file's key frame user data SEI in front of every key frame of this one.</summary>
            public string UserDataSeiFrom { get; set; }

            /// <summary>True adds the multilayer extensions to layer 1's SPS and PPS, false removes them, null leaves them.</summary>
            public bool? LayerExtensions { get; set; }

            /// <summary>True gives the base SPS the colour description Apple's has, false takes it away, null leaves it.</summary>
            public bool? ColourDescription { get; set; }

            /// <summary>
            /// Re-labels layer 1's IDR pictures as CRA, which is what Apple's layer 1 uses. An IDR
            /// above the base layer cannot predict from the layer below it at all - it has no
            /// reference list - so a player may read a layer of IDRs as a second video rather than
            /// as the other eye.
            /// </summary>
            public bool CraAtLayerOne { get; set; }

            /// <summary>
            /// Gives every sample the same duration and no composition offset, as our files have:
            /// one stts entry and no ctts. Apple's timing is variable, and its pictures are coded
            /// out of presentation order, so this is a test of the timing tables and not something
            /// to keep - it changes when Apple's pictures are shown.
            /// </summary>
            public bool FlatTiming { get; set; }
        }

        public static void Write(string sourcePath, string outputPath, Options options)
        {
            var track = MvHevcReader.Read(sourcePath);

            // One parser for both: layer 1's sets are read against the video parameter set and the
            // base layer's, as a decoder reads them.
            var parser = new MvHevcParser();

            var baseParameterSets = Rewrite(track.BaseParameterSets, parser,
                sps => { if (options.ColourDescription.HasValue) SetColourDescription(sps, options.ColourDescription.Value); },
                pps => { });

            var layerParameterSets = Rewrite(track.LayerParameterSets, parser,
                sps => { if (options.LayerExtensions.HasValue) SetMultilayerExtension(sps, options.LayerExtensions.Value); },
                pps => { if (options.LayerExtensions.HasValue) SetMultilayerExtension(pps, options.LayerExtensions.Value); });

            // Apple's key frames carry two: one in front of the base picture, one in front of the
            // layer 1 picture - both at nuh_layer_id 0.
            List<byte[]> seiBeforeBase = null, seiBeforeLayer = null;
            if (options.UserDataSeiFrom != null)
            {
                var first = MvHevcReader.Read(options.UserDataSeiFrom).AccessUnits[0].Nalus;
                int baseSlice = first.FindIndex(n => n.IsSlice);
                int layerSlice = first.FindIndex(n => n.IsSlice && n.LayerId == 1);
                seiBeforeBase = first.Take(baseSlice).Where(IsUserDataSei).Select(n => n.Data).ToList();
                seiBeforeLayer = first.Skip(baseSlice).Take(layerSlice - baseSlice)
                    .Where(IsUserDataSei).Select(n => n.Data).ToList();
                Console.WriteLine($"  user data SEI: {seiBeforeBase.Count} before the base picture, {seiBeforeLayer.Count} before layer 1's");
            }

            // Built from the sets that go into the file, so a re-written slice is written against
            // the same parameter sets a decoder will read it with.
            var restamper = options.CraAtLayerOne
                ? new LayerRestamper(baseParameterSets.Concat(layerParameterSets))
                : null;
            int relabelled = 0;

            // The average, so the track keeps the length it had.
            int flatDuration = (int)Math.Round(track.AccessUnits.Average(a => (double)a.Duration));

            var accessUnits = new List<MultiviewAccessUnit>();
            foreach (var source in track.AccessUnits)
            {
                bool key = source.Nalus.Any(n => n.IsIrap);
                var accessUnit = new MultiviewAccessUnit
                {
                    Duration = options.FlatTiming ? flatDuration : (int)source.Duration,
                    CompositionOffset = options.FlatTiming ? 0 : source.CompositionOffset,
                    IsRandomAccessPoint = key,
                };

                if (key && seiBeforeBase != null)
                    accessUnit.BaseNalus.AddRange(seiBeforeBase);

                // Everything in front of layer 1's first slice is the base picture's; the writer
                // puts BaseNalus first, so that keeps the order.
                bool inLayer = false;
                foreach (var nalu in source.Nalus)
                {
                    if (LayerRestamper.IsParameterSet(nalu.Data))
                        continue;
                    if (options.DropUserDataSei && IsUserDataSei(nalu))
                        continue;

                    if (nalu.IsSlice && nalu.LayerId == 1 && !inLayer)
                    {
                        inLayer = true;
                        if (key && seiBeforeLayer != null)
                            accessUnit.LayerNalus.AddRange(seiBeforeLayer);
                    }

                    var data = nalu.Data;

                    // The picture order count of an access unit whose base picture is an IDR is
                    // zero, and every picture in an access unit shares one, so that is what the
                    // re-labelled picture takes.
                    if (restamper != null && nalu.LayerId == 1 && nalu.IsIdr)
                    {
                        data = restamper.Restamp(data, 1, restamper.PicParameterSetIdOf(data), 0, 21);
                        relabelled++;
                    }

                    (inLayer ? accessUnit.LayerNalus : accessUnit.BaseNalus).Add(data);
                }

                accessUnits.Add(accessUnit);
            }

            if (restamper != null)
                Console.WriteLine($"  re-labelled {relabelled} layer 1 IDR pictures as CRA");

            MvHevcWriter.Write(outputPath, baseParameterSets, layerParameterSets,
                accessUnits, track.Timescale, new StereoMetadata(), track.HasAudio ? track : null);
        }

        private static bool IsUserDataSei(Nalu nalu) =>
            nalu.Type == H265NALTypes.PREFIX_SEI_NUT && nalu.Data.Length > 2 && nalu.Data[2] == 5;

        /// <summary>
        /// Reads each parameter set, lets the caller change it, and writes it back. A set nothing
        /// was done to comes back as it went in, and anything else is a bug in the read or the
        /// write rather than the change asked for - so it says so.
        /// </summary>
        private static List<byte[]> Rewrite(List<byte[]> parameterSets, MvHevcParser parser,
            Action<SeqParameterSetRbsp> onSps, Action<PicParameterSetRbsp> onPps)
        {
            var result = new List<byte[]>();
            foreach (var nalu in parameterSets)
            {
                uint type = (uint)((nalu[0] >> 1) & 0x3F);
                uint layer = (uint)(((nalu[0] & 1) << 5) | (nalu[1] >> 3));
                parser.ParseParameterSets(new[] { nalu });
                var context = parser.Context;

                Func<byte[]> write;
                if (type == H265NALTypes.SPS_NUT)
                {
                    var sps = context.SeqParameterSetRbsp;
                    write = () => MultiviewBuilder.WriteNalUnit(context, type, layer, s => sps.Write(context, s));
                    CheckRoundTrip(nalu, write());
                    onSps(sps);
                }
                else if (type == H265NALTypes.PPS_NUT)
                {
                    var pps = context.PicParameterSetRbsp;
                    write = () => MultiviewBuilder.WriteNalUnit(context, type, layer, s => pps.Write(context, s));
                    CheckRoundTrip(nalu, write());
                    onPps(pps);
                }
                else
                {
                    result.Add(nalu);
                    continue;
                }

                result.Add(write());
            }
            return result;
        }

        private static void CheckRoundTrip(byte[] read, byte[] written)
        {
            if (!written.SequenceEqual(read))
                throw new InvalidOperationException(
                    $"a parameter set does not round trip: {Convert.ToHexString(read)} came back as {Convert.ToHexString(written)}");
        }

        /// <summary>
        /// Gives the sequence parameter set the video signal type Apple's carries - unspecified
        /// video format, limited range, BT.709 primaries, transfer and matrix - or takes it away.
        /// The VUI is read by a display, not by the slice decoder, so this changes nothing about
        /// how the pictures decode.
        /// </summary>
        public static void SetColourDescription(SeqParameterSetRbsp sps, bool present)
        {
            var vui = sps.VuiParameters
                ?? throw new InvalidOperationException("the sequence parameter set has no VUI to change");

            vui.VideoSignalTypePresentFlag = (byte)(present ? 1 : 0);
            if (!present)
                return;

            vui.VideoFormat = 5;
            vui.VideoFullRangeFlag = 0;
            vui.ColourDescriptionPresentFlag = 1;
            vui.ColourPrimaries = 1;
            vui.TransferCharacteristics = 1;
            vui.MatrixCoeffs = 1;
        }

        /// <summary>
        /// Adds sps_multilayer_extension() with inter_view_mv_vert_constraint_flag 0, as Apple's
        /// layer 1 SPS has it, or takes it away.
        /// </summary>
        public static void SetMultilayerExtension(SeqParameterSetRbsp sps, bool present)
        {
            if (sps.SpsRangeExtensionFlag != 0 || sps.Sps3dExtensionFlag != 0 || sps.SpsSccExtensionFlag != 0 || sps.SpsExtension4bits != 0)
                throw new InvalidOperationException("the SPS carries another extension, which this does not keep");

            sps.SpsExtensionPresentFlag = (byte)(present ? 1 : 0);
            sps.SpsMultilayerExtensionFlag = (byte)(present ? 1 : 0);
            sps.SpsMultilayerExtension = present ? new SpsMultilayerExtension() : null;
        }

        /// <summary>
        /// Adds pps_multilayer_extension() with every flag 0 and no reference location offsets, as
        /// Apple's layer 1 PPS has it, or takes it away.
        /// </summary>
        public static void SetMultilayerExtension(PicParameterSetRbsp pps, bool present)
        {
            if (pps.PpsRangeExtensionFlag != 0 || pps.Pps3dExtensionFlag != 0 || pps.PpsSccExtensionFlag != 0 || pps.PpsExtension4bits != 0)
                throw new InvalidOperationException("the PPS carries another extension, which this does not keep");

            pps.PpsExtensionPresentFlag = (byte)(present ? 1 : 0);
            pps.PpsMultilayerExtensionFlag = (byte)(present ? 1 : 0);
            pps.PpsMultilayerExtension = present ? new PpsMultilayerExtension() : null;
        }
    }
}
