# asm2obj — .NET assembly → managed COFF `.obj`

A tool that converts a .NET assembly into a managed COFF object file in the
same format produced by MSVC `/clr` (and by [chibil]'s C compiler). The output
`.obj` can be linked with `link.exe` together with chibil-produced objects to
build mixed managed/native executables.

The intended use case is **writing parts of a C runtime in C#** for chibil:
expose C-callable entry-point shims (`mainCRTStartup`, etc.) as managed
methods, expose ForwardRef declarations that resolve to chibil-compiled
C symbols at link time, and let `asm2obj` produce a `.obj` that the same
linker invocation can consume.

[chibil]: ../../README.md

## CLI

```text
asm2obj <input.dll> --machine x86|x64|arm64 -o <output.obj>
```

One architecture per invocation. The output `.obj`'s embedded Module-row name
matches the leaf of `-o`.

## What asm2obj does

The pipeline is a strict two-pass metadata copier:

1. **Phase A — classification.** Inspect each `TypeDef` and `MethodDef` in the
   input. Decide whether each type is *copied*, *flattened* (when annotated
   with `[CompilerGlobalScopeAttribute]`), or *dropped* (the input
   `<Module>`). Decide whether each method is *regular*, *converted to an
   unresolved MemberRef* (when annotated with `MethodImplOptions.ForwardRef`),
   or *dropped*. Reject inputs we can't safely convert in v1 (see below).
2. **Phase B — row prediction.** Walk input tables in deterministic order
   counting surviving rows per output table; record the prediction in the
   `TokenMap`. Synthesise one `MemberRef` parented on `<Module>` per
   ForwardRef method.
3. **Phase C — table population.** Emit each output table in the order
   predicted by Phase B, asserting that the returned handle matches the
   prediction. Signatures are rewritten through `EcmaSignatureRewriter` and
   metadata-token coded indices are remapped via `TokenMap`. The
   `CustomAttribute` rows are sorted by their `HasCustomAttribute` coded
   index before emission (ECMA requires it).
4. **Phase D — IL body emission.** Pre-register every surviving MethodDef
   in the COFF symbol table (so undefined-CLR-token ordering invariants
   hold), pre-register all external MemberRefs, then walk each MethodDef
   with a body, run `IlBodyRewriter` (raw-IL copy with metadata-token slot
   substitution + `ldstr` UserString remap), and finalise via
   `AddMethodBody`.
5. **Phase E — NEP thunks.** For each method flagged
   `MethodAttributes.UnmanagedExport` (0x0008) or annotated
   `[UnmanagedExportAttribute]`, emit the IJW NEP machinery
   (`__mep@<mangled>` slot in `.data`, indirect-jump thunk in `.nep`,
   bare-name alias `_foo`/`foo` per architecture, `.rdata$ilfixup` entry).
6. **Phase F — COFF serialisation.** Wrap metadata + IL + data + NEP into a
   COFF object via `ManagedCoffBuilder` and write to `-o`.

## Symbol-name rules

Each surviving function gets a COFF symbol name:

1. **`[DecoratedNameAttribute("...")]`** on the method, if present, gives the
   exact COFF symbol name. On `x86` an extra leading `_` is prepended when
   the supplied string does not already start with `?` or `_` — matches
   MSVC's cdecl convention.
2. **Auto-mangling** otherwise: `MsvcNameMangler` produces
   `?name@@$$J0Y<cc><ret><params>@Z` from the ECMA method signature.
   Calling-convention letter is derived from any modopt on the return type:
   - `modopt(CallConvCdecl)` → `A` (cdecl)
   - `modopt(CallConvStdcall)` → `G` (stdcall)
   - default (no callconv modopt) → `M` (clrcall) — this is the natural
     calling convention for managed-to-managed code written in C#.

   Authors of C-shaped CRT code in C# can attach the same modopts that
   chibil emits when they need exact matches: `IsSignUnspecifiedByte` for
   plain `char`, `IsLong` for `long`, `IsConst`/`IsVolatile` for pointer
   qualifiers. These usually require IL rewriting since C# does not
   produce them from source.

The mangler is intentionally byte-compatible with
`chibil/CodeGen.cs::MangleFunctionName` so that asm2obj-emitted and
chibil-emitted symbols agree at link time, including:

- `$$J0` is always emitted (no `$$H` special case for `main`).
- Argument backreference table caps at 10 slots.
- Single-char canonical types are not registered into the backref table.
- Return-type mangling uses the name-backref table but does not register
  into the arg-backref table.

## Custom-attribute hooks recognised by asm2obj

| Attribute | Effect |
|-----------|--------|
| `System.Runtime.CompilerServices.CompilerGlobalScopeAttribute` on a type | Type is *flattened*: its members become members of the output `<Module>` TypeDef. The type itself is dropped. |
| `System.Runtime.CompilerServices.DecoratedNameAttribute` on a method | Overrides auto-mangling. The string is the literal COFF symbol name. Reattached to the synthesised MemberRef when the method also has `ForwardRef`. |
| `System.Runtime.InteropServices.UnmanagedExportAttribute` on a method | Equivalent to setting `MethodAttributes.UnmanagedExport`. Triggers NEP-thunk emission for the method. (Roslyn does not expose the metadata flag directly, so this attribute is the practical opt-in.) |
| `MethodImplOptions.ForwardRef` (set via `[MethodImpl]`) | A method with no body and the ForwardRef flag is converted to an *unresolved MemberRef* parented on the `<Module>` TypeDef (matching the pattern MSVC emits for extern C functions under `/clr`). `link.exe` resolves the CLR token to the matching MethodDef in another object. |

## v1 limitations (rejected loudly)

These inputs cause asm2obj to fail with a clear `NotSupportedException`:

- Method bodies with exception regions (try/finally, try/catch, etc.)
- Multiple `.cctor` methods across `[CompilerGlobalScope]` classes.
- Assemblies that reference `System.Runtime` / `System.Private.CoreLib` /
  `netstandard` instead of `mscorlib`. Build your C# code against the
  Framework `mscorlib.dll` reference assembly (e.g. from the .NETFramework
  4.x reference assembly pack) or chibil's own mscorlib facade.
- `[CompilerGlobalScope]` on a type with nested types, instance members,
  interface implementations, or generic parameters.
- `ForwardRef` on a method that *has* a body.
- `HasFieldRVA` fields whose type is neither a primitive nor a value
  type with an explicit `ClassLayout` size (asm2obj cannot determine
  the data size to copy).

Silently dropped (not relevant to the linker scenario):

- `DeclSecurity`, `ManifestResource`, `File`, `ExportedType`, `ImplMap`,
  `FieldMarshal` rows.
- `Property` and `Event` tables (rejected loudly if present — uncommon in
  CRT code; lift the restriction if needed).
- Custom attributes whose parent is `AssemblyDefinition` or
  `ModuleDefinition` (no place for them in a `.obj`).

## Quirks and gotchas

- **`mainCRTStartup` linker entry name.** `link.exe /entry:mainCRTStartup`
  expects a literal symbol named `mainCRTStartup` (or `_mainCRTStartup` on
  x86). Auto-mangling will produce `?mainCRTStartup@…`. Use
  `[DecoratedName("mainCRTStartup")]` on the C# method to expose the
  unmangled name. The x86 `_` prefix is added automatically.
- **Backref-table compatibility with chibil.** If you want asm2obj to
  produce a symbol that links against a chibil-emitted `__CxxPureMSILEntry`
  with mangled name `?__CxxPureMSILEntry@@$$J0YMHHPEAPEAD0@Z`, the
  `byte**` argument signatures in C# must carry the
  `modopt(IsSignUnspecifiedByte)` marker on the inner `int8`. Plain C#
  cannot emit modopts from source — practical options are IL rewriting
  or using `[DecoratedName]` with the exact mangled string.
- **`MsvcNameMangler` cannot encode managed reference types.** Calling
  conventions for methods that involve `string`, `Object`, `SZArray`,
  generic instances, etc. fall back to a synthetic non-link-callable
  symbol of the form `$asm2obj.<methodName>.<row>`. Such methods are still
  usable from managed code via their CLR-token relocation.

## Project layout

```
tools/asm2obj/
├── Asm2Obj.csproj           # console exe, net10.0
├── Program.cs               # CLI argument parsing
├── AsmToObjConverter.cs     # top-level pipeline driver
├── TokenMap.cs              # input handle → output handle
├── EcmaSignatureRewriter.cs # signature blob rewriter
├── IlBodyRewriter.cs        # raw IL copy with token substitution
├── MsvcNameMangler.cs       # ECMA sig → MSVC decorated name
├── MetadataCopier.cs              # MetadataCopier (partial: fields + entry points)
├── MetadataCopier.PhaseA.cs       # classification
├── MetadataCopier.PhaseB.cs       # row prediction
├── MetadataCopier.PhaseC.cs       # table population
├── MetadataCopier.PhaseD.cs       # IL body emission + COFF function symbols
└── MetadataCopier.PhaseE.cs       # NEP thunk emission
```

## Reusable core

The non-CLI pieces are designed to be reusable by a future MSIL COFF
linker that consumes multiple `.obj` inputs. Each instance of
`MetadataCopier` owns one input `MetadataReader` and shares the output
`MetadataBuilder` with peer instances. A linker would attach its own
`ShouldFlattenType` / `RemapMemberOwner` hooks to deduplicate types across
inputs.
