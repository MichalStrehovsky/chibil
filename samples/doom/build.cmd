@echo off
setlocal enabledelayedexpansion

REM ============================================================================
REM  DOOM sample build script for chibil
REM
REM  Prerequisites:
REM    - Visual Studio with C++ and /clr:pure support (run from VS Developer Prompt)
REM    - .NET SDK (for dotnet run)
REM    - doom1.wad in this directory (Shareware WAD from https://doomwiki.org/wiki/DOOM1.WAD)
REM    - PureDOOM submodule initialized (git submodule update --init)
REM
REM  This script builds:
REM    obj\minicrt.obj  - Managed CRT with .cctor support (compiled with cl /clr:pure)
REM    bin\pal.dll      - Platform Abstraction Layer DLL (native, compiled with cl)
REM    obj\pal.lib      - Import library for pal.dll
REM    bin\doom.exe     - DOOM executable (compiled with chibil, linked with link.exe)
REM ============================================================================

set CHIBIL_DIR=%~dp0..\..\chibil

REM Create output directories
if not exist obj mkdir obj
if not exist bin mkdir bin

REM --------------------------------------------------------------------------
REM  Step 1: Build minicrt.obj (managed CRT with .cctor iterator)
REM --------------------------------------------------------------------------
echo [1/4] Building minicrt.obj...
cl /c /Z7 /Zl /clr:pure ..\..\scenarios\minicrt.cc /Foobj\minicrt.obj >nul 2>&1
if errorlevel 1 (
    echo ERROR: Failed to compile minicrt.cc
    echo Make sure you are running from a VS Developer Command Prompt with /clr:pure support.
    exit /b 1
)

REM --------------------------------------------------------------------------
REM  Step 2: Build pal.dll + pal.lib (native Win32 platform layer)
REM --------------------------------------------------------------------------
echo [2/4] Building pal.dll...
cl /c /Z7 /O2 /DPAL_BUILD_DLL pal.c /Foobj\pal.obj >nul 2>&1
if errorlevel 1 (
    echo ERROR: Failed to compile pal.c
    exit /b 1
)
link /nologo /DLL /DEBUG obj\pal.obj kernel32.lib user32.lib gdi32.lib /OUT:bin\pal.dll /IMPLIB:obj\pal.lib >nul 2>&1
if errorlevel 1 (
    echo ERROR: Failed to link pal.dll
    exit /b 1
)

REM --------------------------------------------------------------------------
REM  Step 3: Compile doom.c with chibil (C -> MSIL COFF .obj)
REM --------------------------------------------------------------------------
echo [3/4] Compiling doom.c with chibil...
dotnet run -c Release --project "%CHIBIL_DIR%" -- -cc1 -cc1-input doom.c -cc1-output obj\doom.obj
if errorlevel 1 (
    echo ERROR: Failed to compile doom.c with chibil
    exit /b 1
)

REM --------------------------------------------------------------------------
REM  Step 4: Link doom.exe
REM --------------------------------------------------------------------------
echo [4/4] Linking doom.exe...
link /nologo /DEBUG /subsystem:console obj\doom.obj obj\minicrt.obj obj\pal.lib ^
     /include:?.cctor@@$$FYMXXZ ^
     /out:bin\doom.exe
if errorlevel 1 (
    echo ERROR: Failed to link doom.exe
    exit /b 1
)

echo.
echo Build successful!
echo   bin\doom.exe  - DOOM executable (MSIL, compiled with chibil)
echo   bin\pal.dll   - Platform Abstraction Layer (native)
echo.
echo To run: cd into PureDOOM directory, set HOME environment
echo variable to where you want .doomrc, then:
echo   ..\bin\doom.exe
