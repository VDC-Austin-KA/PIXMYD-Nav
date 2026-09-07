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
        /// The nwcreate runtime, shipped beside this plugin.
        ///
        /// Not `lcodpnwcreate.dll` from the Navisworks folder, which is the
        /// mistake this replaces. That copy is the *loader* build, and
        /// LiNwcApi.h is explicit about the difference:
        ///
        ///   "These functions must be called when writing an exporter from
        ///    third party software. They should not be called when writing a
        ///    file loader (see LiNwcLoader.h) for NavisWorks."
        ///
        /// A loader does not create its scene -- Navisworks hands it one
        /// through the loader entry point. So the loader build exports no
        /// initialiser, and its LiNwcSceneCreate refuses outright with
        /// "LiNwcSceneCreate: Loader can't create scene". Both of those were
        /// read as puzzles; they were the library saying we had picked the
        /// wrong half of it.
        ///
        /// This plugin is an exporter: it authors a scene from a mesh that
        /// arrived from a phone. So it binds the exporter build, which the
        /// SDK ships and the same header tells us to distribute:
        /// "You should distribute this DLL with your application."
        ///
        /// It is loaded by full path from the plugin's own folder before the
        /// first call -- see NwcWriter.Initialise -- because the plugin
        /// directory is not on the DLL search path. `nwcreate_data` must sit
        /// beside it; that is where nwcreate looks by default, relative to
        /// itself, and it holds the session licence.
        /// </summary>
        internal const string Dll = "nwcreate_21.dll";

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

        /// <summary>
        /// Start the API. Must be called before anything else, and is exported
        /// by the exporter build -- the absence that sent this code down a
        /// blind alley was the loader build's, not nwcreate's.
        ///
        /// nwcreate finds its `nwcreate_data` folder relative to itself, so
        /// this plain form is the right one as long as the data folder ships
        /// beside the DLL. (LiNwcApiInitialiseEx exists for the case where it
        /// does not; we do not need it.)
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern ApiStatus LiNwcApiInitialise();

        /// <summary>
        /// Whether a named licence is available. Exported by the copy inside
        /// Navisworks, unlike the initialiser, and the honest way to answer
        /// "will this be allowed to write" before writing anything.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall,
                   CharSet = CharSet.Ansi)]
        internal static extern ApiStatus LiNwcApiIsLicenseAvailable(
            [MarshalAs(UnmanagedType.LPStr)] string name);

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

        // MARK: - Materials
        //
        // The route a texture takes into an NWC, and it is not the obvious
        // one. LiNwcMaterial carries only colours, and the Presenter material
        // that used to carry maps is marked OBSOLETE in the headers -- every
        // one of its setters says "Does nothing." What is live is the Autodesk
        // shared asset system: a material asset built from a schema, with a
        // texture asset connected to its diffuse slot.
        //
        // The three identifiers below are not recalled. They were read out of
        // assetlibrary_base.adsklib under Common Files\Autodesk Shared, which
        // is a zip of the schema XML: the folder name is the library id, and
        // GenericSchema.xml declares generic_diffuse as a Color with
        // allowconnectedassets="single" -- the connection point -- while
        // UnifiedBitmapSchema.xml declares unifiedbitmap_Bitmap as its
        // TextureURI.

        /// <summary>The Autodesk system asset library. The folder name inside
        /// assetlibrary_base.adsklib.</summary>
        public const string AssetLibrary = "314DE259-5443-4621-BFBD-1730C6CC9AE9";
        /// <summary>The general-purpose material schema.</summary>
        public const string GenericSchema = "GenericSchema";
        /// <summary>The schema for a texture read from an image file.</summary>
        public const string BitmapSchema = "UnifiedBitmapSchema";
        /// <summary>Generic's diffuse colour, which a texture connects to.</summary>
        public const string DiffuseProperty = "generic_diffuse";
        /// <summary>UnifiedBitmap's source file.</summary>
        public const string BitmapProperty = "unifiedbitmap_Bitmap";

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern IntPtr LiNwcAutodeskMaterialCreate();

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcAutodeskMaterialDestroy(IntPtr material);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcAutodeskMaterialSetMaterialAsset(
            IntPtr material, IntPtr asset);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern IntPtr LiNwcAutodeskAssetCreate();

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcAutodeskAssetDestroy(IntPtr asset);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall,
                   CharSet = CharSet.Unicode)]
        internal static extern void LiNwcAutodeskAssetSetLibraryIdentifier(
            IntPtr asset, [MarshalAs(UnmanagedType.LPWStr)] string libraryId);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall,
                   CharSet = CharSet.Unicode)]
        internal static extern void LiNwcAutodeskAssetSetDefinitionIdentifier(
            IntPtr asset, [MarshalAs(UnmanagedType.LPWStr)] string definitionId);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcAutodeskAssetAddData(IntPtr asset, IntPtr data);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern IntPtr LiNwcAutodeskAssetDataCreate();

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcAutodeskAssetDataDestroy(IntPtr data);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall,
                   CharSet = CharSet.Unicode)]
        internal static extern void LiNwcAutodeskAssetDataSetIdentifier(
            IntPtr data, [MarshalAs(UnmanagedType.LPWStr)] string id);

        /// <summary>The image file behind a UnifiedBitmap. A path, not a URL.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall,
                   CharSet = CharSet.Unicode)]
        internal static extern void LiNwcAutodeskAssetDataSetURI(
            IntPtr data, [MarshalAs(UnmanagedType.LPWStr)] string uri);

        /// <summary>Declare that this data is fed by a texture rather than by
        /// its own value. The header: "You also need to specify one or more
        /// connected asset that represents the texture."</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcAutodeskAssetDataSetTexture(IntPtr data);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcAutodeskAssetDataAddConnectedAsset(
            IntPtr data, IntPtr asset);

        /// <summary>C++ `bool` is one byte; the default marshalling of four
        /// would put three bytes of rubbish on the stack.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcAutodeskAssetDataSetConnectedAssetEnabled(
            IntPtr data, [MarshalAs(UnmanagedType.I1)] bool enabled);

        /// <summary>Attach an attribute -- a material, here -- to a node.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern void LiNwcNodeAddAttribute(IntPtr node, IntPtr attribute);
    }
}
