# chilink

chilink links managed COFF object files from chibil, MSVC `/clr`, and asm2obj into
x64 pure-IL .NET Framework assemblies. It reads objects with the repository's
COFF library and writes metadata and PE images with System.Reflection.Metadata.
Metadata merging follows the identity, reference-resolution and member-merging
passes of CoreCLR's `newmerger.cpp`.

## Build and run

From the repository root, build with the .NET 10 SDK. Given a `hello.c` with
a parameterless `int main(void)`, compile and link it separately or use the
compiler driver:

```powershell
dotnet build .\chilink\Chilink.csproj
$env:PATH = "$PWD\chilink\bin\Debug\net10.0;$env:PATH"

dotnet run --project .\chibil\Chibil.csproj -- -ffixed-address-statics -c hello.c -o hello.obj
chilink /out:hello.exe /entry:main /subsystem:console hello.obj

dotnet run --project .\chibil\Chibil.csproj -- -ffixed-address-statics -fuse-ld=chilink hello.c -o hello.exe "-Wl,/entry:main"
```

chibil launches the executable named by `-fuse-ld=<name>` (resolved through PATH
when no path is supplied); it does not discover or install the linker.
By default, chibil uses link.exe. Every linker receives
the same `/DEBUG`, `/subsystem:console`, `/out:` and `mscoree.lib` arguments.
Consequently, compiler-driven chilink links default to `/opt:noref`; pass
`-Wl,/OPT:REF` to enable dead stripping explicitly.

The linker itself does not require Visual Studio or `mscoree.lib`. Executing
these Framework-targeting images requires a compatible runtime. MSVC input
generation and MSVC-only tests still need Visual Studio.

## Command line

| Option | Meaning |
| --- | --- |
| `/out:file` | Output path; otherwise use the first object's base name. |
| `/entry:symbol` | Managed entrypoint, by exact COFF name or unambiguous global method name. |
| `/dll` | Produce a managed DLL; implies `/noentry`. |
| `/noentry` | Accepted with `/dll`. Native DLL startup is not supported. |
| `/subsystem:console` or `/subsystem:windows` | Set the PE subsystem. |
| `/machine:x64` | Validate the supported target machine. |
| `/debug` | Default to `/opt:noref`; currently does not emit debug information or a PDB. |
| `/opt:ref` or `/opt:noref` | Remove unreferenced COMDATs, or retain them; REF is the default without `/debug`. |
| `/opt:noicf` | Disable identical-code folding (the only supported ICF policy). |
| `/include:symbol` | Retain a managed symbol and its dependencies. |
| `/nodefaultlib[:name]` | Suppress input default-library directives. |
| `/defaultlib:name` | Accepted only when suppressed by `/nodefaultlib`; library resolution is not implemented. |
| `/nologo`, `/?`, `/help` | Banner compatibility and usage. |
| `@file` | Read arguments from a response file, including quoted paths. |

Input objects may use `.obj` or `.o` extensions. Switch names are case-insensitive
and accept `/` or `-` prefixes; symbol names are case-sensitive.

Positional `.lib` inputs, including those in response files, are currently ignored
without being opened. This includes `mscoree.lib`, even if the file does not exist.
Library support will be added later; ignored libraries do not provide symbols or
suppress unresolved-symbol errors. `/defaultlib` options and object directives
still require explicit suppression. Other unsupported switches, directives and
required input features are errors.

An explicit `/opt:ref` or `/opt:noref` overrides the `/debug` default regardless
of argument order.

Use `/Zl /d1clrNoPureCRT` when compiling managed-only MSVC fixtures: `/Zl`
omits default-library directives, and `/d1clrNoPureCRT` suppresses the native CRT
initializer dependency. `/nodefaultlib` can also suppress default libraries,
but does not implement the native initialization code.

EXE entrypoints must be static, non-generic methods returning `void` or `int`,
with no parameters or one `string[]` parameter. Without `/entry`, the console
startup symbol is `mainCRTStartup`; chilink does not synthesize a CRT. The
repository's asm2obj-converted CRT supplies this startup method.

Unlike link.exe, `/dll` alone implies `/noentry`: chilink never tries to link
native `_DllMainCRTStartup`. `/dll` cannot be combined with `/entry`.
Public managed types remain assembly roots in both EXEs and DLLs.
Unreferenced global C functions in individual COMDATs can disappear from a DLL;
use `/include:` or `/opt:noref` when they must remain available through reflection.

## Supported boundary

- Managed methods, token relocations, local signatures, managed function
  pointers, exception regions, and ordinary CLR static fields.
- Aggregate metadata, including nested types, packing, unions and real instance
  fields emitted with `-fmanaged-aggregate-fields`.
- Read-only, relocation-free RVA data such as C string literals and constants.
  Static COMDATs remain input-local, even when their symbol names match across
  objects. C literal storage is distinct from the managed user-string heap.
- Writable, BSS and common globals on `<Module>`, converted from RVA fields to
  ordinary CLR static fields. Compatible common definitions coalesce; a
  section-backed definition takes precedence over common storage. Conversion
  requires `System.Runtime.CompilerServices.FixedAddressValueTypeAttribute`.
