@echo off
REM ---------------------------------------------------------------------------
REM  PIXMYD-Nav installer
REM
REM  Copies the plugin, and the OBJ-to-NWC converter it runs, into the Plugins
REM  folder of every installed Navisworks Manage 2024 / 2025 / 2026 / 2027.
REM
REM  The plugin is per-year and comes out of V24..V27. The converter is not:
REM  obj2nwc.exe is a standalone exporter rather than a Navisworks plugin, so
REM  the one build in Converter serves every year. It has to land in the same
REM  folder as the plugin, because that is where the plugin looks for it, and
REM  nwcreate_21.dll and nwcreate_data have to land beside the converter,
REM  because nwcreate finds its data folder relative to its own DLL.
REM
REM  Only mkdir and copy are used, deliberately: no embedded payload, no
REM  certutil, nothing that looks like a self-extracting dropper to Defender.
REM
REM  Right-click this file and choose "Run as administrator".
REM ---------------------------------------------------------------------------
setlocal enabledelayedexpansion

net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo   This installer writes into C:\Program Files and needs administrator rights.
    echo   Right-click Install.cmd and choose "Run as administrator".
    echo.
    pause
    exit /b 1
)

echo.
echo   PIXMYD-Nav installer
echo   ====================
echo.

tasklist /fi "imagename eq Roamer.exe" 2>nul | find /i "Roamer.exe" >nul
if not errorlevel 1 (
    echo   Navisworks is running. Close it before installing.
    echo.
    pause
    exit /b 1
)

set INSTALLED=0

call :install 2024 V24
call :install 2025 V25
call :install 2026 V26
call :install 2027 V27

echo.
if "%INSTALLED%"=="0" (
    echo   No Navisworks Manage 2024-2027 installation was found.
    echo   PIXMYD-Nav was not installed.
) else (
    echo   Done. Start Navisworks and look for PIXMYD-Nav on the Add-Ins ribbon tab.
)
echo.
pause
exit /b 0

:install
set YEAR=%~1
set SRC=%~dp0%~2
set CONV=%~dp0Converter
set DEST=C:\Program Files\Autodesk\Navisworks Manage %YEAR%
set PLUGIN=%DEST%\Plugins\PIXMYD-Nav

if not exist "%DEST%\Autodesk.Navisworks.Api.dll" (
    echo   [ skip ] Navisworks Manage %YEAR% not installed
    exit /b 0
)

if not exist "%SRC%\PIXMYD-Nav.dll" (
    echo   [ skip ] %~2\PIXMYD-Nav.dll missing from this download
    exit /b 0
)

if not exist "%PLUGIN%" mkdir "%PLUGIN%"

copy /y "%SRC%\PIXMYD-Nav.dll"   "%PLUGIN%\" >nul
if errorlevel 1 (
    echo   [ FAIL ] Navisworks Manage %YEAR% - could not copy PIXMYD-Nav.dll
    exit /b 0
)

copy /y "%SRC%\PIXMYD-Nav.addin" "%PLUGIN%\" >nul
if errorlevel 1 (
    echo   [ FAIL ] Navisworks Manage %YEAR% - could not copy PIXMYD-Nav.addin
    exit /b 0
)

REM  The converter and the nwcreate runtime it loads. Not an extra: exporting
REM  a scan as NWC runs obj2nwc.exe out of this folder, because nwcreate
REM  cannot be loaded into Navisworks itself.
copy /y "%CONV%\obj2nwc.exe"        "%PLUGIN%\" >nul
copy /y "%CONV%\obj2nwc.exe.config" "%PLUGIN%\" >nul
copy /y "%CONV%\nwcreate_21.dll"    "%PLUGIN%\" >nul

if not exist "%PLUGIN%\nwcreate_data" mkdir "%PLUGIN%\nwcreate_data"
copy /y "%CONV%\nwcreate_data\*"    "%PLUGIN%\nwcreate_data\" >nul

REM  Checked by what arrived rather than by errorlevel, because the case worth
REM  catching is the half-copy: a converter without its runtime installs
REM  quietly and then fails the first time somebody exports a scan.
set MISSING=
if not exist "%PLUGIN%\obj2nwc.exe"                  set MISSING=obj2nwc.exe
if not exist "%PLUGIN%\nwcreate_21.dll"              set MISSING=nwcreate_21.dll
if not exist "%PLUGIN%\nwcreate_data\session.nwlic"  set MISSING=nwcreate_data
if defined MISSING (
    echo   [ FAIL ] Navisworks Manage %YEAR% - could not install !MISSING!
    exit /b 0
)

echo   [  ok  ] Navisworks Manage %YEAR%
set INSTALLED=1
exit /b 0