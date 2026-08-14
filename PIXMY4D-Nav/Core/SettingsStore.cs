using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PIXMY4D_Nav.Core
{
    /// <summary>
    /// Persists simple settings between sessions as a flat key=value text file
    /// under %AppData%\PIXMY4D-Nav\settings.txt.
    ///
    /// Deliberately not JSON: it round-trips without a parser dependency, survives
    /// hand-editing, and a corrupt or half-written file degrades to defaults
    /// instead of throwing on load. Mirrors NavEx's Core/SettingsStore.cs.
    /// </summary>
    public static class SettingsStore
    {
        private static string RootFolder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PIXMY4D-Nav");
            }
        }

        private static string DefaultPath { get { return Path.Combine(RootFolder, "settings.txt"); } }

        public static void Save(IDictionary<string, string> values) { Save(values, DefaultPath); }

        public static Dictionary<string, string> Load() { return Load(DefaultPath); }

        public static void Save(IDictionary<string, string> values, string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? RootFolder);

                var sb = new StringBuilder();
                foreach (var kvp in values)
                    sb.AppendLine(kvp.Key + "=" + kvp.Value);

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // A failed save should never block the caller; settings are a
                // convenience, not a requirement.
            }
        }

        public static Dictionary<string, string> Load(string path)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(path)) return values;

                foreach (string line in File.ReadAllLines(path))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
                }
            }
            catch (Exception)
            {
                // A corrupt or half-written file degrades to defaults, not a crash.
            }
            return values;
        }
    }
}
