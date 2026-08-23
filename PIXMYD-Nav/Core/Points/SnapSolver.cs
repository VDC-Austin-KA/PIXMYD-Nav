using System;
using System.Collections.Generic;
using System.Globalization;

namespace PIXMYD_Nav.Core.Points
{
    /// <summary>
    /// Where a placed point actually lands.
    ///
    /// A survey point is only worth the coordinate it carries, and a coordinate
    /// taken from the centre of a bounding box is not a coordinate anybody can
    /// find on site -- it is inside the steel. The old "capture selection"
    /// button produced exactly that: one point per selected item, at the middle
    /// of its extents. It is fast to write and it is not a control point.
    ///
    /// What a crew uses is a corner. So a pick is pulled onto real geometry,
    /// with corners first:
    ///
    ///   Corner  the nearest true vertex -- a snap point the model itself
    ///           publishes, else the end of a real edge, else a mesh vertex
    ///   Edge    the nearest position along an edge, when no corner is close
    ///   Face    the nearest position on a surface, for a point on a slab or
    ///           a wall face where there is no corner to take
    ///   Free    exactly where the pick landed, when nothing is within reach
    ///
    /// The cascade is the behaviour, not a fallback bolted on: a user aiming at
    /// a column corner and missing by 30 mm wants the corner, and a user aiming
    /// at the middle of a slab wants the slab. Asking them to pick the mode
    /// first would be a mode they get wrong half the time. So the requested
    /// mode is tried, then the ones below it, and the result says which one
    /// answered so the list can show it.
    ///
    /// Pure: no Navisworks, no I/O. The primitives come from
    /// Core/NavBridge/PrimitiveHarvester.cs, which is the part that cannot be
    /// tested off a workstation. In WriterTests.csproj.
    /// </summary>
    public enum SnapMode
    {
        /// <summary>Nearest true vertex. The default, and what a control point is.</summary>
        Corner = 0,
        /// <summary>Nearest position along an edge.</summary>
        Edge = 1,
        /// <summary>Nearest position on a face.</summary>
        Face = 2,
        /// <summary>Wherever the pick landed. No geometry consulted.</summary>
        Free = 3
    }

    public sealed class SnapResult
    {
        public Vec3 Position;
        /// <summary>Which mode actually answered -- not necessarily the one asked for.</summary>
        public SnapMode Mode;
        /// <summary>How far the pick moved, in the soup's units.</summary>
        public double Distance;
        /// <summary>False when nothing was within the search radius and the raw pick was kept.</summary>
        public bool Snapped;

        public string Describe()
        {
            if (!Snapped) return "free";
            return Mode.ToString().ToLowerInvariant() + " " +
                   (Distance * 1000).ToString("0.#", CultureInfo.InvariantCulture) + " mm";
        }
    }

    /// <summary>
    /// Triangles, edges and published snap points from some part of a model,
    /// in one coordinate frame.
    ///
    /// Vertices are shared and deduplicated on insert: a tessellated wall
    /// arrives as thousands of triangles that between them name a few hundred
    /// distinct positions, and corner snapping is a nearest-vertex search.
    /// Deduplicating at insert makes that search over the positions that exist
    /// rather than over every corner of every triangle.
    /// </summary>
    public sealed class MeshSoup
    {
        /// <summary>Positions below this apart are the same position. Half a
        /// millimetre: finer than any model tolerance, coarser than the noise
        /// a float tessellation introduces.</summary>
        public const double WeldTolerance = 0.0005;

        private readonly List<Vec3> _vertices = new List<Vec3>();
        private readonly List<int> _triangles = new List<int>();
        private readonly List<int> _segments = new List<int>();
        private readonly List<Vec3> _snapPoints = new List<Vec3>();
        private readonly Dictionary<long, List<int>> _buckets = new Dictionary<long, List<int>>();

        public IList<Vec3> Vertices { get { return _vertices; } }
        /// <summary>Index triples into <see cref="Vertices"/>.</summary>
        public IList<int> Triangles { get { return _triangles; } }
        /// <summary>Index pairs into <see cref="Vertices"/>.</summary>
        public IList<int> Segments { get { return _segments; } }
        /// <summary>Positions the model itself publishes as snap targets.</summary>
        public IList<Vec3> SnapPoints { get { return _snapPoints; } }

