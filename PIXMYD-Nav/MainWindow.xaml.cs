using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Navisworks.Api;
using PIXMYD_Nav.Core;
using PIXMYD_Nav.Core.Ar;
using PIXMYD_Nav.Core.Markers;
using PIXMYD_Nav.Core.NavBridge;
using PIXMYD_Nav.Core.Points;
using PIXMYD_Nav.Core.Workspace;
using NavApp = Autodesk.Navisworks.Api.Application;

namespace PIXMYD_Nav
{
    /// <summary>
    /// The window. Everything Navisworks-facing that is not worth its own file.
    ///
    /// ## What changed, and why
    ///
    /// The first version of this tab captured the current selection as points,
    /// one per item, at each item's bounding-box centre. That is a fast thing
    /// to write and it is not a control point: the centre of a column's extents
    /// is inside the column, where nobody can put a tape. Points are now
    /// *placed* -- the user clicks in the model and the pick is pulled onto the
    /// nearest real corner, then edge, then face
    /// (Core/Points/SnapSolver.cs over Core/NavBridge/PrimitiveHarvester.cs).
    ///
    /// The three output folders became one workspace with an EXPORT side and an
    /// IMPORT side (Core/Workspace/PixmydWorkspace.cs), because the previous
    /// arrangement put arriving captures in %TEMP% -- invisible from the folder
    /// the user was told to look in, and swept up by Windows.
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string SettingsWorkspace = "Workspace";
        private const string SettingsSetName = "SetName";
        private const string SettingsSnapMode = "SnapMode";
        private const string SettingsSnapRadiusMm = "SnapRadiusMm";
        private const string SettingsMarkerShape = "MarkerShape";
        private const string SettingsMarkerSizeMm = "MarkerSizeMm";

        // Pre-workspace keys, read once so an existing install keeps its folder.
        private const string LegacyMarkerFolder = "MarkerFolder";

        private Document _document;
        private double _scaleToMeters = 1.0;
        private Units _sourceUnits = Units.Meters;
        private bool _initialized;

        private PixmydWorkspace _workspace;
        private readonly PointPicker _picker = new PointPicker();
        private readonly ModelPlacer _placer = new ModelPlacer();
        private Model _markerModel;

        private readonly ObservableCollection<PointRow> _points = new ObservableCollection<PointRow>();
        private readonly Dictionary<string, string> _settings = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        public MainWindow()
        {
            InitializeComponent();
            PointList.ItemsSource = _points;
            Loaded += OnWindowLoaded;
            Closing += OnWindowClosing;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            _document = NavApp.ActiveDocument;
            if (_document == null)
            {
                DocumentText.Text = "No document";
                StatusText.Text = "Open a model in Navisworks first.";
                return;
            }

            try
            {
                DocumentText.Text = _document.Title + "   ·   " + _document.Models.Count + " model file(s)";
                _sourceUnits = _document.Units;
            }
            catch (Exception) { }

            try { _scaleToMeters = UnitConversion.ScaleFactor(_sourceUnits, Units.Meters); }
            catch (Exception) { _scaleToMeters = 1.0; }

            LoadSettingsIntoUi();

            _picker.Picked += OnPicked;
            _picker.Stopped += OnPickingStopped;

            _initialized = true;
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (!_initialized) return;
            _picker.Picked -= OnPicked;
            _picker.Stopped -= OnPickingStopped;
            _picker.Dispose();

            SaveUiIntoSettings();
            SettingsStore.Save(_settings);
        }

        // ── Settings and workspace ────────────────────────────────────────────

        private void LoadSettingsIntoUi()
        {
            var loaded = SettingsStore.Load();
            foreach (var kvp in loaded) _settings[kvp.Key] = kvp.Value;

            string root = Str(_settings, SettingsWorkspace, "");
            // An install that predates the workspace kept its folder under the
            // marker key. Inheriting it means an upgrade finds the same files
            // rather than a new empty folder somewhere else.
            if (string.IsNullOrWhiteSpace(root)) root = Str(_settings, LegacyMarkerFolder, "");
            if (string.IsNullOrWhiteSpace(root)) root = PixmydWorkspace.DefaultRoot();

            WorkspaceBox.Text = root;
            SetNameBox.Text = Str(_settings, SettingsSetName, "");
            SnapModeBox.SelectedIndex = Int(_settings, SettingsSnapMode, 0, 0, 3);
            SnapRadiusBox.Text = Str(_settings, SettingsSnapRadiusMm, "50");
            MarkerShapeBox.SelectedIndex = Int(_settings, SettingsMarkerShape, 0, 0, 1);
            MarkerSizeBox.Text = Str(_settings, SettingsMarkerSizeMm, "76.2");

            UseWorkspace(root);
        }

