# Scenarios

This directory contains small C programs compiled with MSVC's `/clr:pure` option, alongside C# programs that produce functionally equivalent COFF object files using our emitter library (`tools/coffobjectemitter.cs`).

## Goal

Understand the metadata, IL, and CodeView debug information that the MSVC C++ compiler emits for `/clr:pure` managed object files, and replicate it well enough that:

1. **`link.exe`** can link our emitted `.obj` into a working managed executable
2. **ILDASM** produces the same disassembly as the MSVC-linked executable
3. **The PDB** contains the same debug symbols, line numbers, and source file references (enough for a debugger to step through source)

## File layout

Each scenario has:

| File | Description |
|------|-------------|
| `foo.c` | Source C program (the "spec") |
| `foo.obj` | MSVC-compiled object file (reference) |
| `foo.exe` | MSVC-linked executable (reference) |
| `foo.pdb` | MSVC-linked PDB (reference) |
| `foo.cs` | C# emitter that produces `foo.obj` (can be executed with `dotnet run foo.cs` |

The `blah.cs` scenario is a standalone test of the emitter library and doesn't correspond to a C file.

## How the MSVC reference files are produced

```
cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC foo.c
link.exe /debug /entry:main /subsystem:console foo.obj
```

Key compiler switches:
- `/clr:pure` — generate pure MSIL (no native code)
- `/c` — compile only, don't link (emit `.obj` file)
- `/BC` — undocumented: treat input as C instead of C++
- `/Z7` — embed CodeView debug info in the `.obj` (not a separate `.pdb`)
- `/Zl` — omit default library references
- `/d1clrNoPureCRT` — suppress pure-mode CRT dependencies

For scenarios that reference external functions (e.g., `pinvoke.c` uses `MessageBoxW`), add the import library to the link step:

```
link.exe /debug /entry:main /subsystem:console /libpath:... user32.lib foo.obj
```

## How to create a new scenario

### 1. Write the C file

Keep it small. Focus on one language feature (struct, pointer, function call, string literal, etc.). Add the compile command as a comment at the top.

### 2. Compile with MSVC and link

```
cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC foo.c
link.exe /debug /entry:main /subsystem:console foo.obj
```

### 3. Inspect the MSVC object file

Use the tools in this repo to understand what the compiler generated:

**ILDASM** — shows the metadata and IL that the linker will carry into the EXE:
```
ildasm foo.obj /out=foo_obj.il /nobar
```
This reveals TypeDefs, TypeRefs, MethodDefs, FieldDefs, MemberRefs, signatures, custom attributes, and the IL instruction stream. This is the primary spec for what metadata your C# emitter needs to produce.

**coffobjdumper** — shows IL with resolved tokens, plus the CodeView debug info:
```
dotnet run coffobjdumper.cs foo.obj
```
This shows the actual IL bytes (with COFF token relocations applied), the `.debug$S` symbol records, line numbers, and file checksums.

**cvdump** — the Microsoft PDB dumper, good for inspecting both `.obj` and `.pdb` files:
```
cvdump.exe foo.obj          # debug info in the object file
cvdump.exe -s -l foo.pdb    # symbols and lines in the PDB
```

### 4. Write the C# emitter

Create `foo.cs` in this directory. It automatically picks up the emitter library via `Directory.Build.props`. Follow the pattern of existing scenarios:

1. **Metadata** — build only the metadata tables that appear in the ILDASM output. Only add TypeRefs, MemberRefs, etc. that are actually referenced by IL, signatures, or custom attributes. Don't replicate MSVC boilerplate TypeRefs (like `CallConvStdcall`, `IsVolatile`) unless linking fails without them.

2. **IL** — emit the exact same instruction sequence shown in the ILDASM output.

3. **COFF symbols** — the emitter library handles most of this automatically through `AddMethodBody` and `AddClrToken`. For external imports, call `AddExternalClrToken` before emitting IL. For field data, call `AddDataClrToken` before emitting IL (ordering matters — see below).