        public int TriangleCount { get { return _triangles.Count / 3; } }
        public int SegmentCount { get { return _segments.Count / 2; } }
        public bool IsEmpty
        {
            get { return _triangles.Count == 0 && _segments.Count == 0 && _snapPoints.Count == 0; }
        }

        public void AddTriangle(Vec3 a, Vec3 b, Vec3 c)
        {
            int ia = Weld(a), ib = Weld(b), ic = Weld(c);
            // A degenerate triangle contributes no face and no edge, and its
            // vertices are already welded in, so drop it rather than let a
            // zero-area facet into the closest-point search.
            if (ia == ib || ib == ic || ia == ic) return;
            _triangles.Add(ia); _triangles.Add(ib); _triangles.Add(ic);
        }

        public void AddSegment(Vec3 a, Vec3 b)
        {
            int ia = Weld(a), ib = Weld(b);
            if (ia == ib) return;
            _segments.Add(ia); _segments.Add(ib);
        }

        /// <summary>
        /// A position the model publishes as a snap target. Welded like every
        /// other vertex, and recorded at the welded position rather than the
        /// one passed in, so the same corner reported by two fragments becomes
        /// one candidate instead of two a hair apart.
        /// </summary>
        public void AddSnapPoint(Vec3 p)
        {
            int index = Weld(p);
            Vec3 welded = _vertices[index];
            foreach (Vec3 existing in _snapPoints)
                if (Geometry.DistanceSquared(existing, welded) <= WeldTolerance * WeldTolerance) return;
            _snapPoints.Add(welded);
        }

        private int Weld(Vec3 p)
        {
            long key = BucketKey(p);
            // Neighbouring buckets too: two positions 0.1 mm apart can still
            // straddle a bucket boundary, and missing that would leave a
            // duplicate vertex exactly where snapping is most sensitive.
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        List<int> bucket;
                        if (!_buckets.TryGetValue(key + dx * 73856093L + dy * 19349663L + dz * 83492791L, out bucket))
                            continue;
                        foreach (int index in bucket)
                            if (Geometry.DistanceSquared(_vertices[index], p) <= WeldTolerance * WeldTolerance)
                                return index;
                    }

            _vertices.Add(p);
            int added = _vertices.Count - 1;
            List<int> home;
            if (!_buckets.TryGetValue(key, out home))
            {
                home = new List<int>(4);
                _buckets[key] = home;
            }
            home.Add(added);
            return added;
        }

