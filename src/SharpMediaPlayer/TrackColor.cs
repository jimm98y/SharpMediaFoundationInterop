using SharpISOBMFF;
using SharpMediaFoundationInterop.Codecs;
using SharpMediaFoundationInterop.Utils;
using SharpMP4.Tracks;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// What a track's stream, and the boxes of its file, say of its samples: their bits and chroma, the frames a decoder
    /// hands out of them, and their color - as a <see cref="VideoColor"/>.
    /// </summary>
    internal static class TrackColor
    {
        /// <summary>
        /// The bits of the stream's samples and its chroma, of H.273's chroma_format_idc - 0 monochrome, 1 4:2:0, 2 4:2:2,
        /// 3 4:4:4 - as HEVC's and AVC's SPS and AV1's sequence header say them; null of another codec, taken to be 8 bits
        /// of 4:2:0.
        /// </summary>
        public static (int BitDepth, int ChromaFormat)? SamplesOf(ITrack track)
        {
            switch (track)
            {
                case H265Track hevc when hevc.Sps.Values.FirstOrDefault() is SharpH265.SeqParameterSetRbsp sps:
                    return ((int)sps.BitDepthLumaMinus8 + 8, (int)sps.ChromaFormatIdc);
                case H264Track avc when avc.Sps.Values.FirstOrDefault()?.SeqParameterSetData is SharpH264.SeqParameterSetData sps:
                    return ((int)sps.BitDepthLumaMinus8 + 8, (int)sps.ChromaFormatIdc);
                case AV1Track av1 when av1.SequenceHeaderObu is SharpAV1.AV1Context sequence:
                    return (sequence._HighBitdepth == 0 ? 8 : sequence._TwelveBit != 0 ? 12 : 10,
                        sequence._MonoChrome != 0 ? 0 : sequence._Subsamplingx == 0 ? 3 : sequence._Subsamplingy == 0 ? 2 : 1);
                default:
                    return null;
            }
        }

        /// <summary>
        /// The size of the frames an HEVC decoder hands out, of the stream's SPS: its coded size, of which the picture is
        /// cropped from the top left - a 1080 line picture is coded of 1088, its last 8 lines cropped - and not less than
        /// the picture's, rounded to the decoder's multiple. The frames' planes are laid out of it: of the picture's size,
        /// the chroma would be read 8 rows early.
        /// </summary>
        public static (uint Width, uint Height) HevcFrameSize(SharpH265.SeqParameterSetRbsp sps, uint width, uint height)
        {
            uint alignment = MediaCodecs.DecoderAlignment(VideoCodec.H265);
            return (Math.Max(MediaUtils.RoundToMultipleOf(width, alignment), (uint)sps.PicWidthInLumaSamples),
                Math.Max(MediaUtils.RoundToMultipleOf(height, alignment), (uint)sps.PicHeightInLumaSamples));
        }

        /// <summary>
        /// Throws where Windows' decoder of the codec decodes no samples of these bits and chroma - where it would hand out
        /// nothing, and a video end before it starts: HEVC's of more than 10 bits, and of monochrome of more than 8; AVC's
        /// of other than 8 bits of 4:2:0.
        /// </summary>
        public static void ThrowIfNotDecoded(ITrack track, int bitDepth, int chromaFormat)
        {
            string reason = track switch
            {
                H265Track when bitDepth > 10 => $"HEVC of {bitDepth} bits: Windows' HEVC decoder decodes 10 at most",
                H265Track when chromaFormat == 0 && bitDepth > 8 => $"monochrome HEVC of {bitDepth} bits: Windows' HEVC decoder decodes 8 of it at most",
                H264Track when bitDepth != 8 || chromaFormat != 1 => $"AVC of {bitDepth} bits of chroma_format_idc {chromaFormat}: Windows' AVC decoder decodes 8 bits of 4:2:0 alone",
                _ => null,
            };
            if (reason != null)
                throw new NotSupportedException($"Not decoded: {reason}.");
        }

        /// <summary>
        /// What the decoder hands out of samples of the chroma and the bits: the one format Windows' hands out of each - more
        /// than 8 bits in 16, but 10 of 4:4:4 in Y410's 32 a pixel.
        /// </summary>
        public static VideoFrameFormat FrameFormatOf(int chromaFormat, int bitDepth)
        {
            if (bitDepth != 8 && bitDepth != 10 && bitDepth != 12)
                throw new NotSupportedException($"Samples of {bitDepth} bits are not supported.");
            return (chromaFormat, bitDepth) switch
            {
                (0, 8) => VideoFrameFormat.L8,
                (0, _) => VideoFrameFormat.L16,
                (1, 8) => VideoFrameFormat.NV12,
                (1, 10) => VideoFrameFormat.P010,
                (1, _) => VideoFrameFormat.P016,
                (2, 8) => VideoFrameFormat.YUY2,
                (2, 10) => VideoFrameFormat.Y210,
                (2, _) => VideoFrameFormat.Y216,
                (3, 8) => VideoFrameFormat.AYUV,
                (3, 10) => VideoFrameFormat.Y410,
                (3, _) => VideoFrameFormat.Y416,
                _ => throw new NotSupportedException($"Samples of chroma_format_idc {chromaFormat} are not supported."),
            };
        }

        /// <summary>
        /// The color of a picture of these 'colr's - an 'nclx', an ICC profile, either, both or none - coded in this stream:
        /// of the ICC profile where it is of a matrix and curves, for the primaries and the transfer; else of the 'nclx', else
        /// of the stream's - HEVC's or AVC's VUI, AV1's sequence header - but of a video whose stream says it, the stream's.
        /// The matrix and the range are of the same: an ICC profile has none.
        /// </summary>
        /// <param name="sceneReferred">An image's, whose camera's curve is undone; a video's is shown as sRGB's.</param>
        /// <param name="height">Of a video, its height: of what matrix it is of where it says none - see <see cref="VideoColor.FromCodes"/>.</param>
        public static VideoColor Of(IEnumerable<ColourInformationBox> colors, ITrack track, int bitDepth, bool sceneReferred, uint height = 0)
        {
            colors = colors.ToList();
            var nclx = colors.FirstOrDefault(x => x.ColourType == IsoStream.FromFourCC("nclx"));

            // A video's stream says what its decoder's samples are, where it says it: over its sample entry's 'colr', which
            // can say otherwise - a file of GBR, its 'nclx' of BT.709's matrix. An image's 'colr' is said over its stream's,
            // as HEIF says.
            int primaries = 2, transfer = 2, matrix = 2;
            bool fullRange = false;
            var stream = StreamColor(track);
            bool streamDescribes = stream is { } said && (said.Primaries != 2 || said.Transfer != 2 || said.Matrix != 2);
            if (nclx != null && (sceneReferred || !streamDescribes))
                (primaries, transfer, matrix, fullRange) = (nclx.ColourPrimaries, nclx.TransferCharacteristics, nclx.MatrixCoefficients, nclx.FullRangeFlag);
            else if (stream is { } fromStream)
                (primaries, transfer, matrix, fullRange) = fromStream;

            // an ICC profile, of a matrix and curves, says the primaries and the transfer over what else does
            var prof = colors.FirstOrDefault(x => x.ColourType == IsoStream.FromFourCC("prof") || x.ColourType == IsoStream.FromFourCC("rICC"));
            if (prof?.ICCProfile?.Data is StreamMarker marker && ReadBytes(marker) is byte[] icc
                && VideoColor.FromIcc(icc, matrix, fullRange, bitDepth, height) is VideoColor profiled)
                return profiled;

            return VideoColor.FromCodes(primaries, transfer, matrix, fullRange, bitDepth, sceneReferred, height);
        }

        /// <summary>What a stream says of its color: HEVC's and AVC's VUI, AV1's sequence header; null where it says nothing.</summary>
        private static (int Primaries, int Transfer, int Matrix, bool FullRange)? StreamColor(ITrack track)
        {
            switch (track)
            {
                case H265Track hevc when hevc.Sps.Values.FirstOrDefault() is SharpH265.SeqParameterSetRbsp sps
                    && sps.VuiParametersPresentFlag != 0 && sps.VuiParameters is SharpH265.VuiParameters vui:
                    return vui.ColourDescriptionPresentFlag != 0
                        ? ((int)vui.ColourPrimaries, (int)vui.TransferCharacteristics, (int)vui.MatrixCoeffs, vui.VideoFullRangeFlag != 0)
                        : (2, 2, 2, vui.VideoFullRangeFlag != 0);

                case H264Track avc when avc.Sps.Values.FirstOrDefault()?.SeqParameterSetData is SharpH264.SeqParameterSetData sps
                    && sps.VuiParametersPresentFlag != 0 && sps.VuiParameters is SharpH264.VuiParameters vui:
                    return vui.ColourDescriptionPresentFlag != 0
                        ? ((int)vui.ColourPrimaries, (int)vui.TransferCharacteristics, (int)vui.MatrixCoefficients, vui.VideoFullRangeFlag != 0)
                        : (2, 2, 2, vui.VideoFullRangeFlag != 0);

                case AV1Track av1 when av1.SequenceHeaderObu is SharpAV1.AV1Context sequence:
                    // AV1 says the same codes, of H.273; with none, BT.709's primaries and sRGB's transfer, and BT.601's matrix
                    return sequence._ColorDescriptionPresentFlag != 0
                        ? (sequence._ColorPrimaries, sequence._TransferCharacteristics, sequence._MatrixCoefficients, sequence._ColorRange != 0)
                        : (2, 2, 2, sequence._ColorRange != 0);

                case MjpegTrack:
                    // JPEG's samples are JFIF's (ITU-T T.871 7): BT.601's matrix of the full range, of sRGB's primaries and
                    // curve - as Windows' decoder hands them out, unscaled
                    return (1, 13, 6, true);

                default:
                    return null;
            }
        }

        private static byte[] ReadBytes(StreamMarker marker)
        {
            if (marker.Stream == null || marker.Length <= 0 || marker.Length > 16 * 1024 * 1024)
                return null;
            if (marker.Stream.GetCurrentOffset() != marker.Position)
                marker.Stream.SeekFromBeginning(marker.Position);
            marker.Stream.ReadBytes((ulong)marker.Length, out byte[] bytes);
            return bytes;
        }
    }
}
