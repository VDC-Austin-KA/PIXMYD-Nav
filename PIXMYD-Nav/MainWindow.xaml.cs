using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Navisworks.Api;
using PIXMYD_Nav.Core;
using PIXMYD_Nav.Core.Ar;
using PIXMYD_Nav.Core.Markers;
using PIXMYD_Nav.Core.NavBridge;
using PIXMYD_Nav.Core.Points;
using NavApp = Autodesk.Navisworks.Api.Application;

namespace PIXMYD_Nav
{
    public partial class MainWindow : Window
    {
        private const string SettingsMarkerFolder = "MarkerFolder";
        private const string SettingsArFolder = "ArFolder";
        private const string SettingsSetName = "SetName";

        private Document _document;
        private double _scaleToMeters = 1.0;
        private Units _sourceUnits = Units.Meters;
        private bool _initialized;

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
            _initialized = true;
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (!_initialized) return;
            SaveUiIntoSettings();
            SettingsStore.Save(_settings);
        }

        // ── Settings ──────────────────────────────────────────────────────────

        private void LoadSettingsIntoUi()
        {
            var loaded = SettingsStore.Load();
            foreach (var kvp in loaded) _settings[kvp.Key] = kvp.Value;

            string markerFolder = Str(_settings, SettingsMarkerFolder, "");
            string arFolder = Str(_settings, SettingsArFolder, "");
            if (string.IsNullOrWhiteSpace(markerFolder))
                markerFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "PIXMYD-Nav");
            if (string.IsNullOrWhiteSpace(arFolder)) arFolder = markerFolder;
            _settings[SettingsMarkerFolder] = markerFolder;
            _settings[SettingsArFolder] = arFolder;

