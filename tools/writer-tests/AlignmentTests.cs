using System;
using System.Collections.Generic;
using PIXMYD_Nav.Core.Capture;
using PIXMYD_Nav.Core.Points;

namespace PIXMYD_Nav
{
    /// <summary>
    /// The arithmetic that decides where a scan lands, and where a point lands.
    ///
    /// Both are places where a wrong answer looks right. A transform that is
    /// transposed puts a scan somewhere plausible; a snap that quietly took the
    /// wrong corner moves a control point 200 mm and reports nothing. So these
    /// tests work from transforms and geometries whose answer is known by
    /// construction, and check the number that comes back rather than that
    /// something came back.
    ///
    /// The gravity-solve vectors are shared with
    /// apps/ios/PIXMYDTests/RegistrationTests.swift in the PIXMYD repository.
    /// Two implementations of one closed form can only stay honest against the
    /// same numbers.
    /// </summary>
    internal static class AlignmentTests
    {
        public static int Run()
        {
            int failures = 0;

            TransformRoundTrip(ref failures);
            FbxImportBasisUndoesTheReadersTurn(ref failures);
            AHandPlacedCaptureStandsUp(ref failures);
            NudgeMovesAlongTheDocumentAxes(ref failures);
            TurnHoldsThePivotStill(ref failures);
            GravitySolveRecoversAKnownTransform(ref failures);
            GravitySolveNeedsTwoPointsAndAHorizontalBaseline(ref failures);
            GravitySolveAgreesWithHornOnCleanControl(ref failures);
            GravitySolveHoldsTheVerticalWhenTiltIsWrong(ref failures);
            SnapPrefersCornersThenEdgesThenFaces(ref failures);
            SnapStaysPutWhenNothingIsInReach(ref failures);
            SnapUsesPublishedSnapPointsFirst(ref failures);
            ClosestPointOnTriangleHandlesEveryRegion(ref failures);
            CaptureReaderTakesTwoPointsAndAPlacedMesh(ref failures);
            ReadsAPointSetThePhonePlaced(ref failures);

            return failures;
        }

        /// <summary>
        /// A nudge means the same thing whatever the scan's heading.
        ///
        /// The buttons say +1 in X, so +1 in X is one unit east in the model.
        /// Pre-multiplying instead would send the model one unit along whatever
        /// direction it happens to face, and the same button would do something
        /// different on every scan.
        /// </summary>
        private static void NudgeMovesAlongTheDocumentAxes(ref int failures)
        {
            // A placement already turned 90 degrees about Z, so a rotation-
            // dependent shift would be caught: it would move in Y, not X.
            double[] placed = TransformMath.Compose(
                new double[] { 0, 0, 1 }, Math.PI / 2, new double[] { 10, 20, 30 });

            double[] moved = TransformMath.Nudged(placed, 1, 0, 0);
            Program.Check(Near(moved[12], 11) && Near(moved[13], 20) && Near(moved[14], 30),
                "a +1 X nudge moves +1 in the document's X, got "
                    + moved[12] + "," + moved[13] + "," + moved[14], ref failures);

            // The rotation is untouched: nudging is not re-solving.
            for (int i = 0; i < 12; i++)
                Program.Check(Near(moved[i], placed[i]),
                    "nudging changed the rotation at " + i, ref failures);

            // And it undoes exactly, so a wrong tap costs nothing.
            double[] back = TransformMath.Nudged(
                TransformMath.Nudged(placed, 0.1, -1, 10), -0.1, 1, -10);
            for (int i = 0; i < 16; i++)
                Program.Check(Near(back[i], placed[i]),
                    "a nudge and its opposite did not cancel at " + i, ref failures);
        }

        /// <summary>
        /// Turning is about the model, not about the document origin.
        ///
        /// On a georeferenced model the origin can be kilometres away, and a
        /// one-degree correction about it throws the scan out of the building.
        /// </summary>
        private static void TurnHoldsThePivotStill(ref int failures)
        {
            double[] pivot = new double[] { 1000, 2000, 5 };
            double[] placed = TransformMath.Compose(
                new double[] { 0, 0, 1 }, 0, new double[] { 1000, 2000, 5 });

            double[] turned = TransformMath.Turned(placed, new double[] { 0, 0, 1 }, 10, pivot);
            Program.Check(
                Near(turned[12], pivot[0]) && Near(turned[13], pivot[1]) && Near(turned[14], pivot[2]),
                "a model sitting on the pivot must not move when turned about it, got "
                    + turned[12] + "," + turned[13] + "," + turned[14], ref failures);

            // Degrees, not radians: a quarter turn about Z sends +X to +Y.
            double[] quarter = TransformMath.Turned(
                TransformMath.Compose(new double[] { 0, 0, 1 }, 0, new double[] { 1, 0, 0 }),
                new double[] { 0, 0, 1 }, 90, new double[] { 0, 0, 0 });
            Program.Check(Near(quarter[12], 0) && Near(quarter[13], 1),
                "90 must be degrees: (1,0,0) turns to (0,1,0), got "
                    + quarter[12] + "," + quarter[13], ref failures);

            // Turning about the origin instead would have moved this model by
            // hundreds of units; about its own pivot it moves by none. That gap
            // is the whole reason the parameter exists.
            double[] aboutOrigin = TransformMath.Turned(
                placed, new double[] { 0, 0, 1 }, 10, null);
            double slid = Math.Sqrt(
                Math.Pow(aboutOrigin[12] - pivot[0], 2) + Math.Pow(aboutOrigin[13] - pivot[1], 2));
            Program.Check(slid > 100,
                "turning about the origin should visibly slide a far-out model", ref failures);

            // And it undoes exactly.
            double[] back = TransformMath.Turned(turned, new double[] { 0, 0, 1 }, -10, pivot);
            for (int i = 0; i < 16; i++)
                Program.Check(Near(back[i], placed[i]),
                    "a turn and its opposite did not cancel at " + i, ref failures);
        }

