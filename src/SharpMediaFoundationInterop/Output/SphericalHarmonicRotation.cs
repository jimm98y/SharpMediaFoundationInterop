using System;

namespace SharpMediaFoundationInterop.Output
{
    /// <summary>
    /// The rotation of a sound field of real spherical harmonics, of each order, of a rotation of space: of the first order
    /// the rotation itself, of Y Z X; of each higher one, of the one below it, by Ivanic and Ruedenberg's recursion
    /// ("Rotation Matrices for Real Spherical Harmonics. Direct Determination by Recursion", J. Phys. Chem. 1996, and its
    /// 1998 corrections). Of an order, the same of N3D and SN3D, which differ by the order's factor alone.
    /// </summary>
    internal static class SphericalHarmonicRotation
    {
        /// <summary>
        /// Of each order up to <paramref name="order"/>, the matrix of its 2l + 1 harmonics, of m from -l to l, that turns a
        /// field as <paramref name="rotation"/> turns a direction of x, y and z: a sound from d is, turned, from rotation · d.
        /// </summary>
        public static double[][,] Of(double[,] rotation, int order)
        {
            var matrices = new double[order + 1][,];
            matrices[0] = new double[,] { { 1 } };
            if (order == 0)
                return matrices;

            // of the first order's harmonics, of m -1 0 1, y z x
            int[] axis = { 1, 2, 0 };
            var first = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    first[i, j] = rotation[axis[i], axis[j]];
            matrices[1] = first;

            for (int l = 2; l <= order; l++)
            {
                var below = matrices[l - 1];
                var matrix = new double[2 * l + 1, 2 * l + 1];
                for (int m = -l; m <= l; m++)
                {
                    for (int n = -l; n <= l; n++)
                    {
                        int d = m == 0 ? 1 : 0;
                        double denominator = Math.Abs(n) == l ? 2 * l * (2 * l - 1) : (l + n) * (l - n);
                        double u = Math.Sqrt((l + m) * (l - m) / denominator);
                        double v = 0.5 * Math.Sqrt((1 + d) * (l + Math.Abs(m) - 1) * (l + Math.Abs(m)) / denominator) * (1 - 2 * d);
                        double w = -0.5 * Math.Sqrt((l - Math.Abs(m) - 1) * (l - Math.Abs(m)) / denominator) * (1 - d);

                        double value = 0;
                        if (u != 0) value += u * U(first, below, l, m, n);
                        if (v != 0) value += v * V(first, below, l, m, n);
                        if (w != 0) value += w * W(first, below, l, m, n);
                        matrix[m + l, n + l] = value;
                    }
                }
                matrices[l] = matrix;
            }
            return matrices;
        }

        /// <summary>Of the first order's row i and the order below's row a, of column b of order l.</summary>
        private static double P(double[,] first, double[,] below, int i, int a, int b, int l)
        {
            double r1 = first[i + 1, 2], rm1 = first[i + 1, 0], r0 = first[i + 1, 1];
            if (b == l)
                return r1 * below[a + l - 1, 2 * l - 2] - rm1 * below[a + l - 1, 0];
            if (b == -l)
                return r1 * below[a + l - 1, 0] + rm1 * below[a + l - 1, 2 * l - 2];
            return r0 * below[a + l - 1, b + l - 1];
        }

        private static double U(double[,] first, double[,] below, int l, int m, int n) => P(first, below, 0, m, n, l);

        private static double V(double[,] first, double[,] below, int l, int m, int n)
        {
            if (m == 0)
                return P(first, below, 1, 1, n, l) + P(first, below, -1, -1, n, l);
            if (m > 0)
            {
                int d = m == 1 ? 1 : 0;
                return P(first, below, 1, m - 1, n, l) * Math.Sqrt(1 + d) - P(first, below, -1, -m + 1, n, l) * (1 - d);
            }
            else
            {
                int d = m == -1 ? 1 : 0;
                return P(first, below, 1, m + 1, n, l) * (1 - d) + P(first, below, -1, -m - 1, n, l) * Math.Sqrt(1 + d);
            }
        }

        private static double W(double[,] first, double[,] below, int l, int m, int n)
        {
            if (m > 0)
                return P(first, below, 1, m + 1, n, l) + P(first, below, -1, -m - 1, n, l);
            return P(first, below, 1, m - 1, n, l) - P(first, below, -1, -m + 1, n, l);
        }

        /// <summary>
        /// The rotation that turns the world as a listener sees it who looks <paramref name="yaw"/> degrees to the right and
        /// <paramref name="pitch"/> up: a direction of the world, of x ahead, y to the left and z up, as it is of the head.
        /// </summary>
        public static double[,] OfHead(double yaw, double pitch)
        {
            double a = -yaw * Math.PI / 180, p = pitch * Math.PI / 180;
            double ca = Math.Cos(a), sa = Math.Sin(a), cp = Math.Cos(p), sp = Math.Sin(p);
            // the head's: up by the pitch, then turned by the yaw - Rz(a) · Ry(p) - of which the world's is the transpose
            var head = new double[,]
            {
                { ca * cp, -sa, -ca * sp },
                { sa * cp, ca, -sa * sp },
                { sp, 0, cp },
            };
            var world = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    world[i, j] = head[j, i];
            return world;
        }
    }
}
