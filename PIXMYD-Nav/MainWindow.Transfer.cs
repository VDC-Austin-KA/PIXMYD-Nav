using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Autodesk.Navisworks.Api;
using PIXMYD_Nav.Core.Capture;
using PIXMYD_Nav.Core.Markers;
using PIXMYD_Nav.Core.NavBridge;
using PIXMYD_Nav.Core.Nwc;
using PIXMYD_Nav.Core.Points;
using PIXMYD_Nav.Core.Transfer;
using PIXMYD_Nav.Core.Workspace;

namespace PIXMYD_Nav
{
    /// <summary>
    /// The Transfer tab: show a pairing code, serve EXPORT over the local
    /// network, take a scan back into IMPORT, and place it.
    ///
    /// A separate partial rather than more of MainWindow.xaml.cs, per RULES.md
    /// section 1. Everything here is UI and lifetime. The protocol lives in
    /// Core/Transfer, the capture maths in Core/Capture, the placement in
    /// Core/NavBridge, and all of those are covered by tools/writer-tests. This
    /// file is the part that cannot be tested offline, so it is kept to wiring.
    ///
    /// ## What changed
    ///
    /// Arriving captures used to land in %TEMP%\PIXMYD-Nav-inbox-&lt;stamp&gt;.
    /// They now land in the workspace's IMPORT folder, one dated directory per
    /// arrival, beside the EXPORT folder they were taken against.
    ///
    /// And placing a capture used to write a matrix to a text file and explain
    /// what the user would have to do by hand. It now appends the mesh and sets
    /// its transform -- <c>Document.AppendFile</c> plus
    /// <c>DocumentModels.SetModelUnitsAndTransform</c>, which between them are
    /// the geometry-authoring path the managed API does have. The text file is
    /// still written, because a transform somebody can read is worth keeping.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);
        private const long MaxUploadBytes = 1024L * 1024 * 1024;

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

            PixmydWorkspace workspace = Workspace();
            if (workspace == null) return;

            bool acceptsUpload = TransferUploadCheck.IsChecked == true;
            TransferOffer offer = BuildOffer(workspace.Export);

            if (offer == null && !acceptsUpload)
            {
                TransferStatusText.Text =
                    "EXPORT holds no points.json or ar-model.json, and \"Accept a scan coming back\" " +
                    "is off. A session that offers nothing and accepts nothing is not worth showing " +
                    "a code for — export something first, or tick the box.";
                return;
            }