        private static bool Near(double a, double b)
        {
            return Math.Abs(a - b) < 1e-9;
        }

        // MARK: - TransformMath

        private static void TransformRoundTrip(ref int failures)
        {
            // 37 degrees about a tilted axis, with a translation that is nothing
            // like the rotation -- so a transposed matrix cannot pass by luck.
            var axis = new double[] { 0.3, -0.5, 0.81 };
            const double angle = 0.6457718;
            var translation = new double[] { 12.5, -3.25, 0.75 };

            double[] matrix = TransformMath.Compose(axis, angle, translation);
            TransformMath.Decomposed parts = TransformMath.Decompose(matrix);

            Program.Check(parts != null, "a rigid matrix decomposes", ref failures);
            if (parts == null) return;

            Program.Check(Math.Abs(parts.AngleRadians - angle) < 1e-9,
                "decomposed angle matches: " + parts.AngleRadians, ref failures);
            Program.Check(Math.Abs(parts.Scale - 1.0) < 1e-9,
                "a rigid matrix decomposes to unit scale", ref failures);

            double[] unit = Normalise(axis);
            for (int k = 0; k < 3; k++)
                Program.Check(Math.Abs(parts.Axis[k] - unit[k]) < 1e-9,
                    "decomposed axis component " + k, ref failures);
            for (int k = 0; k < 3; k++)
                Program.Check(Math.Abs(parts.Translation[k] - translation[k]) < 1e-12,
                    "decomposed translation component " + k, ref failures);

            // Recomposing must give the same 16 numbers, or the two halves
            // disagree about which way the rotation goes.
            double[] again = TransformMath.Compose(parts.Axis, parts.AngleRadians, parts.Translation);
            for (int i = 0; i < 16; i++)
                Program.Check(Math.Abs(again[i] - matrix[i]) < 1e-9,
                    "recomposed element " + i, ref failures);

            Program.Check(TransformMath.Decompose(new double[] { 1, 2, 3 }) == null,
                "a matrix that is not 16 numbers decomposes to null, not to identity", ref failures);

            // A collapsed rotation must refuse rather than silently place
            // something at the origin.
            var collapsed = new double[16];
            collapsed[15] = 1;
            Program.Check(TransformMath.Decompose(collapsed) == null,
                "a zero rotation matrix decomposes to null", ref failures);
        }

        /// <summary>
        /// Navisworks turns an FBX from its declared Y-up into the document's
        /// Z-up as it reads it. The basis this composes has to undo exactly
        /// that turn and nothing else -- a sign error here lays every incoming
        /// scan on its side.
        /// </summary>
        /// <summary>
        /// A hand-placed capture arrives upright, and the whole chain says so.
        ///
        /// This is the end of the chain, not a piece of it, because every piece
        /// was individually right while the result was wrong: the converter
        /// turns the mesh into the document's frame, the placement composes the
        /// solution with the inverse of that turn, and the two cancelled
        /// exactly when the solution was the identity. A scan with no
        /// correspondences is hand-placed, which is most of them, so that was
        /// the ordinary case rather than an edge.
        ///
        /// What is checked here is what the operator sees: take the capture's
        /// own up vector, put it through the mesh turn and then through the
        /// transform the plugin hands Navisworks, and it has to come out
        /// pointing at the document's ceiling.
        /// </summary>
        private static void AHandPlacedCaptureStandsUp(ref int failures)
        {
            // What the converter does to the mesh: ARKit up becomes document up.
            double[] captureUp = { 0, 1, 0 };
            double[] turned = { captureUp[0], -captureUp[2], captureUp[1] };

            CaptureSolution byHand = CapturePlacement.ByHand(new double[] { 0, 0, 0 }, "Z");
            double[] placement = CapturePlacement.ModelWorldMatrix(byHand.Matrix, null);
            double[] applied = TransformMath.Multiply(placement, TransformMath.FbxCaptureBasis("Z"));

            double[] up = Rotate(applied, turned);
            Program.Check(Math.Abs(up[0]) < 1e-9 && Math.Abs(up[1]) < 1e-9 && Math.Abs(up[2] - 1) < 1e-9,
                "a hand-placed capture points up, got ("
                    + up[0].ToString("0.###") + ", " + up[1].ToString("0.###") + ", "
                    + up[2].ToString("0.###") + ")", ref failures);

            // And the operator still owns the heading: nothing here decides
            // which way it faces, so the capture's forward axis must stay
            // horizontal rather than being pinned to some arbitrary bearing.
            // Through the mesh turn first, like the up vector: ARKit's forward
            // is -Z, which the converter lands on +Y.
            double[] forward = Rotate(applied, new double[] { 0, 1, 0 });
            Program.Check(Math.Abs(forward[2]) < 1e-9,
                "the capture's forward axis stays horizontal", ref failures);

            // A Y-up document already agrees with the capture, so it turns
            // nothing and the mesh is not turned either.
            CaptureSolution flat = CapturePlacement.ByHand(new double[] { 0, 0, 0 }, "Y");
            double[] same = Rotate(flat.Matrix, captureUp);
            Program.Check(Math.Abs(same[1] - 1) < 1e-9,
                "a Y-up document leaves the capture alone", ref failures);
        }

