using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using PIXMYD_Nav.Core.Capture;
using PIXMYD_Nav.Core.Markers;
using PIXMYD_Nav.Core.Points;
using PIXMYD_Nav.Core.Transfer;

namespace PIXMYD_Nav
{
    /// <summary>
    /// The Transfer tab: show a pairing code, serve the export over the local
    /// network, and take a scan back.
    ///
    /// A separate partial rather than more of MainWindow.xaml.cs, per RULES.md
    /// section 1 -- the default is additive, and the existing code-behind is not
    /// changed at all by this feature.
    ///
    /// Everything here is UI and lifetime. The protocol lives in Core/Transfer,
    /// the capture maths in Core/Capture, and both are covered by
    /// tools/writer-tests. This file is the part that cannot be tested offline,
    /// so it is kept to wiring.
    /// </summary>
    public partial class MainWindow
    {
        private const string SettingsTransferFolder = "TransferFolder";
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);
        private const long MaxUploadBytes = 512L * 1024 * 1024;

        private TransferServer _transfer;
        private string _pendingCaptureFolder;

        // MARK: - Session

        private void OnStartTransfer(object sender, RoutedEventArgs e)
        {
            if (_transfer != null && _transfer.IsRunning)
            {
                TransferStatusText.Text = "A session is already open. Close it first.";
                return;
            }

            string folder = TransferFolderBox.Text != null ? TransferFolderBox.Text.Trim() : "";
            bool acceptsUpload = TransferUploadCheck.IsChecked == true;

            TransferOffer offer = null;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                offer = BuildOffer(folder);
            }

            if (offer == null && !acceptsUpload)
            {
                TransferStatusText.Text =
                    "Pick a folder that holds a points.json or an ar-model.json, or tick " +
                    "\"Accept a scan coming back\". A session that offers nothing and accepts " +
                    "nothing is not worth showing a code for.";
                return;
            }

            string inbox = Path.Combine(
                Path.GetTempPath(),
                "PIXMYD-Nav-inbox-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

            try
            {
                Directory.CreateDirectory(inbox);

                _transfer = new TransferServer(
                    offer,
                    folder,
                    inbox,
                    acceptsUpload,
                    MaxUploadBytes,
                    Environment.MachineName,
                    _document != null ? _document.Title : "");

                _transfer.Activity += OnTransferActivity;
                _transfer.CaptureCommitted += OnCaptureCommitted;

                TransferTicket ticket = _transfer.Start(SessionLifetime);
                ShowTicket(ticket);

                TransferStartButton.IsEnabled = false;
                TransferStopButton.IsEnabled = true;

                TransferStatusText.Text = offer == null
                    ? "Waiting for a scan. The session closes in 15 minutes or when this window closes."
                    : "Offering " + offer.Files.Count + " file(s). The session closes in 15 minutes " +
                      "or when this window closes.";

                _settings[SettingsTransferFolder] = folder;
            }
            catch (Exception ex)
            {
                // A machine with no LAN address, a blocked port, or a firewall
                // that refuses the bind. All of them mean the same thing to the
                // user, and all of them leave the folder export working.
                TransferStatusText.Text = ex.Message +
                    "  You can still export to a folder and copy it to the phone.";
                StopTransfer();
            }
        }

        private void OnStopTransfer(object sender, RoutedEventArgs e)
        {
            StopTransfer();
            TransferStatusText.Text = "Session closed. The code is no longer valid.";
        }

        private void StopTransfer()
        {
            if (_transfer != null)
            {
                _transfer.Activity -= OnTransferActivity;
                _transfer.CaptureCommitted -= OnCaptureCommitted;
                _transfer.Stop();
                _transfer = null;
            }
            TransferQrImage.Source = null;
            TransferPayloadText.Text = "";
            TransferStartButton.IsEnabled = true;
            TransferStopButton.IsEnabled = false;
        }

