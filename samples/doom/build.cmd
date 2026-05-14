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
REM    - Visual Studio with C++ and /clr support (run from VS Developer Prompt)
REM    - .NET SDK (for dotnet run)
REM    - PureDOOM submodule initialized (git submodule update --init)
REM
REM  This script builds:
REM    obj\minicrt.obj  - Managed CRT with .cctor support (compiled with cl /clr)
REM    bin\pal.dll      - Platform Abstraction Layer DLL for windowed mode
REM    obj\pal.lib      - Import library for pal.dll in windowed mode
REM    obj\pal.obj      - Managed PAL object in harness modes
REM    bin\doom.exe     - DOOM executable (compiled with chibil, linked with link.exe)
REM ============================================================================

set CHIBIL_DIR=%~dp0..\..\chibil
set BUILD_MODE=%~1
if "%BUILD_MODE%"=="" set BUILD_MODE=window

set HARNESS_BUILD=0
set CHIBIL_DEFINES=
if /I "%BUILD_MODE%"=="window" goto :mode_done
if /I "%BUILD_MODE%"=="bmp" (
    set HARNESS_BUILD=1
    set CHIBIL_DEFINES=-DREPRODUCIBLE_HARNESS
    goto :mode_done
)
if /I "%BUILD_MODE%"=="checksum" (
    set HARNESS_BUILD=1
    set CHIBIL_DEFINES=-DREPRODUCIBLE_HARNESS -DVALIDATE_CHECKSUM
    goto :mode_done
)

echo Usage: build.cmd [window^|bmp^|checksum]
exit /b 1

:mode_done

REM Create output directories
if not exist obj mkdir obj
if not exist bin mkdir bin

REM --------------------------------------------------------------------------
REM  Step 1: Build minicrt.obj (managed CRT with .cctor iterator)
REM --------------------------------------------------------------------------
echo [1/4] Building minicrt.obj...
cl /c /Z7 /Zl /clr ..\..\scenarios\minicrt.cc /Foobj\minicrt.obj >nul 2>&1
if errorlevel 1 (
    echo ERROR: Failed to compile minicrt.cc
    echo Make sure you are running from a VS Developer Command Prompt with /clr support.
    exit /b 1
)

REM --------------------------------------------------------------------------
REM  Step 2: Build PAL
REM --------------------------------------------------------------------------
if "%HARNESS_BUILD%"=="1" (
    echo [2/4] Compiling pal.c with chibil...
    dotnet run -c Release --project "%CHIBIL_DIR%" -- -cc1 %CHIBIL_DEFINES% -D_WIN64 -cc1-input pal.c -cc1-output obj\pal.obj
    if errorlevel 1 (
        echo ERROR: Failed to compile pal.c with chibil
        exit /b 1
    )
) else (
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
)

REM --------------------------------------------------------------------------
REM  Step 3: Compile doom.c with chibil (C -> MSIL COFF .obj)
REM --------------------------------------------------------------------------
echo [3/4] Compiling doom.c with chibil...
dotnet run -c Release --project "%CHIBIL_DIR%" -- -cc1 %CHIBIL_DEFINES% -D_WIN64 -cc1-input doom.c -cc1-output obj\doom.obj
if errorlevel 1 (
    echo ERROR: Failed to compile doom.c with chibil
    exit /b 1
)

REM --------------------------------------------------------------------------
REM  Step 4: Link doom.exe
REM --------------------------------------------------------------------------
echo [4/4] Linking doom.exe...
if "%HARNESS_BUILD%"=="1" (
    link /nologo /DEBUG /subsystem:console obj\doom.obj obj\pal.obj obj\minicrt.obj kernel32.lib ^
         mscoree.lib ^
         /out:bin\doom.exe
) else (
    link /nologo /DEBUG /subsystem:console obj\doom.obj obj\minicrt.obj obj\pal.lib ^
         mscoree.lib ^
         /out:bin\doom.exe
)
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
if "%HARNESS_BUILD%"=="1" echo   mode          - %BUILD_MODE% harness
if not "%HARNESS_BUILD%"=="1" echo   bin\pal.dll   - Platform Abstraction Layer (native)
echo To run: cd into PureDOOM directory, set HOME environment
echo variable to where you want .doomrc, then:
echo   ..\bin\doom.exe
echo Or, to run with modern .NET:
echo   dotnet exec ..\bin\doom.exe