        /// <summary>The rotation part of a column-major 4x4, applied.</summary>
        private static double[] Rotate(double[] m, double[] v)
        {
            return new double[]
            {
                m[0] * v[0] + m[4] * v[1] + m[8] * v[2],
                m[1] * v[0] + m[5] * v[1] + m[9] * v[2],
                m[2] * v[0] + m[6] * v[1] + m[10] * v[2],
            };
        }

        private static void FbxImportBasisUndoesTheReadersTurn(ref int failures)
        {
            double[] basis = TransformMath.FbxCaptureBasis("Z");

            // What the reader does: (x, y, z) -> (x, -z, y). The basis maps that
            // back, so feeding it a turned point returns the original.
            var original = new double[] { 1.5, -2.25, 7.0 };
            var asImported = new double[] { original[0], -original[2], original[1] };
            double[] back = CapturePlacement.Transform(basis, asImported);

            for (int k = 0; k < 3; k++)
                Program.Check(Math.Abs(back[k] - original[k]) < 1e-12,
                    "the FBX import basis returns axis " + k + ", got " + back[k], ref failures);

            // A Y-up document has nothing turned, so the basis must be identity
            // rather than a quarter turn applied to a frame that never moved.
            double[] identity = TransformMath.FbxCaptureBasis("Y");
            double[] unchanged = CapturePlacement.Transform(identity, original);
            for (int k = 0; k < 3; k++)
                Program.Check(Math.Abs(unchanged[k] - original[k]) < 1e-12,
                    "a Y-up document turns nothing, axis " + k, ref failures);

            // Composition order: the basis has to run first, then the solve.
            double[] solve = TransformMath.Compose(
                new double[] { 0, 0, 1 }, 0.5, new double[] { 10, 20, 30 });
            double[] composed = TransformMath.Multiply(solve, basis);

            double[] viaComposed = CapturePlacement.Transform(composed, asImported);
            double[] viaSteps = CapturePlacement.Transform(solve, CapturePlacement.Transform(basis, asImported));
            for (int k = 0; k < 3; k++)
                Program.Check(Math.Abs(viaComposed[k] - viaSteps[k]) < 1e-12,
                    "Multiply(a, b) applies b first, axis " + k, ref failures);

            // And composing with an identity must change nothing at all.
            double[] withIdentity = TransformMath.Multiply(solve, TransformMath.FbxCaptureBasis("Y"));
            for (int i = 0; i < 16; i++)
                Program.Check(Math.Abs(withIdentity[i] - solve[i]) < 1e-12,
                    "composing with identity leaves element " + i + " alone", ref failures);
        }

        // MARK: - GravitySolve

        /// <summary>
        /// Two points, a known heading and a known shift: the solve has to
        /// return exactly what was applied. Anything less than exact here is a
        /// sign error, not a tolerance question.
        /// </summary>
        private static void GravitySolveRecoversAKnownTransform(ref int failures)
        {
            // Capture frame is ARKit's: Y up. Model frame is Navisworks': Z up.
            double[] captureUp = GravitySolve.CaptureUp;
            double[] projectUp = GravitySolve.UpVectorFor("Z");

            // Two column marks 6.4 m apart on one floor.
            var observed = new double[][]
            {
                new double[] { 0.0, 1.2, 0.0 },
                new double[] { 5.1, 1.2, -3.87 }
            };

            const double heading = 0.9773844;              // 56 degrees
            var shift = new double[] { 104.25, -58.5, 12.4 };
            var project = new double[observed.Length][];
            for (int i = 0; i < observed.Length; i++)
                project[i] = ApplyKnown(observed[i], heading, shift);

            var pairs = new List<ControlPair>
            {
                new ControlPair("P001", project[0], observed[0]),
                new ControlPair("P002", project[1], observed[1])
            };

            RigidSolution solved = GravitySolve.Solve(pairs, captureUp, projectUp);

            Program.Check(solved.PairCount == 2, "two pairs is enough", ref failures);
            Program.Check(solved.RmsError < 1e-9,
                "an exact two-point network fits exactly: " + solved.RmsError, ref failures);
            Program.Check(Math.Abs(solved.Scale - 1.0) < 1e-12, "scale stays at 1", ref failures);

            // The matrix has to map the observations onto the project
            // coordinates, not merely have a small residual.
            for (int i = 0; i < pairs.Count; i++)
            {
                double[] mapped = CapturePlacement.Transform(solved.Matrix, observed[i]);
                for (int k = 0; k < 3; k++)
                    Program.Check(Math.Abs(mapped[k] - project[i][k]) < 1e-9,
                        "point " + i + " maps onto its project coordinate, axis " + k, ref failures);
            }
        }