        private void SaveUiIntoSettings()
        {
            _settings[SettingsWorkspace] = WorkspaceBox.Text.Trim();
            _settings[SettingsSetName] = SetNameBox.Text.Trim();
            _settings[SettingsSnapMode] = SnapModeBox.SelectedIndex.ToString(CultureInfo.InvariantCulture);
            _settings[SettingsSnapRadiusMm] = SnapRadiusBox.Text.Trim();
            _settings[SettingsMarkerShape] = MarkerShapeBox.SelectedIndex.ToString(CultureInfo.InvariantCulture);
            _settings[SettingsMarkerSizeMm] = MarkerSizeBox.Text.Trim();
        }

        /// <summary>
        /// Point everything at a root folder, creating the two sides and moving
        /// any loose export from an older layout into EXPORT.
        /// </summary>
        private void UseWorkspace(string root)
        {
            try
            {
                _workspace = new PixmydWorkspace(root).EnsureCreated();
                List<string> migrated = _workspace.MigrateLooseFiles();
                if (migrated.Count > 0)
                    StatusText.Text = "Moved " + migrated.Count + " file(s) from an older layout into EXPORT.";
            }
            catch (Exception ex)
            {
                _workspace = null;
                StatusText.Text = "That folder cannot be used: " + ex.Message;
            }

            RefreshExportList();
            RefreshImportList();
        }

