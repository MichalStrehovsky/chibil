@echo off
setlocal enabledelayedexpansion

REM ============================================================================
REM  DOOM sample build script for chibil
REM
REM  Usage:
REM    build.cmd             - build the interactive windowed executable
REM    build.cmd bmp         - build the reproducible BMP harness
REM    build.cmd checksum    - build the reproducible checksum validator
REM
REM  Prerequisites:
REM    - Visual Studio with C++ tools (run from VS Developer Command Prompt)
REM    - .NET SDK (for dotnet run)
REM    - PureDOOM submodule initialized (git submodule update --init)
REM
REM  This script builds:
REM    obj\pal.obj      - Platform Abstraction Layer (compiled with chibil)
REM    obj\doom.obj     - DOOM engine (compiled with chibil)
REM    bin\doom.exe     - DOOM executable (linked with link.exe)
REM ============================================================================

set CHIBIL_DIR=%~dp0..\..\chibil
set BUILD_MODE=%~1
if "%BUILD_MODE%"=="" set BUILD_MODE=window

set CHIBIL_DEFINES=-D_WIN64 -D_WIN32 -DDOOM_WIN32
if /I "%BUILD_MODE%"=="window" goto :mode_done
if /I "%BUILD_MODE%"=="bmp" (
    set CHIBIL_DEFINES=%CHIBIL_DEFINES% -DREPRODUCIBLE_HARNESS
    goto :mode_done
)
if /I "%BUILD_MODE%"=="checksum" (
    set CHIBIL_DEFINES=%CHIBIL_DEFINES% -DREPRODUCIBLE_HARNESS -DVALIDATE_CHECKSUM
    goto :mode_done
)

echo Usage: build.cmd [window^|bmp^|checksum]
exit /b 1

:mode_done

REM Create output directories
if not exist obj mkdir obj
if not exist bin mkdir bin

REM --------------------------------------------------------------------------
REM  Step 1: Compile pal.c with chibil
REM --------------------------------------------------------------------------
echo [1/3] Compiling pal.c with chibil...
dotnet run -c Release --project "%CHIBIL_DIR%" -- -cc1 %CHIBIL_DEFINES% -cc1-input pal.c -cc1-output obj\pal.obj
if errorlevel 1 (
    echo ERROR: Failed to compile pal.c with chibil
    exit /b 1
)

REM --------------------------------------------------------------------------
REM  Step 2: Compile doom.c with chibil (C -> MSIL COFF .obj)
REM --------------------------------------------------------------------------
echo [2/3] Compiling doom.c with chibil...
dotnet run -c Release --project "%CHIBIL_DIR%" -- -cc1 %CHIBIL_DEFINES% -cc1-input doom.c -cc1-output obj\doom.obj
if errorlevel 1 (
    echo ERROR: Failed to compile doom.c with chibil
    exit /b 1
)

REM --------------------------------------------------------------------------
REM  Step 3: Link doom.exe
REM --------------------------------------------------------------------------
echo [3/3] Linking doom.exe...
link /nologo /DEBUG /entry:main /subsystem:console obj\doom.obj obj\pal.obj ^
     kernel32.lib user32.lib gdi32.lib mscoree.lib ^
     /out:bin\doom.exe
if errorlevel 1 (
    echo ERROR: Failed to link doom.exe
    exit /b 1
)

(
    echo {
    echo   "runtimeOptions": {
    echo     "tfm": "net10.0",
    echo     "rollForward": "Major",
    echo     "framework": {
    echo       "name": "Microsoft.NETCore.App",
    echo       "version": "10.0.0"
    echo     }
    echo   }
    echo }
) > bin\doom.runtimeconfig.json
if errorlevel 1 (
    echo ERROR: Failed to write bin\doom.runtimeconfig.json
    exit /b 1
)

echo(
echo Build successful
echo   bin\doom.exe  - DOOM executable (MSIL, compiled with chibil)
echo   bin\doom.runtimeconfig.json - runtime configuration for modern .NET
if /I "%BUILD_MODE%"=="bmp" echo   mode          - BMP harness (headless, saves frames)
if /I "%BUILD_MODE%"=="checksum" echo   mode          - checksum validator (headless)
if /I "%BUILD_MODE%"=="window" echo   mode          - windowed (interactive)
echo To run: cd into PureDOOM directory, set HOME environment
echo variable to where you want .doomrc, then:
echo   ..\bin\doom.exe
echo Or, to run with modern .NET:
echo   dotnet exec ..\bin\doom.exe