        private static void GravitySolveNeedsTwoPointsAndAHorizontalBaseline(ref int failures)
        {
            double[] up = GravitySolve.UpVectorFor("Z");

            var one = new List<ControlPair>
            {
                new ControlPair("P001", new double[] { 1, 2, 3 }, new double[] { 0, 0, 0 })
            };
            Program.Check(Throws(one, up), "one point is refused", ref failures);

            // Two points stacked vertically: nothing constrains the heading, and
            // an arbitrary answer here is worse than a refusal.
            var stacked = new List<ControlPair>
            {
                new ControlPair("P001", new double[] { 10, 20, 0.0 }, new double[] { 0, 0.0, 0 }),
                new ControlPair("P002", new double[] { 10, 20, 3.0 }, new double[] { 0, 3.0, 0 })
            };
            Program.Check(Throws(stacked, up),
                "a vertical-only baseline is refused rather than guessed", ref failures);

            // The refusal has to be readable by someone on a slab.
            try
            {
                GravitySolve.Solve(stacked, GravitySolve.CaptureUp, up);
            }
            catch (RigidSolveException ex)
            {
                Program.Check(ex.Message.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0,
                    "the refusal says what to do about it: " + ex.Message, ref failures);
            }

            Program.Check(GravitySolve.RedundancyGuidance(2).IndexOf("no redundancy", StringComparison.Ordinal) >= 0,
                "two points are described as having no redundancy", ref failures);
            Program.Check(GravitySolve.RedundancyGuidance(6).IndexOf("identified", StringComparison.Ordinal) >= 0,
                "four or more points are described as identifying a blunder", ref failures);
        }

        /// <summary>
        /// On control that is level and clean, holding the vertical must land in
        /// the same place Horn's unconstrained solve does. If the two disagree
        /// on easy data, one of them has a convention wrong.
        /// </summary>
        private static void GravitySolveAgreesWithHornOnCleanControl(ref int failures)
        {
            double[] up = GravitySolve.UpVectorFor("Z");
            var observed = new double[][]
            {
                new double[] { 0.0, 0.0, 0.0 },
                new double[] { 8.2, 0.0, -1.5 },
                new double[] { 3.1, 0.0, -7.4 },
                new double[] { 9.6, 2.7, -6.1 }
            };

            const double heading = -1.2217305;             // -70 degrees
            var shift = new double[] { -12.0, 340.5, 61.25 };

            var pairs = new List<ControlPair>();
            for (int i = 0; i < observed.Length; i++)
                pairs.Add(new ControlPair("P" + i, ApplyKnown(observed[i], heading, shift), observed[i]));

            RigidSolution constrained = GravitySolve.Solve(pairs, GravitySolve.CaptureUp, up);
            RigidSolution horn = RigidSolve.Solve(pairs);

            Program.Check(constrained.RmsError < 1e-9, "constrained fit is exact", ref failures);
            Program.Check(horn.RmsError < 1e-6, "Horn's fit is exact", ref failures);
            for (int i = 0; i < 16; i++)
                Program.Check(Math.Abs(constrained.Matrix[i] - horn.Matrix[i]) < 1e-6,
                    "the two solvers agree on clean control, element " + i, ref failures);
        }