        private static long BucketKey(Vec3 p)
        {
            const double cell = WeldTolerance * 2;
            long x = (long)Math.Floor(p.X / cell);
            long y = (long)Math.Floor(p.Y / cell);
            long z = (long)Math.Floor(p.Z / cell);
            return x * 73856093L + y * 19349663L + z * 83492791L;
        }
    }

    public static class SnapSolver
    {
        /// <summary>
        /// How far a pick is allowed to move, in metres, when the caller has no
        /// better idea. 50 mm is about the width of a fingertip on a phone-sized
        /// target at arm's length in a viewport, and it is small enough that a
        /// snap can never quietly take a different corner.
        /// </summary>
        public const double DefaultRadius = 0.050;

        /// <summary>
        /// Pull a raw pick onto real geometry.
        ///
        /// Tries the requested mode, then every weaker one in turn, and reports
        /// which answered. Free always answers, so this never returns null.
        /// </summary>
        public static SnapResult Snap(MeshSoup soup, Vec3 pick, SnapMode preferred, double radius)
        {
            if (radius <= 0) radius = DefaultRadius;

            if (soup != null && !soup.IsEmpty)
            {
                for (int mode = (int)preferred; mode < (int)SnapMode.Free; mode++)
                {
                    SnapResult found = Try(soup, pick, (SnapMode)mode, radius);
                    if (found != null) return found;
                }
            }

            return new SnapResult
            {
                Position = pick,
                Mode = SnapMode.Free,
                Distance = 0,
                Snapped = false
            };
        }

        public static SnapResult Snap(MeshSoup soup, Vec3 pick, SnapMode preferred)
        {
            return Snap(soup, pick, preferred, DefaultRadius);
        }

        private static SnapResult Try(MeshSoup soup, Vec3 pick, SnapMode mode, double radius)
        {
            switch (mode)
            {
                case SnapMode.Corner: return NearestCorner(soup, pick, radius);
                case SnapMode.Edge: return NearestEdge(soup, pick, radius);
                case SnapMode.Face: return NearestFace(soup, pick, radius);
                default: return null;
            }
        }

        /// <summary>
        /// The nearest true vertex.
        ///
        /// Ranked, not merely nearest. A snap point the model publishes beats an
        /// edge end, and an edge end beats a bare mesh vertex, because a
        /// tessellated cylinder is covered in mesh vertices that are not corners
        /// of anything. Only if no better class has a candidate inside the
        /// radius does the next one get consulted.
        /// </summary>
        private static SnapResult NearestCorner(MeshSoup soup, Vec3 pick, double radius)
        {
            SnapResult best = Nearest(soup.SnapPoints, pick, radius);
            if (best != null) return best;

            best = NearestOfIndices(soup, soup.Segments, pick, radius);
            if (best != null) return best;

            return NearestOfIndices(soup, soup.Triangles, pick, radius);
        }

        private static SnapResult Nearest(IList<Vec3> points, Vec3 pick, double radius)
        {
            double bestSq = radius * radius;
            int bestIndex = -1;
            for (int i = 0; i < points.Count; i++)
            {
                double d = Geometry.DistanceSquared(points[i], pick);
                if (d < bestSq) { bestSq = d; bestIndex = i; }
            }
            if (bestIndex < 0) return null;
            return Result(points[bestIndex], SnapMode.Corner, Math.Sqrt(bestSq));
        }

        private static SnapResult NearestOfIndices(MeshSoup soup, IList<int> indices, Vec3 pick, double radius)
        {
            double bestSq = radius * radius;
            int bestVertex = -1;
            for (int i = 0; i < indices.Count; i++)
            {
                double d = Geometry.DistanceSquared(soup.Vertices[indices[i]], pick);
                if (d < bestSq) { bestSq = d; bestVertex = indices[i]; }
            }
            if (bestVertex < 0) return null;
            return Result(soup.Vertices[bestVertex], SnapMode.Corner, Math.Sqrt(bestSq));
        }

        /// <summary>
        /// The nearest position along an edge.
        ///
        /// Real edges (line primitives) are searched before triangle edges for
        /// the same reason corners are ranked: a tessellation seam across the
        /// middle of a flat wall is a triangle edge and is not an edge of
        /// anything a person can see.
        /// </summary>
        private static SnapResult NearestEdge(MeshSoup soup, Vec3 pick, double radius)
        {
            SnapResult best = NearestSegment(soup, pick, radius);
            if (best != null) return best;

            double bestSq = radius * radius;
            var bestPoint = new Vec3();
            bool found = false;
            for (int t = 0; t + 2 < soup.Triangles.Count; t += 3)
            {
                Vec3 a = soup.Vertices[soup.Triangles[t]];
                Vec3 b = soup.Vertices[soup.Triangles[t + 1]];
                Vec3 c = soup.Vertices[soup.Triangles[t + 2]];
                ConsiderSegment(a, b, pick, ref bestSq, ref bestPoint, ref found);
                ConsiderSegment(b, c, pick, ref bestSq, ref bestPoint, ref found);
                ConsiderSegment(c, a, pick, ref bestSq, ref bestPoint, ref found);
            }
            return found ? Result(bestPoint, SnapMode.Edge, Math.Sqrt(bestSq)) : null;
        }

        private static SnapResult NearestSegment(MeshSoup soup, Vec3 pick, double radius)
        {
            double bestSq = radius * radius;
            var bestPoint = new Vec3();
            bool found = false;
            for (int s = 0; s + 1 < soup.Segments.Count; s += 2)
            {
                ConsiderSegment(
                    soup.Vertices[soup.Segments[s]],
                    soup.Vertices[soup.Segments[s + 1]],
                    pick, ref bestSq, ref bestPoint, ref found);
            }
            return found ? Result(bestPoint, SnapMode.Edge, Math.Sqrt(bestSq)) : null;
        }

        private static void ConsiderSegment(
            Vec3 a, Vec3 b, Vec3 pick, ref double bestSq, ref Vec3 bestPoint, ref bool found)
        {
            Vec3 candidate = Geometry.ClosestPointOnSegment(a, b, pick);
            double d = Geometry.DistanceSquared(candidate, pick);
            if (d >= bestSq) return;
            bestSq = d;
            bestPoint = candidate;
            found = true;
        }

        private static SnapResult NearestFace(MeshSoup soup, Vec3 pick, double radius)
        {
            double bestSq = radius * radius;
            var bestPoint = new Vec3();
            bool found = false;
            for (int t = 0; t + 2 < soup.Triangles.Count; t += 3)
            {
                Vec3 candidate = Geometry.ClosestPointOnTriangle(
                    soup.Vertices[soup.Triangles[t]],
                    soup.Vertices[soup.Triangles[t + 1]],
                    soup.Vertices[soup.Triangles[t + 2]],
                    pick);
                double d = Geometry.DistanceSquared(candidate, pick);
                if (d >= bestSq) continue;
                bestSq = d;
                bestPoint = candidate;
                found = true;
            }
            return found ? Result(bestPoint, SnapMode.Face, Math.Sqrt(bestSq)) : null;
        }

        private static SnapResult Result(Vec3 position, SnapMode mode, double distance)
        {
            return new SnapResult
            {
                Position = position,
                Mode = mode,
                Distance = distance,
                Snapped = true
            };
        }
    }

    /// <summary>The vector arithmetic the snapper needs, and nothing else.</summary>
    public static class Geometry
    {
        public static Vec3 Sub(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vec3 Add(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vec3 Scale(Vec3 a, double k) { return new Vec3(a.X * k, a.Y * k, a.Z * k); }
        public static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

        public static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        public static double Length(Vec3 a) { return Math.Sqrt(Dot(a, a)); }

        public static Vec3 Normalise(Vec3 a)
        {
            double length = Length(a);
            return length < 1e-15 ? new Vec3(0, 0, 0) : Scale(a, 1.0 / length);
        }

        public static double DistanceSquared(Vec3 a, Vec3 b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        public static double Distance(Vec3 a, Vec3 b) { return Math.Sqrt(DistanceSquared(a, b)); }

        public static Vec3 ClosestPointOnSegment(Vec3 a, Vec3 b, Vec3 p)
        {
            Vec3 ab = Sub(b, a);
            double denominator = Dot(ab, ab);
            if (denominator < 1e-18) return a;
            double t = Dot(Sub(p, a), ab) / denominator;
            if (t <= 0) return a;
            if (t >= 1) return b;
            return Add(a, Scale(ab, t));
        }

        /// <summary>
        /// Closest point on a triangle, by Voronoi region (Ericson, Real-Time
        /// Collision Detection, section 5.1.5). Handles the three vertex regions
        /// and the three edge regions explicitly rather than projecting onto the
        /// plane and hoping the projection lands inside, which it does not for a
        /// pick beside a slab edge.
        /// </summary>
        public static Vec3 ClosestPointOnTriangle(Vec3 a, Vec3 b, Vec3 c, Vec3 p)
        {
            Vec3 ab = Sub(b, a);
            Vec3 ac = Sub(c, a);
            Vec3 ap = Sub(p, a);

            double d1 = Dot(ab, ap);
            double d2 = Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return a;

            Vec3 bp = Sub(p, b);
            double d3 = Dot(ab, bp);
            double d4 = Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return b;

            double vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0)
            {
                double denominator = d1 - d3;
                double v = denominator == 0 ? 0 : d1 / denominator;
                return Add(a, Scale(ab, v));
            }

            Vec3 cp = Sub(p, c);
            double d5 = Dot(ab, cp);
            double d6 = Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return c;

            double vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0)
            {
                double denominator = d2 - d6;
                double w = denominator == 0 ? 0 : d2 / denominator;
                return Add(a, Scale(ac, w));
            }

            double va = d3 * d6 - d5 * d4;
            if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
            {
                double denominator = (d4 - d3) + (d5 - d6);
                double w = denominator == 0 ? 0 : (d4 - d3) / denominator;
                return Add(b, Scale(Sub(c, b), w));
            }

            double total = va + vb + vc;
            if (total == 0) return a;
            return Add(a, Add(Scale(ab, vb / total), Scale(ac, vc / total)));
        }
    }
}
