using System;
using System.Collections.Generic;

namespace PIXMYD_Nav.Core.Capture
{
    /// <summary>
    /// The two-point solve: registration with vertical held fixed.
    ///
    /// Horn's method in RigidSolve.cs is unconstrained, so it needs three
    /// non-collinear pairs to pin six degrees of freedom. On site that is the
    /// step that stops people -- a crew has two column marks they can reach and
    /// a third that is behind a stack of drywall, and the app refuses.
    ///
    /// It does not have to. Both frames already know which way is down. ARKit
    /// runs gravity-aligned, so the capture's +Y is vertical to a fraction of a
    /// degree; a Navisworks model states its up axis. Fixing the vertical
    /// removes roll and pitch and leaves four unknowns -- heading, and three
    /// translations -- which two points over-determine.
    ///
    /// That is not a lower-quality answer. Gravity from an IMU is *better*
    /// conditioned than roll and pitch fitted from three hand-aimed picks, and
    /// this solve is often the right one at five points too. What two points
    /// cannot do is tell you when one of them is wrong: with three residuals a
    /// blunder shows up, with two the fit absorbs it and reports zero. So the
    /// solve is offered and the caller says so -- see the guidance strings
    /// below, which the UI shows verbatim.
    ///
    /// Closed form, no iteration:
    ///
    ///   1. rotate the capture's up onto the model's up (shortest arc)
    ///   2. solve the one remaining angle about that axis --
    ///      theta = atan2( sum w U.(a x b), sum w (a.b) ) over the horizontal
    ///      components, which is the exact weighted least-squares heading
    ///   3. translation from the weighted centroids
    ///
    /// Mirrors apps/ios/PIXMYD/Geo/Registration.swift's
    /// solveGravityConstrained, vector for vector, and shares its test vectors
    /// so the two cannot drift.
    ///
    /// Pure. In WriterTests.csproj.
    /// </summary>
    public static class GravitySolve
    {
        /// <summary>Two pairs determine heading and translation. One does not.</summary>
        public const int MinimumPairs = 2;

        /// <summary>
        /// The up vector for a contract up-axis string. Anything unrecognised is
        /// Z: that is what every Navisworks document this plugin has met uses,
        /// and guessing Y for a typo would rotate a whole scan onto its side.
        /// </summary>
        public static double[] UpVectorFor(string upAxis)
        {
            string axis = (upAxis ?? "").Trim().ToUpperInvariant();
            if (axis == "Y") return new double[] { 0, 1, 0 };
            if (axis == "X") return new double[] { 1, 0, 0 };
            return new double[] { 0, 0, 1 };
        }

        /// <summary>
        /// ARKit's world frame is gravity-aligned with +Y up, and every capture
        /// this suite reads comes from ARKit. Named rather than inlined so the
        /// day something else produces a capture, there is one place to look.
        /// </summary>
        public static double[] CaptureUp { get { return new double[] { 0, 1, 0 }; } }