- Initialized globals, including arrays and unmanaged fixed-size aggregates.
  A generated module `.cctor` uses `ldsfld`/`stsfld` to initialize globals from
  private read-only RVA fields that reuse each target field's signature, so each
  backing field's type describes the size of its data without synthetic types or
  core-library references. It then applies x64 ADDR64 data relocations using the
  runtime field addresses. This preserves pointer addends, cross-object references
  and cyclic data references. Relocated read-only globals also use ordinary static
  storage.
- Managed vtfixup slots in `<Module>` RVA globals, including arrays and aggregates.
  Each x64 slot uses a method TOKEN relocation and a pointer-width-only `0x02`
  vtfixup record. The generated module `.cctor` replaces the token with `ldftn`,
  preserving the CLR's managed-entrypoint fixup semantics without emitting native
  vtfixup machinery.
- Function COMDAT selection and section-granularity stripping. A shared section
  retains all its functions; `-ffunction-sections` permits separate removal.
  Chibil scopes static function metadata names to their translation unit using
  MSVC's `?A0x<hash>.<name>` format, so same-named static functions in different
  objects remain distinct. Their COFF symbol base names retain the
  `<name>_?A0x<hash>` format.
- Unused compiler-generated native-entrypoint sidecars are omitted. A retained
  dependency on them is not converted into managed code.

Retained IL sections are copied intact and their COFF TOKEN relocations are
patched with merged metadata tokens. Method bodies are not decoded or re-encoded;
method RVAs come from the output section location plus the COFF symbol offset.
The generated module initializer is appended as a new method. It is emitted only
when retained globals need copied initialization data; BSS/common globals rely on
CLR zero initialization. Dead COMDAT initializers do not retain their dependencies.

Every field requiring conversion, including zero-initialized and relocated
read-only fields, must carry `System.Runtime.CompilerServices.FixedAddressValueTypeAttribute`.
This keeps GC-allocated value-type statics from moving after C code takes their
addresses. Only the attribute's namespace and name are checked, not its assembly
identity; annotations on coalesced definitions are retained. Compile chibil
inputs with `-ffixed-address-statics` to emit the attribute. The switch is opt-in,
and `-fno-fixed-address-statics` disables it. Selecting `-fuse-ld=chilink` does not
enable it automatically. Read-only fields that retain their RVA storage and
discarded globals do not require the attribute.

When synthesis is required, an existing `<Module>..cctor` in any input is an
error; combining module initializers is not implemented. Ordinary named-type
static constructors are unaffected. RVA conversion on named managed types is
not supported.

For chibil constants, use `-fdata-sections` to obtain separate read-only sections,
including for `static const` data. `const` alone does not guarantee read-only COFF
storage. Relocation-free read-only fields keep their original RVA representation
and COFF alignment; converted statics use CLR storage and alignment instead.
Overlapping/aliased field storage and incompatible common field sizes are errors.

Not supported: native/from-unmanaged vtfixup-associated fields and other vtfixup
flags, NEP/UNEP dependencies, native method bodies, library/archive parsing,
P/Invoke, native exports, debug
information, manifest resources, security metadata, properties/events, bigobj,
and relocation-overflow encodings. An ordinary call to a framework API does not
require chilink to link that API's native implementation.

The native vtfixup exclusion includes indirect dependencies: a global C function
pointer whose relocation targets an IJW native thunk is still unsupported. chilink does
not replace native callable entrypoints with managed function pointers. Existing
managed IL function pointers and explicitly managed vtfixup slots targeting
nongeneric static methods remain supported. An ADDR64 relocation to a method
symbol is not a managed function pointer and is rejected.

For `__clrcall` global initializers, chibil makes the global storage itself a
TOKEN-relocated vtfixup slot. The `.rdata$ilfixup` record uses `0x01` on x86 or
`0x02` on x64, without either from-unmanaged flag. This also works with link.exe:
the linker writes the method token and the CLR replaces it with a managed
callable entrypoint. Fixup records for COMDAT globals are associated with their
storage, so discarded globals do not retain their fixups. Managed function
pointer initializers cannot carry addends. Chibil requires pointer-sized
destination storage for all symbolic global initializers, including data
addresses and native function addresses; narrower integer casts are rejected
during parsing. Local aggregate initializers can still truncate pointers at
runtime.
Native MEP slots retain `0x09`/`0x0A`; functions using native calling conventions
still use native entrypoint relocations and remain unsupported by chilink.
Unsupported relocation kinds and targets fail explicitly.

As with link.exe, an unresolved managed extern can still be an error when the
function referencing it would otherwise be discarded by `/opt:ref`. Metadata
binding and section retention are separate operations.

## Tests

Tests exercise behavior end to end. `CompilationBuilder.Link` uses the chilink
driver. `MsvcLink` uses link.exe, including `mscoree.lib`, for excluded features.
Tests using `Link` do not also run `MsvcLink`, and there is no automatic fallback.
The compilation helper enables `-ffixed-address-statics`; tests for unannotated
inputs override it with `-fno-fixed-address-statics`.

Run `tests\Chibil.Tests\Chibil.Tests.csproj` in a vcvarsall x64 environment,
locating Visual Studio with vswhere. The existing default, field-backed
aggregate and optimized configurations cover both supported chilink cases and
the retained MSVC cases:

```text
dotnet test tests\Chibil.Tests\Chibil.Tests.csproj
dotnet test tests\Chibil.Tests\Chibil.Tests.csproj -p:FieldBackedAggregateTests=true
dotnet test tests\Chibil.Tests\Chibil.Tests.csproj -p:OptimizedTests=true
```