        /// <summary>
        /// The point of the constraint: when one observation is out in the
        /// vertical, an unconstrained solve tilts the whole scan to absorb it
        /// and the constrained one does not.
        /// </summary>
        private static void GravitySolveHoldsTheVerticalWhenTiltIsWrong(ref int failures)
        {
            double[] up = GravitySolve.UpVectorFor("Z");
            var observed = new double[][]
            {
                new double[] { 0.0, 0.0, 0.0 },
                new double[] { 9.0, 0.0, 0.0 },
                new double[] { 0.0, 0.0, -9.0 }
            };

            var pairs = new List<ControlPair>();
            for (int i = 0; i < observed.Length; i++)
                pairs.Add(new ControlPair("P" + i, ApplyKnown(observed[i], 0, new double[] { 0, 0, 0 }), observed[i]));

            // Push one observation 80 mm up: a bad aim on a ceiling mark.
            pairs[2].Observed = new double[] { 0.0, 0.08, -9.0 };

            RigidSolution constrained = GravitySolve.Solve(pairs, GravitySolve.CaptureUp, up);
            RigidSolution horn = RigidSolve.Solve(pairs);

            // Horn spreads the blunder into a tilt, which shrinks the residual.
            // The constrained solve cannot, so it reports more error -- and that
            // larger number is the honest one.
            Program.Check(constrained.RmsError > horn.RmsError,
                "holding the vertical reports the blunder instead of absorbing it (" +
                constrained.RmsError + " vs " + horn.RmsError + ")", ref failures);

            // And the model's vertical must come out of the solve untouched.
            double[] mappedUp = MapDirection(constrained.Matrix, GravitySolve.CaptureUp);
            Program.Check(Math.Abs(mappedUp[0]) < 1e-9 && Math.Abs(mappedUp[1]) < 1e-9 && mappedUp[2] > 0.999,
                "the capture's up maps onto the model's up exactly", ref failures);
        }

        // MARK: - SnapSolver

        /// <summary>
        /// One 2 m cube of floor, with a real edge along one side. A pick near
        /// the corner takes the corner; a pick near the middle of the edge takes
        /// the edge; a pick in the middle of the slab takes the face.
        /// </summary>
        private static void SnapPrefersCornersThenEdgesThenFaces(ref int failures)
        {
            MeshSoup soup = Slab();

            SnapResult corner = SnapSolver.Snap(soup, new Vec3(1.98, 0.01, 0.012), SnapMode.Corner);
            Program.Check(corner.Snapped && corner.Mode == SnapMode.Corner,
                "a pick near a corner snaps to the corner, got " + corner.Describe(), ref failures);
            Program.Check(Near(corner.Position, new Vec3(2, 0, 0)),
                "and it is that corner: " + Show(corner.Position), ref failures);

            // Mid-edge: no vertex within reach, but the edge is right there.
            SnapResult edge = SnapSolver.Snap(soup, new Vec3(1.0, 0.004, 0.006), SnapMode.Corner);
            Program.Check(edge.Snapped && edge.Mode == SnapMode.Edge,
                "a pick along an edge falls through to the edge, got " + edge.Describe(), ref failures);
            Program.Check(Math.Abs(edge.Position.X - 1.0) < 1e-9 && Math.Abs(edge.Position.Y) < 1e-9,
                "and it lands on the edge: " + Show(edge.Position), ref failures);

            // Open surface, kept clear of the tessellation diagonal -- which is
            // a triangle edge and would rightly answer first.
            SnapResult face = SnapSolver.Snap(soup, new Vec3(1.4, 0.9, 0.02), SnapMode.Corner);
            Program.Check(face.Snapped && face.Mode == SnapMode.Face,
                "a pick on open surface falls through to the face, got " + face.Describe(), ref failures);
            Program.Check(Near(face.Position, new Vec3(1.4, 0.9, 0.0)),
                "and it lands on the surface below it: " + Show(face.Position), ref failures);

            // Asking for Face explicitly must not climb back up to a corner --
            // a user who has chosen the surface wants the surface.
            SnapResult forced = SnapSolver.Snap(soup, new Vec3(1.99, 0.005, 0.01), SnapMode.Face);
            Program.Check(forced.Mode == SnapMode.Face,
                "an explicit face request stays on the face, got " + forced.Describe(), ref failures);
        }

        private static void SnapStaysPutWhenNothingIsInReach(ref int failures)
        {
            MeshSoup soup = Slab();
            var pick = new Vec3(1.0, 1.0, 5.0);

            SnapResult free = SnapSolver.Snap(soup, pick, SnapMode.Corner);
            Program.Check(!free.Snapped && free.Mode == SnapMode.Free,
                "a pick five metres off the model is left alone", ref failures);
            Program.Check(Near(free.Position, pick), "and keeps its coordinate exactly", ref failures);

            SnapResult empty = SnapSolver.Snap(new MeshSoup(), pick, SnapMode.Corner);
            Program.Check(!empty.Snapped, "an empty soup cannot snap anything", ref failures);

            SnapResult nothing = SnapSolver.Snap(null, pick, SnapMode.Corner);
            Program.Check(!nothing.Snapped && Near(nothing.Position, pick),
                "a null soup is a free pick, not a crash", ref failures);
        }

