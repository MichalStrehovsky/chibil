# Scenarios

This directory contains small C programs compiled with MSVC's `/clr:pure` option, alongside C# emitters that produce functionally equivalent COFF object files using our emitter library (`tools/coffobjectemitter.cs`). An xUnit test suite validates that the emitted `.obj` files match the MSVC reference objects.

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
| `foo.cs` | C# xUnit test that emits `foo.obj` and compares against the reference |
| `reference/foo/x86/foo.obj` | MSVC-compiled reference object file (x86) |
| `reference/foo/x64/foo.obj` | MSVC-compiled reference object file (x64) |
| `reference/foo/arm64/foo.obj` | MSVC-compiled reference object file (ARM64) |

Supporting files:

| File | Description |
|------|-------------|
| `CoffEmitterTests.csproj` | xUnit test project |
| `ObjDumper.cs` | Normalized COFF `.obj` dumper for test comparison |
| `Directory.Build.props` | Shared build properties, includes `coffobjectemitter.cs` |

## Running the tests

```
dotnet test CoffEmitterTests.csproj
```

Each `.cs` file is a `[Theory]` with `[InlineData(Machine.I386)]`, `[InlineData(Machine.Arm64)]`, and `[InlineData(Machine.Amd64)]`. The test emits the `.obj` in memory, dumps both the emitted and reference objects using `ObjDumper`, and asserts the dumps are identical.

## What the tests compare

`ObjDumper.DumpForComparison()` produces a normalized text dump covering:

- **TypeDefs** — name, flags, base type, layout, custom attributes
- **Fields** — name, flags, RVA data bytes (or resolved token references for `.CRTMA` data), custom attributes
- **Method bodies** — flags, code size, locals (yes/no), full IL with resolved tokens
- **Debug symbols** — S_COMPILE3 (language, machine, compiler version), S_GMANPROC (code length, proc name), S_FRAMEPROC (frame/pad/saveRegs), S_MANSLOT (slot index, variable name), S_BLOCK32 (relative offset, length)
- **Line numbers** — per method, source filename, checksum, line-to-offset mappings

## What the tests intentionally skip

These are known acceptable differences between MSVC and our emitter:

| What | Why |
|------|-----|
| TypeRefs / MemberRefs tables | MSVC emits unused boilerplate refs; we only emit what IL/signatures reference |
| MSVC boilerplate types (`vc.cppcli.*`, `__clr_*`) | Compiler-internal helper types not needed for linking |
| MSVC boilerplate methods (`__CxxPure*`) | CRT initialization stubs we don't emit |
| `.debug$T` section | Build provenance metadata (LF_BUILDINFO); not needed for debugging |
| S_OBJNAME | Contains the obj file path, which differs per environment |
| S_BUILDINFO | References `.debug$T` type records we don't emit |
| S_FRAMEPROC flags | `fOptSpeed` and `fSecurityChecks` bits differ between our emitter and MSVC |
| S_COMPILE3 HotPatch flag | MSVC sets `fHotPatch` (0x4000) on x64 but not on x86/ARM64; not needed for linking or debugging |
| S_MANSLOT CV_LVARFLAGS | Informational annotations (`fAddrTaken`, `fIsParam`); not used by linker or debugger |
| S_MANSLOT typind | StandaloneSig token numbers differ due to different metadata row counts |
| S_MANSLOT for parameters | MSVC emits `fIsParam=1` slots; parameter names come from the metadata Parameter table instead |
| S_GMANPROC segment/offset | Section layout differs between COMDAT (MSVC) and merged (our emitter) |
| MaxStack | MSVC uses fat headers with explicit maxstack; our emitter may use tiny headers |
| `?A0x<hash>` prefixes | Hash depends on source file path, normalized to `?A0x*` |
| Raw token numbers in IL | Resolved to `TableKind:Name` since row numbers differ when extra TypeRefs/MemberRefs exist |

## How to create a new scenario

### 1. Write the C file

Keep it small. Focus on one language feature (struct, pointer, function call, string literal, etc.). Add the compile command as a comment at the top.

### 2. Compile with MSVC for each architecture

```
cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC foo.c
```

Key compiler switches:
- `/clr:pure` — generate pure MSIL (no native code)
- `/c` — compile only, don't link (emit `.obj` file)
- `/BC` — undocumented: treat input as C instead of C++
- `/Z7` — embed CodeView debug info in the `.obj` (not a separate `.pdb`)
- `/Zl` — omit default library references
- `/d1clrNoPureCRT` — suppress pure-mode CRT dependencies

Place the resulting `.obj` files in `reference/foo/x86/`, `reference/foo/x64/`, and `reference/foo/arm64/`.

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

### 4. Write the C# emitter as an xUnit test

Create `foo.cs` in this directory following the pattern of existing scenarios:

```csharp
public class FooTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";
        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "foo", refDir, "foo.obj"));
        string emittedDump = ObjDumper.DumpForComparison(emitted);
        string referenceDump = ObjDumper.DumpForComparison(reference);
        Assert.Equal(referenceDump, emittedDump);
    }

    static byte[] EmitObj(Machine machine) { /* ... */ }
}
```

