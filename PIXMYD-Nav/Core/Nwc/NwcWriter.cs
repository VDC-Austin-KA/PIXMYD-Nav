using System;
using System.IO;
using PIXMYD_Nav.Core.Points;

namespace PIXMYD_Nav.Core.Nwc
{
    /// <summary>
    /// Writes a mesh as an NWC, through nwcreate.
    ///
    /// This is the thin half: everything that decides what gets written is in
    /// <see cref="NwcMesh"/>, where it is tested. What is left here is the
    /// sequence of native calls and the discipline of undoing them, which
    /// cannot be exercised without the runtime and so is kept as small and as
    /// obvious as it can be made.
    ///
    /// ## Why NWC at all
    ///
    /// Appending an NWC is a load rather than a translation -- it is the format
    /// Navisworks writes for its own cache, so its reader is the one path that
    /// is never the weak link. FBX has to survive a reader this suite does not
    /// control and has spent a long time failing to.
    ///
    /// ## Every exit path frees
    ///
    /// nwcreate handles are native allocations in the host's process. A leak
    /// here is not a leak in a tool that exits; it is a leak in the user's
    /// Navisworks session, which stays open all day. So the scene and the
    /// geometry are freed on every path, including the failure ones, and the
    /// stream is closed before the geometry that owns it.
    /// </summary>
    public static class NwcWriter
    {
        public sealed class Result
        {
            public bool Ok;
            public string Message = "";
            public string Path = "";
        }

        private static bool _initialised;
        private static readonly object _gate = new object();

        /// <summary>
        /// Start the API once per process.
        ///
        /// `LiNwcApiInitialise` must be called before anything else and is not
        /// re-entrant, and this plugin can be driven from more than one button,
        /// so the flag and the lock are both load-bearing.
        /// </summary>
        private static string Initialise()
        {
            lock (_gate)
            {
                if (_initialised) return null;
                NwcApi.ApiStatus status;
                try
                {
                    status = NwcApi.LiNwcApiInitialise();
                }
                catch (DllNotFoundException)
                {
                    return "The nwcreate library that writes NWC files is not in this "
                         + "Navisworks installation.";
                }
                catch (EntryPointNotFoundException ex)
                {
                    return "The nwcreate library is present but does not have the entry point "
                         + "this plugin expects: " + ex.Message;
                }

                if (status != NwcApi.ApiStatus.Ok)
                {
                    return status == NwcApi.ApiStatus.NotLicensed
                        ? "Navisworks did not license the NWC writer on this machine."
                        : "The NWC writer would not start (status " + status + ").";
                }
                _initialised = true;
                return null;
            }
        }

        /// <summary>Write <paramref name="mesh"/> to <paramref name="path"/>.</summary>
        public static Result Write(NwcMesh mesh, string path)
        {
            var result = new Result { Path = path ?? "" };
            if (mesh == null) { result.Message = "There is no mesh to write."; return result; }

            string wrong = mesh.Problem();
            if (wrong != null) { result.Message = wrong; return result; }

            string unstarted = Initialise();
            if (unstarted != null) { result.Message = unstarted; return result; }

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                result.Message = "Could not create the folder for " + path + ": " + ex.Message;
                return result;
            }

            IntPtr scene = IntPtr.Zero, geometry = IntPtr.Zero;
            try
            {
                scene = NwcApi.LiNwcSceneCreate();
                if (scene == IntPtr.Zero)
                {
                    result.Message = "The NWC writer would not create a scene.";
                    return result;
                }
                NwcApi.LiNwcSceneSetLinearUnits(scene, mesh.Units);

                geometry = NwcApi.LiNwcGeometryCreate();
                if (geometry == IntPtr.Zero)
                {
                    result.Message = "The NWC writer would not create a geometry node.";
                    return result;
                }
                NwcApi.LiNwcNodeSetName(geometry, mesh.Name ?? "PIXMYD scan");

                IntPtr stream = NwcApi.LiNwcGeometryOpenStream(geometry);
                if (stream == IntPtr.Zero)
                {
                    result.Message = "The NWC writer would not open a geometry stream.";
                    return result;
                }

                NwcApi.VertexProperty properties = mesh.Properties;
                NwcApi.LiNwcGeometryStreamBegin(stream, properties);

                bool normals = (properties & NwcApi.VertexProperty.Normal) != 0;
                bool colors = (properties & NwcApi.VertexProperty.Color) != 0;
                bool uvs = (properties & NwcApi.VertexProperty.TexCoord) != 0;

                foreach (NwcMesh.Corner corner in mesh.Corners())
                {
                    // Order matters: the properties of a vertex are set before
                    // the vertex itself, and the vertex call is what commits
                    // them. Writing the position first attaches the previous
                    // corner's colour to this one.
                    if (normals)
                        NwcApi.LiNwcGeometryStreamNormal(
                            stream, corner.Normal.X, corner.Normal.Y, corner.Normal.Z);
                    if (colors)
                        NwcApi.LiNwcGeometryStreamColor(
                            stream, corner.Color.X, corner.Color.Y, corner.Color.Z, 1.0);
                    if (uvs)
                        NwcApi.LiNwcGeometryStreamTexCoord(stream, corner.U, corner.V);

                    NwcApi.LiNwcGeometryStreamTriangleVertex(
                        stream, corner.Position.X, corner.Position.Y, corner.Position.Z);
                }

                NwcApi.LiNwcGeometryStreamEnd(stream);
                NwcApi.LiNwcGeometryCloseStream(geometry, stream);

                // The scene takes the node; it is not ours to destroy after
                // this, which is why `geometry` is cleared rather than freed
                // in the finally below.
                NwcApi.LiNwcSceneAddNode(scene, geometry);
                geometry = IntPtr.Zero;

                NwcApi.WriteStatus written = NwcApi.LiNwcSceneWrite(
                    scene, path, IntPtr.Zero, IntPtr.Zero);
                if (written != NwcApi.WriteStatus.Ok)
                {
                    result.Message = "The NWC writer refused to write the file (status "
                                   + written + ").";
                    return result;
                }

                result.Ok = true;
                result.Message = "Wrote " + Path.GetFileName(path) + " — "
                               + mesh.TriangleCount + " triangles.";
                return result;
            }
            catch (Exception ex)
            {
                result.Message = "The NWC writer failed: " + ex.Message;
                return result;
            }
            finally
            {
                if (geometry != IntPtr.Zero)
                {
                    try { NwcApi.LiNwcGeometryDestroy(geometry); } catch (Exception) { }
                }
                if (scene != IntPtr.Zero)
                {
                    try { NwcApi.LiNwcSceneDestroy(scene); } catch (Exception) { }
                }
            }
        }
    }
}