        /// <summary>
        /// Navisworks publishes snap points for real model features. A mesh
        /// vertex 1 mm closer must not beat one: the tessellation of a curved
        /// face is covered in vertices that are not corners of anything.
        /// </summary>
        private static void SnapUsesPublishedSnapPointsFirst(ref int failures)
        {
            var soup = new MeshSoup();
            // A mesh vertex deliberately placed nearer the pick than the
            // published corner is.
            soup.AddTriangle(new Vec3(0.502, 0.503, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0));
            soup.AddSnapPoint(new Vec3(0.5, 0.5, 0));

            // 5 mm from the published corner; 1.4 mm from the mesh vertex.
            var pick = new Vec3(0.503, 0.504, 0.0);
            SnapResult result = SnapSolver.Snap(soup, pick, SnapMode.Corner, 1.0);

            Program.Check(result.Mode == SnapMode.Corner, "a published snap point is a corner", ref failures);
            Program.Check(Near(result.Position, new Vec3(0.5, 0.5, 0)),
                "and it wins over a nearer mesh vertex: " + Show(result.Position), ref failures);

            // Welding: the same corner reported twice is one candidate.
            soup.AddSnapPoint(new Vec3(0.5, 0.5, 0.0001));
            Program.Check(soup.SnapPoints.Count == 1,
                "two reports of one corner weld to a single snap point", ref failures);
        }

        private static void ClosestPointOnTriangleHandlesEveryRegion(ref int failures)
        {
            var a = new Vec3(0, 0, 0);
            var b = new Vec3(1, 0, 0);
            var c = new Vec3(0, 1, 0);

            Program.Check(Near(Geometry.ClosestPointOnTriangle(a, b, c, new Vec3(-1, -1, 0)), a),
                "outside vertex A returns A", ref failures);
            Program.Check(Near(Geometry.ClosestPointOnTriangle(a, b, c, new Vec3(2, -1, 0)), b),
                "outside vertex B returns B", ref failures);
            Program.Check(Near(Geometry.ClosestPointOnTriangle(a, b, c, new Vec3(-1, 2, 0)), c),
                "outside vertex C returns C", ref failures);
            Program.Check(Near(Geometry.ClosestPointOnTriangle(a, b, c, new Vec3(0.5, -1, 0)), new Vec3(0.5, 0, 0)),
                "beside edge AB returns a point on AB", ref failures);
            Program.Check(Near(Geometry.ClosestPointOnTriangle(a, b, c, new Vec3(-1, 0.5, 0)), new Vec3(0, 0.5, 0)),
                "beside edge AC returns a point on AC", ref failures);
            Program.Check(Near(Geometry.ClosestPointOnTriangle(a, b, c, new Vec3(1, 1, 0)), new Vec3(0.5, 0.5, 0)),
                "beyond edge BC returns a point on BC", ref failures);
            Program.Check(Near(Geometry.ClosestPointOnTriangle(a, b, c, new Vec3(0.25, 0.25, 3)), new Vec3(0.25, 0.25, 0)),
                "above the interior projects onto the interior", ref failures);
        }

        // MARK: - The capture reader, end to end

        /// <summary>
        /// A capture from a crew who could only reach two marks, carrying a mesh
        /// the phone already put in model coordinates.
        ///
        /// Both of those are new and both are things the old reader would have
        /// got wrong: it refused below three points, and it had no way to say a
        /// mesh was already placed -- so it would have applied the transform a
        /// second time and put the scan twice as far out as it started.
        /// </summary>
        private static void CaptureReaderTakesTwoPointsAndAPlacedMesh(ref int failures)
        {
            const string json = @"{
  ""contractVersion"": ""1.0"",
  ""captureId"": ""c0ffee11-2233-4455-6677-889900aabbcc"",
  ""capturedUtc"": ""2026-08-23T09:14:02.000Z"",
  ""pointSetId"": ""b7f3c2e1-0000-0000-0000-000000000000"",
  ""device"": { ""model"": ""iPhone17,2"", ""hasLidar"": true },
  ""correspondences"": [
    { ""pointId"": ""P001"", ""observed"": [ 0.0, 1.2, 0.0 ] },
    { ""pointId"": ""P002"", ""observed"": [ 5.1, 1.2, -3.87 ] }
  ],
  ""geometry"": { ""file"": ""capture.fbx"", ""bytes"": 4194304, ""frame"": ""model"" },
  ""provenance"": {
    ""navex:sourceDocument"": ""TowerA.nwd"",
    ""navex:sourceUnits"": ""Meters"",
    ""navex:targetUnits"": ""Meters"",
    ""navex:upAxis"": ""Z"",
    ""navex:originMode"": ""ModelMin"",
    ""navex:appliedOffset"": [ -1204.5, 883.2, 0.0 ],
    ""pixmyd:captureUpAxis"": ""Y""
  }
}";

            CaptureFile capture = CaptureReader.Read(json);

            Program.Check(capture.Correspondences.Count == 2, "both observations are read", ref failures);
            Program.Check(!capture.HasSolution, "a capture with no solution is not an error", ref failures);
            Program.Check(capture.GeometryFile == "capture.fbx", "the mesh is named", ref failures);
            Program.Check(capture.GeometryIsPlaced,
                "frame \"model\" means the mesh is already in model coordinates", ref failures);
            Program.Check(capture.GeometryIsAppendable, "and FBX is a format Navisworks reads", ref failures);
            Program.Check(capture.CaptureUpAxis == "Y", "the capture's own up axis is carried", ref failures);

