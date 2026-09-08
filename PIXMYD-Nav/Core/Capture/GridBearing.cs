using System;
using System.Collections.Generic;
using PIXMYD_Nav.Core.Points;

namespace PIXMYD_Nav.Core.Capture
{
    /// <summary>
    /// Which way a building faces, read off its walls.
    ///
    /// ## Why this exists
    ///
    /// A capture is levelled but not oriented. ARKit runs gravity-aligned, so
    /// its Y is up and its horizontal axes are wherever the phone happened to
    /// be pointing when the session started. Nothing in the capture records
    /// north or a building grid, so a scan with no control points arrives
    /// upright at an arbitrary heading -- measured across three captures of one
    /// building: 49, 37 and 87 degrees. There is no constant to bake.
    ///
    /// But the walls know. A rectilinear building has one horizontal direction,
    /// with everything vertical parallel or perpendicular to it, so the
    /// dominant bearing of the wall faces is the grid. Read it off the scan,
    /// read it off the model, and the difference is the heading nobody
    /// measured.
    ///
    /// ## What it is not
    ///
    /// Not a fit, and it must never be reported as one. A grade quotes an RMS
    /// against control, and there is no control here: this is a guess from
    /// shape, right often enough to save the operator a job and wrong in ways
    /// only they can see. So it stays inside hand placement, where the
    /// alternative is an arbitrary heading anyway, and never touches a solved
    /// capture.
    ///
    /// ## Mod 90, deliberately
    ///
    /// Four wall directions ninety degrees apart are indistinguishable to this,
    /// so the answer is folded into [0, 90) and the correction it implies into
    /// (-45, +45]. That gets the grid right and the facing only by luck. A scan
    /// that comes back a quarter turn out is one gizmo operation away; a scan
    /// that arrives 49 degrees out is a fiddle.
    ///
    /// Z-up throughout: both meshes are in the document's frame by the time
    /// anyone asks.
    /// </summary>
    public static class GridBearing
    {
        /// <summary>A face this far from vertical is a floor or a ceiling, and
        /// says nothing about the grid.</summary>
        public const double MaxVerticalComponent = 0.25;

        /// <summary>
        /// How much of the wall area has to agree before the answer is offered.
        ///
        /// Measured on real captures, the dominant direction takes 37 to 53 per
        /// cent of wall area within four degrees. A curved or cluttered space
        /// lands far below that, and a building whose walls do not agree with
        /// each other has no grid to snap to. Better to say so and let the
        /// operator turn it.
        /// </summary>
        public const double MinimumShare = 0.20;

        /// <summary>Half-width of the band counted as agreeing, in degrees.</summary>
        public const int Tolerance = 4;

        public sealed class Result
        {
            /// <summary>The grid direction, folded into [0, 90).</summary>
            public double Degrees;
            /// <summary>Fraction of wall area within <see cref="Tolerance"/> of
            /// it. Confidence, not accuracy.</summary>
            public double Share;
            /// <summary>Whether the walls agreed enough to be worth using.</summary>
            public bool Found;
        }

        /// <summary>
        /// The dominant wall bearing of a Z-up triangle mesh.
        ///
        /// Area-weighted, because a wall is a few large triangles and the
        /// clutter in front of it is thousands of small ones. Counting faces
        /// would let a potted plant outvote the wall behind it.
        /// </summary>
        public static Result Estimate(IList<Vec3> vertices, IList<int> triangles)
        {
            var result = new Result();
            if (vertices == null || triangles == null) return result;

            // One bucket per degree across the folded range. Finer would be
            // false precision: the input is a tessellation, not a survey.
            var histogram = new double[90];
            double total = 0;

            for (int i = 0; i + 2 < triangles.Count; i += 3)
            {
                int ia = triangles[i], ib = triangles[i + 1], ic = triangles[i + 2];
                if (ia < 0 || ib < 0 || ic < 0) continue;
                if (ia >= vertices.Count || ib >= vertices.Count || ic >= vertices.Count) continue;

                Vec3 a = vertices[ia], b = vertices[ib], c = vertices[ic];
                double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
                double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;

                // The cross product's length is twice the area, so the weight
                // arrives with the normal rather than costing a second pass.
                double nx = uy * vz - uz * vy;
                double ny = uz * vx - ux * vz;
                double nz = ux * vy - uy * vx;

                double area = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (area < 1e-12) continue;
                if (Math.Abs(nz / area) > MaxVerticalComponent) continue;

                double horizontal = Math.Sqrt(nx * nx + ny * ny);
                if (horizontal < 1e-9) continue;

                double degrees = Math.Atan2(ny, nx) * 180.0 / Math.PI;
                int bucket = (int)Math.Floor(Fold(degrees));
                if (bucket < 0) bucket = 0;
                if (bucket > 89) bucket = 89;

                histogram[bucket] += area;
                total += area;
            }

            if (total <= 0) return result;

            // The best bucket is the one whose neighbourhood holds the most
            // area, not the tallest single bucket: a grid a fraction of a
            // degree off a bucket edge splits its area across two.
            int best = 0;
            double bestWeight = -1;
            for (int centre = 0; centre < 90; centre++)
            {
                double weight = 0;
                for (int d = -Tolerance; d <= Tolerance; d++)
                    weight += histogram[((centre + d) % 90 + 90) % 90];
                if (weight > bestWeight) { bestWeight = weight; best = centre; }
            }

            // Refine to the area-weighted centre of that neighbourhood, so the
            // answer is not quantised to whole degrees.
            double moment = 0, mass = 0;
            for (int d = -Tolerance; d <= Tolerance; d++)
            {
                double w = histogram[((best + d) % 90 + 90) % 90];
                moment += w * (best + d + 0.5);
                mass += w;
            }

            result.Degrees = mass > 0 ? Fold(moment / mass) : best + 0.5;
            result.Share = bestWeight / total;
            result.Found = result.Share >= MinimumShare;
            return result;
        }

        /// <summary>
        /// The turn that puts <paramref name="scanDegrees"/> onto
        /// <paramref name="modelDegrees"/>, about the vertical, folded into
        /// (-45, +45].
        ///
        /// Folded because the two grids are only known mod 90: any larger
        /// answer is the same alignment reached the long way round, and turning
        /// a building 70 degrees to achieve what 20 the other way does is the
        /// sort of thing that reads as a bug.
        /// </summary>
        public static double Correction(double scanDegrees, double modelDegrees)
        {
            double turn = Fold(modelDegrees - scanDegrees);
            return turn > 45 ? turn - 90 : turn;
        }

        private static double Fold(double degrees)
        {
            double folded = degrees % 90.0;
            return folded < 0 ? folded + 90.0 : folded;
        }
    }
}
