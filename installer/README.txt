PIXMYD-Nav — Navisworks field marker and AR model bridge
========================================================

Points, Field Marker Export and AR Model Export — the bridge between the
Navisworks model and the PIXMYD phone app.


INSTALL
-------

1. Close Navisworks.
2. Right-click Install.cmd  ->  "Run as administrator"  ->  accept the prompt.
3. Start Navisworks. PIXMYD-Nav appears on the Add-Ins ribbon tab.

The installer copies two files per Navisworks version into

    C:\Program Files\Autodesk\Navisworks Manage <year>\Plugins\PIXMYD-Nav\

That is all it does — mkdir and copy, nothing else.


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