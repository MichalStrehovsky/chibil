## What is chibil

Chibil is a C compiler based on [chibicc](https://github.com/rui314/chibicc) rewritten in C# and updated to target .NET IL (MSIL).

It is complete enough to run [DOOM](samples/doom) (PureDOOM).

# Pipeline

Chibil takes C source files and generates COFF OBJ files. These OBJ files are binary-compatible with OBJ files produced by the MSVC compiler. link.exe from Visual Studio is used to link the object files together and produce final executables. One can actually mix and match C++/CLI and chibil-produced object files.

Chibil will probably have its own linker later, if for no other reason, just so we don't need Windows.

## Debugging

Line numbers and locals work as expected. You can step through the C code in a .NET debugger.

## Consuming C code from .NET code

This is not complete yet. The code is generated into global namespace so if you want to consume the compiled code from elsewhere (i.e. don't intend to just run the EXE), you'll need to use reflection such as [Module.GetMethod](https://learn.microsoft.com/dotnet/api/system.reflection.module.getmethod) to find the methods and reflection-invoke them.

# Useful tools

The tools/coffobjdumper.cs file contains a COFF OBJ dumper that dumps .NET OBJ files. It's a good complement for ILDASM (the desktop CLR ILDASM!) that can dump the .NET metadata from COFF OBJ files, but doesn't show method bodies, and dumpbin.exe that can dump various things from COFF objects, but not much in terms of .NET metadata.