The `EmitObj` method takes `Machine` as a parameter (not a const) and returns the serialized `.obj` as a byte array. Follow the emitter guidelines:

1. **Metadata** — build only the metadata tables that appear in the ILDASM output. Only add TypeRefs, MemberRefs, etc. that are actually referenced by IL, signatures, or custom attributes. Don't replicate MSVC boilerplate TypeRefs (like `CallConvStdcall`, `IsVolatile`) unless linking fails without them.

2. **IL** — emit the exact same instruction sequence shown in the ILDASM output.

3. **COFF symbols** — the emitter library handles most of this automatically through `AddMethodBody` and `AddClrToken`. For external imports, call `AddExternalClrToken` before emitting IL. For field data, call `AddDataClrToken` before emitting IL (ordering matters — see below).

4. **Debug info** — add `CodeViewSymbolBuilder` with `S_OBJNAME`, `S_COMPILE3`, source file with SHA-256 checksum, line numbers via `MarkLineNumber`, and local variable slots via `CodeViewManSlot` and `CodeViewLocalScope`.

### 5. Run the tests

```
dotnet test CoffEmitterTests.csproj
```

For manual validation, you can also link and compare:
```
link.exe /debug /entry:main /subsystem:console foo.obj /out:foo_ours.exe
ildasm foo_ours.exe /out=foo_ours.il /nobar
ildasm foo.exe /out=foo_msvc.il /nobar
```

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
Fat method bodies (those with locals, maxStack > 8, or exception handlers) require 4-byte alignment. The emitter handles this automatically. When multiple methods are in the same `.text$mn` section, the COFF symbol for each method must have the correct `Value` (offset within the section) — the emitter sets this via `AddFunctionClrToken`.

### COFF symbol ordering matters
`AddDataClrToken` and `AddExternalClrToken` must be called **before** emitting IL that references the same metadata tokens. This is because IL token references create CLR token COFF symbols via `GetOrAddCoffSymbol`, which caches by name. If the IL emission creates the symbol first (at section 0), the later `AddDataClrToken` call is a no-op — it gets the cached version. Pre-registering the symbol ensures the correct section number is used.

### Architecture parameterization (x86 vs x64 vs ARM64)
Each `.cs` test receives `Machine` as a parameter. The distinction is primarily 32-bit vs 64-bit — x64 and ARM64 share the same codegen for all aspects below:

| Aspect | x86 (32-bit) | x64 / ARM64 (64-bit) |
|--------|-----|-------|
| mscorlib hash | `32 CD 81 47...` | `28 DC 37 8B...` |
| Pointer arithmetic IL | no `conv.i8` | `conv.i8` after integer constants |
| Calling convention (pinvoke) | `CallConvStdcall` | `CallConvCdecl` |
| Decorated names (pinvoke) | `PAX`, `J216YGH` | `PEAX`, `J0YAH` |
| CRTMA slot size | 4 bytes | 8 bytes |
| CRTMA section alignment | Align4Bytes | Align8Bytes |
| `<alignment member>` field (struct) | not emitted | emitted |
| CodeView machine | `I386` | `Amd64` / `Arm64` |

### .CRTMA section and global variable initializers
The `init.c` scenario demonstrates global variables with initializers (e.g., `char* str = "Hello!"`). The MSVC compiler generates:
1. An initializer function `??__Estr` that sets `str = &"Hello!"`
2. A `.CRTMA$XCC` section containing a function pointer (CLR token relocation) to `??__Estr`
3. The `.CRTMA$XCC` data is merged alphabetically by the linker with `.CRTMA$XCA` (start sentinel) and `.CRTMA$XCZ` (end sentinel) from `minicrt.obj`
4. The module constructor (`.cctor` in `minicrt.obj`) iterates this table and calls each non-null function pointer

**Critical:** The `.CRTMA$XCC` section alignment must match pointer size (4 bytes on x86, 8 bytes on ARM64). Incorrect alignment inserts padding gaps that break the table iteration.

## Tools reference

| Tool | Location | Purpose |
|------|----------|---------|
| `coffobjdumper.cs` | `tools/` | Dump IL, metadata tokens, COFF symbols, and `.debug$S` from `.obj` files |
| `coffobjectemitter.cs` | `tools/` | Library for emitting managed COFF `.obj` files |
| `ObjDumper.cs` | `scenarios/` | Normalized `.obj` dumper for test comparison |
| `cvdump.exe` | `references/microsoft-pdb/cvdump/` | Microsoft's CodeView/PDB dumper |
| `dumpbin.exe` | MSVC toolset | COFF/PE dumper (headers, symbols, sections, relocations) |
| `ildasm.exe` | Windows SDK | .NET IL disassembler |
| `link.exe` | MSVC toolset | Microsoft linker |
| `cl.exe` | MSVC toolset | Microsoft C/C++ compiler |

### Linker paths
- x86: `C:\Program Files\Microsoft Visual Studio\...\bin\Hostx86\x86\link.exe`
- x64: `C:\Program Files\Microsoft Visual Studio\...\bin\Hostx64\x64\link.exe`
- ARM64: `C:\Program Files\Microsoft Visual Studio\...\bin\Hostx64\arm64\link.exe`
