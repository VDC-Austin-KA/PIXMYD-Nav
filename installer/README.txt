PIXMYD-Nav — Navisworks field marker and AR model bridge
========================================================

Points, Field Marker Export and AR Model Export — the bridge between the
Navisworks model and the PIXMYD phone app.


INSTALL
-------

1. Close Navisworks.
2. Right-click Install.cmd  ->  "Run as administrator"  ->  accept the prompt.
3. Start Navisworks. PIXMYD-Nav appears on the Add-Ins ribbon tab.

The installer copies the plugin, and the OBJ-to-NWC converter it runs, into

    C:\Program Files\Autodesk\Navisworks Manage <year>\Plugins\PIXMYD-Nav\

That is all it does — mkdir and copy, nothing else.

The converter is obj2nwc.exe, with nwcreate_21.dll and the nwcreate_data
folder beside it. It is a separate program because nwcreate cannot be loaded
into Navisworks itself, so exporting a scan as NWC runs it and reads back what
it says. Without it the plugin still runs; scan export is what stops working.


MANUAL INSTALL
--------------

For each Navisworks Manage year you have:

1. Create   C:\Program Files\Autodesk\Navisworks Manage <year>\Plugins\PIXMYD-Nav\
2. Copy PIXMYD-Nav.dll and PIXMYD-Nav.addin from the matching folder in this
   download:

       V24  ->  Navisworks Manage 2024
       V25  ->  Navisworks Manage 2025
       V26  ->  Navisworks Manage 2026
       V27  ->  Navisworks Manage 2027

3. Copy everything in Converter\ into the same folder — obj2nwc.exe,
   obj2nwc.exe.config, nwcreate_21.dll and the nwcreate_data folder. One build
   serves every year, and all four have to sit together: the plugin looks for
   the converter beside itself, and nwcreate looks for nwcreate_data beside
   its own DLL.


UNINSTALL
---------

Run Uninstall.cmd as administrator, or delete the Plugins\PIXMYD-Nav folder
from each Navisworks install by hand.


QUICK START
-----------

1. Add-Ins tab  ->  PIXMYD-Nav
2. Place points in the model.
3. Export field marker pages (QR code + coordinates + grid intersection).
4. Export an AR model bundle for the PIXMYD phone app.