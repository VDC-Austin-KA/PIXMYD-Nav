using System;
using System.Collections.Generic;

namespace PIXMYD_Nav.Core.Capture
{
    /// <summary>
    /// Horn's closed-form absolute orientation, ported from PIXMYD's
    /// packages/geo/src/registration.ts and its Swift port
    /// apps/ios/PIXMYD/Geo/Registration.swift.
    ///
    /// This exists because docs/contracts/capture.md ships the raw
    /// correspondences alongside the solved transform specifically so the
    /// consumer "can re-solve rather than trust a number it cannot check", and
    /// names a capture with correspondences but no solution as "the useful
    /// degraded mode, not an error". Neither is true if the only implementation
    /// lives on the phone that produced the number.
    ///
    /// It is a third copy of one algorithm, which RULES.md would normally object
    /// to -- but section 2 is explicit that shared code is vendored by copy and
    /// that no plugin references another tool's assembly. The honesty mechanism
    /// is the test vectors: the fixtures in CaptureTests.cs are the same ones the
    /// TypeScript and Swift suites use, so the three cannot drift silently.
    ///
    /// Scale is fixed at 1.0. A LiDAR capture and a BIM model are both metric,
    /// and letting scale float hides real error as a fitted parameter -- the RMS
    /// drops and the mesh is still in the wrong place.
    ///
    /// Pure. In WriterTests.csproj.
    /// </summary>
    public sealed class ControlPair
    {
        /// <summary>Surveyed position from points.json, metres.</summary>
        public double[] Project;
        /// <summary>The same point in the capture's frame, metres.</summary>
        public double[] Observed;
        public string Id;
        /// <summary>1-sigma accuracy, when recorded. Weights the solve.</summary>
        public double Sigma;

        public ControlPair(string id, double[] project, double[] observed)
        {
            Id = id;
            Project = project;
            Observed = observed;
            Sigma = 0;
        }
    }

    public sealed class Residual
    {
        public string Id;
        public double Error;
        public double[] Delta;
    }

    public sealed class RigidSolution
    {
        /// <summary>Column-major 4x4, capture frame to point-set frame.</summary>
        public double[] Matrix;
        public double Scale;
        public double RmsError;
        public double MaxError;
        public List<Residual> Residuals;
        public int PairCount;
    }

    public class RigidSolveException : Exception
    {
        public RigidSolveException(string message) : base(message) { }
    }

    public static class RigidSolve
    {
        /// <summary>
        /// Solve, or throw with the same wording the phone uses. The UI copy and
        /// the field guidance were written against these messages.
        /// </summary>
        public static RigidSolution Solve(List<ControlPair> pairs)
        {
            if (pairs == null || pairs.Count < 3)
                throw new RigidSolveException(
                    "A rigid transform needs at least 3 control points; got " +
                    (pairs == null ? 0 : pairs.Count) + ".");

            int n = pairs.Count;
            var weights = new double[n];
            for (int i = 0; i < n; i++)
                weights[i] = pairs[i].Sigma > 0 ? 1.0 / (pairs[i].Sigma * pairs[i].Sigma) : 1.0;

            double[] cSrc = WeightedCentroid(pairs, weights, true);
            double[] cDst = WeightedCentroid(pairs, weights, false);

            var pSrc = new double[n][];
            var pDst = new double[n][];
            for (int i = 0; i < n; i++)
            {
                pSrc[i] = Sub(pairs[i].Observed, cSrc);
                pDst[i] = Sub(pairs[i].Project, cDst);
            }

            if (IsDegenerate(pSrc) || IsDegenerate(pDst))
                throw new RigidSolveException(
                    "The control points are collinear or coincident, so the rotation about that " +
                    "line is undetermined. Locate a point well off the line.");

            // Weighted 3x3 cross-covariance, row-major.
            var m = new double[9];
            for (int i = 0; i < n; i++)
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++)
                        m[r * 3 + c] += weights[i] * pSrc[i][r] * pDst[i][c];

            double sxx = m[0], sxy = m[1], sxz = m[2];
            double syx = m[3], syy = m[4], syz = m[5];
            double szx = m[6], szy = m[7], szz = m[8];

            // Horn's symmetric 4x4, in (w, x, y, z) ordering.
            var nMatrix = new double[]
            {
                sxx + syy + szz, syz - szy,        szx - sxz,         sxy - syx,
                syz - szy,       sxx - syy - szz,  sxy + syx,         szx + sxz,
                szx - sxz,       sxy + syx,       -sxx + syy - szz,   syz + szy,
                sxy - syx,       szx + sxz,        syz + szy,        -sxx - syy + szz
            };

            double[] q = LargestEigenvector4(nMatrix); // (x, y, z, w)

            const double scale = 1.0;
            double[] translation = Sub(cDst, Mul(Rotate(q, cSrc), scale));

            var residuals = new List<Residual>(n);
            double sumSq = 0, maxError = 0;
            for (int i = 0; i < n; i++)
            {
                double[] mapped = Add(Mul(Rotate(q, pairs[i].Observed), scale), translation);
                double[] delta = Sub(mapped, pairs[i].Project);
                double error = Length(delta);
                residuals.Add(new Residual { Id = pairs[i].Id, Error = error, Delta = delta });
                sumSq += error * error;
                if (error > maxError) maxError = error;
            }

            return new RigidSolution
            {
                Matrix = Compose(translation, q, scale),
                Scale = scale,
                RmsError = Math.Sqrt(sumSq / n),
                MaxError = maxError,
                Residuals = residuals,
                PairCount = n
            };
        }

