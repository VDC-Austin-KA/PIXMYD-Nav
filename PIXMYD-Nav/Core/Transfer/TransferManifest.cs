using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PIXMYD_Nav.Core.Transfer
{
    /// <summary>
    /// The JSON bodies of docs/contracts/transfer.md, and the rules that decide
    /// whether a request is allowed to touch the filesystem.
    ///
    /// Split from the HttpListener deliberately. Everything that can be got
    /// wrong -- which names are safe, what a session is allowed to offer, what a
    /// commit answers -- is here where WriterTests can reach it. The server is
    /// the part that cannot be tested without binding a port, so it is kept to
    /// plumbing with no decisions in it.
    ///
    /// Pure. In WriterTests.csproj.
    /// </summary>
    public sealed class TransferFileEntry
    {
        /// <summary>Bundle-relative, forward slashes.</summary>
        public string Name;
        public long Bytes;

        public TransferFileEntry(string name, long bytes)
        {
            Name = name;
            Bytes = bytes;
        }
    }

    /// <summary>Which way the bytes are going.</summary>
    public enum TransferDirection
    {
        Sending = 0,
        Receiving = 1
    }

    /// <summary>
    /// How far through one file a transfer is.
    ///
    /// Bytes, not files. The two legs of this protocol are lopsided -- an
    /// export is a JSON and a handful of PNGs, a return leg is a JSON and a
    /// mesh three orders of magnitude larger -- so a bar driven by files
    /// completed tells the user nothing during the only part that takes time.
    ///
    /// Pure. In WriterTests.csproj.
    /// </summary>
    public sealed class TransferProgress
    {
        public readonly TransferDirection Direction;
        public readonly string FileName;
        public readonly long BytesDone;
        public readonly long BytesTotal;

        public TransferProgress(TransferDirection direction, string fileName, long done, long total)
        {
            Direction = direction;
            FileName = fileName ?? "";
            BytesDone = done < 0 ? 0 : done;
            BytesTotal = total < 0 ? 0 : total;
        }

        /// <summary>0 to 1. Zero total reads as zero rather than as complete:
        /// an empty file is not evidence that the transfer finished.</summary>
        public double Fraction
        {
            get
            {
                if (BytesTotal <= 0) return 0;
                double value = (double)BytesDone / BytesTotal;
                return value < 0 ? 0 : (value > 1 ? 1 : value);
            }
        }

        public bool IsComplete { get { return BytesTotal > 0 && BytesDone >= BytesTotal; } }

        /// <summary>One line for a status bar.</summary>
        public string Describe()
        {
            string verb = Direction == TransferDirection.Sending ? "Sending" : "Receiving";
            return verb + " " + FileName + " -- " + Bytes(BytesDone) + " of " + Bytes(BytesTotal);
        }

        public static string Bytes(long value)
        {
            if (value < 1024) return value.ToString(CultureInfo.InvariantCulture) + " B";
            if (value < 1024 * 1024)
                return (value / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            if (value < 1024L * 1024 * 1024)
                return (value / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
            return (value / (1024.0 * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        }
    }

    public sealed class TransferOffer
    {
        public string Name;
        /// <summary>"points", "ar-model" or "both".</summary>
        public string Kind;
        public List<TransferFileEntry> Files = new List<TransferFileEntry>();
    }

    public static class TransferManifest
    {
        public const string ContractVersion = "1.0";

        /// <summary>
        /// Render the GET /session body.
        ///
        /// contractVersion is written first: the guest probes it before it
        /// commits to the rest of the schema, exactly as every other contract
        /// file in the suite is read.
        /// </summary>
        public static string Session(
            string sessionId,
            string host,
            string document,
            DateTime expiresUtc,
            TransferOffer download,
            bool acceptsUpload,
            long maxUploadBytes)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            Member(sb, "contractVersion", ContractVersion, true);
            Member(sb, "sessionId", sessionId, false);
            Member(sb, "host", host, false);
            Member(sb, "document", document, false);
            Member(sb, "expiresUtc", Iso8601(expiresUtc), false);

            sb.Append(",\"download\":");
            if (download == null || download.Files.Count == 0)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append('{');
                Member(sb, "name", download.Name, true);
                Member(sb, "kind", download.Kind, false);
                sb.Append(",\"files\":[");
                for (int i = 0; i < download.Files.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('{');
                    Member(sb, "name", download.Files[i].Name, true);
                    sb.Append(",\"bytes\":")
                      .Append(download.Files[i].Bytes.ToString(CultureInfo.InvariantCulture));
                    sb.Append('}');
                }
                sb.Append(']');
                sb.Append('}');
            }

            sb.Append(",\"upload\":{\"accepted\":").Append(acceptsUpload ? "true" : "false");
            sb.Append(",\"maxBytes\":").Append(maxUploadBytes.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');

            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Render the POST /capture/commit body.</summary>
        public static string CommitResult(bool accepted, string captureId, string message)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            Member(sb, "contractVersion", ContractVersion, true);
            sb.Append(",\"accepted\":").Append(accepted ? "true" : "false");
            if (!string.IsNullOrEmpty(captureId)) Member(sb, "captureId", captureId, false);
            Member(sb, "message", message, false);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Whether a name from a request may become a path component.
        ///
        /// Rejected before it reaches the filesystem, not after. The list is the
        /// contract's: no "..", no backslash, no drive letter, no leading slash,
        /// no empty segment, no control characters.
        /// </summary>
        public static bool IsSafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (Encoding.UTF8.GetByteCount(name) > 255) return false;
            if (name.IndexOf('\\') >= 0) return false;
            if (name.IndexOf(':') >= 0) return false;
            if (name[0] == '/') return false;

            foreach (char c in name)
                if (c < 0x20) return false;

            string[] parts = name.Split('/');
            foreach (string part in parts)
            {
                if (part.Length == 0) return false;
                if (part == "." || part == "..") return false;
                // Trailing dots and spaces are stripped by Windows, so "a. "
                // and "a" would name the same file on the host but not on the
                // guest. Refuse rather than let the two ends disagree.
                if (part[part.Length - 1] == '.' || part[part.Length - 1] == ' ') return false;
            }
            return true;
        }

        /// <summary>
        /// The kind string for a folder holding these files.
        /// </summary>
        public static string KindFor(bool hasPoints, bool hasArModel)
        {
            if (hasPoints && hasArModel) return "both";
            if (hasArModel) return "ar-model";
            return "points";
        }

        /// <summary>Matches the format every other contract file uses.</summary>
        public static string Iso8601(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'.'fff'Z'", CultureInfo.InvariantCulture);
        }

        private static void Member(StringBuilder sb, string name, string value, bool first)
        {
            if (!first) sb.Append(',');
            sb.Append('"').Append(Escape(name)).Append("\":\"").Append(Escape(value ?? "")).Append('"');
        }

        private static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
