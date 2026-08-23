using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PIXMYD_Nav.Core.Workspace
{
    /// <summary>
    /// One folder per model, with a side for each direction of travel.
    ///
    /// Before this there were three folder settings that all defaulted to the
    /// same place (markers, AR model, transfer) and a fourth location nobody
    /// chose: arriving captures landed in %TEMP%\PIXMYD-Nav-inbox-&lt;stamp&gt;.
    /// That is the worst possible home for the one artefact in the suite a
    /// coordinator has to keep -- it is invisible from the folder they were
    /// told to look in, and Windows deletes it.
    ///
    /// So there is one root and two sides:
    ///
    ///     &lt;root&gt;\
    ///       EXPORT\                 everything this plugin produces for the phone
    ///         points.json
    ///         markers.html
    ///         points-markers.dxf
    ///         ar-model.json / ar-model.glb
    ///         P001_photo.png ...
    ///       IMPORT\                 everything that arrives from the phone
    ///         20260823-141502-3f9c1a2b\
    ///           capture.json
    ///           capture.fbx
    ///           points.json         the points the phone placed, when it sent any
    ///           placement.txt
    ///
    /// EXPORT is flat and overwritten: it is the current state of this model's
    /// hand-off, and a folder that accumulates points-2.json is a folder where
    /// somebody prints the wrong markers. IMPORT is one dated directory per
    /// arrival, because a capture is a record of a moment on site and the second
    /// scan of a room does not supersede the first.
    ///
    /// Pure: System.IO only, no Navisworks. In WriterTests.csproj.
    /// </summary>
    public sealed class PixmydWorkspace
    {
        public const string ExportFolderName = "EXPORT";
        public const string ImportFolderName = "IMPORT";

        /// <summary>The folder the user picked. Everything else hangs off it.</summary>
        public string Root { get; private set; }

        public PixmydWorkspace(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new ArgumentException("A PIXMYD workspace needs a root folder.", "root");
            Root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public string Export { get { return Path.Combine(Root, ExportFolderName); } }
        public string Import { get { return Path.Combine(Root, ImportFolderName); } }

        /// <summary>Where the plugin puts a workspace when the user has not picked one.</summary>
        public static string DefaultRoot()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "PIXMYD-Nav");
        }

        /// <summary>
        /// Create both sides. Called before anything is written rather than at
        /// each write, so a user who opens the folder before exporting finds the
        /// two directories and understands the layout without reading anything.
        /// </summary>
        public PixmydWorkspace EnsureCreated()
        {
            Directory.CreateDirectory(Export);
            Directory.CreateDirectory(Import);
            return this;
        }

        public string ExportFile(string name) { return Path.Combine(Export, name); }

        /// <summary>
        /// The directory an arriving capture is written into.
        ///
        /// Named by arrival time first so the folder sorts chronologically in
        /// Explorer, then by the capture id so two scans committed in the same
        /// second cannot collide. The id is filtered rather than trusted: it
        /// arrives from another process over a network and becomes a path
        /// component here.
        /// </summary>
        public string NewImportFolder(string captureId, DateTime whenUtc)
        {
            string stamp = whenUtc.ToUniversalTime()
                .ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string suffix = ShortId(captureId);
            string name = suffix.Length == 0 ? stamp : stamp + "-" + suffix;

            string path = Path.Combine(Import, name);
            // Two commits inside one second with the same (or no) id.
            int attempt = 2;
            while (Directory.Exists(path))
            {
                path = Path.Combine(Import, name + "-" + attempt.ToString(CultureInfo.InvariantCulture));
                attempt++;
            }
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>Import folders, newest first. Empty when nothing has arrived.</summary>
        public List<string> ImportFolders()
        {
            var folders = new List<string>();
            try
            {
                if (!Directory.Exists(Import)) return folders;
                folders.AddRange(Directory.GetDirectories(Import));
            }
            catch (Exception)
            {
                // An unreadable IMPORT directory means no history to show, not a
                // failed export. The caller renders an empty list.
                return folders;
            }
            folders.Sort(StringComparer.OrdinalIgnoreCase);
            folders.Reverse();
            return folders;
        }

        /// <summary>The most recent arrival, or null when there is none.</summary>
        public string LatestImport()
        {
            List<string> folders = ImportFolders();
            return folders.Count == 0 ? null : folders[0];
        }

        /// <summary>
        /// Move a pre-workspace layout into EXPORT, once.
        ///
        /// Older builds wrote points.json and ar-model.json directly into the
        /// root the user picked. Leaving them there would mean the Transfer tab
        /// offers an empty EXPORT while the files the user can see sit beside
        /// it, which reads as the plugin having lost them. Copy rather than
        /// move: the originals are the user's, and a migration that deletes
        /// somebody's export folder to tidy up is not a migration.
        /// </summary>
        public List<string> MigrateLooseFiles()
        {
            var moved = new List<string>();
            try
            {
                if (!Directory.Exists(Root)) return moved;
                EnsureCreated();

                foreach (string path in Directory.GetFiles(Root))
                {
                    string name = Path.GetFileName(path);
                    if (!IsExportArtefact(name)) continue;

                    string destination = Path.Combine(Export, name);
                    // Never overwrite: a file already in EXPORT is the current
                    // export and the loose one is the leftover, not the other
                    // way round.
                    if (File.Exists(destination)) continue;

                    File.Copy(path, destination);
                    moved.Add(name);
                }
            }
            catch (Exception)
            {
                // A migration is a convenience. A locked file or a read-only
                // folder must not stop the window opening.
            }
            return moved;
        }

        /// <summary>
        /// Whether a loose file in the root is one this plugin wrote.
        ///
        /// Deliberately a fixed list plus the photo suffixes, not "everything
        /// that is not a directory". The root a user picks is often a project
        /// folder with drawings in it, and a migration that swept those into
        /// EXPORT would be indistinguishable from a bug.
        /// </summary>
        public static bool IsExportArtefact(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            string lower = name.ToLowerInvariant();
            switch (lower)
            {
                case "points.json":
                case "markers.html":
                case "points-markers.dxf":
                case "ar-model.json":
                case "ar-model.glb":
                case "ar-bundle.json":
                    return true;
            }
            return lower.EndsWith("_photo.png", StringComparison.Ordinal)
                || lower.EndsWith("_photo_mono.png", StringComparison.Ordinal);
        }

        /// <summary>
        /// The first 8 characters of an id, filtered to what may be a path
        /// component. Empty when nothing usable survives.
        /// </summary>
        public static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            var kept = new System.Text.StringBuilder(8);
            foreach (char c in id)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                if (!ok) continue;
                kept.Append(char.ToLowerInvariant(c));
                if (kept.Length == 8) break;
            }
            return kept.ToString();
        }
    }
}
