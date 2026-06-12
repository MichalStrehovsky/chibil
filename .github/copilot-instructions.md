This repo hosts a C compiler targeting MSIL. The compiler is in `chibil` directory.

The tests for the compiler are in the `tests/Chibil.Tests` directory. Tests need to run in a vcvarsall environment. Find vcvarsall with vswhere.

The compiler is heavily inspired by MSVC's `/clr` mode. The original port was based on MSVC `/clr` mode research that is now archived in the `scenarios` directory. `scenarios/README.md` talks about this research.

The compiler generates COFF OBJ files that are compatible with MSVC in `/clr` mode. Following tools are used to inspect the OBJ file:
* `ildasm /text` (available under vcvarsall). shows metadata, but won't show IL
* `dumpbin` (available under vcvarsall). shows COFF related information
* `tools/coffobjdumper.cs` (run with `dotnet run tools/coffobjdumper.cs -- filename.obj`). repo local custom tool.

The final linked executable can be inspected with ildasm.

asm2obj tool in `tools/asm2obj` can be used to convert .NET assemblies to COFF object files

Entrypoint in a managed application needs to be `void`/`int` returning and `void`/`string[]` accepting method. if no parameters are needed, make an `int main()` and pass `/entry:main` to the linker (Chibil forwards parameters to the linker with `-Wl,` command line option). if parameters are needed, the `crt` directory has `mainCRTStartup` implemented in C#. it calls __CxxPureMSILEntry that chibil generates. The C# needs to be built and converted with asm2obj and passed to linker with `-Wl`.

chibil is based on the chibicc compiler so architecture is similar.

When fixing a bug make sure you have a test first and ensure it fails. Then fix the bug.
