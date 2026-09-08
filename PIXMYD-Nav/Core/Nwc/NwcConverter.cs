using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PIXMYD_Nav.Core.Nwc
{
    /// <summary>
    /// Turns an OBJ into an NWC by running obj2nwc.exe.
    ///
    /// ## Why a process and not a call
    ///
    /// Because nwcreate cannot be run inside Navisworks, and both attempts to
    /// do it anyway failed in a way worth writing down so nobody tries again.
    ///
    /// `lcodpnwcreate.dll`, the copy already in the Navisworks folder, is the
    /// loader build. LiNwcApi.h is explicit: "These functions must be called
    /// when writing an exporter from third party software. They should not be
    /// called when writing a file loader." A loader does not create its scene
    /// -- the host hands it one -- so that build exports no initialiser and
    /// its LiNwcSceneCreate refuses with "Loader can't create scene".
    ///
    /// `nwcreate_21.dll`, the exporter build, does export the initialiser. But
    /// loading it into a running Navisworks puts a second copy of a NavisWorks
    /// module into a process that already has one, with its own globals and
    /// its own licence session. That took the whole application down with it.
    ///
    /// So the conversion happens where nwcreate is meant to run: in an
    /// exporting application of its own. This is the arrangement every
    /// exporter Autodesk ships uses. It also means the failure modes are a
    /// non-zero exit code and a line of text instead of an access violation in
    /// the user's session, and that the conversion can be reproduced from a
    /// command line without opening Navisworks.
    ///
    /// Navisworks-adjacent but not Navisworks-dependent: this file only starts
    /// a process, so it compiles anywhere.
    /// </summary>
    public static class NwcConverter
    {
        public sealed class Result
        {
            public bool Ok;
            public string Message = "";
            public string Path = "";
        }

        /// <summary>The converter ships beside the plugin, because it and its
        /// nwcreate runtime are installed together.</summary>
        public const string ExeName = "obj2nwc.exe";

        /// <summary>
        /// How long to wait before giving up.
        ///
        /// Generous, because a fine-detail room scan is millions of triangles
        /// and the geometry stream is one native call per corner. Not
        /// unbounded, because a converter that hangs would otherwise hang
        /// Navisworks behind it.
        /// </summary>
        public const int TimeoutMilliseconds = 10 * 60 * 1000;

        public static string ExePath()
        {
            try
            {
                string here = Path.GetDirectoryName(typeof(NwcConverter).Assembly.Location);
                return string.IsNullOrEmpty(here) ? "" : Path.Combine(here, ExeName);
            }
            catch (Exception) { return ""; }
        }

        /// <summary>
        /// Convert <paramref name="objPath"/> to <paramref name="nwcPath"/>.
        ///
        /// <paramref name="name"/> is what the node is called in the model
        /// tree; empty leaves the converter's default.
        /// </summary>
        public static Result Convert(string objPath, string nwcPath, string name)
        {
            var result = new Result { Path = nwcPath ?? "" };

            if (string.IsNullOrWhiteSpace(objPath) || !File.Exists(objPath))
            {
                result.Message = "There is no OBJ at " + (objPath ?? "(no path)") + ".";
                return result;
            }

            string exe = ExePath();
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                result.Message = "The NWC converter is not installed. " + ExeName + " and the "
                               + "nwcreate runtime beside it are part of the plugin bundle; "
                               + "reinstall it from the installer folder.";
                return result;
            }

            var start = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = Quote(objPath) + " " + Quote(nwcPath)
                          + (string.IsNullOrWhiteSpace(name) ? "" : " " + Quote(name)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // nwcreate finds nwcreate_data relative to its own DLL, not to
                // the working directory, but a converter started in whatever
                // folder Navisworks happened to be in is harder to reason
                // about than one started in its own.
                WorkingDirectory = Path.GetDirectoryName(exe),
            };

            var output = new StringBuilder();
            try
            {
                using (Process process = Process.Start(start))
                {
                    if (process == null)
                    {
                        result.Message = "The NWC converter would not start.";
                        return result;
                    }

                    // Read before waiting. A process whose pipe fills up blocks
                    // writing to it, and a parent waiting for exit before
                    // draining the pipe is the classic way to deadlock both.
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data != null) lock (output) output.AppendLine(e.Data);
                    };
                    process.ErrorDataReceived += (s, e) => { };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(TimeoutMilliseconds))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        result.Message = "The NWC converter did not finish within "
                                       + (TimeoutMilliseconds / 60000) + " minutes and was "
                                       + "stopped. The scan may be too large to convert on "
                                       + "this machine.";
                        return result;
                    }

                    string said = LastLine(output.ToString());
                    if (process.ExitCode == 0 && File.Exists(nwcPath))
                    {
                        result.Ok = true;
                        result.Message = said.Length > 0
                            ? said
                            : "Converted " + Path.GetFileName(objPath) + ".";
                        return result;
                    }

                    // A converter that died without saying anything is the case
                    // worth naming rather than reporting as a blank failure.
                    result.Message = said.Length > 0
                        ? said
                        : "The NWC converter stopped with code " + process.ExitCode
                          + " without saying why.";
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.Message = "The NWC converter could not be run: " + ex.Message;
                return result;
            }
        }

        private static string LastLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? "" : lines[lines.Length - 1].Trim();
        }

        /// <summary>
        /// A command-line argument, quoted.
        ///
        /// Site names carry spaces and the workspace lives under a user
        /// profile, so an unquoted path is not a rare case here. Trailing
        /// backslashes are doubled because a backslash before the closing
        /// quote escapes it, which is how a folder path turns into an
        /// unterminated argument.
        /// </summary>
        internal static string Quote(string value)
        {
            if (value == null) return "\"\"";
            int trailing = 0;
            for (int i = value.Length - 1; i >= 0 && value[i] == '\\'; i--) trailing++;
            return "\"" + value + new string('\\', trailing) + "\"";
        }
    }
}