            MarkerFolderBox.Text = markerFolder;
            ArFolderBox.Text = arFolder;
            SetNameBox.Text = Str(_settings, SettingsSetName, "");
        }

        private void SaveUiIntoSettings()
        {
            _settings[SettingsMarkerFolder] = MarkerFolderBox.Text.Trim();
            _settings[SettingsArFolder] = ArFolderBox.Text.Trim();
            _settings[SettingsSetName] = SetNameBox.Text.Trim();
        }

        private static string Str(Dictionary<string, string> values, string key, string fallback)
        {
            string value;
            if (values.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value)) return value;
            return fallback;
        }

        // ── Points ────────────────────────────────────────────────────────────

        private void OnCaptureSelection(object sender, RoutedEventArgs e)
        {
            if (_document == null) return;

            try
            {
                Selection selection = _document.CurrentSelection;
                if (!selection.HasExplicitSelection)
                {
                    StatusText.Text = "Nothing selected in Navisworks — select items first.";
                    return;
                }

                int added = 0;
                foreach (ModelItem item in selection.ExplicitSelection)
                {
                    PointRecord rec = SceneReader.PointFromItem(_document, item, _scaleToMeters, NextPointId());
                    if (string.IsNullOrEmpty(rec.Label)) rec.Label = rec.Id;
                    _points.Add(new PointRow(rec));
                    added++;
                }

                StatusText.Text = "Captured " + added + " point(s) from the selection.";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Capture failed:" + Environment.NewLine + ex.Message,
                    "PIXMYD-Nav", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCaptureModel(object sender, RoutedEventArgs e)
        {
            if (_document == null) return;

            try
            {
                BoundingBox3D box = _document.GetBoundingBox(false);
                if (box.IsEmpty) { StatusText.Text = "The model has no geometry bounding box."; return; }

                var rec = new PointRecord
                {
                    Id = NextPointId(),
                    Label = "Model centre",
                    Position = Scale(new Vec3(box.Center.X, box.Center.Y, box.Center.Z))
                };
                _points.Add(new PointRow(rec));
                StatusText.Text = "Added a point at the model centre.";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Capture failed:" + Environment.NewLine + ex.Message,
                    "PIXMYD-Nav", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnClearPoints(object sender, RoutedEventArgs e) { _points.Clear(); }

        private void OnRemoveSelected(object sender, RoutedEventArgs e)
        {
            var doomed = new List<PointRow>();
            foreach (PointRow row in PointList.SelectedItems) doomed.Add(row);
            foreach (PointRow row in doomed) _points.Remove(row);
        }

        private void OnOpenOutputFolder(object sender, RoutedEventArgs e)
        {
            string folder = WhatFolder();
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception) { }
        }

        private void OnClose(object sender, RoutedEventArgs e) { Close(); }

        // ── Field markers / points export ─────────────────────────────────────

        private void OnExportPoints(object sender, RoutedEventArgs e)
        {
            string folder = MarkerFolderBox.Text.Trim();
            if (ExportPointsJson(folder)) OpenFolder(folder);
        }

        private void OnExportMarkers(object sender, RoutedEventArgs e)
        {
            string folder = MarkerFolderBox.Text.Trim();

            PointSet set = TryBuildSet(folder);
            if (set == null || set.Points.Count == 0) return;

            try
            {
                ViewportCapture.Capture(folder, "markers-shot", 240);

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

                set.Write(Path.Combine(folder, "points.json"));
                File.WriteAllText(Path.Combine(folder, "markers.html"), MarkerPage.Render(set),
                    new System.Text.UTF8Encoding(false));

                MarkerStatusText.Text = "Wrote points.json and markers.html — " +
                    set.Points.Count + " point(s). Print markers.html in a browser.";
                OpenFolder(folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Marker export failed:" + Environment.NewLine + ex.Message,
                    "PIXMYD-Nav", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnBrowseMarkerFolder(object sender, RoutedEventArgs e)
        {
            string picked = PickFolder(MarkerFolderBox.Text);
            if (picked != null) MarkerFolderBox.Text = picked;
        }

        private void OnBrowseArFolder(object sender, RoutedEventArgs e)
        {
            string picked = PickFolder(ArFolderBox.Text);
            if (picked != null) ArFolderBox.Text = picked;
        }

        // ── AR model export ───────────────────────────────────────────────────

        private void OnExportAr(object sender, RoutedEventArgs e)
        {
            if (_document == null) { StatusText.Text = "No document."; return; }
            string folder = ArFolderBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder)) { StatusText.Text = "Choose an output folder first."; return; }

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
                    ViewportCapture.Capture(folder, "ar-anchor", 240);
                    if (ViewportCapture.LastFullImage != "")
                        ar.Image = Path.GetFileName(ViewportCapture.LastFullImage);
                    if (ArMonoCheck.IsChecked == true && ViewportCapture.LastMonoThumb != "")
                        ar.ThumbMono = Path.GetFileName(ViewportCapture.LastMonoThumb);
                }

                ar.Write(Path.Combine(folder, "ar-model.json"));

                ArPreviewText.Text =
                    "modelName: " + ar.ModelName + Environment.NewLine +
                    "units: " + ar.SourceUnits + " → " + ar.TargetUnits + " (" + _scaleToMeters.ToString("0.###", CultureInfo.InvariantCulture) + ")" + Environment.NewLine +
                    "upAxis: " + ar.UpAxis + Environment.NewLine +
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

                ArStatusText.Text = "Wrote ar-model.json to " + folder;
                OpenFolder(folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show("AR export failed:" + Environment.NewLine + ex.Message,
                    "PIXMYD-Nav", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Shared helpers ────────────────────────────────────────────────────

        private PointSet TryBuildSet(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) { StatusText.Text = "Choose an output folder first."; return null; }

            var set = new PointSet();
            set.SetName = SetNameBox.Text.Trim();
            if (string.IsNullOrEmpty(set.SetName)) set.SetName = "PIXMYD points";

            foreach (PointRow row in _points) set.Points.Add(row.Record);

            if (set.Points.Count == 0)
            {
                StatusText.Text = "No points yet — capture some from the selection first.";
                MessageBox.Show("No points yet.\n\nSelect items in Navisworks and press “Capture selection”.",
                    "PIXMYD-Nav", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            return set;
        }

        private bool ExportPointsJson(string folder)
        {
            PointSet set = TryBuildSet(folder);
            if (set == null || set.Points.Count == 0) return false;

            try
            {
                if (CapturePhotoCheck.IsChecked == true)
                {
                    ViewportCapture.Capture(folder, "points-shot", 240);
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

                set.Write(Path.Combine(folder, "points.json"));
                StatusText.Text = "Wrote points.json — " + set.Points.Count + " point(s).";
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Points export failed:" + Environment.NewLine + ex.Message,
                    "PIXMYD-Nav", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
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

        private string WhatFolder()
        {
            if (Tabs.SelectedIndex == 2 && !string.IsNullOrWhiteSpace(ArFolderBox.Text))
                return ArFolderBox.Text.Trim();
            return MarkerFolderBox.Text.Trim();
        }

        private void OpenFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception) { }
        }

        private string PickFolder(string current)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the output folder",
                FileName = ".",
                CheckFileExists = false,
                InitialDirectory = !string.IsNullOrWhiteSpace(current) ? current : ""
            };
            bool? result = dialog.ShowDialog(this);
            if (result != true) return null;
            string folder = Path.GetDirectoryName(dialog.FileName);
            return string.IsNullOrWhiteSpace(folder) ? current : folder;
        }

        private Vec3 Scale(Vec3 v)
        {
            return _scaleToMeters == 1.0
                ? v
                : new Vec3(v.X * _scaleToMeters, v.Y * _scaleToMeters, v.Z * _scaleToMeters);
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

        public PointRow(PointRecord record) { _record = record; }

        public PointRecord Record { get { return _record; } }
        public string Id { get { return _record.Id; } }

        public string Label
        {
            get { return _record.Label; }
            set { _record.Label = value ?? ""; OnChanged("Label"); }
        }

        public string PositionText { get { return SceneReader.FormatVec(_record.Position); } }

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

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string name)
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(name));
        }
    }
}