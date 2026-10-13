using System;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// How a picture is turned and flipped to be shown, of the eight a track's matrix can say ('tkhd', ISO/IEC 14496-12
    /// 8.3.2): of its first column - where the picture's x goes - and its second, where its y goes; each a unit along x or
    /// y, either way, of a picture whose y runs down. A phone held upright records its picture on its side, and turned 90
    /// degrees clockwise to be shown.
    /// </summary>
    public readonly struct VideoOrientation : IEquatable<VideoOrientation>
    {
        // A and D less 1: the default, of all 0, is shown as it is
        private readonly int _a, _b, _c, _d;

        /// <summary>The matrix, of x' = A x + C y and y' = B x + D y: each of -1, 0 and 1.</summary>
        public int A => _a + 1;
        public int B => _b;
        public int C => _c;
        public int D => _d + 1;

        public VideoOrientation(int a, int b, int c, int d)
        {
            if (Math.Abs(a) + Math.Abs(b) != 1 || Math.Abs(c) + Math.Abs(d) != 1 || a * c + b * d != 0)
                throw new ArgumentException("Not a turn or a flip of a picture: each column one unit along x or y, at right angles.");
            _a = a - 1;
            _b = b;
            _c = c;
            _d = d - 1;
        }

        /// <summary>Shown as it is: the default.</summary>
        public static VideoOrientation Identity => default;

        /// <summary>Turned clockwise by so many degrees - 0, 90, 180 or 270 - and then mirrored, left and right, where asked.</summary>
        public static VideoOrientation Of(int degrees, bool mirrored = false)
        {
            var (a, b, c, d) = (((degrees % 360) + 360) % 360) switch
            {
                0 => (1, 0, 0, 1),
                90 => (0, 1, -1, 0),
                180 => (-1, 0, 0, -1),
                270 => (0, -1, 1, 0),
                _ => throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "0, 90, 180 or 270"),
            };
            return mirrored ? new VideoOrientation(-a, b, -c, d) : new VideoOrientation(a, b, c, d);
        }

        /// <summary>
        /// Of a track's matrix - 'tkhd's a, b, c and d, of 16.16 fixed point - the turn and flip it is nearest; shown as it
        /// is where it is none of them: scaled, skewed, or turned by another angle.
        /// </summary>
        public static VideoOrientation FromMatrix(int a, int b, int c, int d)
        {
            static int Unit(int value) => Math.Abs(value) < 0x8000 ? 0 : Math.Sign(value);
            var (ua, ub, uc, ud) = (Unit(a), Unit(b), Unit(c), Unit(d));
            bool valid = Math.Abs(ua) + Math.Abs(ub) == 1 && Math.Abs(uc) + Math.Abs(ud) == 1 && ua * uc + ub * ud == 0;
            return valid ? new VideoOrientation(ua, ub, uc, ud) : Identity;
        }

        public bool IsIdentity => A == 1 && B == 0 && C == 0 && D == 1;

        /// <summary>Whether the picture's width is shown down, and its height across: turned 90 or 270 degrees.</summary>
        public bool SwapsAxes => A == 0;

        /// <summary>Whether it is mirrored, as no turn alone makes it.</summary>
        public bool IsMirrored => A * D - B * C < 0;

        /// <summary>The degrees it turns clockwise, of what it does before it mirrors - left and right - where it does.</summary>
        public int Degrees
        {
            get
            {
                // the turn of the column of x, of the matrix unmirrored
                int a = IsMirrored ? -A : A, b = B;
                return a == 1 ? 0 : b == 1 ? 90 : a == -1 ? 180 : 270;
            }
        }

        /// <summary>The size a picture of a width and a height is shown at.</summary>
        public (uint Width, uint Height) Shown(uint width, uint height) => SwapsAxes ? (height, width) : (width, height);

        /// <summary>
        /// Where in the picture - <paramref name="width"/> by <paramref name="height"/> - a pixel shown at
        /// (<paramref name="x"/>, <paramref name="y"/>) is from: the matrix undone, its transpose, of a picture shown from
        /// its top left.
        /// </summary>
        public (int X, int Y) SourceOf(int x, int y, int width, int height) =>
            (A * x + B * y + OffsetX(width), C * x + D * y + OffsetY(height));

        /// <summary>Of <see cref="SourceOf"/>, what is added to the x and the y made of a shown pixel's: where they come out below 0.</summary>
        public int OffsetX(int width) => A + B < 0 ? width - 1 : 0;
        public int OffsetY(int height) => C + D < 0 ? height - 1 : 0;

        public bool Equals(VideoOrientation other) => A == other.A && B == other.B && C == other.C && D == other.D;
        public override bool Equals(object obj) => obj is VideoOrientation other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(A, B, C, D);
        public static bool operator ==(VideoOrientation left, VideoOrientation right) => left.Equals(right);
        public static bool operator !=(VideoOrientation left, VideoOrientation right) => !left.Equals(right);
        public override string ToString() => IsIdentity ? "as it is" : $"{Degrees} degrees clockwise{(IsMirrored ? ", mirrored" : "")}";
    }
}