        // MARK: helpers

        private static double[] WeightedCentroid(List<ControlPair> pairs, double[] weights, bool observed)
        {
            double total = 0;
            var c = new double[3];
            for (int i = 0; i < pairs.Count; i++)
            {
                double[] p = observed ? pairs[i].Observed : pairs[i].Project;
                for (int k = 0; k < 3; k++) c[k] += p[k] * weights[i];
                total += weights[i];
            }
            for (int k = 0; k < 3; k++) c[k] /= total;
            return c;
        }

        /// <summary>
        /// Collinear or coincident? A rigid transform is not determined by such a
        /// set, and the solve would return an arbitrary rotation about the line.
        /// </summary>
        private static bool IsDegenerate(double[][] centred)
        {
            double maxLen = 0;
            double[] axis = new double[3];
            foreach (double[] p in centred)
            {
                double l = Length(p);
                if (l > maxLen) { maxLen = l; axis = p; }
            }
            if (maxLen < 1e-9) return true; // all coincident

            double[] unit = Mul(axis, 1.0 / maxLen);
            double maxPerp = 0;
            foreach (double[] p in centred)
            {
                double along = Dot(p, unit);
                double perp = Length(Sub(p, Mul(unit, along)));
                if (perp > maxPerp) maxPerp = perp;
            }
            // Perpendicular spread under 0.1% of the longest baseline is
            // collinear for any practical purpose.
            return maxPerp < maxLen * 1e-3;
        }

        /// <summary>
        /// Largest eigenvector of a symmetric 4x4 (row-major), by power iteration
        /// with a shift. Shifting by the trace guarantees the dominant eigenvalue
        /// is the one power iteration finds, even when the true largest is
        /// negative. Returns (x, y, z, w).
        /// </summary>
        private static double[] LargestEigenvector4(double[] n)
        {
            double trace = 0;
            for (int i = 0; i < 4; i++) trace += Math.Abs(n[i * 5]);
            double shift = trace + 1;

            var m = (double[])n.Clone();
            for (int i = 0; i < 4; i++) m[i * 5] += shift;

            // Start away from any axis so a symmetric matrix cannot leave us on
            // an eigenvector of the wrong eigenvalue.
            var v = new double[] { 0.5, 0.5, 0.5, 0.5 };
            for (int iteration = 0; iteration < 200; iteration++)
            {
                var next = new double[4];
                for (int r = 0; r < 4; r++)
                {
                    double sum = 0;
                    for (int c = 0; c < 4; c++) sum += m[r * 4 + c] * v[c];
                    next[r] = sum;
                }
                double norm = Math.Sqrt(next[0] * next[0] + next[1] * next[1] + next[2] * next[2] + next[3] * next[3]);
                if (norm < 1e-300) break;
                for (int i = 0; i < 4; i++) next[i] /= norm;

                double delta = 0;
                for (int i = 0; i < 4; i++) delta += Math.Abs(next[i] - v[i]);
                v = next;
                if (delta < 1e-15) break;
            }

            // Horn's quaternion is (w, x, y, z); this code stores (x, y, z, w).
            return Normalize(new double[] { v[1], v[2], v[3], v[0] });
        }

        /// <summary>
        /// Compose translation * rotation * uniform scale into a column-major
        /// 4x4, matching the TypeScript mat4.compose the contract names.
        /// </summary>
        private static double[] Compose(double[] translation, double[] q, double scale)
        {
            double x = q[0], y = q[1], z = q[2], w = q[3];
            double x2 = x + x, y2 = y + y, z2 = z + z;
            double xx = x * x2, xy = x * y2, xz = x * z2;
            double yy = y * y2, yz = y * z2, zz = z * z2;
            double wx = w * x2, wy = w * y2, wz = w * z2;

            return new double[]
            {
                (1 - (yy + zz)) * scale, (xy + wz) * scale,       (xz - wy) * scale,       0,
                (xy - wz) * scale,       (1 - (xx + zz)) * scale, (yz + wx) * scale,       0,
                (xz + wy) * scale,       (yz - wx) * scale,       (1 - (xx + yy)) * scale, 0,
                translation[0],          translation[1],          translation[2],          1
            };
        }

        private static double[] Rotate(double[] q, double[] p)
        {
            double x = q[0], y = q[1], z = q[2], w = q[3];
            double tx = 2 * (y * p[2] - z * p[1]);
            double ty = 2 * (z * p[0] - x * p[2]);
            double tz = 2 * (x * p[1] - y * p[0]);
            return new double[]
            {
                p[0] + w * tx + (y * tz - z * ty),
                p[1] + w * ty + (z * tx - x * tz),
                p[2] + w * tz + (x * ty - y * tx)
            };
        }

        private static double[] Normalize(double[] q)
        {
            double len = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            if (len < 1e-300) return new double[] { 0, 0, 0, 1 };
            return new double[] { q[0] / len, q[1] / len, q[2] / len, q[3] / len };
        }

        private static double[] Add(double[] a, double[] b)
        {
            return new double[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        }

        private static double[] Sub(double[] a, double[] b)
        {
            return new double[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        }

        private static double[] Mul(double[] a, double s)
        {
            return new double[] { a[0] * s, a[1] * s, a[2] * s };
        }

        private static double Dot(double[] a, double[] b)
        {
            return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        }

        private static double Length(double[] a)
        {
            return Math.Sqrt(Dot(a, a));
        }
    }
}
