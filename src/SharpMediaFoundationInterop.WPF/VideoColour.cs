using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SharpMediaFoundationInterop.Codecs;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// How a picture's decoded samples - Y, Cb and Cr of 8, 10 or 12 bits - become the sRGB it is shown as, of the codes of
    /// ITU-T H.273 a stream says its colour by: of its matrix and range, the samples made into three values - R'G'B' of
    /// most matrices - and those, of its transfer, made linear; of its primaries made into sRGB's - colours sRGB has not
    /// are clipped - and encoded by sRGB's transfer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What follows the three values is one function of them, <see cref="Display"/>: made exactly of each pixel of an
    /// image, and of a video's frames by a table of it - in three dimensions, looked up between its steps - which the CPU
    /// reads, and a GPU's shader as a texture. Where the values are sRGB's already there is none: they are shown as they
    /// are.
    /// </para>
    /// <para>
    /// HDR - PQ, HLG - is shown as SDR: its reference white, 203 cd/m² (ITU-R BT.2408), as sRGB's white, and what is
    /// brighter rolled off below it. A camera's curve - BT.709's and the like - is undone into the scene's light, an
    /// image's, where it is <see cref="SceneReferred"/>; a video's is shown as sRGB's, as a video is made to be seen on a
    /// display of a curve like it, and as players show it.
    /// </para>
    /// </remarks>
    public sealed class VideoColour
    {
        // H.273 MatrixCoefficients
        private const int MatrixIdentity = 0, MatrixYCgCo = 8, MatrixBt2020Cl = 10, MatrixSt2085 = 11, MatrixChromaticityCl = 13, MatrixICtCp = 14;

        // H.273 TransferCharacteristics of HDR
        private const int TransferPq = 16, TransferHlg = 18;

        /// <summary>BT.2408's reference white of HDR, which sRGB's white is made of: cd/m².</summary>
        private const double ReferenceWhite = 203;

        /// <summary>The nominal peak of HLG, of its OOTF: cd/m².</summary>
        private const double HlgPeak = 1000;

        /// <summary>Where HDR's roll-off starts, of sRGB's white: below it, HDR is as it is.</summary>
        private const double RollOffKnee = 0.75;

        /// <summary>What the three values are: R'G'B' of the primaries; R', Y' and B' of constant luminance; ICtCp's L'M'S'.</summary>
        private enum Values { Rgb, ConstantLuminance, Lms }

        private readonly Values _values;
        private readonly int _transfer;
        private readonly double _kr, _kb;

        /// <summary>The bits of a sample: 8, 10 or 12.</summary>
        public int BitDepth { get; }

        /// <summary>Whether the samples are of the full range, not the limited.</summary>
        public bool FullRange { get; }

        /// <summary>Whether a camera's curve is undone into the scene's light, as an image's is; else shown as sRGB's.</summary>
        public bool SceneReferred { get; }

        /// <summary>What the colour is of, as it was said: of the codes, or of an ICC profile.</summary>
        public string Description { get; }

        /// <summary>Whether the three values are sRGB's R'G'B' already: shown as they are, of no table.</summary>
        public bool IsSrgb { get; }

        /// <summary>
        /// Whether a GPU's video processor shows it as well as this does: 8 bit SDR of BT.709's primaries, or sRGB's, of
        /// BT.601's or BT.709's matrix - what it is told of, the matrix and the range - and of R'G'B' shown as it is.
        /// </summary>
        public bool IsVideoProcessorColour { get; }

        /// <summary>Of the video processor's, whether the matrix is BT.709's, not BT.601's.</summary>
        internal bool IsBt709Matrix { get; }

        // of each code, its part of each of the three values: of the luma, of Cb and of Cr - what a value is of the codes is
        // their sum
        private readonly float[][] _ofY, _ofCb, _ofCr;

        // the coefficients of the same, of a shader: of the luma, and of Cb and Cr below 0 and above - of constant luminance
        // they differ - and what a code is of the range, its offset and its scale, of the luma and of the chroma
        internal readonly float[] YCoefficients, CbBelow, CbAbove, CrBelow, CrAbove;
        internal readonly float LumaOffset, LumaScale, ChromaOffset, ChromaScale;

        // the three values made linear, each of its own curve - an ICC profile has one of each - from LinearFrom to
        // LinearTo; the curves themselves of a value outside them
        private const double LinearFrom = -0.25, LinearTo = 1.25;
        private const int LinearSteps = 6144;
        private readonly float[][] _toLinear;
        private readonly Func<double, double>[] _toLinearFunction;

        /// <summary>Linear RGB of the primaries to linear sRGB, row by row; for HLG's OOTF, their luminance.</summary>
        private readonly double[] _toSrgb;
        private readonly double[] _luminance;

        // ICtCp's L'M'S' of I, Ct and Cp, and linear LMS to BT.2020's RGB
        private static readonly double[] ICtCpToLms = Invert(new[]
        {
            0.5, 0.5, 0,
            6610 / 4096.0, -13613 / 4096.0, 7003 / 4096.0,
            17933 / 4096.0, -17390 / 4096.0, -543 / 4096.0,
        });
        private static readonly double[] LmsToBt2020 = Invert(new[]
        {
            1688 / 4096.0, 2146 / 4096.0, 262 / 4096.0,
            683 / 4096.0, 2951 / 4096.0, 462 / 4096.0,
            99 / 4096.0, 309 / 4096.0, 3688 / 4096.0,
        });

        private VideoColour(int matrix, bool fullRange, int bitDepth, double[] primariesToXyzD65, Func<double, double>[] toLinear,
            int transfer, bool isSrgb, bool sceneReferred, bool videoProcessor, string description)
        {
            if (bitDepth != 8 && bitDepth != 10 && bitDepth != 12)
                throw new ArgumentOutOfRangeException(nameof(bitDepth), bitDepth, "8, 10 or 12");

            BitDepth = bitDepth;
            FullRange = fullRange;
            SceneReferred = sceneReferred;
            Description = description;
            _transfer = transfer;
            _values = matrix switch
            {
                MatrixBt2020Cl or MatrixChromaticityCl => Values.ConstantLuminance,
                MatrixICtCp => Values.Lms,
                _ => Values.Rgb,
            };
            IsSrgb = isSrgb && _values == Values.Rgb;
            IsVideoProcessorColour = videoProcessor && bitDepth == 8 && IsSrgb && matrix is 1 or 2 or 5 or 6;
            IsBt709Matrix = matrix == 1;

            // ICtCp is of BT.2020's primaries, whatever is said
            if (matrix == MatrixICtCp)
                primariesToXyzD65 = PrimariesToXyzD65(9);

            // the luminance of the primaries: of the chromaticity derived matrices, their Kr and Kb
            _luminance = new[] { primariesToXyzD65[3], primariesToXyzD65[4], primariesToXyzD65[5] };
            (_kr, _kb) = matrix switch
            {
                1 => (0.2126, 0.0722),
                4 => (0.30, 0.11),
                7 => (0.212, 0.087),
                9 or 10 => (0.2627, 0.0593),
                12 or 13 => (_luminance[0], _luminance[2]),
                _ => (0.299, 0.114), // 5 and 6, BT.601, and what says nothing
            };
            _toSrgb = Multiply(XyzD65ToSrgb, primariesToXyzD65);

            _toLinearFunction = toLinear;
            _toLinear = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                if (c > 0 && toLinear[c] == toLinear[0])
                {
                    _toLinear[c] = _toLinear[0];
                    continue;
                }
                var table = new float[LinearSteps + 1];
                for (int i = 0; i <= LinearSteps; i++)
                    table[i] = (float)toLinear[c](LinearFrom + (LinearTo - LinearFrom) * i / LinearSteps);
                _toLinear[c] = table;
            }

            // Of n bits, the limited range is 8 bits' scaled up: luma of 16 to 235, chroma of 16 to 240, each times
            // 2^(n-8); the full, of 0 to 2^n - 1, the chroma's 0 at 2^(n-1). Identity's chroma is of the luma's scale, as
            // it is no chroma.
            int codes = 1 << bitDepth;
            double unit = 1 << (bitDepth - 8);
            LumaOffset = fullRange ? 0 : (float)(16 * unit);
            LumaScale = (float)(fullRange ? 1.0 / (codes - 1) : 1 / (219 * unit));
            ChromaOffset = matrix == MatrixIdentity ? LumaOffset : fullRange ? codes / 2 : (float)(128 * unit);
            ChromaScale = matrix == MatrixIdentity ? LumaScale : (float)(fullRange ? 1.0 / (codes - 1) : 1 / (224 * unit));

            // the three values of the luma, Cb and Cr of the range: each of the luma, and of Cb and Cr below 0 and above
            double kg = 1 - _kr - _kb;
            double[] y, cbBelow, cbAbove, crBelow, crAbove;
            switch (matrix)
            {
                case MatrixIdentity:
                    // G, B and R as they are
                    (y, cbBelow, crBelow) = (new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 }, new double[] { 1, 0, 0 });
                    (cbAbove, crAbove) = (cbBelow, crBelow);
                    break;
                case MatrixYCgCo:
                    // Cb is Cg, and Cr is Co
                    (y, cbBelow, crBelow) = (new double[] { 1, 1, 1 }, new double[] { -1, 1, -1 }, new double[] { 1, 0, -1 });
                    (cbAbove, crAbove) = (cbBelow, crBelow);
                    break;
                case MatrixBt2020Cl:
                case MatrixChromaticityCl:
                    {
                        // R', Y' and B': Y' is of the luminance, and B' and R' of their differences from it, of their
                        // extremes - of the transfer, B or R alone, and all but it
                        Func<double, double> oetf = v => Inverse(toLinear[0], v);
                        double pb = 1 - oetf(_kb), nb = -oetf(1 - _kb), pr = 1 - oetf(_kr), nr = -oetf(1 - _kr);
                        y = new double[] { 1, 1, 1 };
                        (cbBelow, cbAbove) = (new double[] { 0, 0, -2 * nb }, new double[] { 0, 0, 2 * pb });
                        (crBelow, crAbove) = (new double[] { -2 * nr, 0, 0 }, new double[] { 2 * pr, 0, 0 });
                        break;
                    }
                case MatrixSt2085:
                    // X'Y'Z' of Y'D'zD'x: Cb is D'z, Cr is D'x
                    (y, cbBelow, crBelow) = (new double[] { 0.991902, 1, 1 / 0.986566 }, new double[] { 0, 0, 2 / 0.986566 }, new double[] { 2, 0, 0 });
                    (cbAbove, crAbove) = (cbBelow, crBelow);
                    break;
                case MatrixICtCp:
                    {
                        var m = ICtCpToLms;
                        (y, cbBelow, crBelow) = (new[] { m[0], m[3], m[6] }, new[] { m[1], m[4], m[7] }, new[] { m[2], m[5], m[8] });
                        (cbAbove, crAbove) = (cbBelow, crBelow);
                        break;
                    }
                default:
                    y = new double[] { 1, 1, 1 };
                    cbBelow = new[] { 0, -2 * (1 - _kb) * _kb / kg, 2 * (1 - _kb) };
                    crBelow = new[] { 2 * (1 - _kr), -2 * (1 - _kr) * _kr / kg, 0 };
                    (cbAbove, crAbove) = (cbBelow, crBelow);
                    break;
            }
            YCoefficients = ToFloats(y);
            CbBelow = ToFloats(cbBelow);
            CbAbove = ToFloats(cbAbove);
            CrBelow = ToFloats(crBelow);
            CrAbove = ToFloats(crAbove);

            _ofY = new float[3][];
            _ofCb = new float[3][];
            _ofCr = new float[3][];
            for (int k = 0; k < 3; k++)
            {
                _ofY[k] = new float[codes];
                _ofCb[k] = new float[codes];
                _ofCr[k] = new float[codes];
                for (int code = 0; code < codes; code++)
                {
                    double luma = (code - LumaOffset) * (double)LumaScale;
                    double chroma = (code - ChromaOffset) * (double)ChromaScale;
                    _ofY[k][code] = (float)(y[k] * luma);
                    _ofCb[k][code] = (float)((chroma < 0 ? cbBelow[k] : cbAbove[k]) * chroma);
                    _ofCr[k][code] = (float)((chroma < 0 ? crBelow[k] : crAbove[k]) * chroma);
                }
            }
        }

        private static float[] ToFloats(double[] values) => Array.ConvertAll(values, v => (float)v);

        #region Of what a stream says

        /// <summary>
        /// The colour of the codes of H.273 - ColourPrimaries, TransferCharacteristics and MatrixCoefficients - and the range,
        /// of samples of so many bits.
        /// </summary>
        /// <param name="sceneReferred">An image's, whose camera's curve is undone; a video's is shown as sRGB's.</param>
        /// <param name="height">
        /// Of a video that says no matrix, the height of its picture: of 720 lines and more BT.709's, else BT.601's, as a
        /// video of either is made; 0 for BT.601's, as an image that says none is taken to be.
        /// </param>
        public static VideoColour FromCodes(int primaries, int transfer, int matrix, bool fullRange, int bitDepth, bool sceneReferred, uint height = 0)
        {
            if (matrix == 2 && height >= 720)
                matrix = 1;
            var toLinear = TransferToLinear(transfer, sceneReferred);
            bool srgbTransfer = transfer is 0 or 2 or 3 or 13 || !sceneReferred && IsCameraCurve(transfer);
            bool isSrgb = primaries is 1 or 2 && srgbTransfer;
            return new VideoColour(matrix, fullRange, bitDepth, PrimariesToXyzD65(primaries), new[] { toLinear, toLinear, toLinear },
                transfer, isSrgb, sceneReferred, videoProcessor: true,
                $"primaries {primaries}, transfer {transfer}, matrix {matrix}, {(fullRange ? "full" : "limited")} range, {bitDepth} bits");
        }

        /// <summary>
        /// The colour of an ICC profile - of a matrix and curves, as an iPhone's Display P3 is - for the primaries and the
        /// transfer, and of the matrix and the range the stream says; null where the profile is not of a matrix and curves.
        /// </summary>
        public static VideoColour FromIcc(byte[] icc, int matrix, bool fullRange, int bitDepth, uint height = 0)
        {
            if (matrix == MatrixICtCp || icc == null || !IccProfile.TryParse(icc, out var profile))
                return null;
            if (matrix == 2 && height >= 720)
                matrix = 1;
            var toXyzD65 = Multiply(Bradford(D50, D65), profile.ToXyzD50);
            bool isSrgb = IsIdentity(Multiply(XyzD65ToSrgb, toXyzD65)) && profile.ToLinear.All(IsSrgbCurve);
            return new VideoColour(matrix, fullRange, bitDepth, toXyzD65, profile.ToLinear, transfer: 13, isSrgb, sceneReferred: true,
                videoProcessor: false, $"ICC profile, matrix {matrix}, {(fullRange ? "full" : "limited")} range, {bitDepth} bits");
        }

        /// <summary>
        /// What a video that says nothing of its colour is taken to be: 8 bits of the limited range, of BT.709's matrix of
        /// 720 lines and more, else BT.601's, of sRGB's primaries and curve - what a decoder of either makes.
        /// </summary>
        public static VideoColour Default(uint height) => FromCodes(2, 2, 2, false, 8, sceneReferred: false, height);

        /// <summary>Whether a transfer is a camera's curve - BT.709's and the like - of SDR.</summary>
        private static bool IsCameraCurve(int transfer) => transfer is 1 or 6 or 7 or 14 or 15 or 11 or 12;

        #endregion

        #region Values and the display of them

        /// <summary>The three values of a sample's codes - of <see cref="BitDepth"/> bits - as a sum of each code's part.</summary>
        private void Values3(int yCode, int cbCode, int crCode, out double t0, out double t1, out double t2)
        {
            t0 = _ofY[0][yCode] + _ofCb[0][cbCode] + _ofCr[0][crCode];
            t1 = _ofY[1][yCode] + _ofCb[1][cbCode] + _ofCr[1][crCode];
            t2 = _ofY[2][yCode] + _ofCb[2][cbCode] + _ofCr[2][crCode];
        }

        /// <summary>
        /// The three values, as the samples make them, shown: sRGB's R'G'B', of 0 to 1 - each made linear, of sRGB's
        /// primaries, HDR rolled off, encoded by sRGB's curve. What a table of a video's colour is made of.
        /// </summary>
        public void Display(double t0, double t1, double t2, out double sr, out double sg, out double sb)
        {
            if (IsSrgb)
            {
                (sr, sg, sb) = (Math.Clamp(t0, 0, 1), Math.Clamp(t1, 0, 1), Math.Clamp(t2, 0, 1));
                return;
            }

            double r, g, b;
            switch (_values)
            {
                case Values.ConstantLuminance:
                    {
                        // R', Y' and B': R and B linear, and G of what of the luminance they are not
                        r = Linear(0, t0);
                        double yl = Linear(0, t1);
                        b = Linear(2, t2);
                        g = (yl - _kr * r - _kb * b) / (1 - _kr - _kb);
                        break;
                    }
                case Values.Lms:
                    {
                        double l = Linear(0, t0), m = Linear(0, t1), s = Linear(0, t2);
                        var n = LmsToBt2020;
                        (r, g, b) = (n[0] * l + n[1] * m + n[2] * s, n[3] * l + n[4] * m + n[5] * s, n[6] * l + n[7] * m + n[8] * s);
                        break;
                    }
                default:
                    (r, g, b) = (Linear(0, t0), Linear(1, t1), Linear(2, t2));
                    break;
            }

            // HDR as SDR: of its reference white
            bool hdr = _transfer == TransferPq || _transfer == TransferHlg;
            if (_transfer == TransferPq)
            {
                // of 10000 cd/m²
                double scale = 10000 / ReferenceWhite;
                (r, g, b) = (r * scale, g * scale, b * scale);
            }
            else if (_transfer == TransferHlg)
            {
                // HLG's OOTF, of a display of its nominal peak: of the scene's luminance, gamma 1.2
                double ys = Math.Max(0, _luminance[0] * r + _luminance[1] * g + _luminance[2] * b);
                double scale = HlgPeak / ReferenceWhite * Math.Pow(ys, 0.2);
                (r, g, b) = (r * scale, g * scale, b * scale);
            }

            var x = _toSrgb;
            sr = x[0] * r + x[1] * g + x[2] * b;
            sg = x[3] * r + x[4] * g + x[5] * b;
            sb = x[6] * r + x[7] * g + x[8] * b;

            if (hdr)
            {
                // brighter than white rolled off below it, of the brightest channel, the hue kept
                double max = Math.Max(sr, Math.Max(sg, sb));
                if (max > RollOffKnee)
                {
                    double over = (max - RollOffKnee) / (1 - RollOffKnee);
                    double scale = (RollOffKnee + (1 - RollOffKnee) * over / (1 + over)) / max;
                    (sr, sg, sb) = (sr * scale, sg * scale, sb * scale);
                }
            }

            sr = EncodeSrgb(sr);
            sg = EncodeSrgb(sg);
            sb = EncodeSrgb(sb);
        }

        // sRGB's curve of 0 to 1, of so many steps, looked up between them: within a millionth of the curve - of 8 bits,
        // a thousandth of a step - where three powers a pixel were most of the time of converting it
        private const int EncodeSteps = 16384;
        private static readonly float[] SrgbEncodeTable = BuildEncodeTable();

        private static float[] BuildEncodeTable()
        {
            var table = new float[EncodeSteps + 2];
            for (int i = 0; i <= EncodeSteps; i++)
                table[i] = (float)SrgbEncode(i / (double)EncodeSteps);
            table[EncodeSteps + 1] = table[EncodeSteps];
            return table;
        }

        private static double EncodeSrgb(double linear)
        {
            double at = Math.Clamp(linear, 0, 1) * EncodeSteps;
            int i = (int)at;
            return SrgbEncodeTable[i] + (SrgbEncodeTable[i + 1] - SrgbEncodeTable[i]) * (at - i);
        }

        /// <summary>A sample - its codes, of <see cref="BitDepth"/> bits - made into a BGRA pixel, exactly, at a place of an array.</summary>
        public void ToBgra(int yCode, int cbCode, int crCode, byte[] pixels, int at)
        {
            Values3(yCode, cbCode, crCode, out double t0, out double t1, out double t2);
            Display(t0, t1, t2, out double r, out double g, out double b);
            pixels[at] = ToByte(b);
            pixels[at + 1] = ToByte(g);
            pixels[at + 2] = ToByte(r);
            pixels[at + 3] = 255;
        }

        private double Linear(int channel, double value)
        {
            if (value < LinearFrom || value > LinearTo)
                return _toLinearFunction[channel](value);
            // between the steps, of both: of 10 bits and more, and of PQ's steep curve, the nearest is too coarse
            double at = (value - LinearFrom) * (LinearSteps / (LinearTo - LinearFrom));
            int i = Math.Min((int)at, LinearSteps - 1);
            var table = _toLinear[channel];
            return table[i] + (table[i + 1] - table[i]) * (at - i);
        }

        private static byte ToByte(double value) => (byte)Math.Clamp((int)(value * 255 + 0.5), 0, 255);

        #endregion

        #region The table

        /// <summary>The steps of the table each way: of 0 to 1 of each of the three values.</summary>
        public const int TableSize = 65;

        private ushort[] _table;
        private readonly object _tableLock = new object();

        /// <summary>
        /// <see cref="Display"/> of each step of the three values, of 0 to 1 each way - the first fastest - as RGBA of 16 bits;
        /// null where they are shown as they are, <see cref="IsSrgb"/>. Made once.
        /// </summary>
        public ushort[] Table
        {
            get
            {
                if (IsSrgb)
                    return null;
                lock (_tableLock)
                {
                    if (_table != null)
                        return _table;
                    const int n = TableSize;
                    var table = new ushort[n * n * n * 4];
                    Parallel.For(0, n, z =>
                    {
                        for (int yy = 0; yy < n; yy++)
                        {
                            for (int xx = 0; xx < n; xx++)
                            {
                                Display(xx / (n - 1.0), yy / (n - 1.0), z / (n - 1.0), out double r, out double g, out double b);
                                int at = ((z * n + yy) * n + xx) * 4;
                                table[at] = (ushort)(r * 65535 + 0.5);
                                table[at + 1] = (ushort)(g * 65535 + 0.5);
                                table[at + 2] = (ushort)(b * 65535 + 0.5);
                                table[at + 3] = 65535;
                            }
                        }
                    });
                    return _table = table;
                }
            }
        }

        /// <summary>The table looked up between its steps, of the three values, into a pixel's B, G and R.</summary>
        private static unsafe void LookUp(ushort* table, float t0, float t1, float t2, byte* pixel)
        {
            const int n = TableSize;
            float x = Math.Clamp(t0, 0f, 1f) * (n - 1), y = Math.Clamp(t1, 0f, 1f) * (n - 1), z = Math.Clamp(t2, 0f, 1f) * (n - 1);
            int x0 = Math.Min((int)x, n - 2), y0 = Math.Min((int)y, n - 2), z0 = Math.Min((int)z, n - 2);
            float fx = x - x0, fy = y - y0, fz = z - z0;
            ushort* corner = table + ((z0 * n + y0) * n + x0) * 4;
            const int dx = 4, dy = n * 4, dz = n * n * 4;
            for (int c = 0; c < 3; c++)
            {
                ushort* i = corner + c;
                float c00 = i[0] + (i[dx] - i[0]) * fx;
                float c10 = i[dy] + (i[dy + dx] - i[dy]) * fx;
                float c01 = i[dz] + (i[dz + dx] - i[dz]) * fx;
                float c11 = i[dz + dy] + (i[dz + dy + dx] - i[dz + dy]) * fx;
                float c0 = c00 + (c10 - c00) * fy;
                float c1 = c01 + (c11 - c01) * fy;
                // BGR: the table's R first
                pixel[2 - c] = (byte)((c0 + (c1 - c0) * fz) * (255f / 65535) + 0.5f);
            }
        }

        #endregion

        #region Frames

        /// <summary>
        /// A decoded frame of any <see cref="VideoFrameFormat"/> - rows of <paramref name="stride"/> bytes; of 4:2:0, the
        /// chroma's plane after the luma's <paramref name="planeHeight"/> rows; samples of more than 8 bits in the high
        /// ones of 16 - made into BGR or BGRA: <paramref name="width"/> by <paramref name="height"/> of it, written where
        /// <paramref name="placement"/> says, into a target of rows of <paramref name="targetWidth"/> pixels. What of it
        /// falls outside the picture - a grid's tiles past its size, a crop - is cut off.
        /// </summary>
        /// <param name="exact">Of each pixel exactly, as an image is; else of the table, as a video's frames are.</param>
        public unsafe void Convert(byte[] frame, VideoFrameFormat format, int stride, int planeHeight, int width, int height,
            byte[] target, int targetWidth, int bytesPerPixel, in FramePlacement placement, bool exact = false)
        {
            var p = placement;
            int x0 = Math.Max(0, -p.Left), x1 = Math.Min(width, p.PictureWidth - p.Left);
            int y0 = Math.Max(0, -p.Top), y1 = Math.Min(height, p.PictureHeight - p.Top);
            if (x1 <= x0 || y1 <= y0)
                return;

            // what is read, and written, of pointers: within the frame, and every corner within the target
            long read = format is VideoFrameFormat.NV12 or VideoFrameFormat.P010 or VideoFrameFormat.P016
                ? (long)stride * planeHeight + (long)((y1 - 1) / 2 + 1) * stride
                : (long)y1 * stride;
            if (frame.Length < read || (long)stride * (y1 - 1) + VideoFrameFormats.Stride(format, (uint)x1) > frame.Length)
                throw new ArgumentException("The frame is smaller than its stride and rows say.", nameof(frame));

            var o = p.Orientation;
            int a = o.A, b = o.B, c = o.C, d = o.D;
            int shiftX = (a < 0 ? p.PictureWidth - 1 : 0) + (c < 0 ? p.PictureHeight - 1 : 0) + p.TargetX;
            int shiftY = (b < 0 ? p.PictureWidth - 1 : 0) + (d < 0 ? p.PictureHeight - 1 : 0) + p.TargetY;
            int targetRows = target.Length / (targetWidth * bytesPerPixel);
            (int X, int Y) Shown(int x, int y) => (a * (p.Left + x) + c * (p.Top + y) + shiftX, b * (p.Left + x) + d * (p.Top + y) + shiftY);
            foreach (var (x, y) in new[] { (x0, y0), (x1 - 1, y0), (x0, y1 - 1), (x1 - 1, y1 - 1) })
            {
                var (sx, sy) = Shown(x, y);
                if (sx < 0 || sx >= targetWidth || sy < 0 || sy >= targetRows)
                    throw new ArgumentException("The frame placed falls outside the target.", nameof(placement));
            }

            // each pixel on from the one before it, along a row of the frame: the matrix, x' = A x + C y and y' = B x + D y
            int step = (b * targetWidth + a) * bytesPerPixel;
            int mode = IsSrgb ? 0 : exact ? 2 : 1;
            var table = mode == 1 ? Table : null;
            int columns = x1 - x0;
            int shift = 16 - BitDepth;
            int neutral = 1 << (BitDepth - 1);
            int chroma = stride * planeHeight;
            Parallel.For(y0, y1, () => new int[columns * 3], (row, _, codes) =>
            {
                var (sx, sy) = Shown(x0, row);
                fixed (byte* source = frame)
                fixed (byte* output = target)
                fixed (int* samples = codes)
                fixed (ushort* lookUp = table)
                {
                    ReadRow(source, format, stride, chroma, row, x0, columns, shift, neutral, samples);
                    WriteRow(samples, columns, output + ((long)sy * targetWidth + sx) * bytesPerPixel, step, bytesPerPixel, mode, lookUp);
                }
                return codes;
            }, _ => { });
        }

        /// <summary>
        /// The codes of a row of a frame, from a column on, as its format lays them out: so many of Y, then as many of Cb,
        /// then of Cr - of monochrome, the neutral chroma.
        /// </summary>
        private static unsafe void ReadRow(byte* frame, VideoFrameFormat format, int stride, int chroma, int row, int x0, int count,
            int shift, int neutral, int* codes)
        {
            int* ys = codes, cbs = codes + count, crs = codes + 2 * count;
            byte* line = frame + (long)row * stride;
            byte* chromaLine = frame + chroma + (long)(row / 2) * stride;
            switch (format)
            {
                case VideoFrameFormat.NV12:
                    for (int i = 0; i < count; i++)
                    {
                        int x = x0 + i, pair = x & ~1;
                        (ys[i], cbs[i], crs[i]) = (line[x], chromaLine[pair], chromaLine[pair + 1]);
                    }
                    break;
                case VideoFrameFormat.P010:
                case VideoFrameFormat.P016:
                    {
                        ushort* luma = (ushort*)line, pairs = (ushort*)chromaLine;
                        for (int i = 0; i < count; i++)
                        {
                            int x = x0 + i, pair = x & ~1;
                            (ys[i], cbs[i], crs[i]) = (luma[x] >> shift, pairs[pair] >> shift, pairs[pair + 1] >> shift);
                        }
                        break;
                    }
                case VideoFrameFormat.YUY2:
                    // of each two pixels Y0, Cb, Y1, Cr
                    for (int i = 0; i < count; i++)
                    {
                        int x = x0 + i;
                        byte* two = line + (x & ~1) * 2;
                        (ys[i], cbs[i], crs[i]) = (two[(x & 1) * 2], two[1], two[3]);
                    }
                    break;
                case VideoFrameFormat.Y210:
                case VideoFrameFormat.Y216:
                    for (int i = 0; i < count; i++)
                    {
                        int x = x0 + i;
                        ushort* two = (ushort*)line + (x & ~1) * 2;
                        (ys[i], cbs[i], crs[i]) = (two[(x & 1) * 2] >> shift, two[1] >> shift, two[3] >> shift);
                    }
                    break;
                case VideoFrameFormat.AYUV:
                    // Cr, Cb, Y, alpha
                    for (int i = 0; i < count; i++)
                    {
                        byte* pixel = line + (x0 + i) * 4;
                        (ys[i], cbs[i], crs[i]) = (pixel[2], pixel[1], pixel[0]);
                    }
                    break;
                case VideoFrameFormat.Y410:
                    // Cb of bits 0 to 9, Y of 10 to 19, Cr of 20 to 29
                    for (int i = 0; i < count; i++)
                    {
                        uint packed = ((uint*)line)[x0 + i];
                        (ys[i], cbs[i], crs[i]) = ((int)(packed >> 10) & 0x3FF, (int)packed & 0x3FF, (int)(packed >> 20) & 0x3FF);
                    }
                    break;
                case VideoFrameFormat.Y416:
                    // Cb, Y, Cr, alpha
                    for (int i = 0; i < count; i++)
                    {
                        ushort* pixel = (ushort*)line + (x0 + i) * 4;
                        (ys[i], cbs[i], crs[i]) = (pixel[1] >> shift, pixel[0] >> shift, pixel[2] >> shift);
                    }
                    break;
                case VideoFrameFormat.L8:
                    for (int i = 0; i < count; i++)
                        (ys[i], cbs[i], crs[i]) = (line[x0 + i], neutral, neutral);
                    break;
                case VideoFrameFormat.L16:
                    for (int i = 0; i < count; i++)
                        (ys[i], cbs[i], crs[i]) = (((ushort*)line)[x0 + i] >> shift, neutral, neutral);
                    break;
                default:
                    throw new NotSupportedException($"Frames of {format} are not supported.");
            }
        }

        /// <summary>
        /// A row's codes made into pixels, each <paramref name="step"/> bytes on from the one before: of sRGB as they are,
        /// of the table, or exactly.
        /// </summary>
        private unsafe void WriteRow(int* codes, int count, byte* at, int step, int bytesPerPixel, int mode, ushort* table)
        {
            int* ys = codes, cbs = codes + count, crs = codes + 2 * count;
            float[] y0 = _ofY[0], y1 = _ofY[1], y2 = _ofY[2];
            float[] cb0 = _ofCb[0], cb1 = _ofCb[1], cb2 = _ofCb[2];
            float[] cr0 = _ofCr[0], cr1 = _ofCr[1], cr2 = _ofCr[2];
            for (int i = 0; i < count; i++, at += step)
            {
                int y = ys[i], cb = cbs[i], cr = crs[i];
                float t0 = y0[y] + cb0[cb] + cr0[cr];
                float t1 = y1[y] + cb1[cb] + cr1[cr];
                float t2 = y2[y] + cb2[cb] + cr2[cr];
                if (mode == 0)
                {
                    (at[0], at[1], at[2]) = (ToByte(t2), ToByte(t1), ToByte(t0));
                }
                else if (mode == 1)
                {
                    LookUp(table, t0, t1, t2, at);
                }
                else
                {
                    Display(t0, t1, t2, out double r, out double g, out double bl);
                    (at[0], at[1], at[2]) = (ToByte(bl), ToByte(g), ToByte(r));
                }
                if (bytesPerPixel == 4)
                    at[3] = 255;
            }
        }

        private static byte ToByte(float value) => (byte)Math.Clamp((int)(value * 255f + 0.5f), 0, 255);

        #endregion

        #region Transfers

        private static double SrgbEncode(double linear) =>
            linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;

        /// <summary>sRGB's, IEC 61966-2-1, as sYCC has it of values below 0 too.</summary>
        private static double SrgbDecode(double value) =>
            value < 0 ? -SrgbDecode(-value) : value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

        /// <summary>BT.709's OETF undone - BT.601's, BT.2020's and SMPTE 240M's alike - of its alpha, its beta and its slope below beta.</summary>
        private static double OetfDecode(double value, double alpha, double beta, double slope) =>
            value < beta * slope ? value / slope : Math.Pow((value + alpha - 1) / alpha, 1 / 0.45);

        /// <summary>
        /// R'G'B' to linear, of an H.273 TransferCharacteristics: of PQ, of 10000 cd/m²; of HLG, of the scene. A camera's
        /// curve undone into the scene's light where <paramref name="sceneReferred"/>, else taken as sRGB's.
        /// </summary>
        public static Func<double, double> TransferToLinear(int transfer, bool sceneReferred = true)
        {
            const double alpha = 1.09929682680944, beta = 0.018053968510807;
            if (!sceneReferred && IsCameraCurve(transfer))
                return SrgbDecode;
            return transfer switch
            {
                1 or 6 or 14 or 15 => v => OetfDecode(Math.Max(0, v), alpha, beta, 4.5),
                4 => v => Math.Pow(Math.Max(0, v), 2.2),
                5 => v => Math.Pow(Math.Max(0, v), 2.8),
                7 => v => OetfDecode(Math.Max(0, v), 1.1115, 0.0228, 4),
                8 => v => v,
                9 => v => v <= 0 ? 0 : Math.Pow(10, 2 * (v - 1)),
                10 => v => v <= 0 ? 0 : Math.Pow(10, 2.5 * (v - 1)),
                // xvYCC: BT.709's, of values below 0 as of those above
                11 => v => Math.Sign(v) * OetfDecode(Math.Abs(v), alpha, beta, 4.5),
                // BT.1361's extended gamut: below 0, of a quarter of the scale
                12 => v => v >= 0 ? OetfDecode(v, alpha, beta, 4.5) : -OetfDecode(-4 * v, alpha, beta, 4.5) / 4,
                // SMPTE ST 2084, PQ
                16 => v =>
                {
                    const double m1 = 2610 / 16384.0, m2 = 2523 / 4096.0 * 128, c1 = 3424 / 4096.0, c2 = 2413 / 4096.0 * 32, c3 = 2392 / 4096.0 * 32;
                    double p = Math.Pow(Math.Max(0, v), 1 / m2);
                    return Math.Pow(Math.Max(p - c1, 0) / (c2 - c3 * p), 1 / m1);
                },
                // SMPTE ST 428-1, of 48 cd/m² of 52.37
                17 => v => Math.Pow(Math.Max(0, v), 2.6) * 52.37 / 48,
                // ARIB STD-B67, HLG: of the scene, 0 to 1
                18 => v =>
                {
                    const double a = 0.17883277, b = 1 - 4 * a, c = 0.55991073;
                    v = Math.Max(0, v);
                    return v <= 0.5 ? v * v / 3 : (Math.Exp((v - c) / a) + b) / 12;
                },
                // sRGB's and sYCC's, of 13, and what says nothing
                _ => SrgbDecode,
            };
        }

        /// <summary>The value a curve makes a linear value of: what it is the inverse of, which it rises through.</summary>
        private static double Inverse(Func<double, double> toLinear, double linear)
        {
            double low = 0, high = 1;
            for (int i = 0; i < 40; i++)
            {
                double mid = (low + high) / 2;
                if (toLinear(mid) < linear)
                    low = mid;
                else
                    high = mid;
            }
            return (low + high) / 2;
        }

        private static bool IsSrgbCurve(Func<double, double> curve)
        {
            for (int i = 0; i <= 16; i++)
            {
                double v = i / 16.0;
                if (Math.Abs(curve(v) - SrgbDecode(v)) > 0.002)
                    return false;
            }
            return true;
        }

        #endregion

        #region Primaries

        private static readonly double[] D65 = { 0.95047, 1, 1.08883 };
        private static readonly double[] D50 = { 0.96422, 1, 0.82521 };

        private static readonly double[] XyzD65ToSrgb =
        {
            3.2404542, -1.5371385, -0.4985314,
            -0.9692660, 1.8760108, 0.0415560,
            0.0556434, -0.2040259, 1.0572252,
        };

        /// <summary>
        /// Linear RGB of an H.273 ColourPrimaries to XYZ of D65 - of another white, adapted to it. BT.709's - sRGB's - where
        /// it says nothing, or what is not known.
        /// </summary>
        public static double[] PrimariesToXyzD65(int primaries)
        {
            // R, G, B and the white, each x and y
            double[] xy = primaries switch
            {
                4 => new[] { 0.67, 0.33, 0.21, 0.71, 0.14, 0.08, 0.310, 0.316 },              // BT.470 M, of illuminant C
                5 => new[] { 0.64, 0.33, 0.29, 0.60, 0.15, 0.06, 0.3127, 0.3290 },            // BT.601 625, BT.470 B and G
                6 or 7 => new[] { 0.630, 0.340, 0.310, 0.595, 0.155, 0.070, 0.3127, 0.3290 }, // BT.601 525, SMPTE 240M
                8 => new[] { 0.681, 0.319, 0.243, 0.692, 0.145, 0.049, 0.310, 0.316 },        // generic film, of illuminant C
                9 => new[] { 0.708, 0.292, 0.170, 0.797, 0.131, 0.046, 0.3127, 0.3290 },      // BT.2020, BT.2100
                10 => new[] { 1.0, 0.0, 0.0, 1.0, 0.0, 0.0, 1 / 3.0, 1 / 3.0 },               // SMPTE ST 428-1: XYZ itself, of E
                11 => new[] { 0.680, 0.320, 0.265, 0.690, 0.150, 0.060, 0.314, 0.351 },       // SMPTE RP 431-2, DCI-P3
                12 => new[] { 0.680, 0.320, 0.265, 0.690, 0.150, 0.060, 0.3127, 0.3290 },     // SMPTE EG 432-1, Display P3
                22 => new[] { 0.630, 0.340, 0.295, 0.605, 0.155, 0.077, 0.3127, 0.3290 },     // EBU Tech. 3213-E
                _ => new[] { 0.64, 0.33, 0.30, 0.60, 0.15, 0.06, 0.3127, 0.3290 },            // BT.709, sRGB
            };

            double[] toXyz = primaries == 10
                ? new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }
                : RgbToXyz(xy);
            double[] white = { xy[6] / xy[7], 1, (1 - xy[6] - xy[7]) / xy[7] };
            return Multiply(Bradford(white, D65), toXyz);
        }

        /// <summary>The linear RGB of these primaries to linear sRGB, row by row: what a picture is converted by.</summary>
        public static double[] ToSrgb(int primaries) => Multiply(XyzD65ToSrgb, PrimariesToXyzD65(primaries));

        /// <summary>Linear RGB to XYZ, of primaries and a white of their x and y: the white of Y 1.</summary>
        private static double[] RgbToXyz(double[] xy)
        {
            static double[] Column(double x, double y) => new[] { x / y, 1, (1 - x - y) / y };
            var r = Column(xy[0], xy[1]);
            var g = Column(xy[2], xy[3]);
            var b = Column(xy[4], xy[5]);
            var w = Column(xy[6], xy[7]);
            double[] primaries = { r[0], g[0], b[0], r[1], g[1], b[1], r[2], g[2], b[2] };
            // each primary scaled by how much of it the white is of
            var s = Apply(Invert(primaries), w);
            return new[]
            {
                r[0] * s[0], g[0] * s[1], b[0] * s[2],
                r[1] * s[0], g[1] * s[1], b[1] * s[2],
                r[2] * s[0], g[2] * s[1], b[2] * s[2],
            };
        }

        /// <summary>The Bradford adaptation of XYZ of one white to another.</summary>
        private static double[] Bradford(double[] from, double[] to)
        {
            double[] m = { 0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296 };
            var f = Apply(m, from);
            var t = Apply(m, to);
            double[] scale = { t[0] / f[0], 0, 0, 0, t[1] / f[1], 0, 0, 0, t[2] / f[2] };
            return Multiply(Invert(m), Multiply(scale, m));
        }

        private static bool IsIdentity(double[] m)
        {
            for (int i = 0; i < 9; i++)
            {
                if (Math.Abs(m[i] - (i % 4 == 0 ? 1 : 0)) > 0.01)
                    return false;
            }
            return true;
        }

        private static double[] Multiply(double[] a, double[] b)
        {
            var c = new double[9];
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    c[row * 3 + col] = a[row * 3] * b[col] + a[row * 3 + 1] * b[3 + col] + a[row * 3 + 2] * b[6 + col];
            return c;
        }

        private static double[] Apply(double[] m, double[] v) => new[]
        {
            m[0] * v[0] + m[1] * v[1] + m[2] * v[2],
            m[3] * v[0] + m[4] * v[1] + m[5] * v[2],
            m[6] * v[0] + m[7] * v[1] + m[8] * v[2],
        };

        private static double[] Invert(double[] m)
        {
            double det = m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);
            return new[]
            {
                (m[4] * m[8] - m[5] * m[7]) / det, (m[2] * m[7] - m[1] * m[8]) / det, (m[1] * m[5] - m[2] * m[4]) / det,
                (m[5] * m[6] - m[3] * m[8]) / det, (m[0] * m[8] - m[2] * m[6]) / det, (m[2] * m[3] - m[0] * m[5]) / det,
                (m[3] * m[7] - m[4] * m[6]) / det, (m[1] * m[6] - m[0] * m[7]) / det, (m[0] * m[4] - m[1] * m[3]) / det,
            };
        }

        #endregion
    }

    /// <summary>
    /// Where a frame's pixels go: the frame at a place of a picture - a grid's tile, at its column and row, or a crop's
    /// offset - the picture turned and flipped as it is shown, and that at a place of the target. What of the frame falls
    /// outside the picture is cut off.
    /// </summary>
    public readonly struct FramePlacement
    {
        public FramePlacement(int left, int top, int pictureWidth, int pictureHeight, VideoOrientation orientation = default, int targetX = 0, int targetY = 0)
        {
            Left = left;
            Top = top;
            PictureWidth = pictureWidth;
            PictureHeight = pictureHeight;
            Orientation = orientation;
            TargetX = targetX;
            TargetY = targetY;
        }

        /// <summary>A whole frame's picture, of its size, shown turned at the target's top left.</summary>
        public static FramePlacement Whole(int width, int height, VideoOrientation orientation = default) =>
            new FramePlacement(0, 0, width, height, orientation);

        /// <summary>Where in the picture the frame's top left is: of a crop, before its start.</summary>
        public int Left { get; }
        public int Top { get; }

        /// <summary>The picture's size, before it is turned.</summary>
        public int PictureWidth { get; }
        public int PictureHeight { get; }

        public VideoOrientation Orientation { get; }

        /// <summary>Where in the target the picture shown starts.</summary>
        public int TargetX { get; }
        public int TargetY { get; }
    }

    /// <summary>
    /// An ICC profile of a matrix and curves (ICC.1:2010 F.3): its primaries' XYZ, of D50, and each channel's curve - of a
    /// gamma, a table, or a parametric function. Profiles of lookup tables alone are not read.
    /// </summary>
    internal sealed class IccProfile
    {
        /// <summary>Linear RGB to XYZ of D50, row by row: the columns rXYZ, gXYZ and bXYZ.</summary>
        public double[] ToXyzD50 { get; private set; }

        /// <summary>R'G'B' to linear, of the curves of red, green and blue.</summary>
        public Func<double, double>[] ToLinear { get; private set; }

        public static bool TryParse(byte[] icc, out IccProfile profile)
        {
            profile = null;
            if (icc.Length < 132 || ReadTag(icc, 16) != "RGB ")
                return false;

            // the tag table: each tag's signature, offset and size
            var tags = new Dictionary<string, (int Offset, int Size)>();
            uint count = ReadUInt32(icc, 128);
            for (int i = 0; i < count && 132 + i * 12 + 12 <= icc.Length; i++)
            {
                int at = 132 + i * 12;
                tags[ReadTag(icc, at)] = ((int)ReadUInt32(icc, at + 4), (int)ReadUInt32(icc, at + 8));
            }

            double[] Xyz(string tag)
            {
                if (!tags.TryGetValue(tag, out var t) || t.Offset + 20 > icc.Length || ReadTag(icc, t.Offset) != "XYZ ")
                    return null;
                return new[] { ReadS15Fixed16(icc, t.Offset + 8), ReadS15Fixed16(icc, t.Offset + 12), ReadS15Fixed16(icc, t.Offset + 16) };
            }

            Func<double, double> Curve(string tag) => tags.TryGetValue(tag, out var t) ? ReadCurve(icc, t.Offset) : null;

            var r = Xyz("rXYZ");
            var g = Xyz("gXYZ");
            var b = Xyz("bXYZ");
            var curves = new[] { Curve("rTRC"), Curve("gTRC"), Curve("bTRC") };
            if (r == null || g == null || b == null || curves.Any(x => x == null))
                return false;

            // the same tag of each channel, as most profiles share one: one curve, made into one table
            if (tags["gTRC"].Offset == tags["rTRC"].Offset)
                curves[1] = curves[0];
            if (tags["bTRC"].Offset == tags["rTRC"].Offset)
                curves[2] = curves[0];

            profile = new IccProfile
            {
                ToXyzD50 = new[] { r[0], g[0], b[0], r[1], g[1], b[1], r[2], g[2], b[2] },
                ToLinear = curves,
            };
            return true;
        }

        /// <summary>A 'curv' - none, a gamma, or a table - or a 'para' of its function types 0 to 4; of values below 0, 0.</summary>
        private static Func<double, double> ReadCurve(byte[] icc, int offset)
        {
            if (offset + 12 > icc.Length)
                return null;
            string type = ReadTag(icc, offset);
            if (type == "curv")
            {
                uint entries = ReadUInt32(icc, offset + 8);
                if (entries == 0)
                    return v => v;
                if (entries == 1)
                {
                    double gamma = ReadUInt16(icc, offset + 12) / 256.0;
                    return v => Math.Pow(Math.Max(0, v), gamma);
                }
                if (offset + 12 + entries * 2 > icc.Length)
                    return null;
                var table = new double[entries];
                for (int i = 0; i < entries; i++)
                    table[i] = ReadUInt16(icc, offset + 12 + i * 2) / 65535.0;
                return v =>
                {
                    double at = Math.Clamp(v, 0, 1) * (table.Length - 1);
                    int i = Math.Min((int)at, table.Length - 2);
                    return table[i] + (table[i + 1] - table[i]) * (at - i);
                };
            }
            if (type == "para")
            {
                int function = ReadUInt16(icc, offset + 8);
                int[] counts = { 1, 3, 4, 5, 7 };
                if (function >= counts.Length || offset + 12 + counts[function] * 4 > icc.Length)
                    return null;
                var p = new double[7];
                for (int i = 0; i < counts[function]; i++)
                    p[i] = ReadS15Fixed16(icc, offset + 12 + i * 4);
                double gm = p[0], a = p[1], b = p[2], c = p[3], d = p[4], e = p[5], f = p[6];
                return function switch
                {
                    0 => v => Math.Pow(Math.Max(0, v), gm),
                    1 => v => v >= -b / a ? Math.Pow(a * v + b, gm) : 0,
                    2 => v => v >= -b / a ? Math.Pow(a * v + b, gm) + c : c,
                    3 => v => v >= d ? Math.Pow(a * v + b, gm) : c * Math.Max(0, v),
                    _ => v => v >= d ? Math.Pow(a * v + b, gm) + e : c * Math.Max(0, v) + f,
                };
            }
            return null;
        }

        private static string ReadTag(byte[] data, int at) => System.Text.Encoding.ASCII.GetString(data, at, 4);
        private static uint ReadUInt32(byte[] data, int at) => (uint)(data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3]);
        private static int ReadUInt16(byte[] data, int at) => data[at] << 8 | data[at + 1];
        private static double ReadS15Fixed16(byte[] data, int at) => (int)ReadUInt32(data, at) / 65536.0;
    }
}
