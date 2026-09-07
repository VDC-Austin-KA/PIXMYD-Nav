# PIXMYD-Nav

A Navisworks add-in: place control points on a model, print field markers, send
the model to a phone, and take a scan back.

The other half is [PIXMYD](https://github.com/VDC-Austin-KA/PIXMYD), an iOS
capture app. Between them a scan goes onto a model and a model goes onto a site,
and the coordinates survive the journey both ways.

**Status: builds against the 2024–2027 managed API and is covered by an offline
test suite. It has not been run inside Navisworks on a real federated model.**
See [What has and has not been exercised](#what-has-and-has-not-been-exercised)
before planning around it.

---

## One folder per model

Everything the add-in touches lives under a single PIXMYD folder that you pick
once:

```
<your folder>/
  EXPORT/                  everything produced for the phone
    points.json            the control points
    markers.html           one printable A4 page per point, with its QR
    points-markers.dxf     the same points as 3D geometry
    ar-model.json          bounding box, camera, provenance
    ar-model.glb           the model geometry, when included
    P001_photo.png …       the reference shot for each point
  IMPORT/                  everything that arrives from the phone
    20260823-141502-3f9c1a2b/
      capture.json         the scan's registration and provenance
      capture.fbx          the mesh
      points.json          the points the crew placed on site
      placement.txt        the transform, in a form a person can read
```

EXPORT is flat and overwritten — it is the current state of this model's
hand-off, and a folder that accumulates `points-2.json` is a folder where
somebody prints the wrong markers. IMPORT is one dated directory per arrival,
because a capture is a record of a moment on site and the second scan of a room
does not supersede the first.

Arrivals used to land in `%TEMP%`. That is the worst possible home for the one
artefact a coordinator has to keep: invisible from the folder they were told to
look in, and eventually deleted by Windows.

## Placing points

Turn on **Pick points** and click in the model. One click, one point.

The click is Navisworks' own measure pick, so it runs the application's own
snapping — vertex, edge, line vertex, whatever is set in Options — against the
real tessellation, with the highlight drawn under the cursor. The add-in then
pulls the result onto the nearest **corner**, falling through to **edge**, then
**face**, then the raw pick, and the list says which answered and how far the
pick moved.

Corners first because that is what a control point is. The previous version
captured the current selection instead, one point per item at its bounding-box
centre — which is inside the steel, where nobody can put a tape.

The geometry behind the snap comes from the COM primitive bridge
(`InwOaFragment3.GenerateSimplePrimitives`), which also reports the snap points
the model itself publishes. Those beat a bare mesh vertex: a tessellated
cylinder is covered in vertices that are not corners of anything.

### The markers are real geometry

**Write and show in model** turns the point list into `points-markers.dxf` — a
sphere or a cross per point, each on its own layer named for the point — and
appends it to the open document. So a point is visible in the viewport,
selectable in the tree by name, and present in anything the model is exported
to. A DXF `POINT` entity carries the coordinate; the solid beside it is what
makes it findable.

DXF rather than FBX because a layer name is how a point id survives a file
format: `P007` in the list is `PIXMYD_P007` in the tree, and an STL would be one
anonymous blob.

### The gizmo is Navisworks'

Once a marker is in the model it can be dragged with **Item Tools › Move**, with
the application's own snapping. **Read back gizmo moves** folds whatever moved
into the point's coordinate. The read is idempotent — pressing it twice does not
move a point twice — and the nudge buttons cover the millimetre a drag cannot.

## Taking a scan back

**Show transfer code** puts a QR on screen. The phone scans it, pulls EXPORT
over the local network, and sends a scan back into IMPORT. Nothing leaves the
machine until the phone asks, and the session closes with the window.

Progress is reported in bytes, not files: the return leg is a `capture.json` of
a few kilobytes and a mesh of a few hundred megabytes, and a bar driven by files
completed sits still for the entire transfer and then finishes.

When the scan arrives, **Review and place** shows the fit — RMS, max residual,
the construction tolerance band it falls in, and how much redundancy the point
count actually leaves — and asks. A grade below survey tolerance defaults the
dialog to No. Then the mesh is appended and the appended model's transform is
set, so the scan lands where the solve says it belongs.

### Two points are enough

Both frames know which way down is: ARKit runs gravity-aligned and a model
states its up axis. Holding the vertical removes roll and pitch and leaves
heading and three translations, which two points over-determine.

This is not a lower-quality answer — gravity from an IMU is better conditioned
than roll and pitch fitted from three hand-aimed picks. What two points cannot
do is tell you when one of them is wrong: the fit reports near-zero error either
way. The review dialog says exactly that beside the number.

### Points can start on the phone

A crew walking a space for the first time has no model points to find. They
place points on the phone while scanning, and the scan comes home with a
`points.json` naming them. **Place the phone's points** puts those ids into the
Points tab as empty rows, and picking works down the list in order: click the
same feature on the model, and P001 fills in, then P002. When they are all
placed, the registration is by id.

## AR model export

`ar-model.json` carries the whole-model bounding box, the current camera and the
provenance, shifted so the box's minimum corner sits at the origin. With
geometry included it also writes `ar-model.glb`, tessellated out of Navisworks
through the same COM bridge the snapping uses, so the phone can draw the model
over the room rather than only say where it is.

GLB going out and FBX coming back, because the two directions have different
consumers: a phone loads GLB without a converter, and a Navisworks plugin can
only add geometry by appending a file in a format Navisworks reads.

## Building

Local development against an installed Navisworks:

```
msbuild PIXMYD-Nav\PIXMYD-Nav.csproj /p:Configuration=Release-NW2027 /p:Platform=x64
```

### Installing

The plugin folder has to carry three things, not one:

```
<Navisworks>\Plugins\PIXMYD-Nav\
    PIXMYD-Nav.dll
    nwcreate_21.dll
    nwcreate_data\
```

`installer/V25` holds all of them, and a local build copies them to its output.

`nwcreate_21.dll` is the exporter build of nwcreate, from the Navisworks SDK.
It is deliberately *not* the `lcodpnwcreate.dll` already sitting in the
Navisworks directory: that one is the loader build, and `LiNwcApi.h` is plain
about the difference — "These functions must be called when writing an exporter
from third party software. They should not be called when writing a file
loader." A loader is handed its scene by the host, so the loader build exports
no initialiser and its `LiNwcSceneCreate` refuses with "Loader can't create
scene". Binding to it cost a long time; the header said so all along.

`nwcreate_data` is found by nwcreate relative to itself and holds the session
licence, so it travels beside the DLL or nothing works.

Everything can also be checked with no Windows and no Navisworks at all:

```
# compiles every source file against the managed API reference assemblies
python tools/typecheck/gen_xaml_stub.py
dotnet build tools/typecheck/typecheck.csproj

# runs the pure-logic suite
dotnet build tools/writer-tests/WriterTests.csproj -v minimal -o artifacts/bin
dotnet exec artifacts/bin/WriterTests.dll
```

Pass `-p:NWPackageVersion=2025.0.0` to typecheck against a specific year.

## What has and has not been exercised

Covered by `tools/writer-tests`, on any platform:

- the snap cascade, and the closest-point geometry under it
- the gravity-constrained solve, against the same vectors the iOS suite uses
- the transform decomposition, including the basis change Navisworks' FBX
  reader applies on the way in — a test that caught a real sign error
- the DXF and GLB writers, read back structurally
- the workspace layout and its migration
- the points and capture readers, against the exact bytes the phone writes

Not covered, and worth knowing:

- **Nothing here has run inside Navisworks.** The managed API is checked by the
  compiler against the real reference assemblies, which catches a wrong method
  and cannot catch a wrong assumption about behaviour.
- **`points-markers.dxf` has not been opened by Navisworks' DWG/DXF reader.**
  It is R12 ASCII, which is the most widely readable DXF there is, and the tests
  read it back line by line — but that is not the same as an import succeeding.
- **The FBX round trip has not been measured end to end.** The reader's Y-up to
  Z-up turn is undone explicitly and tested as arithmetic; whether a real
  Navisworks import agrees has not been observed.
- **Placing an appended model** goes through `SetModelUnitsAndTransform`, with a
  fall back to overriding the root item's transform. Both are reported honestly
  when they fail rather than leaving a scan somewhere plausible.

## Layout

```
PIXMYD-Nav/Core/Workspace    the EXPORT / IMPORT folder layout
PIXMYD-Nav/Core/Points       the point set, its reader, and the snap solver
PIXMYD-Nav/Core/Markers      QR encoding, the printable page, marker geometry
PIXMYD-Nav/Core/Capture      registration, tolerance bands, transform maths
PIXMYD-Nav/Core/Transfer     the pairing ticket, manifest and local server
PIXMYD-Nav/Core/Ar           the AR bundle and its GLB writer
PIXMYD-Nav/Core/Nwc          the OBJ reader and the NWC writer over nwcreate
PIXMYD-Nav/Core/NavBridge    everything that touches Autodesk.Navisworks.Api
PIXMYD-Nav/Core/Json         a dependency-free JSON reader
tools/typecheck              compiles the whole add-in without Navisworks
tools/writer-tests           the offline suite
installer                    per-year bundle manifests
```

`Core/NavBridge` is the only directory that imports the Navisworks API.
Everything else is arithmetic and string building, which is why the test suite
can cover it from a Linux runner.
