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
        }

        public static void Write(string sourcePath, string outputPath, Options options)
        {
            var track = MvHevcReader.Read(sourcePath);

            var layerParameterSets = options.LayerExtensions.HasValue
                ? WithLayerExtensions(track.BaseParameterSets, track.LayerParameterSets, options.LayerExtensions.Value)
                : track.LayerParameterSets;

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

            var accessUnits = new List<MultiviewAccessUnit>();
            foreach (var source in track.AccessUnits)
            {
                bool key = source.Nalus.Any(n => n.IsIrap);
                var accessUnit = new MultiviewAccessUnit
                {
                    Duration = (int)source.Duration,
                    CompositionOffset = source.CompositionOffset,
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

                    (inLayer ? accessUnit.LayerNalus : accessUnit.BaseNalus).Add(nalu.Data);
                }

                accessUnits.Add(accessUnit);
            }

            MvHevcWriter.Write(outputPath, track.BaseParameterSets, layerParameterSets,
                accessUnits, track.Timescale, new StereoMetadata(), track.HasAudio ? track : null);
        }

        private static bool IsUserDataSei(Nalu nalu) =>
            nalu.Type == H265NALTypes.PREFIX_SEI_NUT && nalu.Data.Length > 2 && nalu.Data[2] == 5;

        /// <summary>
        /// Layer 1's parameter sets with the multilayer extensions added or removed. Every field in
        /// them is at its inferred value, so a slice parses the same either way.
        /// </summary>
        public static List<byte[]> WithLayerExtensions(List<byte[]> baseParameterSets,
            List<byte[]> layerParameterSets, bool present)
        {
            var parser = new MvHevcParser();
            parser.ParseParameterSets(baseParameterSets);

            var result = new List<byte[]>();
            foreach (var nalu in layerParameterSets)
            {
                uint type = (uint)((nalu[0] >> 1) & 0x3F);
                uint layer = (uint)(((nalu[0] & 1) << 5) | (nalu[1] >> 3));
                parser.ParseParameterSets(new[] { nalu });
                var context = parser.Context;

                if (type == H265NALTypes.SPS_NUT)
                {
                    var sps = context.SeqParameterSetRbsp;
                    SetMultilayerExtension(sps, present);
                    result.Add(MultiviewBuilder.WriteNalUnit(context, type, layer, s => sps.Write(context, s)));
                }
                else if (type == H265NALTypes.PPS_NUT)
                {
                    var pps = context.PicParameterSetRbsp;
                    SetMultilayerExtension(pps, present);
                    result.Add(MultiviewBuilder.WriteNalUnit(context, type, layer, s => pps.Write(context, s)));
                }
                else
                {
                    result.Add(nalu);
                }
            }
            return result;
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