        /// <summary>
        /// Everything in the folder that the contract files reference, plus the
        /// contract files themselves.
        ///
        /// The whole folder rather than a filtered subset: the exports are
        /// points.json plus its PNGs, and a guest that gets the JSON without the
        /// photos has a point set that looks complete and is not.
        /// </summary>
        private static TransferOffer BuildOffer(string folder)
        {
            bool hasPoints = File.Exists(Path.Combine(folder, "points.json"));
            bool hasArModel = File.Exists(Path.Combine(folder, "ar-model.json"))
                           || File.Exists(Path.Combine(folder, "ar-bundle.json"));
            if (!hasPoints && !hasArModel) return null;

            var offer = new TransferOffer();
            offer.Kind = TransferManifest.KindFor(hasPoints, hasArModel);
            offer.Name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));

            foreach (string path in Directory.GetFiles(folder))
            {
                string name = Path.GetFileName(path);
                // markers.html is for a printer, not a phone, and it is the one
                // file in the folder that can be large for no benefit here.
                if (string.Equals(name, "markers.html", StringComparison.OrdinalIgnoreCase)) continue;
                if (!TransferManifest.IsSafeName(name)) continue;
                offer.Files.Add(new TransferFileEntry(name, new FileInfo(path).Length));
            }

            return offer.Files.Count == 0 ? null : offer;
        }

        private void ShowTicket(TransferTicket ticket)
        {
            QrCode qr = QrEncoder.Encode(ticket.Payload);
            byte[] bmp = QrRender.ToBmp(qr, 8);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bmp);
            image.EndInit();
            image.Freeze();

            TransferQrImage.Source = image;
            TransferPayloadText.Text = ticket.Host + ":" + ticket.Port + "   " + ticket.Payload;
        }

        // MARK: - Server callbacks

        /// <summary>
        /// Raised on the listener thread. Everything below touches WPF, so it is
        /// marshalled first -- a control touched from another thread throws, and
        /// it would throw inside a socket handler where nothing would report it.
        /// </summary>
        private void OnTransferActivity(string message)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                string stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                TransferLogText.Text = stamp + "  " + message + Environment.NewLine + TransferLogText.Text;
                if (TransferLogText.Text.Length > 4000)
                    TransferLogText.Text = TransferLogText.Text.Substring(0, 4000);
            }));
        }

        private void OnCaptureCommitted(string inbox)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                _pendingCaptureFolder = inbox;
                CaptureReviewButton.IsEnabled = true;
                DescribePendingCapture();
            }));
        }

        // MARK: - The return leg

        private void OnOpenCaptureFolder(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Pick the folder holding capture.json";
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                _pendingCaptureFolder = dialog.SelectedPath;
            }
            CaptureReviewButton.IsEnabled = true;
            DescribePendingCapture();
        }

        private void DescribePendingCapture()
        {
            CaptureFile capture = ReadPendingCapture();
            if (capture == null) return;

            var text = new System.Text.StringBuilder();
            text.Append("Capture ").Append(Short(capture.CaptureId));
            if (!string.IsNullOrEmpty(capture.DeviceModel))
                text.Append(" from ").Append(capture.DeviceModel);
            text.Append(".");

            if (capture.HasSolution)
            {
                AccuracyGrade grade = AccuracyBands.Classify(capture.Solution.RmsError);
                text.Append("  RMS ").Append(Millimetres(capture.Solution.RmsError));
                text.Append(", max ").Append(Millimetres(capture.Solution.MaxError));
                text.Append(" — ").Append(grade.Label).Append(".");
                if (capture.Solution.OutlierPointIds.Length > 0)
                    text.Append("  ").Append(capture.Solution.OutlierPointIds.Length)
                        .Append(" point(s) excluded as outliers.");
            }
            else
            {
                text.Append("  No solution — ").Append(capture.Correspondences.Count)
                    .Append(" raw observation(s) came with it, which can be solved here.");
            }

            CaptureSummaryText.Text = text.ToString();
        }

        private CaptureFile ReadPendingCapture()
        {
            if (string.IsNullOrEmpty(_pendingCaptureFolder))
            {
                CaptureSummaryText.Text = "Nothing yet.";
                return null;
            }

            string path = Path.Combine(_pendingCaptureFolder, "capture.json");
            if (!File.Exists(path))
            {
                CaptureSummaryText.Text = "No capture.json in " + _pendingCaptureFolder + ".";
                CaptureReviewButton.IsEnabled = false;
                return null;
            }

            try
            {
                return CaptureReader.Read(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                CaptureSummaryText.Text = ex.Message;
                CaptureReviewButton.IsEnabled = false;
                return null;
            }
        }

        /// <summary>
        /// Show the fit and ask before anything is placed.
        ///
        /// This is the one place in the suite where a silent success does real
        /// damage: a skin placed 300 mm out looks correct on screen, gets built
        /// to, and is found by someone with a tape measure much later. So the
        /// numbers go in front of the user, and a grade below survey tolerance
        /// defaults the dialog to No.
        /// </summary>
        private void OnReviewCapture(object sender, RoutedEventArgs e)
        {
            CaptureFile capture = ReadPendingCapture();
            if (capture == null) return;

            CaptureSolution solution = capture.Solution;
            string provenanceOfSolution = "solved on the phone";

            if (solution == null)
            {
                // The documented degraded mode: correspondences with no solution
                // is an invitation to solve here, not an error.
                Dictionary<string, double[]> positions = PointPositionsForCurrentSet(capture.PointSetId);
                if (positions == null)
                {
                    MessageBox.Show(this,
                        "This capture has no solution, and the point set it names (" +
                        Short(capture.PointSetId) + ") is not the one loaded in the Points tab, so " +
                        "there is nothing to solve it against.\n\nCapture the same point set again, " +
                        "or open the export it came from.",
                        "Cannot place this capture", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                try
                {
                    solution = CaptureReader.SolveLocally(capture, positions);
                    provenanceOfSolution = "solved here from the raw observations";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Cannot solve this capture",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            AccuracyGrade grade = AccuracyBands.Classify(solution.RmsError);
            double[] placement = CapturePlacement.ModelWorldMatrix(solution.Matrix, capture.AppliedOffset);

            var message = new System.Text.StringBuilder();
            message.Append("Capture ").Append(Short(capture.CaptureId));
            if (!string.IsNullOrEmpty(capture.DeviceModel))
                message.Append(" from ").Append(capture.DeviceModel);
            message.AppendLine().AppendLine();

            message.Append("Fit (").Append(provenanceOfSolution).AppendLine("):");
            message.Append("  RMS error   ").AppendLine(Millimetres(solution.RmsError));
            message.Append("  Max error   ").AppendLine(Millimetres(solution.MaxError));
            message.Append("  Grade       ").Append(grade.Label).Append("  (").Append(grade.Band).AppendLine(")");
            message.Append("  Outliers    ").AppendLine(
                solution.OutlierPointIds.Length == 0
                    ? "none"
                    : solution.OutlierPointIds.Length + " (" + string.Join(", ", solution.OutlierPointIds) + ")");
            message.AppendLine();
            message.AppendLine(grade.Guidance);
            message.AppendLine();

            message.Append("Geometry: ");
            if (capture.HasGeometry)
            {
                string geometryPath = Path.Combine(_pendingCaptureFolder, capture.GeometryFile);
                message.Append(capture.GeometryFile);
                message.Append(File.Exists(geometryPath) ? " (present)" : " (MISSING from the folder)");
            }
            else
            {
                message.Append("none");
            }
            message.AppendLine().AppendLine();

            message.Append("Origin offset applied: ")
                   .Append(Vector(capture.AppliedOffset)).AppendLine();
            message.Append("Placement translation: ")
                   .Append(placement == null ? "n/a"
                        : Vector(new double[] { placement[12], placement[13], placement[14] }))
                   .AppendLine();

            if (!grade.WithinSurveyTolerance)
            {
                message.AppendLine();
                message.AppendLine(
                    "This fit is below survey tolerance. Placing it will look correct on screen " +
                    "and be wrong on site. Place it anyway?");
            }
            else
            {
                message.AppendLine();
                message.AppendLine("Place this capture?");
            }

            MessageBoxResult answer = MessageBox.Show(
                this,
                message.ToString(),
                grade.WithinSurveyTolerance ? "Place capture" : "Place capture — BELOW TOLERANCE",
                MessageBoxButton.YesNo,
                grade.WithinSurveyTolerance ? MessageBoxImage.Question : MessageBoxImage.Warning,
                // Default to No when the fit is poor: the safe answer should be
                // the one you get by pressing Enter without reading.
                grade.WithinSurveyTolerance ? MessageBoxResult.Yes : MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes) return;

            PlaceCapture(capture, solution, placement);
        }

        /// <summary>
        /// Where the mesh actually enters the model.
        ///
        /// Not implemented: the managed Navisworks API has no geometry authoring
        /// surface -- a document is built from converted files, and there is no
        /// AddGeometry or AppendMesh to call. Placing a mesh means writing it as
        /// a file Navisworks can append and appending it, which is NavEx's job
        /// and a separate piece of work.
        ///
        /// So this does the honest thing: writes the transform beside the mesh
        /// and tells the user exactly what to do with it. A button that silently
        /// did nothing, or that placed the mesh at the origin, would be worse
        /// than one that says what it cannot do.
        /// </summary>
        private void PlaceCapture(CaptureFile capture, CaptureSolution solution, double[] placement)
        {
            try
            {
                string manifest = Path.Combine(_pendingCaptureFolder, "placement.txt");
                var text = new System.Text.StringBuilder();
                text.AppendLine("PIXMYD-Nav placement for capture " + capture.CaptureId);
                text.AppendLine("Written " + TransferManifest.Iso8601(DateTime.UtcNow));
                text.AppendLine();
                text.AppendLine("Mesh file:  " + capture.GeometryFile);
                text.AppendLine("Point set:  " + capture.PointSetId);
                text.AppendLine("RMS error:  " + Millimetres(solution.RmsError));
                text.AppendLine("Grade:      " + AccuracyBands.Classify(solution.RmsError).Band);
                text.AppendLine();
                text.AppendLine("Column-major 4x4, capture frame to model world coordinates");
                text.AppendLine("(solution.matrix with the point set's appliedOffset folded into the");
                text.AppendLine("translation column):");
                for (int column = 0; column < 4; column++)
                {
                    text.Append("  ");
                    for (int row = 0; row < 4; row++)
                        text.Append(placement[column * 4 + row].ToString("R", CultureInfo.InvariantCulture)).Append('\t');
                    text.AppendLine();
                }

                File.WriteAllText(manifest, text.ToString());

                MessageBox.Show(this,
                    "The placement transform is written to:\n\n" + manifest + "\n\n" +
                    "The managed Navisworks API cannot author geometry into an open document, so " +
                    "the mesh has to be appended as a file. Transform " + capture.GeometryFile +
                    " by the matrix above, then append it to this model.",
                    "Placement written", MessageBoxButton.OK, MessageBoxImage.Information);

                OnTransferActivity("Wrote placement.txt for capture " + Short(capture.CaptureId));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not write the placement",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// The points currently in the Points tab, if they are the set this
        /// capture was taken against.
        ///
        /// Refusing on a mismatch is the contract's rule: "pointSetId names a set
        /// the consumer does not have -> refuse, and name the set. Do not place
        /// the mesh at the origin as a fallback."
        /// </summary>
        private Dictionary<string, double[]> PointPositionsForCurrentSet(string pointSetId)
        {
            if (_points == null || _points.Count == 0) return null;

            var positions = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (PointRow row in _points)
            {
                Vec3 p = row.Record.Position;
                positions[row.Id] = new double[] { p.X, p.Y, p.Z };
            }
            return positions.Count == 0 ? null : positions;
        }

        private void OnBrowseTransferFolder(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Pick the exported folder to share";
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    TransferFolderBox.Text = dialog.SelectedPath;
            }
        }

        /// <summary>
        /// A listener must not outlive the window that opened it.
        ///
        /// Overriding OnClosed rather than adding to the existing Closing handler
        /// keeps this feature out of MainWindow.xaml.cs entirely -- the base
        /// implementation still raises Closed, so the other partial's own
        /// teardown is unaffected.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            StopTransfer();
            base.OnClosed(e);
        }

        // MARK: - Formatting

        private static string Short(string id)
        {
            if (string.IsNullOrEmpty(id)) return "(no id)";
            return id.Length <= 8 ? id : id.Substring(0, 8);
        }

        private static string Millimetres(double metres)
        {
            return (metres * 1000).ToString("0.#", CultureInfo.InvariantCulture) + " mm";
        }

        private static string Vector(double[] v)
        {
            if (v == null || v.Length < 3) return "n/a";
            return v[0].ToString("0.###", CultureInfo.InvariantCulture) + ", " +
                   v[1].ToString("0.###", CultureInfo.InvariantCulture) + ", " +
                   v[2].ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
