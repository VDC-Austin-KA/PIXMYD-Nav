using System;

namespace PIXMYD_Nav.Core.Capture
{
    /// <summary>
    /// Taking a solved 4x4 apart again.
    ///
    /// The solvers produce a column-major 4x4 because that is what the contract
    /// carries and what glTF, the TypeScript suite and the phone all use.
    /// Navisworks wants a rotation and a translation, and it will take the
    /// rotation as an axis and an angle -- which is the one spelling with no
    /// convention to get wrong. Its 3x3 constructor does not say whether it
    /// reads row-major or column-major, and a transposed rotation is a scan
    /// mirrored about its own centre: plausible on screen, wrong everywhere.
    ///
    /// So the matrix is decomposed here, in a file that can be tested, and the
    /// bridge only ever hands Navisworks an axis, an angle and three numbers.
    ///
    /// Pure. In WriterTests.csproj.
    /// </summary>
    public static class TransformMath
    {
        /// <summary>Rotation as a unit axis and an angle in radians, plus the
        /// translation, taken from a column-major rigid 4x4.</summary>
        public sealed class Decomposed
        {
            public double[] Axis = new double[] { 0, 0, 1 };
            public double AngleRadians;
            public double[] Translation = new double[] { 0, 0, 0 };
            /// <summary>Uniform scale, read off the matrix. 1 for a rigid solve;
            /// anything else means the matrix did not come from one.</summary>
            public double Scale = 1.0;
        }

        /// <summary>
        /// Decompose. Returns null for a matrix that is not 16 numbers or whose
        /// rotation part has collapsed -- a caller that cannot place something
        /// must say so rather than place it at the origin.
        /// </summary>
        public static Decomposed Decompose(double[] m)
        {
            if (m == null || m.Length != 16) return null;

            // Column lengths are the scale; a rigid solve makes all three 1.
            double sx = Math.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]);
            double sy = Math.Sqrt(m[4] * m[4] + m[5] * m[5] + m[6] * m[6]);
            double sz = Math.Sqrt(m[8] * m[8] + m[9] * m[9] + m[10] * m[10]);
            if (sx < 1e-12 || sy < 1e-12 || sz < 1e-12) return null;

            // Row r, column c of the rotation is m[c * 4 + r].
            double r00 = m[0] / sx, r10 = m[1] / sx, r20 = m[2] / sx;
            double r01 = m[4] / sy, r11 = m[5] / sy, r21 = m[6] / sy;
            double r02 = m[8] / sz, r12 = m[9] / sz, r22 = m[10] / sz;

            // Shepperd's method: pick the largest of the four to divide by, so
            // the square root is never taken of something near zero.
            double trace = r00 + r11 + r22;
            double qw, qx, qy, qz;
            if (trace > 0)
            {
                double s = Math.Sqrt(trace + 1.0) * 2;
                qw = 0.25 * s;
                qx = (r21 - r12) / s;
                qy = (r02 - r20) / s;
                qz = (r10 - r01) / s;
            }
            else if (r00 > r11 && r00 > r22)
            {
                double s = Math.Sqrt(1.0 + r00 - r11 - r22) * 2;
                qw = (r21 - r12) / s;
                qx = 0.25 * s;
                qy = (r01 + r10) / s;
                qz = (r02 + r20) / s;
            }
            else if (r11 > r22)
            {
                double s = Math.Sqrt(1.0 + r11 - r00 - r22) * 2;
                qw = (r02 - r20) / s;
                qx = (r01 + r10) / s;
                qy = 0.25 * s;
                qz = (r12 + r21) / s;
            }
            else
            {
                double s = Math.Sqrt(1.0 + r22 - r00 - r11) * 2;
                qw = (r10 - r01) / s;
                qx = (r02 + r20) / s;
                qy = (r12 + r21) / s;
                qz = 0.25 * s;
            }

            double length = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (length < 1e-12) return null;
            qx /= length; qy /= length; qz /= length; qw /= length;

            double sine = Math.Sqrt(qx * qx + qy * qy + qz * qz);
            var result = new Decomposed
            {
                Translation = new double[] { m[12], m[13], m[14] },
                Scale = (sx + sy + sz) / 3.0
            };

            if (sine < 1e-12)
            {
                // No rotation at all. Any axis will do and the angle is zero;
                // pick a fixed one so the result is reproducible.
                result.Axis = new double[] { 0, 0, 1 };
                result.AngleRadians = 0;
                return result;
            }

            result.Axis = new double[] { qx / sine, qy / sine, qz / sine };
            // atan2 rather than 2*acos(w): accurate for small angles, which is
            // where a good alignment lives.
            result.AngleRadians = 2.0 * Math.Atan2(sine, qw);
            return result;
        }

        /// <summary>
        /// A column-major 4x4 from an axis, an angle and a translation. The
        /// inverse of <see cref="Decompose"/>, kept beside it so the round trip
        /// can be asserted.
        /// </summary>
        public static double[] Compose(double[] axis, double angleRadians, double[] translation)
        {
            double length = axis == null || axis.Length != 3
                ? 0
                : Math.Sqrt(axis[0] * axis[0] + axis[1] * axis[1] + axis[2] * axis[2]);
            double x = 0, y = 0, z = 1;
            if (length > 1e-15) { x = axis[0] / length; y = axis[1] / length; z = axis[2] / length; }

            double c = Math.Cos(angleRadians);
            double s = Math.Sin(angleRadians);
            double t = 1 - c;

            double[] shift = translation != null && translation.Length == 3
                ? translation
                : new double[] { 0, 0, 0 };

            return new double[]
            {
                t * x * x + c,     t * x * y + s * z, t * x * z - s * y, 0,
                t * x * y - s * z, t * y * y + c,     t * y * z + s * x, 0,
                t * x * z + s * y, t * y * z - s * x, t * z * z + c,     0,
                shift[0],          shift[1],          shift[2],          1
            };
        }

        /// <summary>Scale the translation column, leaving the rotation alone.
        /// Used when a transform solved in metres has to act on a document in
        /// millimetres or feet.</summary>
        public static double[] WithTranslationScaled(double[] m, double factor)
        {
            if (m == null || m.Length != 16) return m;
            var copy = (double[])m.Clone();
            copy[12] *= factor;
            copy[13] *= factor;
            copy[14] *= factor;
            return copy;
        }
    }
}
