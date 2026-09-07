using System;
using System.IO;
using System.Runtime.InteropServices;
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(
            [MarshalAs(UnmanagedType.LPWStr)] string path);

        /// <summary>Where nwcreate and its data folder live: beside this
        /// assembly, because that is what we ship and what the installer
        /// puts down.</summary>
        private static string PluginFolder()
        {
            try
            {
                string here = typeof(NwcWriter).Assembly.Location;
                return string.IsNullOrEmpty(here) ? "" : Path.GetDirectoryName(here);
            }
            catch (Exception) { return ""; }
        }

        /// <summary>
        /// Start the API once per process.
        ///
        /// The DLL is loaded by full path first. P/Invoke would otherwise
        /// search the process directory, which is Navisworks own, and a plugin
        /// folder is not on that search path. Loading it by hand puts the
        /// right module in the process under the right name, and every
        /// DllImport after this resolves to it.
        /// </summary>
        private static string Initialise()
        {
            lock (_gate)
            {
                if (_initialised) return null;

                string folder = PluginFolder();
                string dll = string.IsNullOrEmpty(folder)
                    ? "" : Path.Combine(folder, NwcApi.Dll);

                if (string.IsNullOrEmpty(dll) || !File.Exists(dll))
                    return "The NWC writer is missing: " + NwcApi.Dll + " should be installed "
                         + "beside the plugin, in " + (string.IsNullOrEmpty(folder)
                             ? "the plugin folder" : folder) + ".";

                // nwcreate looks for this beside itself, and the session
                // licence is in it. Without the folder it starts and then
                // fails later, somewhere much less legible.
                string data = Path.Combine(folder, "nwcreate_data");
                if (!Directory.Exists(data))
                    return "The NWC writer is missing its nwcreate_data folder, which has to "
                         + "sit beside " + NwcApi.Dll + " in " + folder + ".";

                IntPtr module = LoadLibraryW(dll);
                if (module == IntPtr.Zero)
                    return "Windows would not load " + dll + " (error "
                         + Marshal.GetLastWin32Error() + ").";

                NwcApi.ApiStatus status;
                try
                {
                    // Order, and it is load-bearing. The header says
                    // LiNwcApiInitialise brings up error handling itself;
                    // calling it without this first is an access violation
                    // rather than a status code, which is what took the host
                    // application down when this ran in-process. Autodesk's own
                    // C exporter opens its main() with exactly this line.
                    //
                    // The Ex forms take the module nwcreate should find
                    // `nwcreate_data` beside. The plain forms work that out
                    // from nwcreate's own location, which is one more thing to
                    // be wrong about when the DLL was loaded by hand from a
                    // path the loader never searched -- and we are holding the
                    // handle already.
                    NwcApi.LiNwcApiErrorInitialiseEx(module);
                    status = NwcApi.LiNwcApiInitialiseEx(module);
                }
                catch (DllNotFoundException)
                {
                    return "Windows loaded " + NwcApi.Dll + " but .NET could not bind to it.";
                }
                catch (EntryPointNotFoundException ex)
                {
                    // The old failure, worth naming rather than swallowing: it
                    // means the loader build got loaded instead of the exporter
                    // build, and the loader build cannot create a scene at all.
                    return "The nwcreate beside this plugin is the loader build, which cannot "
                         + "author a scene. Install the exporter build from the Navisworks "
                         + "SDK (" + NwcApi.Dll + "). " + ex.Message;
                }

                if (status != NwcApi.ApiStatus.Ok)
                {
                    return status == NwcApi.ApiStatus.NotLicensed
                        ? "nwcreate is not licensed on this machine, so it will not write an NWC."
                        : "The NWC writer would not start (status " + status + ").";
                }
                _initialised = true;
                return null;
            }
        }

        /// <summary>
        /// Attach the atlas to a node as a material, or do nothing when there
        /// is no atlas to attach.
        ///
        /// Two assets, connected. A Generic material has a diffuse slot that
        /// takes either a colour or a connected texture; a UnifiedBitmap is a
        /// texture that names an image file. Connecting the second into the
        /// first is what puts the photograph on the mesh instead of a flat
        /// grey. LiNwcMaterial cannot do it -- it carries colours only -- and
        /// the Presenter material that used to is marked OBSOLETE, every
        /// setter documented as "Does nothing."
        ///
        /// Every handle created here is destroyed here. nwcreate reference
        /// counts, so destroying after attaching releases our claim on the
        /// object rather than the object.
        /// </summary>
        private static void Paint(IntPtr node, NwcMesh mesh)
        {
            if (!mesh.HasTexture) return;

            IntPtr bitmap = IntPtr.Zero, bitmapFile = IntPtr.Zero;
            IntPtr material = IntPtr.Zero, generic = IntPtr.Zero, diffuse = IntPtr.Zero;
            try
            {
                bitmapFile = NwcApi.LiNwcAutodeskAssetDataCreate();
                if (bitmapFile == IntPtr.Zero) return;
                NwcApi.LiNwcAutodeskAssetDataSetIdentifier(bitmapFile, NwcApi.BitmapProperty);
                NwcApi.LiNwcAutodeskAssetDataSetURI(bitmapFile, mesh.TexturePath);

                bitmap = NwcApi.LiNwcAutodeskAssetCreate();
                if (bitmap == IntPtr.Zero) return;
                NwcApi.LiNwcAutodeskAssetSetLibraryIdentifier(bitmap, NwcApi.AssetLibrary);
                NwcApi.LiNwcAutodeskAssetSetDefinitionIdentifier(bitmap, NwcApi.BitmapSchema);
                NwcApi.LiNwcAutodeskAssetAddData(bitmap, bitmapFile);

                diffuse = NwcApi.LiNwcAutodeskAssetDataCreate();
                if (diffuse == IntPtr.Zero) return;
                NwcApi.LiNwcAutodeskAssetDataSetIdentifier(diffuse, NwcApi.DiffuseProperty);
                NwcApi.LiNwcAutodeskAssetDataSetTexture(diffuse);
                NwcApi.LiNwcAutodeskAssetDataAddConnectedAsset(diffuse, bitmap);
                NwcApi.LiNwcAutodeskAssetDataSetConnectedAssetEnabled(diffuse, true);

                generic = NwcApi.LiNwcAutodeskAssetCreate();
                if (generic == IntPtr.Zero) return;
                NwcApi.LiNwcAutodeskAssetSetLibraryIdentifier(generic, NwcApi.AssetLibrary);
                NwcApi.LiNwcAutodeskAssetSetDefinitionIdentifier(generic, NwcApi.GenericSchema);
                NwcApi.LiNwcAutodeskAssetAddData(generic, diffuse);

                material = NwcApi.LiNwcAutodeskMaterialCreate();
                if (material == IntPtr.Zero) return;
                NwcApi.LiNwcAutodeskMaterialSetMaterialAsset(material, generic);
                NwcApi.LiNwcNodeAddAttribute(node, material);
            }
            catch (Exception)
            {
                // A mesh with no material is a worse model, not a failed one.
                // The geometry is already written by the time this runs.
            }
            finally
            {
                Free(NwcApi.LiNwcAutodeskMaterialDestroy, material);
                Free(NwcApi.LiNwcAutodeskAssetDestroy, generic);
                Free(NwcApi.LiNwcAutodeskAssetDestroy, bitmap);
                Free(NwcApi.LiNwcAutodeskAssetDataDestroy, diffuse);
                Free(NwcApi.LiNwcAutodeskAssetDataDestroy, bitmapFile);
            }
        }

        private static void Free(Action<IntPtr> destroy, IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try { destroy(handle); } catch (Exception) { }
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

                Paint(geometry, mesh);

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
                               + mesh.TriangleCount + " triangles"
                               + (mesh.HasTexture
                                    ? ", textured with " + Path.GetFileName(mesh.TexturePath)
                                    : mesh.HasColors ? ", vertex coloured" : "") + ".";
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