            // An older capture says nothing about a frame, and the answer has to
            // be the one that was true before the field existed.
            CaptureFile older = CaptureReader.Read(json.Replace(@", ""frame"": ""model""", ""));
            Program.Check(!older.GeometryIsPlaced,
                "a capture with no frame field is in the capture frame", ref failures);

            CaptureFile glb = CaptureReader.Read(json.Replace("capture.fbx", "capture.glb"));
            Program.Check(!glb.GeometryIsAppendable,
                "a GLB from an older build is reported as unappendable rather than failing at the append",
                ref failures);

            // The two-point solve has to be reachable through the reader, not
            // only through GravitySolve directly.
            var positions = new Dictionary<string, double[]>
            {
                { "P001", new double[] { 104.25, -58.5, 13.6 } },
                { "P002", new double[] { 104.25 + 5.1 * Math.Cos(0.9773844) - 3.87 * Math.Sin(0.9773844),
                                         -58.5 + 5.1 * Math.Sin(0.9773844) + 3.87 * Math.Cos(0.9773844),
                                         13.6 } }
            };

            CaptureSolution solved = CaptureReader.SolveLocally(capture, positions);
            Program.Check(solved.SolvedLocally, "the reader marks a local solve as one", ref failures);
            Program.Check(solved.VerticalHeld,
                "two points are solved with the vertical held rather than refused", ref failures);
            Program.Check(solved.RmsError < 1e-9,
                "and the fit is exact on exact data: " + solved.RmsError, ref failures);

            // Three or more points go back to the unconstrained solve, which is
            // what every number in the contract has always meant.
            var three = new List<ControlPair>
            {
                new ControlPair("P001", new double[] { 0, 0, 0 }, new double[] { 0, 0, 0 }),
                new ControlPair("P002", new double[] { 9, 0, 0 }, new double[] { 9, 0, 0 }),
                new ControlPair("P003", new double[] { 0, 9, 0 }, new double[] { 0, 9, 0 })
            };
            CaptureSolution unconstrained = CaptureReader.Solve(three, "Z", "Z", false);
            Program.Check(!unconstrained.VerticalHeld,
                "three points use Horn's solve unless the vertical is forced", ref failures);
            Program.Check(CaptureReader.Solve(three, "Z", "Z", true).VerticalHeld,
                "and the caller can force the constrained one anyway", ref failures);
        }