        /// <summary>
        /// Solve with the vertical locked.
        ///
        /// <paramref name="captureUp"/> is up in the capture's own frame and
        /// <paramref name="projectUp"/> is up in the model's. Throws with a line
        /// that can be shown verbatim, like RigidSolve does.
        /// </summary>
        public static RigidSolution Solve(
            List<ControlPair> pairs,
            double[] captureUp,
            double[] projectUp)
        {
            if (pairs == null || pairs.Count < MinimumPairs)
                throw new RigidSolveException(
                    "Holding the vertical fixed still needs at least 2 control points; got " +
                    (pairs == null ? 0 : pairs.Count) + ". One point fixes where the scan sits " +
                    "and nothing about which way it faces.");

            double[] up = Normalise(projectUp);
            double[] sourceUp = Normalise(captureUp);
            if (Length(up) < 0.5 || Length(sourceUp) < 0.5)
                throw new RigidSolveException(
                    "The up axis for one of the two frames is not a direction, so the vertical " +
                    "cannot be held fixed. Solve with three points instead.");

            int n = pairs.Count;
            var weights = new double[n];
            for (int i = 0; i < n; i++)
                weights[i] = pairs[i].Sigma > 0 ? 1.0 / (pairs[i].Sigma * pairs[i].Sigma) : 1.0;

            // 1. Level the capture: the shortest rotation carrying its up onto
            //    the model's. Everything after this happens in a frame whose
            //    vertical is already right.
            double[] levelling = ShortestArc(sourceUp, up);

            var levelled = new double[n][];
            for (int i = 0; i < n; i++) levelled[i] = Rotate(levelling, pairs[i].Observed);

            double[] centreSource = WeightedCentroid(levelled, weights);
            double[] centreTarget = WeightedCentroid(Projects(pairs), weights);

            // 2. Heading, in closed form, from the horizontal components only.
            //    The vertical components carry no information about a rotation
            //    around the vertical, and including them would let a height
            //    difference bias the angle.
            double numerator = 0, denominator = 0, horizontalWeight = 0;
            for (int i = 0; i < n; i++)
            {
                double[] a = Horizontal(Sub(levelled[i], centreSource), up);
                double[] b = Horizontal(Sub(pairs[i].Project, centreTarget), up);
                numerator += weights[i] * Dot(up, Cross(a, b));
                denominator += weights[i] * Dot(a, b);
                horizontalWeight += weights[i] * Length(a) * Length(b);
            }

            if (horizontalWeight < 1e-9)
                throw new RigidSolveException(
                    "These control points sit in a vertical line, so holding the vertical fixed " +
                    "leaves the heading undetermined. Locate a point somewhere else on the floor.");

            double theta = Math.Atan2(numerator, denominator);
            double[] heading = AboutAxis(up, theta);

            // 3. Compose. The levelling happens first, so it is the right-hand
            //    factor: q = heading * levelling.
            double[] rotation = Multiply(heading, levelling);
            double[] translation = Sub(centreTarget, Rotate(heading, centreSource));

            const double scale = 1.0;
            var residuals = new List<Residual>(n);
            double sumSquared = 0, maxError = 0;
            for (int i = 0; i < n; i++)
            {
                double[] mapped = Add(Rotate(rotation, pairs[i].Observed), translation);
                double[] delta = Sub(mapped, pairs[i].Project);
                double error = Length(delta);
                residuals.Add(new Residual { Id = pairs[i].Id, Error = error, Delta = delta });
                sumSquared += error * error;
                if (error > maxError) maxError = error;
            }

            return new RigidSolution
            {
                Matrix = Compose(translation, rotation, scale),
                Scale = scale,
                RmsError = Math.Sqrt(sumSquared / n),
                MaxError = maxError,
                Residuals = residuals,
                PairCount = n
            };
        }

        /// <summary>
        /// What a fit from this many points can and cannot tell you.
        ///
        /// Shown next to the RMS, because an RMS from two points is a number
        /// with no redundancy behind it: it is zero by construction whether the
        /// points were right or wrong, and a user who reads "0 mm" without this
        /// line will trust it more than a 4 mm fit from six points that is
        /// actually the better answer.
        /// </summary>
        public static string RedundancyGuidance(int pairCount)
        {
            if (pairCount <= 2)
                return "Two points fix the scan with the vertical held from gravity, but they " +
                       "leave no redundancy: the fit reports near-zero error whether the points " +
                       "are right or wrong. Locate a third to get an error you can believe, and " +
                       "check the placement against something you can see before working from it.";
            if (pairCount == 3)
                return "Three points give one check on the fit. A blunder in any of them shows " +
                       "up as a raised error but cannot yet be told apart from the other two.";
            return "Four or more points leave enough redundancy for a bad one to be identified " +
                   "rather than merely suspected.";
        }

        // MARK: - Quaternions
        //
        // Stored (x, y, z, w) throughout, matching RigidSolve.cs, Registration
        // .swift and the glTF convention. A second layout in a third file is
        // exactly how a sign error gets in.