        private void OnBrowseWorkspace(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Pick the PIXMYD folder for this model";
                if (!string.IsNullOrWhiteSpace(WorkspaceBox.Text)) dialog.SelectedPath = WorkspaceBox.Text.Trim();
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                WorkspaceBox.Text = dialog.SelectedPath;
            }
            UseWorkspace(WorkspaceBox.Text.Trim());
        }

        /// <summary>The workspace as it stands, or null with the reason on screen.</summary>
        private PixmydWorkspace Workspace()
        {
            string wanted = WorkspaceBox.Text != null ? WorkspaceBox.Text.Trim() : "";
            if (string.IsNullOrWhiteSpace(wanted))
            {
                StatusText.Text = "Pick a PIXMYD folder first.";
                return null;
            }
            if (_workspace == null || !string.Equals(_workspace.Root, wanted.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                UseWorkspace(wanted);
            return _workspace;
        }

        private static string Str(Dictionary<string, string> values, string key, string fallback)
        {
            string value;
            if (values.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value)) return value;
            return fallback;
        }

        private static int Int(Dictionary<string, string> values, string key, int fallback, int low, int high)
        {
            string raw = Str(values, key, "");
            int value;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return fallback;
            return value < low || value > high ? fallback : value;
        }

        // ── Placing points ────────────────────────────────────────────────────

        private void OnTogglePicking(object sender, RoutedEventArgs e)
        {
            if (_document == null) return;

            if (_picker.IsArmed)
            {
                _picker.Disarm();
                PickToggleButton.Content = "Pick points";
                StatusText.Text = "Picking off.";
                return;
            }

            _picker.Arm(_document);
            PickToggleButton.Content = "Stop picking";
            StatusText.Text =
                "Picking on — click in the model to place a point. Navisworks' own snapping runs first; " +
                "each pick is then pulled onto the nearest " + SnapModeLabel() + ".";
        }

        private void OnPickingStopped(string reason)
        {
            PickToggleButton.Content = "Pick points";
            StatusText.Text = "Picking stopped: " + reason;
        }

        private void OnTakeCurrentPick(object sender, RoutedEventArgs e)
        {
            Point3D picked;
            if (!_picker.TryTakeCurrent(_document, out picked))
            {
                StatusText.Text =
                    "There is no measurement on screen to take. Use a measure tool to click a point first.";
                return;
            }
            OnPicked(picked);
        }

        /// <summary>
        /// A raw pick becomes a point: snapped onto real geometry, numbered, and
        /// added to the list.
        /// </summary>
        private void OnPicked(Point3D picked)
        {
            if (picked == null || _document == null) return;

            try
            {
                double radiusMm = Double(SnapRadiusBox.Text, 50);
                double radiusMetres = radiusMm / 1000.0;
                // The bounding-box search runs in document units; the snap runs
                // in metres, which is the frame every point is recorded in.
                double radiusDocument = _scaleToMeters == 0 ? radiusMetres : radiusMetres / _scaleToMeters;

                var pick = new Vec3(picked.X * _scaleToMeters, picked.Y * _scaleToMeters, picked.Z * _scaleToMeters);

                SnapResult snapped;
                SnapMode wanted = (SnapMode)Math.Max(0, SnapModeBox.SelectedIndex);
                if (wanted == SnapMode.Free)
                {
                    snapped = new SnapResult { Position = pick, Mode = SnapMode.Free, Snapped = false };
                }
                else
                {
                    ModelItemCollection near = PrimitiveHarvester.ItemsNear(
                        _document, picked, radiusDocument, PrimitiveHarvester.DefaultItemBudget);
                    MeshSoup soup = PrimitiveHarvester.Harvest(near, _scaleToMeters, 60000);
                    snapped = SnapSolver.Snap(soup, pick, wanted, radiusMetres);
                }

                var record = new PointRecord { Id = NextPointId(), Position = snapped.Position };
                record.Label = record.Id;
                var row = new PointRow(record, snapped);
                _points.Add(row);
                PointList.SelectedItem = row;
                PointList.ScrollIntoView(row);

                StatusText.Text = "Placed " + record.Id + " at " + SceneReader.FormatVec(record.Position) +
                                  " (" + snapped.Describe() + ").";
            }
            catch (Exception ex)
            {
                StatusText.Text = "That pick could not be placed: " + ex.Message;
            }
        }

        private string SnapModeLabel()
        {
            switch (Math.Max(0, SnapModeBox.SelectedIndex))
            {
                case 1: return "edge";
                case 2: return "face";
                case 3: return "nothing — the raw pick is kept";
                default: return "corner";
            }
        }

        private void OnClearPoints(object sender, RoutedEventArgs e)
        {
            _points.Clear();
            _placer.ForgetMoves();
            StatusText.Text = "Point list cleared.";
        }

        private void OnRemoveSelected(object sender, RoutedEventArgs e)
        {
            var doomed = new List<PointRow>();
            foreach (PointRow row in PointList.SelectedItems) doomed.Add(row);
            foreach (PointRow row in doomed) _points.Remove(row);
        }

        // ── The gizmo ─────────────────────────────────────────────────────────

        /// <summary>
        /// Move the selected points by a fixed step.
        ///
        /// The step is in millimetres and the axes are the model's, so this is
        /// the instrument for "that column mark is 8 mm high", which is the
        /// adjustment a drag gizmo is worst at.
        /// </summary>
        private void OnNudge(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;
            string tag = button.Tag as string;
            if (string.IsNullOrEmpty(tag) || tag.Length < 2) return;

            double step = Double(NudgeStepBox.Text, 5) / 1000.0;
            if (tag[1] == '-') step = -step;

            var moved = new List<PointRow>();
            foreach (PointRow row in PointList.SelectedItems) moved.Add(row);
            if (moved.Count == 0)
            {
                StatusText.Text = "Select a point in the list to nudge it.";
                return;
            }

            foreach (PointRow row in moved)
            {
                Vec3 p = row.Record.Position;
                switch (tag[0])
                {
                    case 'X': row.Record.Position = new Vec3(p.X + step, p.Y, p.Z); break;
                    case 'Y': row.Record.Position = new Vec3(p.X, p.Y + step, p.Z); break;
                    default: row.Record.Position = new Vec3(p.X, p.Y, p.Z + step); break;
                }
                row.MovedByHand();
            }

            StatusText.Text = "Nudged " + moved.Count + " point(s) " +
                (step < 0 ? "-" : "+") + Math.Abs(step * 1000).ToString("0.#", CultureInfo.InvariantCulture) +
                " mm in " + tag[0] + ".";
        }

        /// <summary>Pull a point that has drifted back onto the nearest corner.</summary>
        private void OnResnap(object sender, RoutedEventArgs e)
        {
            if (_document == null) return;

            var chosen = new List<PointRow>();
            foreach (PointRow row in PointList.SelectedItems) chosen.Add(row);
            if (chosen.Count == 0) { StatusText.Text = "Select a point in the list to re-snap it."; return; }

            double radiusMetres = Double(SnapRadiusBox.Text, 50) / 1000.0;
            double radiusDocument = _scaleToMeters == 0 ? radiusMetres : radiusMetres / _scaleToMeters;
            int moved = 0;

            foreach (PointRow row in chosen)
            {
                Vec3 p = row.Record.Position;
                var inDocument = new Point3D(
                    p.X / _scaleToMeters, p.Y / _scaleToMeters, p.Z / _scaleToMeters);

                ModelItemCollection near = PrimitiveHarvester.ItemsNear(
                    _document, inDocument, radiusDocument, PrimitiveHarvester.DefaultItemBudget);
                MeshSoup soup = PrimitiveHarvester.Harvest(near, _scaleToMeters, 60000);
                SnapResult snapped = SnapSolver.Snap(soup, p, SnapMode.Corner, radiusMetres);
                if (!snapped.Snapped) continue;

                row.Record.Position = snapped.Position;
                row.Resnapped(snapped);
                moved++;
            }

            StatusText.Text = moved == 0
                ? "Nothing to snap to within " + Double(SnapRadiusBox.Text, 50).ToString("0.#", CultureInfo.InvariantCulture) + " mm."
                : "Re-snapped " + moved + " point(s) onto the nearest corner.";
        }

        private void OnSelectMarker(object sender, RoutedEventArgs e)
        {
            var row = PointList.SelectedItem as PointRow;
            if (row == null) { StatusText.Text = "Select a point in the list first."; return; }
            if (_markerModel == null)
            {
                StatusText.Text = "The markers are not in the model yet — press “Write and show in model”.";
                return;
            }

            StatusText.Text = ModelPlacer.TrySelectMarker(_document, _markerModel, row.Id)
                ? "Selected " + row.Id + " in Navisworks. Use Item Tools › Move to drag it, then read the move back."
                : "Could not find a marker for " + row.Id + " in the model.";
        }

        // ── Markers as geometry ───────────────────────────────────────────────

        private void OnShowMarkers(object sender, RoutedEventArgs e)
        {
            PixmydWorkspace workspace = Workspace();
            if (workspace == null || _document == null) return;
            if (_points.Count == 0) { StatusText.Text = "Place some points first."; return; }

            try
            {
                string path = WriteMarkerFile(workspace);

                ModelPlacer.AppendResult appended = ModelPlacer.Append(_document, path);
                if (!appended.Ok)
                {
                    MarkerModelStatusText.Text = appended.Message;
                    return;
                }

                _markerModel = appended.Model ?? ModelPlacer.FindByFile(_document, path);
                // A freshly appended set has no moves on it yet, so anything
                // recorded against the previous one must not be re-applied.
                _placer.ForgetMoves();

                MarkerModelStatusText.Text =
                    _points.Count + " marker(s) appended from " + Path.GetFileName(path) + ". " +
                    "Navisworks cannot remove an appended file from a plugin, so a previous marker set stays " +
                    "in the tree — its layers are named for the points it held.";
                StatusText.Text = "Markers written to EXPORT and appended to the model.";
                RefreshExportList();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Markers could not be written:" + Environment.NewLine + ex.Message,
                    "PIXMYD-Nav", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Write points-markers.dxf in the document's own units.
        ///
        /// An appended file lands at its own coordinates, so a marker written in
        /// metres into a model drawn in millimetres arrives a thousand times too
        /// close to the origin. The point list is in metres; this is where it
        /// goes back.
        /// </summary>
        private string WriteMarkerFile(PixmydWorkspace workspace)
        {
            double toDocument = _scaleToMeters == 0 ? 1.0 : 1.0 / _scaleToMeters;

            var ids = new List<string>();
            var positions = new List<Vec3>();
            foreach (PointRow row in _points)
            {
                ids.Add(row.Id);
                Vec3 p = row.Record.Position;
                positions.Add(new Vec3(p.X * toDocument, p.Y * toDocument, p.Z * toDocument));
            }

            var shape = MarkerShapeBox.SelectedIndex == 1 ? MarkerShape.Cross : MarkerShape.Sphere;
            double diameter = Double(MarkerSizeBox.Text, MarkerGlyphs.DefaultDiameterMetres * 1000) / 1000.0 * toDocument;

            string path = workspace.ExportFile(MarkerDxf.FileName);
            MarkerDxf.Write(path, ids, positions, shape, diameter);
            return path;
        }

        /// <summary>
        /// Fold whatever the user dragged in Navisworks back into the numbers.
        ///
        /// Idempotent: ModelPlacer remembers what it has already consumed, so
        /// pressing this twice does not move a point twice.
        /// </summary>
        private void OnReadBackMoves(object sender, RoutedEventArgs e)
        {
            if (_markerModel == null)
            {
                MarkerModelStatusText.Text =
                    "There are no markers in the model yet. Press “Write and show in model” first.";
                return;
            }

            var ids = new List<string>();
            foreach (PointRow row in _points) ids.Add(row.Id);

            Dictionary<string, Vec3> moves = _placer.ReadMoves(_markerModel, ids, _scaleToMeters);
            if (moves.Count == 0)
            {
                MarkerModelStatusText.Text =
                    "Nothing has moved since the last read. Drag a marker with Item Tools › Move, then try again.";
                return;
            }

            double largest = 0;
            foreach (PointRow row in _points)
            {
                Vec3 delta;
                if (!moves.TryGetValue(row.Id, out delta)) continue;
                Vec3 p = row.Record.Position;
                row.Record.Position = new Vec3(p.X + delta.X, p.Y + delta.Y, p.Z + delta.Z);
                row.MovedByHand();

                double size = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z);
                if (size > largest) largest = size;
            }

            MarkerModelStatusText.Text =
                "Folded " + moves.Count + " move(s) in, the largest " +
                (largest * 1000).ToString("0.#", CultureInfo.InvariantCulture) + " mm.";
            StatusText.Text = "Point coordinates updated from the model. Re-export points.json to publish them.";
        }

        // ── Field markers / points export ─────────────────────────────────────

        private void OnExportPoints(object sender, RoutedEventArgs e)
        {
            PixmydWorkspace workspace = Workspace();
            if (workspace == null) return;
            if (ExportPointsJson(workspace)) OpenFolder(workspace.Export);
        }

        private void OnExportMarkers(object sender, RoutedEventArgs e)
        {
            PixmydWorkspace workspace = Workspace();
            if (workspace == null) return;

            PointSet set = TryBuildSet();
            if (set == null || set.Points.Count == 0) return;

            try
            {
                ViewportCapture.Capture(workspace.Export, "markers-shot", 240);
                AttachViewpoints(set, workspace.Export);

                set.Write(workspace.ExportFile("points.json"));
                File.WriteAllText(workspace.ExportFile("markers.html"), MarkerPage.Render(set),
                    new System.Text.UTF8Encoding(false));

                MarkerStatusText.Text = "Wrote points.json and markers.html — " +
                    set.Points.Count + " point(s). Print markers.html in a browser.";
                RefreshExportList();
                OpenFolder(workspace.Export);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Marker export failed:" + Environment.NewLine + ex.Message,
                    "PIXMYD-Nav", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Give every point the current view as its reference shot.</summary>
        private void AttachViewpoints(PointSet set, string folder)
        {
            foreach (PointRecord point in set.Points)
            {
                var vp = new ViewpointInfo();
                vp.Camera = _document != null ? SceneReader.CaptureCamera(_document) : new CameraInfo();
                if (ViewportCapture.LastFullImage != "")
                {
                    string imageName = point.Id + "_photo.png";
                    File.Copy(ViewportCapture.LastFullImage, Path.Combine(folder, imageName), true);
                    vp.Image = imageName;
                }
                if (MonoPhotoCheck.IsChecked == true && ViewportCapture.LastMonoThumb != "")
                {
                    string monoName = point.Id + "_photo_mono.png";
                    File.Copy(ViewportCapture.LastMonoThumb, Path.Combine(folder, monoName), true);
                    vp.ThumbMono = monoName;
                }
                point.Viewpoint = vp;
            }
        }

        // ── AR model export ───────────────────────────────────────────────────

        private void OnExportAr(object sender, RoutedEventArgs e)
        {
            PixmydWorkspace workspace = Workspace();
            if (workspace == null || _document == null) { StatusText.Text = "No document."; return; }

            try
            {
                SceneReader.SceneSnapshot scene = SceneReader.Capture(_document);

                var ar = new ArModelSet();
                ar.ModelName = string.IsNullOrEmpty(scene.ModelName) ? "Navisworks model" : scene.ModelName;
                ar.SourceDocument = scene.SourceDocument;
                ar.SourceUnits = scene.SourceUnits.ToString();
                ar.UpAxis = scene.UpAxis;
                ar.BBoxMin = scene.ModelMin;
                ar.BBoxMax = scene.ModelMax;
                ar.Camera = scene.Camera;

                // Shift everything so the box's minimum corner sits at the origin
                // and the shift is recorded, per the ar-model contract.
                ar.AppliedOffset = ar.BBoxMin;
                ar.BBoxMin = new Vec3(0, 0, 0);
                ar.BBoxMax = new Vec3(
                    ar.BBoxMax.X - ar.AppliedOffset.X,
                    ar.BBoxMax.Y - ar.AppliedOffset.Y,
                    ar.BBoxMax.Z - ar.AppliedOffset.Z);
                ar.Camera.Position = Sub(ar.Camera.Position, ar.AppliedOffset);
                ar.Camera.LookAt = Sub(ar.Camera.LookAt, ar.AppliedOffset);

                if (ArCaptureCheck.IsChecked == true)
                {
                    ViewportCapture.Capture(workspace.Export, "ar-anchor", 240);
                    if (ViewportCapture.LastFullImage != "")
                        ar.Image = Path.GetFileName(ViewportCapture.LastFullImage);
                    if (ArMonoCheck.IsChecked == true && ViewportCapture.LastMonoThumb != "")
                        ar.ThumbMono = Path.GetFileName(ViewportCapture.LastMonoThumb);
                }

                string geometryNote = "no geometry — the phone can show where the model is, not draw it";
                if (ArGeometryCheck.IsChecked == true)
                    geometryNote = WriteArGeometry(workspace, ar);

                ar.Write(workspace.ExportFile("ar-model.json"));

                ArPreviewText.Text =
                    "modelName: " + ar.ModelName + Environment.NewLine +
                    "units: " + ar.SourceUnits + " → " + ar.TargetUnits + " (" + _scaleToMeters.ToString("0.###", CultureInfo.InvariantCulture) + ")" + Environment.NewLine +
                    "upAxis: " + ar.UpAxis + Environment.NewLine +
                    "geometry: " + geometryNote + Environment.NewLine +
                    Environment.NewLine +
                    "boundingBox.min: " + SceneReader.FormatVec(ar.BBoxMin) + Environment.NewLine +
                    "boundingBox.max: " + SceneReader.FormatVec(ar.BBoxMax) + Environment.NewLine +
                    "camera.position:   " + SceneReader.FormatVec(ar.Camera.Position) + Environment.NewLine +
                    "camera.lookAt:     " + SceneReader.FormatVec(ar.Camera.LookAt) + Environment.NewLine +
                    "camera.upVector:   " + SceneReader.FormatVec(ar.Camera.UpVector) + Environment.NewLine +
                    "camera.fovDegrees: " + ar.Camera.FovDegrees.ToString("0.0", CultureInfo.InvariantCulture) + Environment.NewLine +
                    Environment.NewLine +
                    "appliedOffset (add back for source world coords): " +
                    SceneReader.FormatVec(ar.AppliedOffset);

                ArStatusText.Text = "Wrote the AR model to EXPORT.";
                RefreshExportList();
                OpenFolder(workspace.Export);
            }
            catch (Exception ex)
            {
                MessageBox.Show("AR export failed:" + Environment.NewLine + ex.Message,
                    "PIXMYD-Nav", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Tessellate the model (or the selection) and write it beside
        /// ar-model.json, so the bundle carries something to draw.
        ///
        /// Runs on the UI thread with the cursor changed rather than on a worker:
        /// the COM primitive bridge is not documented as thread-safe, and a
        /// tessellation that races the renderer is a crash inside Navisworks
        /// rather than an exception this plugin could report.
        /// </summary>
        private string WriteArGeometry(PixmydWorkspace workspace, ArModelSet ar)
        {
            ArProgressBar.Visibility = Visibility.Visible;
            ArProgressBar.IsIndeterminate = true;
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                ModelItemCollection items = SelectedOrWholeModel();
                if (items.Count == 0)
                    return "nothing to tessellate — the selection is empty";

                int budget = (int)Double(ArTriangleBudgetBox.Text, PrimitiveHarvester.DefaultTriangleBudget);
                MeshSoup soup = PrimitiveHarvester.Harvest(items, _scaleToMeters, budget);
                if (soup.TriangleCount == 0)
                    return "the selection tessellated to nothing — point clouds and 2D sheets have no triangles";

                var options = new GlbWriter.Options
                {
                    Name = ar.ModelName,
                    ZUpToYUp = string.Equals(ar.UpAxis, "Z", StringComparison.OrdinalIgnoreCase),
                    Offset = ar.AppliedOffset,
                    IncludeNormals = true
                };
                long bytes = GlbWriter.Write(workspace.ExportFile(GlbWriter.FileName), soup, options);

                ar.GeometryFile = GlbWriter.FileName;
                ar.GeometryBytes = bytes;
                ar.GeometryTriangles = soup.TriangleCount;

                return GlbWriter.FileName + " — " + soup.TriangleCount.ToString("N0", CultureInfo.InvariantCulture) +
                       " triangles, " + Core.Transfer.TransferProgress.Bytes(bytes) +
                       (soup.TriangleCount >= budget ? " (stopped at the triangle budget)" : "");
            }
            finally
            {
                Mouse.OverrideCursor = null;
                ArProgressBar.IsIndeterminate = false;
                ArProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private ModelItemCollection SelectedOrWholeModel()
        {
            var items = new ModelItemCollection();
            try
            {
                if (ArGeometrySourceBox.SelectedIndex == 0)
                {
                    Selection selection = _document.CurrentSelection;
                    if (selection.HasExplicitSelection)
                    {
                        foreach (ModelItem item in selection.ExplicitSelection) items.Add(item);
                        return items;
                    }
                    // An empty selection means the whole model rather than
                    // nothing: exporting an empty AR bundle helps no one.
                }
                foreach (ModelItem root in _document.Models.RootItems) items.Add(root);
            }
            catch (Exception) { }
            return items;
        }

        // ── Shared helpers ────────────────────────────────────────────────────

        private PointSet TryBuildSet()
        {
            var set = new PointSet();
            set.SetName = SetNameBox.Text.Trim();
            if (string.IsNullOrEmpty(set.SetName)) set.SetName = "PIXMYD points";

            foreach (PointRow row in _points) set.Points.Add(row.Record);

            if (set.Points.Count == 0)
            {
                StatusText.Text = "No points yet — turn on Pick points and click in the model.";
                MessageBox.Show(
                    "No points yet.\n\nPress “Pick points”, then click a corner in the model. " +
                    "Every click places one point.",
                    "PIXMYD-Nav", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            try
            {
                SceneReader.SceneSnapshot scene = SceneReader.Capture(_document);
                set.Provenance.SourceDocument = scene.SourceDocument;
                set.Provenance.SourceUnits = scene.SourceUnits.ToString();
                set.Provenance.UpAxis = scene.UpAxis;
            }
            catch (Exception) { }

            return set;
        }

        private bool ExportPointsJson(PixmydWorkspace workspace)
        {
            PointSet set = TryBuildSet();
            if (set == null || set.Points.Count == 0) return false;

            try
            {
                if (CapturePhotoCheck.IsChecked == true)
                {
                    ViewportCapture.Capture(workspace.Export, "points-shot", 240);
                    AttachViewpoints(set, workspace.Export);
                }

                set.Write(workspace.ExportFile("points.json"));
                StatusText.Text = "Wrote points.json to EXPORT — " + set.Points.Count + " point(s).";
                RefreshExportList();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Points export failed:" + Environment.NewLine + ex.Message,
                    "PIXMYD-Nav", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void RefreshExportList()
        {
            var lines = new List<string>();
            try
            {
                if (_workspace != null && Directory.Exists(_workspace.Export))
                    foreach (string path in Directory.GetFiles(_workspace.Export))
                        lines.Add(Path.GetFileName(path).PadRight(28) +
                                  Core.Transfer.TransferProgress.Bytes(new FileInfo(path).Length));
            }
            catch (Exception) { }
            if (lines.Count == 0) lines.Add("(nothing exported yet)");
            MarkerPlanList.ItemsSource = lines;
        }

        private string NextPointId()
        {
            int highest = 0;
            foreach (PointRow row in _points)
            {
                string id = row.Record.Id ?? "";
                if (id.StartsWith("P", StringComparison.OrdinalIgnoreCase))
                {
                    int value;
                    if (int.TryParse(id.Substring(1), out value) && value > highest) highest = value;
                }
            }
            return "P" + (highest + 1).ToString("000", CultureInfo.InvariantCulture);
        }

        private void OnOpenOutputFolder(object sender, RoutedEventArgs e)
        {
            PixmydWorkspace workspace = Workspace();
            if (workspace == null) return;
            OpenFolder(Tabs.SelectedIndex == 3 ? workspace.Import : workspace.Export);
        }

        private void OnClose(object sender, RoutedEventArgs e) { Close(); }

        private void OpenFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception) { }
        }

        private static double Double(string text, double fallback)
        {
            double value;
            if (double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && !double.IsNaN(value) && !double.IsInfinity(value))
                return value;
            return fallback;
        }

        private static Vec3 Sub(Vec3 a, Vec3 b)
        {
            return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }
    }

    /// <summary>Bindable wrapper around a <see cref="PointRecord"/> for the list.</summary>
    public sealed class PointRow : INotifyPropertyChanged
    {
        private readonly PointRecord _record;
        private string _snapText;

        public PointRow(PointRecord record) : this(record, null) { }

        public PointRow(PointRecord record, SnapResult snap)
        {
            _record = record;
            _snapText = snap == null ? "" : snap.Describe();
        }

        public PointRecord Record { get { return _record; } }
        public string Id { get { return _record.Id; } }

        public string Label
        {
            get { return _record.Label; }
            set { _record.Label = value ?? ""; OnChanged("Label"); }
        }

        public string PositionText { get { return SceneReader.FormatVec(_record.Position); } }

        /// <summary>What the pick landed on, so a point taken off a face rather
        /// than a corner is visible in the list rather than only in the
        /// coordinate.</summary>
        public string SnapText { get { return _snapText; } }

        public string Intersection
        {
            get { return _record.Grid != null ? _record.Grid.Intersection : ""; }
            set
            {
                if (_record.Grid != null) _record.Grid.Intersection = value ?? "";
                OnChanged("Intersection");
            }
        }

        public string Level
        {
            get { return _record.Grid != null ? _record.Grid.Level : ""; }
            set
            {
                if (_record.Grid != null) _record.Grid.Level = value ?? "";
                OnChanged("Level");
            }
        }

        /// <summary>The coordinate changed by hand -- a nudge, or a drag read
        /// back out of the model. The snap it was placed with no longer
        /// describes where it is, and saying so beats leaving a stale badge.</summary>
        public void MovedByHand()
        {
            _snapText = "moved";
            OnChanged("PositionText");
            OnChanged("SnapText");
        }

        public void Resnapped(SnapResult snap)
        {
            _snapText = snap == null ? "" : snap.Describe();
            OnChanged("PositionText");
            OnChanged("SnapText");
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string name)
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(name));
        }
    }
}