            try
            {
                string inbox = workspace.NewImportFolder("", DateTime.UtcNow);

                _transfer = new TransferServer(
                    offer,
                    workspace.Export,
                    inbox,
                    acceptsUpload,
                    MaxUploadBytes,
                    Environment.MachineName,
                    _document != null ? _document.Title : "");

                _transfer.Activity += OnTransferActivity;
                _transfer.CaptureCommitted += OnCaptureCommitted;
                _transfer.Progress += OnTransferProgress;

                TransferTicket ticket = _transfer.Start(SessionLifetime);
                ShowTicket(ticket);

                TransferStartButton.IsEnabled = false;
                TransferStopButton.IsEnabled = true;

                TransferStatusText.Text = offer == null
                    ? "Waiting for a scan. The session closes in 15 minutes or when this window closes."
                    : "Offering " + offer.Files.Count + " file(s) from EXPORT. The session closes in 15 " +
                      "minutes or when this window closes.";
            }
            catch (Exception ex)
            {
                // A machine with no LAN address, a blocked port, or a firewall
                // that refuses the bind. All of them mean the same thing to the
                // user, and all of them leave the folder export working.
                TransferStatusText.Text = ex.Message +
                    "  You can still export to the folder and copy it to the phone.";
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
                _transfer.Progress -= OnTransferProgress;
                _transfer.Stop();
                _transfer = null;
            }
            TransferQrImage.Source = null;
            TransferPayloadText.Text = "";
            TransferProgressBar.Visibility = Visibility.Collapsed;
            TransferProgressText.Text = "";
            TransferStartButton.IsEnabled = true;
            TransferStopButton.IsEnabled = false;
        }

        /// <summary>
        /// Everything in EXPORT that a phone can use.
        ///
        /// The whole folder rather than a filtered subset: the exports are
        /// points.json plus its PNGs plus, now, a mesh, and a guest that gets
        /// the JSON without the photos has a point set that looks complete and
        /// is not.
        /// </summary>
        private static TransferOffer BuildOffer(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;

            bool hasPoints = File.Exists(Path.Combine(folder, "points.json"));
            bool hasArModel = File.Exists(Path.Combine(folder, "ar-model.json"))
                           || File.Exists(Path.Combine(folder, "ar-bundle.json"));
            if (!hasPoints && !hasArModel) return null;

            var offer = new TransferOffer();
            offer.Kind = TransferManifest.KindFor(hasPoints, hasArModel);
            // The workspace's own name, not "EXPORT" -- the phone shows this as
            // the name of the thing it is about to download.
            offer.Name = Path.GetFileName(Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar)));
            if (string.IsNullOrEmpty(offer.Name)) offer.Name = "PIXMYD export";

            foreach (string path in Directory.GetFiles(folder))
            {
                string name = Path.GetFileName(path);
                // markers.html is for a printer, not a phone. points-markers.dxf
                // is for Navisworks and means nothing on a phone. Both are large
                // for no benefit here.
                if (string.Equals(name, "markers.html", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, MarkerDxf.FileName, StringComparison.OrdinalIgnoreCase)) continue;
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

        /// <summary>
        /// The bar. Bytes, not files -- see TransferProgress for why that
        /// distinction is the whole point of this feature.
        /// </summary>
        private void OnTransferProgress(TransferProgress progress)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                TransferProgressBar.Visibility = Visibility.Visible;
                TransferProgressBar.Value = progress.Fraction;
                TransferProgressText.Text = progress.Describe();
            }));
        }

        private void OnCaptureCommitted(string inbox)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                string settled = SettleImportFolder(inbox);
                _pendingCaptureFolder = settled;

                // The session keeps running, so give it somewhere else to write:
                // a second scan must not overwrite the first one's capture.json.
                if (_transfer != null && _workspace != null)
                {
                    try { _transfer.InboxDirectory = _workspace.NewImportFolder("", DateTime.UtcNow); }
                    catch (Exception) { }
                }

                RefreshImportList();
                SelectImport(settled);
                DescribePendingCapture();
                TransferProgressText.Text = "A scan arrived and is waiting for review.";
            }));
        }

        /// <summary>
        /// Rename a freshly filled inbox to carry the capture's id.
        ///
        /// The folder has to exist before the first byte arrives and the id is
        /// only known once capture.json is in it, so the name is completed here
        /// rather than guessed earlier. A rename that fails is not worth
        /// reporting -- the folder is still correct, just less readable.
        /// </summary>
        private string SettleImportFolder(string inbox)
        {
            if (_workspace == null || string.IsNullOrEmpty(inbox)) return inbox;
            try
            {
                string json = Path.Combine(inbox, "capture.json");
                if (!File.Exists(json)) return inbox;

                CaptureFile capture = CaptureReader.Read(File.ReadAllText(json));
                string suffix = PixmydWorkspace.ShortId(capture.CaptureId);
                if (suffix.Length == 0) return inbox;

                string parent = Path.GetDirectoryName(inbox);
                string settled = Path.Combine(parent, Path.GetFileName(inbox) + "-" + suffix);
                if (Directory.Exists(settled)) return inbox;

                Directory.Move(inbox, settled);
                return settled;
            }
            catch (Exception)
            {
                return inbox;
            }
        }

        // MARK: - The IMPORT list

        private void RefreshImportList()
        {
            var rows = new List<string>();
            if (_workspace != null)
                foreach (string folder in _workspace.ImportFolders())
                    rows.Add(Path.GetFileName(folder));

            ImportList.ItemsSource = rows;
            if (rows.Count == 0)
            {
                CaptureSummaryText.Text = "Nothing yet.";
                CaptureReviewButton.IsEnabled = false;
            }
        }

        private void SelectImport(string folder)
        {
            if (_workspace == null || string.IsNullOrEmpty(folder)) return;
            ImportList.SelectedItem = Path.GetFileName(folder);
        }

        private void OnImportSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            string name = ImportList.SelectedItem as string;
            if (name == null || _workspace == null) return;
            _pendingCaptureFolder = Path.Combine(_workspace.Import, name);
            CaptureReviewButton.IsEnabled = true;
            DescribePendingCapture();
        }

        private void OnOpenCaptureFolder(object sender, RoutedEventArgs e)
        {
            string picked = FolderPrompt.Pick(
                "Pick the folder holding capture.json",
                _workspace != null ? _workspace.Import : null);
            if (string.IsNullOrEmpty(picked)) return;
            _pendingCaptureFolder = picked;
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

            if (capture.HasGeometry)
            {
                text.Append("  Mesh: ").Append(capture.GeometryFile);
                text.Append(capture.GeometryIsPlaced ? " (already in model coordinates)" : " (capture frame)");
            }

            ReadPointSet fromPhone = ReadPhonePoints();
            SeedPointsButton.IsEnabled = fromPhone != null && fromPhone.Points.Count > 0;
            if (fromPhone != null && fromPhone.Points.Count > 0)
                text.Append("  ").Append(PointSetReader.Describe(fromPhone));

            CaptureSummaryText.Text = text.ToString();
        }

        /// <summary>
        /// The points the phone placed, when this arrival carries any.
        ///
        /// Read straight off the folder rather than out of capture.json: the
        /// file is a points.json in the shape this plugin already writes, which
        /// is the whole reason the phone writes it that way.
        /// </summary>
        private ReadPointSet ReadPhonePoints()
        {
            if (string.IsNullOrEmpty(_pendingCaptureFolder)) return null;
            string path = Path.Combine(_pendingCaptureFolder, "points.json");
            if (!File.Exists(path)) return null;

            try
            {
                return PointSetReader.Read(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                OnTransferActivity("Could not read the phone's points.json: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Put the phone's ids into the Points tab so they can be placed on the
        /// model, one click each.
        ///
        /// This is the half of the round trip that did not exist. Points could
        /// only ever start at the workstation; now a crew can place them on
        /// site, and this is where those names arrive.
        /// </summary>
        private void OnSeedPhonePoints(object sender, RoutedEventArgs e)
        {
            ReadPointSet fromPhone = ReadPhonePoints();
            if (fromPhone == null || fromPhone.Points.Count == 0)
            {
                CaptureSummaryText.Text = "This arrival carries no points.json, so there are no ids to place.";
                return;
            }

            int added = SeedExpectedPoints(fromPhone);
            StatusText.Text = added == 0
                ? "Every id from the phone is already in the list."
                : "Added " + added + " id(s) from the phone. Turn on Pick points and click each one " +
                  "on the model — the list fills in order.";
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

        // MARK: - The return leg

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
                    OfferHandPlacement(capture,
                        "This capture has no solution, and there are no points in the Points tab "
                        + "to solve it against.");
                    return;
                }

                try
                {
                    solution = CaptureReader.SolveLocally(capture, positions);
                    provenanceOfSolution = solution.VerticalHeld
                        ? "solved here from the raw observations, with the vertical held from gravity"
                        : "solved here from the raw observations";
                }
                catch (Exception ex)
                {
                    OfferHandPlacement(capture, ex.Message);
                    return;
                }
            }

            AccuracyGrade grade = AccuracyBands.Classify(solution.RmsError);
            double[] placement = CapturePlacement.ModelWorldMatrix(solution.Matrix, capture.AppliedOffset);
            int pairCount = capture.Correspondences != null ? capture.Correspondences.Count : 0;

            var message = new System.Text.StringBuilder();
            message.Append("Capture ").Append(Short(capture.CaptureId));
            if (!string.IsNullOrEmpty(capture.DeviceModel))
                message.Append(" from ").Append(capture.DeviceModel);
            message.AppendLine().AppendLine();

            message.Append("Fit (").Append(provenanceOfSolution).AppendLine("):");
            message.Append("  Points      ").AppendLine(pairCount.ToString(CultureInfo.InvariantCulture));
            message.Append("  RMS error   ").AppendLine(Millimetres(solution.RmsError));
            message.Append("  Max error   ").AppendLine(Millimetres(solution.MaxError));
            message.Append("  Grade       ").Append(grade.Label).Append("  (").Append(grade.Band).AppendLine(")");
            message.Append("  Outliers    ").AppendLine(
                solution.OutlierPointIds.Length == 0
                    ? "none"
                    : solution.OutlierPointIds.Length + " (" + string.Join(", ", solution.OutlierPointIds) + ")");
            message.AppendLine();
            message.AppendLine(grade.Guidance);

            // A two-point fit reports near-zero error whether it is right or
            // wrong. Saying so beside the number is the difference between a
            // reassuring statistic and an honest one.
            if (pairCount > 0 && pairCount <= 3)
            {
                message.AppendLine();
                message.AppendLine(GravitySolve.RedundancyGuidance(pairCount));
            }
            message.AppendLine();

            message.Append("Geometry: ");
            if (capture.HasGeometry)
            {
                string geometryPath = Path.Combine(_pendingCaptureFolder, capture.GeometryFile);
                message.Append(capture.GeometryFile);
                if (!File.Exists(geometryPath)) message.Append(" (MISSING from the folder)");
                else if (!capture.GeometryIsAppendable)
                    message.Append(" (Navisworks cannot append this format — the transform will be written instead)");
                else message.Append(capture.GeometryIsPlaced
                    ? " (already in model coordinates)"
                    : " (capture frame — it will be placed by the transform below)");
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
        /// Append the file, then set the appended model's transform. Both halves
        /// are documented managed API, and together they are the geometry path
        /// the earlier version of this plugin said did not exist -- it was right
        /// that a document cannot be authored into, and wrong that this meant
        /// nothing could be placed.
        ///
        /// The transform is decomposed to an axis, an angle and a translation
        /// before it crosses the boundary, and its translation is converted from
        /// the contract's metres into the document's own units. Getting that
        /// second conversion wrong on a model drawn in millimetres puts the scan
        /// a kilometre away, which at least is obvious; getting it wrong on one
        /// drawn in feet puts it three metres away, which is not.
        /// </summary>
        private void PlaceCapture(CaptureFile capture, CaptureSolution solution, double[] placement)
        {
            string manifest = WritePlacementFile(capture, solution, placement);

            if (!capture.HasGeometry)
            {
                MessageBox.Show(this,
                    "This capture carries no mesh, so there is nothing to place. The alignment is " +
                    "written to:\n\n" + manifest,
                    "Nothing to place", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string geometryPath = Path.Combine(_pendingCaptureFolder, capture.GeometryFile);
            if (!File.Exists(geometryPath))
            {
                MessageBox.Show(this,
                    capture.GeometryFile + " is named by capture.json but is not in the folder, so the " +
                    "transfer did not finish. Send the scan again.",
                    "The mesh is missing", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // An OBJ is converted rather than appended. Navisworks does not
            // read OBJ, and NWC is what it writes for its own cache -- so
            // appending one is a load rather than a translation, through the
            // one reader that is never the weak link. The phone sends OBJ
            // because it is the format this plugin can read and it carries a
            // texture coordinate per polygon corner, which is what a
            // photographic atlas needs.
            bool convertedToNwc = false;
            if (string.Equals(Path.GetExtension(geometryPath), ".obj",
                              StringComparison.OrdinalIgnoreCase))
            {
                // Out of process, and that is not an implementation detail.
                // nwcreate cannot run inside Navisworks: the loader build in
                // the Navisworks folder refuses to create a scene, and the
                // exporter build loaded in-process takes the application down
                // with it. See NwcConverter for the whole story.
                string nwcPath = Path.ChangeExtension(geometryPath, ".nwc");
                NwcConverter.Result made = NwcConverter.Convert(
                    geometryPath, nwcPath, "Scan " + Short(capture.CaptureId));
                if (!made.Ok)
                {
                    MessageBox.Show(this, made.Message, "The scan could not be converted",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                OnTransferActivity("Converted " + capture.GeometryFile + " to NWC — "
                                   + made.Message);
                geometryPath = nwcPath;
                convertedToNwc = true;
            }
            else if (!capture.GeometryIsAppendable)
            {
                MessageBox.Show(this,
                    "Navisworks does not read " + Path.GetExtension(capture.GeometryFile) +
                    ", so this mesh cannot be appended. Newer PIXMYD builds send OBJ, which " +
                    "this plugin converts to NWC." + Environment.NewLine + Environment.NewLine
                    + "The alignment is written to:" + Environment.NewLine + Environment.NewLine
                    + manifest,
                    "Cannot append this format", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ModelPlacer.AppendResult appended = ModelPlacer.Append(_document, geometryPath);
            if (!appended.Ok || appended.Model == null)
            {
                MessageBox.Show(this,
                    appended.Message + "\n\nThe alignment is written to:\n\n" + manifest,
                    "The scan could not be appended", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // A mesh the phone already put in model coordinates needs no
            // transform. Applying one would move it exactly as far past the
            // model as it was short of it.
            if (capture.GeometryIsPlaced)
            {
                OnTransferActivity("Placed " + capture.GeometryFile + " (already in model coordinates).");
                StatusText.Text = "Scan appended in place. " + AccuracyBands.Classify(solution.RmsError).Label + " fit.";
                return;
            }

            // Two conversions, and both of them are silent when wrong.
            //
            // The mesh arrived as an FBX declaring Y as up, and Navisworks' own
            // reader turned it into the document's Z-up frame on the way in --
            // so the solution, which maps the capture's ARKit frame, has to be
            // composed with the inverse of that turn.
            //
            // And the translation is in the contract's metres while the
            // document may be in millimetres or feet. Getting that wrong on a
            // millimetre model puts the scan a kilometre away, which at least
            // is obvious; on a model drawn in feet it puts it three metres
            // away, which is not.
            string upAxis = "Z";
            try { upAxis = SceneReader.Capture(_document).UpAxis; } catch (Exception) { }

            double toDocument = _scaleToMeters == 0 ? 1.0 : 1.0 / _scaleToMeters;
            // An FBX declares Y as up, so Navisworks' reader turns it into
            // the document's Z-up frame on the way in, and the solution -- which
            // maps the capture's own ARKit frame -- has to be composed with the
            // inverse of that turn. An NWC written here went in unturned,
            // because nothing read it; the coordinates are the ones we wrote.
            // Applying the FBX correction to it would lay the scan on its side.
            double[] basis = convertedToNwc
                ? TransformMath.Compose(new double[] { 0, 0, 1 }, 0, new double[] { 0, 0, 0 })
                : TransformMath.FbxCaptureBasis(upAxis);
            double[] fromImportedFbx = TransformMath.Multiply(placement, basis);
            double[] inDocumentUnits = TransformMath.WithTranslationScaled(fromImportedFbx, toDocument);

            string error;
            if (!ModelPlacer.TryTransform(_document, appended.Model, inDocumentUnits, out error))
            {
                MessageBox.Show(this,
                    "The scan was appended but could not be moved into place:\n\n" + error +
                    "\n\nThe transform is written to:\n\n" + manifest,
                    "Appended, not placed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            OnTransferActivity("Placed " + capture.GeometryFile + " at RMS " + Millimetres(solution.RmsError) + ".");
            StatusText.Text =
                "Scan appended and placed — " + AccuracyBands.Classify(solution.RmsError).Label +
                " fit at " + Millimetres(solution.RmsError) + ".";
        }

        /// <summary>
        /// When there is nothing to solve against, offer to place it anyway.
        ///
        /// This used to be a dead end: a box saying why it could not be solved,
        /// and no way forward. But a scan the operator cannot see is worth
        /// nothing, and "I know where this goes, let me put it there" is the
        /// ordinary case for a capture taken without control -- which is most
        /// of them, since placing points is a deliberate extra step on site.
        ///
        /// So the refusal becomes a choice. The placement carries
        /// <see cref="CaptureSolution.NotMeasured"/>, and everything that would
        /// otherwise quote a grade says that instead: a hand placement reports
        /// zero error, and zero is the number an excellent fit reports.
        /// </summary>
        private void OfferHandPlacement(CaptureFile capture, string why)
        {
            var message = new System.Text.StringBuilder();
            message.AppendLine(why);
            message.AppendLine();

            // Where it lands. The centre of the selection when there is one,
            // because pointing at somewhere in the model first is how an
            // operator says "put it here".
            var anchor = new Vec3();
            bool haveSelection = false;
            try { haveSelection = SceneReader.TrySelectionCenter(_document, out anchor); }
            catch (Exception) { }

            message.Append("It can still be placed by hand");
            message.AppendLine(haveSelection
                ? ", at the centre of what you have selected."
                : ", at the model origin. (Select something first to drop it there instead.)");
            message.AppendLine();
            message.AppendLine(
                "It arrives unrotated and NOT MEASURED -- there is no fit behind it and no error to "
                + "quote. Work it into position with the nudge buttons or the Item Tools gizmo.");
            message.AppendLine();
            message.AppendLine("Place it by hand?");

            MessageBoxResult answer = MessageBox.Show(
                this, message.ToString(), "Place by hand - NOT MEASURED",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            // The selection centre is in document units; the placement chain
            // works in metres and scales again on the way out.
            double[] metres = haveSelection
                ? new double[] { anchor.X * _scaleToMeters, anchor.Y * _scaleToMeters, anchor.Z * _scaleToMeters }
                : new double[] { 0, 0, 0 };

            CaptureSolution byHand = CapturePlacement.ByHand(metres);
            PlaceCapture(capture, byHand,
                CapturePlacement.ModelWorldMatrix(byHand.Matrix, capture.AppliedOffset));
        }

        /// <summary>
        /// The transform, in a file a person can read.
        ///
        /// Kept even though the mesh is placed automatically now: an alignment
        /// somebody can check, quote in an RFI, or apply in another tool is
        /// worth more than the twelve lines it costs.
        /// </summary>
        private string WritePlacementFile(CaptureFile capture, CaptureSolution solution, double[] placement)
        {
            try
            {
                string manifest = Path.Combine(_pendingCaptureFolder, "placement.txt");
                var text = new System.Text.StringBuilder();
                text.AppendLine("PIXMYD-Nav placement for capture " + capture.CaptureId);
                text.AppendLine("Written " + TransferManifest.Iso8601(DateTime.UtcNow));
                text.AppendLine();
                text.AppendLine("Mesh file:  " + capture.GeometryFile + "  (" + capture.GeometryFrame + " frame)");
                text.AppendLine("Point set:  " + capture.PointSetId);
                text.AppendLine("Points:     " +
                    (capture.Correspondences == null ? 0 : capture.Correspondences.Count));
                text.AppendLine("RMS error:  " + Millimetres(solution.RmsError));
                text.AppendLine("Grade:      " + AccuracyBands.Classify(solution.RmsError).Band);
                text.AppendLine("Vertical:   " + (solution.VerticalHeld ? "held from gravity" : "fitted"));
                text.AppendLine();
                text.AppendLine("Column-major 4x4, capture frame to model world coordinates, in metres");
                text.AppendLine("(solution.matrix with the point set's appliedOffset folded into the");
                text.AppendLine("translation column):");
                if (placement != null)
                {
                    for (int column = 0; column < 4; column++)
                    {
                        text.Append("  ");
                        for (int row = 0; row < 4; row++)
                            text.Append(placement[column * 4 + row].ToString("R", CultureInfo.InvariantCulture)).Append('\t');
                        text.AppendLine();
                    }
                }

                File.WriteAllText(manifest, text.ToString());
                return manifest;
            }
            catch (Exception ex)
            {
                OnTransferActivity("Could not write placement.txt: " + ex.Message);
                return "(placement.txt could not be written)";
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
                // A row that has not been placed has no coordinate. Pairing
                // against (0, 0, 0) would drag the whole solve to the origin
                // and report an RMS that looks like a bad scan.
                if (!row.IsPlaced) continue;
                Vec3 p = row.Record.Position;
                positions[row.Id] = new double[] { p.X, p.Y, p.Z };
            }
            return positions.Count == 0 ? null : positions;
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