4. **Debug info** — add `CodeViewSymbolBuilder` with `S_OBJNAME`, `S_COMPILE3`, source file with SHA-256 checksum, line numbers via `MarkLineNumber`, and local variable slots via `CodeViewManSlot` and `CodeViewLocalScope`.

### 5. Build and validate

```
dotnet run foo.cs
link.exe /debug /entry:main /subsystem:console foo.obj /out:foo_ours.exe
ildasm foo_ours.exe /out=foo_ours.il /nobar
ildasm foo.exe /out=foo_msvc.il /nobar
```

Compare the ILDASM outputs (ignoring MVID, timestamps, image base, assembly/module names). They should be identical.

For PDB validation:
```
cvdump -s -l foo_ours.pdb
cvdump -s -l foo.pdb
```

Compare the symbol records (S_GMANPROC, S_FRAMEPROC, S_MANSLOT, S_BLOCK32, S_END) and line number mappings.

## Design decisions

### Only emit metadata that's needed
MSVC generates several TypeRefs that aren't used by the IL or signatures (e.g., `CallConvStdcall`, `CallConvFastcall`, `IsVolatile`). We skip these — the linker doesn't need them, and the executable is identical without them.

### No `.debug$T` section
The `.debug$T` section contains CodeView type records (LF_PROCEDURE, LF_FUNC_ID, LF_STRING_ID, LF_BUILDINFO). These provide build environment metadata (source directory, compiler path, PDB path, command line). We omit this entirely because:
- It's only used for `S_BUILDINFO` which is build provenance metadata
- The core debugging experience (stepping, breakpoints, locals) works without it
- Method type signatures are already in the `.cormeta` metadata

### No `CV_LVARFLAGS` (AddrTaken, IsParam, etc.)
The `S_MANSLOT` records have a `CV_LVARFLAGS` field with bits like `fAddrTaken` and `fIsParam`. We always write these as 0 because:
- The linker doesn't use them
- The managed debugger doesn't use them (parameter names come from the metadata Parameter table, not from CodeView)
- They're informational annotations, not functional

### No debug info for parameters
MSVC emits `S_MANSLOT` with `fIsParam=1` for function parameters (e.g., `pS` in `sum_struct`). We skip this because parameter names in ILDASM come from the metadata Parameter table, not from CodeView debug info.

### Nested lexical scopes are supported
When a function has multiple `{ }` blocks with their own local variables (like `struct.c`), we emit `S_BLOCK32` + `S_END` records to create nested scopes. This ensures that local variable names are correctly scoped in the PDB — without scoping, the debugger wouldn't know which `m` is which in overlapping blocks.

### Method body alignment
Fat method bodies (those with locals, maxStack > 8, or exception handlers) require 4-byte alignment. The emitter handles this automatically. When multiple methods are in the same `.text$mn` section, the COFF symbol for each method must have the correct `Value` (offset within the section) — the emitter sets this via `AddClrToken`.

### COFF symbol ordering matters
`AddDataClrToken` and `AddExternalClrToken` must be called **before** emitting IL that references the same metadata tokens. This is because IL token references create CLR token COFF symbols via `GetOrAddCoffSymbol`, which caches by name. If the IL emission creates the symbol first (at section 0), the later `AddDataClrToken` call is a no-op — it gets the cached version. Pre-registering the symbol ensures the correct section number is used.

## Tools reference

| Tool | Location | Purpose |
|------|----------|---------|
| `coffobjdumper.cs` | `tools/` | Dump IL, metadata tokens, COFF symbols, and `.debug$S` from `.obj` files |
| `coffobjectemitter.cs` | `tools/` | Library for emitting managed COFF `.obj` files |
| `cvdump.exe` | `references/microsoft-pdb/cvdump/` | Microsoft's CodeView/PDB dumper |
| `ildasm.exe` | Windows SDK | .NET IL disassembler |
| `link.exe` | MSVC toolset | Microsoft linker |
| `cl.exe` | MSVC toolset | Microsoft C/C++ compiler |
