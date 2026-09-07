using System;
using System.Runtime.InteropServices;

namespace PIXMYD_Nav.Core.Nwc
{
    /// <summary>
    /// The nwcreate C API, declared.
    ///
    /// NWC is the format Navisworks reads best, because it is the format
    /// Navisworks writes: appending one is a load, not a translation. Nothing
    /// else in this suite can produce one -- the managed API cannot author
    /// geometry at all, and FBX has to survive a reader we do not control.
    /// nwcreate is Autodesk's supported way in, and it is what every exporter
    /// they ship is built on.
    ///
    /// ## Why P/Invoke and not a wrapper
    ///
    /// The headers offer two faces: a flat C API (`LiNwc*`) and C++ classes
    /// (`LcNwc*`) that inline over it. The C API is `__stdcall` with opaque
    /// handles and scalar arguments, which is exactly what P/Invoke marshals
    /// without help. The C++ face would need a C++/CLI shim -- another binary
    /// to build, sign and ship into Navisworks' process for no gain.
    ///
    /// ## Every signature here was read out of the 2024 headers
    ///
    /// Not recalled, not inferred from the export names. A wrong signature
    /// against a native x64 API does not throw; it corrupts the stack inside
    /// the host application, and the host here is the user's Navisworks with
    /// their model open. `LtFloat` is `double`, not `float` -- the one that
    /// would look right and be wrong in every call below.
    ///
    /// Sources, all under api/nwcreate/include/nwcreate:
    ///   LiNwcPublic.h        LI_NWC_API is __stdcall
    ///   LiNwcTypes.h         LtFloat = double, LtInt32 = int, LtNat32 = uint
    ///   LiNwcApi.h           LiNwcApiInitialise, LtNwcApiStatus
    ///   LiNwcScene.h         Create/Destroy/AddNode/SetLinearUnits/Write
    ///   LiNwcGeometry.h      Create/OpenStream/CloseStream
    ///   LiNwcGeometryStream.h Begin/Color/Normal/TexCoord/TriangleVertex/End
    ///   LiNwcNode.h          SetName
    ///
    /// Navisworks-only, and untestable offline: nothing here can be exercised
    /// without the runtime. What *is* tested is everything that decides what
    /// gets fed to it -- see NwcMesh and its tests.
    /// </summary>
    public static class NwcApi
    {
        /// <summary>
        /// The nwcreate runtime, which ships inside Navisworks itself.
        ///
        /// No version suffix and no path: the SDK download carries
        /// `nwcreate_21.dll`, but Navisworks' own install directory carries
        /// `lcodpnwcreate.dll` with the same exports, and that is the copy
        /// already loaded in the process this code runs in. Binding to the
        /// installed one means the plugin has no redistributable to ship and
        /// no version to keep in step with the host.
        /// </summary>
        private const string Dll = "lcodpnwcreate.dll";

        // LiNwcApi.h: LtNwcApiStatus
        public enum ApiStatus
        {
            Ok = 0,
            NotLicensed = 1,
            InternalError = 2,
        }

        // LiNwcScene.h: LtNwcWriteStatus. Only the values this code reacts to
        // are named; the rest are reported by number.
        public enum WriteStatus
        {
            Ok = 0,
        }

        // LiNwcScene.h: LtNwcLinearUnits, in declaration order.
        public enum LinearUnits
        {
            Meters = 0,
            Centimeters = 1,
            Millimeters = 2,
            Feet = 3,
            Inches = 4,
            Yards = 5,
            Kilometers = 6,
            Miles = 7,
            Micrometers = 8,
        }

        // LiNwcGeometryStream.h: LtNwcVertexProperty, a bitfield.
        [Flags]
        public enum VertexProperty : uint
        {
            None = 0x0,
            Normal = 0x1,
            Color = 0x2,
            TexCoord = 0x4,
        }

        // MARK: - Lifetime

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern ApiStatus LiNwcApiInitialise();

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcApiTerminate();

        // MARK: - Scene

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern IntPtr LiNwcSceneCreate();

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcSceneDestroy(IntPtr scene);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcSceneSetLinearUnits(IntPtr scene, LinearUnits units);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcSceneAddNode(IntPtr scene, IntPtr node);

        /// <summary>
        /// Write the scene. `filename` is `LtWideString`, so UTF-16 -- which is
        /// also why every path this is handed can carry the site names people
        /// actually use.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall,
                   CharSet = CharSet.Unicode)]
        internal static extern WriteStatus LiNwcSceneWrite(
            IntPtr scene,
            [MarshalAs(UnmanagedType.LPWStr)] string filename,
            IntPtr progressCallback,
            IntPtr userData);

        // MARK: - Geometry

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern IntPtr LiNwcGeometryCreate();

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcGeometryDestroy(IntPtr geometry);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern IntPtr LiNwcGeometryOpenStream(IntPtr geometry);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcGeometryCloseStream(IntPtr geometry, IntPtr stream);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall,
                   CharSet = CharSet.Unicode)]
        internal static extern void LiNwcNodeSetName(
            IntPtr node, [MarshalAs(UnmanagedType.LPWStr)] string name);

        // MARK: - Geometry stream
        //
        // LtFloat is double throughout. Declaring these as float compiles,
        // links, runs, and puts garbage in the model.

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcGeometryStreamBegin(
            IntPtr stream, VertexProperty vertexProperties);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcGeometryStreamEnd(IntPtr stream);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcGeometryStreamColor(
            IntPtr stream, double r, double g, double b, double a);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcGeometryStreamNormal(
            IntPtr stream, double x, double y, double z);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcGeometryStreamTexCoord(
            IntPtr stream, double x, double y);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcGeometryStreamTriangleVertex(
            IntPtr stream, double x, double y, double z);
    }
}