        /// <summary>Shortest rotation carrying unit vector <paramref name="from"/> onto <paramref name="to"/>.</summary>
        internal static double[] ShortestArc(double[] from, double[] to)
        {
            double[] a = Normalise(from);
            double[] b = Normalise(to);
            double cosine = Dot(a, b);

            // Opposed: every rotation through 180 degrees is equally short, so
            // pick one perpendicular axis deterministically rather than letting
            // a near-zero cross product choose it out of rounding noise.
            if (cosine < -0.999999)
                return AboutAxis(AnyPerpendicular(a), Math.PI);

            double[] axis = Cross(a, b);
            return NormaliseQuaternion(new double[] { axis[0], axis[1], axis[2], 1 + cosine });
        }

        internal static double[] AboutAxis(double[] axis, double radians)
        {
            double[] n = Normalise(axis);
            double half = radians * 0.5;
            double s = Math.Sin(half);
            return new double[] { n[0] * s, n[1] * s, n[2] * s, Math.Cos(half) };
        }

        /// <summary>Apply <paramref name="b"/> first, then <paramref name="a"/>.</summary>
        internal static double[] Multiply(double[] a, double[] b)
        {
            double ax = a[0], ay = a[1], az = a[2], aw = a[3];
            double bx = b[0], by = b[1], bz = b[2], bw = b[3];
            return NormaliseQuaternion(new double[]
            {
                aw * bx + ax * bw + ay * bz - az * by,
                aw * by - ax * bz + ay * bw + az * bx,
                aw * bz + ax * by - ay * bx + az * bw,
                aw * bw - ax * bx - ay * by - az * bz
            });
        }

        internal static double[] Rotate(double[] q, double[] p)
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

        // MARK: - Vectors

        private static double[][] Projects(List<ControlPair> pairs)
        {
            var all = new double[pairs.Count][];
            for (int i = 0; i < pairs.Count; i++) all[i] = pairs[i].Project;
            return all;
        }

        private static double[] WeightedCentroid(double[][] points, double[] weights)
        {
            var c = new double[3];
            double total = 0;
            for (int i = 0; i < points.Length; i++)
            {
                for (int k = 0; k < 3; k++) c[k] += points[i][k] * weights[i];
                total += weights[i];
            }
            if (total <= 0) return c;
            for (int k = 0; k < 3; k++) c[k] /= total;
            return c;
        }

        /// <summary>The part of a vector at right angles to the up axis.</summary>
        private static double[] Horizontal(double[] v, double[] up)
        {
            double along = Dot(v, up);
            return new double[] { v[0] - up[0] * along, v[1] - up[1] * along, v[2] - up[2] * along };
        }

        private static double[] AnyPerpendicular(double[] v)
        {
            // Cross with whichever axis this vector is least aligned to, so the
            // result is never near zero.
            double[] axis = Math.Abs(v[0]) < 0.9
                ? new double[] { 1, 0, 0 }
                : new double[] { 0, 1, 0 };
            return Normalise(Cross(v, axis));
        }

        private static double[] Cross(double[] a, double[] b)
        {
            return new double[]
            {
                a[1] * b[2] - a[2] * b[1],
                a[2] * b[0] - a[0] * b[2],
                a[0] * b[1] - a[1] * b[0]
            };
        }

        private static double[] Normalise(double[] v)
        {
            if (v == null || v.Length != 3) return new double[] { 0, 0, 0 };
            double length = Length(v);
            if (length < 1e-15) return new double[] { 0, 0, 0 };
            return new double[] { v[0] / length, v[1] / length, v[2] / length };
        }

        private static double[] NormaliseQuaternion(double[] q)
        {
            double length = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            if (length < 1e-300) return new double[] { 0, 0, 0, 1 };
            return new double[] { q[0] / length, q[1] / length, q[2] / length, q[3] / length };
        }

        private static double[] Add(double[] a, double[] b)
        {
            return new double[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        }

        private static double[] Sub(double[] a, double[] b)
        {
            return new double[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
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