        /// <summary>
        /// The other half of the round trip: points that started on the phone.
        ///
        /// The fixture is the exact output of PIXMYD's
        /// FieldPointSet.renderPointsJson — same field order, same namespaced
        /// additions, same provenance. It is the same shape this plugin writes,
        /// on purpose, so there is one schema and one version gate rather than
        /// two; what differs is the frame, and the file has to say so or a
        /// consumer would read ARKit coordinates as model coordinates and place
        /// a scan at the origin.
        /// </summary>
        private static void ReadsAPointSetThePhonePlaced(ref int failures)
        {
            const string json = @"{
  ""contractVersion"": ""1.0"",
  ""setId"": ""9ab41f2c-1111-2222-3333-444444444444"",
  ""setName"": ""Level 2 plant room"",
  ""createdUtc"": ""2026-02-02T02:40:00.000Z"",
  ""provenance"": {
    ""navex:sourceDocument"": ""Plant room walk"",
    ""navex:sourceUnits"": ""Meters"",
    ""navex:targetUnits"": ""Meters"",
    ""navex:upAxis"": ""Y"",
    ""navex:originMode"": ""CaptureOrigin"",
    ""navex:appliedOffset"": [ 0.0, 0.0, 0.0 ],
    ""pixmyd:frame"": ""capture"",
    ""pixmyd:captureUpAxis"": ""Y""
  },
  ""points"": [
    {
      ""id"": ""P001"",
      ""label"": ""Door reveal"",
      ""position"": [ 1.25, -0.5, 3.0 ],
      ""grid"": { ""intersection"": """", ""level"": """", ""offset"": [ 0.0, 0.0, 0.0 ], ""distance"": 0.0 },
      ""qrPayload"": ""pixmy://p/9ab41f2c/P001"",
      ""pixmyd:source"": ""mesh""
    },
    {
      ""id"": ""P002"",
      ""label"": ""P002"",
      ""position"": [ 4.0, -0.5, 3.0 ],
      ""grid"": { ""intersection"": """", ""level"": """", ""offset"": [ 0.0, 0.0, 0.0 ], ""distance"": 0.0 },
      ""qrPayload"": ""pixmy://p/9ab41f2c/P002"",
      ""pixmyd:source"": ""plane"",
      ""pixmyd:rangeMetres"": 5.2
    }
  ]
}";

            ReadPointSet set = PointSetReader.Read(json);

            Program.Check(set.SetName == "Level 2 plant room", "the set name is read", ref failures);
            Program.Check(set.ShortId == "9ab41f2c", "the short id matches the printed QR prefix", ref failures);
            Program.Check(set.Points.Count == 2, "both points are read", ref failures);
            Program.Check(set.Points[0].Id == "P001" && set.Points[0].Label == "Door reveal",
                "ids and labels survive", ref failures);
            Program.Check(Math.Abs(set.Points[0].Position.X - 1.25) < 1e-12 &&
                          Math.Abs(set.Points[0].Position.Z - 3.0) < 1e-12,
                "and so do the coordinates", ref failures);

            // The field that stops a scan being placed at the origin.
            Program.Check(set.IsCaptureFrame,
                "a set from the phone is recognised as capture-frame, not model-frame", ref failures);
            Program.Check(set.UpAxis == "Y", "and its up axis is ARKit's", ref failures);

            // The quality fields a consumer written against 1.0 would skip.
            Program.Check(set.Points[0].Source == "mesh" && set.Points[1].Source == "plane",
                "how each point was measured is carried", ref failures);
            Program.Check(Math.Abs(set.Points[1].RangeMetres - 5.2) < 1e-12,
                "and how far away it was taken from", ref failures);

            Program.Check(PointSetReader.Describe(set).IndexOf("place the same ids", StringComparison.Ordinal) >= 0,
                "the summary says what to do with a capture-frame set", ref failures);

            // A set this plugin wrote is not capture-frame, and must not be
            // mistaken for one.
            var mine = new PointSet { SetId = "aaaabbbb-0000-0000-0000-000000000000", SetName = "L01" };
            mine.Provenance.OriginMode = "ModelMin";
            mine.Points.Add(new PointRecord { Id = "P001", Position = new Vec3(1, 2, 3) });
            ReadPointSet roundTripped = PointSetReader.Read(mine.ToJson());
            Program.Check(!roundTripped.IsCaptureFrame,
                "a set this plugin wrote reads back as model-frame", ref failures);
            Program.Check(roundTripped.Points.Count == 1, "and round-trips through its own writer", ref failures);

            // Version gate before anything else, like every other contract file.
            bool refused = false;
            try { PointSetReader.Read(json.Replace("\"1.0\"", "\"2.0\"")); }
            catch (PointSetReadException e)
            {
                refused = e.Message.IndexOf("contract version 2.0", StringComparison.Ordinal) >= 0;
            }
            Program.Check(refused, "a version-2 file is refused by version, not by field", ref failures);

            bool refusedGarbage = false;
            try { PointSetReader.Read("not json"); }
            catch (PointSetReadException) { refusedGarbage = true; }
            Program.Check(refusedGarbage, "garbage is refused with a readable message", ref failures);
        }

        // MARK: - Fixtures and helpers

        /// <summary>Two metres of slab in the XY plane, with the Y=0 side also
        /// published as a real edge, the way a model reader reports one.</summary>
        private static MeshSoup Slab()
        {
            var soup = new MeshSoup();
            soup.AddTriangle(new Vec3(0, 0, 0), new Vec3(2, 0, 0), new Vec3(2, 2, 0));
            soup.AddTriangle(new Vec3(0, 0, 0), new Vec3(2, 2, 0), new Vec3(0, 2, 0));
            soup.AddSegment(new Vec3(0, 0, 0), new Vec3(2, 0, 0));
            return soup;
        }

        /// <summary>Rotate about +Z (the model's up) by <paramref name="heading"/>,
        /// then shift -- after mapping ARKit's Y-up frame onto a Z-up one.</summary>
        private static double[] ApplyKnown(double[] observed, double heading, double[] shift)
        {
            // Y-up to Z-up is the shortest arc from (0,1,0) to (0,0,1): a +90
            // degree turn about X, taking (x, y, z) to (x, -z, y).
            double x = observed[0];
            double y = -observed[2];
            double z = observed[1];

            double cos = Math.Cos(heading), sin = Math.Sin(heading);
            return new double[]
            {
                x * cos - y * sin + shift[0],
                x * sin + y * cos + shift[1],
                z + shift[2]
            };
        }

        private static double[] MapDirection(double[] m, double[] v)
        {
            return new double[]
            {
                m[0] * v[0] + m[4] * v[1] + m[8] * v[2],
                m[1] * v[0] + m[5] * v[1] + m[9] * v[2],
                m[2] * v[0] + m[6] * v[1] + m[10] * v[2]
            };
        }

        private static bool Throws(List<ControlPair> pairs, double[] up)
        {
            try
            {
                GravitySolve.Solve(pairs, GravitySolve.CaptureUp, up);
                return false;
            }
            catch (RigidSolveException)
            {
                return true;
            }
        }

        private static double[] Normalise(double[] v)
        {
            double length = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return new double[] { v[0] / length, v[1] / length, v[2] / length };
        }

        private static bool Near(Vec3 a, Vec3 b)
        {
            return Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9 && Math.Abs(a.Z - b.Z) < 1e-9;
        }

        private static string Show(Vec3 v)
        {
            return "(" + v.X + ", " + v.Y + ", " + v.Z + ")";
        }
    }
}
