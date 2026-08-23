using System;
using System.Windows.Threading;
using Autodesk.Navisworks.Api;

namespace PIXMYD_Nav.Core.NavBridge
{
    /// <summary>
    /// One click in the viewport, one point.
    ///
    /// ## Why the measure tool
    ///
    /// A plugin window cannot receive a click in the Navisworks 3D view --
    /// there is no managed hook for it, and there never has been. What the
    /// managed API does expose is the result of Navisworks' own picking:
    /// <c>Document.CurrentMeasurement.FirstPoint</c> and
    /// <c>EndPoint</c>, set every time the user clicks with a measure tool.
    ///
    /// That is better than a hook would have been. Navisworks' measure pick
    /// already runs the application's own snapping -- vertex, edge, line vertex,
    /// whatever the user has set in Options -- against the real tessellation,
    /// at the cursor, with the highlight drawn under it. Borrowing it means the
    /// pick behaves exactly like every other pick in the application instead of
    /// like a plugin's imitation of one, and the plugin's own corner snapping
    /// (Core/Points/SnapSolver.cs) refines the result afterwards rather than
    /// competing with it.
    ///
    /// Point-to-point measure sets FirstPoint on the first click and EndPoint on
    /// the second, then starts over. Watching both means every click produces a
    /// point, which is the whole interaction: click, click, click, and P001 to
    /// P003 are placed.
    ///
    /// ## Why a timer
    ///
    /// CurrentMeasurement raises no event -- verified against the 2027 API
    /// surface, where the type has four properties and nothing else. So the
    /// alternative to polling is a button the user presses after every click,
    /// which doubles the work of the one action this feature exists for.
    ///
    /// It is a foreground timer, on the UI thread, that runs only while the
    /// user has picking armed and is stopped by Disarm, by the window closing
    /// and by an exception. It is not a background poller: there is a visible
    /// switch, and nothing ticks when it is off.
    ///
    /// Navisworks-only.
    /// </summary>
    public sealed class PointPicker : IDisposable
    {
        /// <summary>Fast enough to feel immediate, slow enough that reading two
        /// properties costs nothing.</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

        /// <summary>Two picks closer than this are the same click seen twice.</summary>
        private const double SamePointTolerance = 1e-7;

        private readonly DispatcherTimer _timer;
        private Document _document;
        private bool _hasLastFirst, _hasLastEnd;
        private Point3D _lastFirst, _lastEnd;

        /// <summary>Raised on the UI thread with a pick in document units.</summary>
        public event Action<Point3D> Picked;
        /// <summary>Raised when picking stops for a reason the user did not choose.</summary>
        public event Action<string> Stopped;

        public bool IsArmed { get; private set; }

        public PointPicker()
        {
            _timer = new DispatcherTimer();
            _timer.Interval = PollInterval;
            _timer.Tick += OnTick;
        }

        /// <summary>
        /// Start watching, and put Navisworks into point-to-point measure so the
        /// user's very next click is a pick.
        ///
        /// Switching the tool for them is the difference between "press this,
        /// then find the measure tool on the ribbon, then click" and "press
        /// this, then click".
        /// </summary>
        public void Arm(Document document)
        {
            if (IsArmed || document == null) return;
            _document = document;

            try
            {
                _document.Tool.Value = Tool.MeasurePointToPoint;
            }
            catch (Exception)
            {
                // A document that will not take a tool change still picks fine
                // if the user selects measure themselves, so this is not fatal.
            }

            _hasLastFirst = false;
            _hasLastEnd = false;
            Seed();

            IsArmed = true;
            _timer.Start();
        }

        /// <summary>
        /// Take whatever is already on screen as the baseline, so arming while a
        /// measurement is showing does not immediately fire for a click the user
        /// made before they armed.
        /// </summary>
        private void Seed()
        {
            try
            {
                var measurement = _document.CurrentMeasurement;
                if (measurement.HasFirstPoint) { _lastFirst = measurement.FirstPoint; _hasLastFirst = true; }
                if (measurement.HasEndPoint) { _lastEnd = measurement.EndPoint; _hasLastEnd = true; }
            }
            catch (Exception) { }
        }

        public void Disarm()
        {
            if (!IsArmed) return;
            IsArmed = false;
            _timer.Stop();

            try
            {
                // Back to select: leaving a measure tool armed after the user
                // turned picking off means their next click measures something.
                if (_document != null) _document.Tool.Value = Tool.Select;
            }
            catch (Exception) { }
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!IsArmed || _document == null) return;

            bool hasFirst = false, hasEnd = false;
            Point3D first = null, end = null;
            try
            {
                var measurement = _document.CurrentMeasurement;
                hasFirst = measurement.HasFirstPoint;
                if (hasFirst) first = measurement.FirstPoint;
                hasEnd = measurement.HasEndPoint;
                if (hasEnd) end = measurement.EndPoint;
            }
            catch (Exception ex)
            {
                // A closed document, or the measurement part going away under
                // us. Stop rather than tick forever against a dead handle.
                Disarm();
                Report(ex.Message);
                return;
            }

            if (!hasFirst)
            {
                // The measurement was cleared; the next click starts fresh.
                _hasLastFirst = false;
                _hasLastEnd = false;
                return;
            }

            if (!_hasLastFirst || !Same(first, _lastFirst))
            {
                _lastFirst = first;
                _hasLastFirst = true;
                // A new first point means a new measurement, so the previous
                // end point is history and must not be re-emitted.
                _hasLastEnd = false;
                Emit(first);
            }

            if (hasEnd && (!_hasLastEnd || !Same(end, _lastEnd)))
            {
                _lastEnd = end;
                _hasLastEnd = true;
                Emit(end);
            }
        }

        /// <summary>
        /// The manual path: take the measurement showing right now, once.
        ///
        /// Kept for the user who would rather not have anything watching, and
        /// for the case where a click landed before picking was armed.
        /// </summary>
        public bool TryTakeCurrent(Document document, out Point3D picked)
        {
            picked = null;
            if (document == null) return false;
            try
            {
                var measurement = document.CurrentMeasurement;
                if (measurement.HasEndPoint) { picked = measurement.EndPoint; return true; }
                if (measurement.HasFirstPoint) { picked = measurement.FirstPoint; return true; }
            }
            catch (Exception) { }
            return false;
        }

        private void Emit(Point3D point)
        {
            if (point == null) return;
            Action<Point3D> handler = Picked;
            if (handler != null) handler(point);
        }

        private void Report(string message)
        {
            Action<string> handler = Stopped;
            if (handler != null) handler(message);
        }

        /// <summary>
        /// Two clicks on the same corner produce the same coordinate and only
        /// one point. That is deliberate: a duplicate control point adds no
        /// information to a solve and one more row to check.
        /// </summary>
        private static bool Same(Point3D a, Point3D b)
        {
            if (a == null || b == null) return false;
            return Math.Abs(a.X - b.X) < SamePointTolerance
                && Math.Abs(a.Y - b.Y) < SamePointTolerance
                && Math.Abs(a.Z - b.Z) < SamePointTolerance;
        }

        public void Dispose()
        {
            Disarm();
            _timer.Tick -= OnTick;
        }
    }
}
