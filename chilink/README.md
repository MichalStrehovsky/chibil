# chilink

chilink links managed COFF object files from chibil, MSVC `/clr`, and asm2obj into
x64 pure-IL .NET Framework or modern .NET assemblies. It reads objects with the repository's
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

dotnet run --project .\chibil\Chibil.csproj -- --target=x86_64-netfx-windows-chibil -c hello.c -o hello.obj
chilink /out:hello.exe /entry:main /subsystem:console hello.obj

dotnet run --project .\chibil\Chibil.csproj -- --target=x86_64-netfx-windows-chibil hello.c -o hello.exe "-Wl,/entry:main"
dotnet run --project .\chibil\Chibil.csproj -- --target=x86_64-dotnet-windows hello.c -o hello.dll "-Wl,/entry:main"
```

chibil launches the executable named by `-fuse-ld=<name>` (resolved through PATH
when no path is supplied); it does not discover or install the linker.
With no target selected, chibil uses link.exe. `chibil` targets select chilink;
see [target presets](#target-presets) below. Every linker receives
the same `/DEBUG`, `/subsystem:console`, `/out:` and `mscoree.lib` arguments.
Consequently, compiler-driven chilink links default to `/opt:noref`; pass
`-Wl,/OPT:REF` to enable dead stripping explicitly.

The linker itself does not require Visual Studio or `mscoree.lib`. Executing
Framework-targeting images requires a compatible runtime. Modern .NET images
require a separately supplied runtimeconfig. The target does not select an
entry point or provide a CRT automatically. MSVC input
generation and MSVC-only tests still need Visual Studio.

## Target presets

Chibil accepts `--target=cpu-runtime-os[-abi]` or
`--target cpu-runtime-os[-abi]`. The runtime is `dotnet` or `netfx`;
the ABI is `chibil` (chibil/chilink's private managed ABI) or `msvc`
(MSVC `/clr` compatibility). An omitted ABI means `chibil` for `dotnet`
and `msvc` for `netfx`.

| Target | Explicit alias | Linker | C data model |
| --- | --- | --- | --- |
| `x86_64-netfx-windows` (default) | `x86_64-netfx-windows-msvc` | `link` | LLP64 |
| `x86_64-dotnet-windows` | `x86_64-dotnet-windows-chibil` | `chilink` | LLP64 |
| `x86_64-dotnet-windows-msvc` | None | `link` | LLP64 |
| `x86_64-netfx-windows-chibil` | None | `chilink` | LLP64 |
| `x86_64-dotnet-linux` | `x86_64-dotnet-linux-chibil` | `chilink` | Managed LP64 |

Only these lowercase spellings are supported; no new CPUs are enabled.
The default is fixed, independent of the host OS.
Both `chibil` runtimes enable `-ffixed-address-statics` and
`-fmanaged-aggregate-fields`. Modern .NET `chibil` also enables
`-fuco-entrypoints` and `-femulated-varargs`. MSVC presets leave all four
features disabled and only select the requested runtime binding.
Emulated varargs use a private argument-buffer ABI, not native varargs;
variadic function pointers still require `__clrcall`.

The last `--target` wins, but every supplied target must be valid.
Explicit low-level switches override preset defaults regardless of position;
the last explicit switch for a setting wins. These include
`-fnetcore`/`-fnetframework`, `-fuse-ld=`, and the positive/negative forms of
the four features above. Overrides do not recompute other preset settings:
modern `chibil` plus `-fnetframework` also requires `-fno-uco-entrypoints`.
Linux requires an effective modern .NET runtime.
Expert overrides may produce incompatible combinations; changing the linker
does not change the selected ABI or C layout.
Without `--target`, existing switches retain their behavior:
`-fnetcore` alone does not select `chibil`, and `-fuse-ld=chilink` alone does
not enable its emission features.

For a parameterless `int main(void)`, with chibil and the selected linker on PATH:

```powershell
chibil --target=x86_64-netfx-windows hello.c -o hello.exe "-Wl,/entry:main"
chibil --target=x86_64-dotnet-windows hello.c -o hello.dll "-Wl,/entry:main"
chibil --target=x86_64-dotnet-linux hello.c -o hello-linux.dll "-Wl,/entry:main"
```

Linker discovery/installation, entry point selection, CRT objects for an
argc/argv `main`, and modern .NET `runtimeconfig.json` files remain manual.
All targets still emit x64 managed COFF objects and PE assemblies, not ELF.
Framework `chibil` retains the native-address limitations described below;
its presets do not provide UCO support.

Windows uses LLP64: `long` is 4 bytes, pointers are 8, and wide characters
use 2-byte `unsigned short`. Linux uses managed LP64: `long` and pointers
are 8 bytes, and wide characters use 4-byte `int`.
**`long double` is an 8-byte double on every target.** Linux selection does
not promise native SysV ABI, libc, system-header, native aggregate/callback,
or native `long double` compatibility. Managed-only Linux programs can be
cross-compiled and linked on Windows without changing chilink's CLI.

### Predefined macros and headers

Definitions follow the selected target, not the build host.
Windows defines `_WIN32`, `_WIN64`, `__WIN32`, `__WIN32__`, `__WIN64`,
`__WIN64__`, `__WINNT`, `__WINNT__`, and `_M_AMD64`/`_M_X64` (both 100).
Linux defines `__linux`, `__linux__`, `__unix`, and `__unix__`.
All targets retain the x86_64/amd64 aliases and `__chibil__`.
Size/type macros, including `__WCHAR_TYPE__`, match the selected layout;
`_LP64`/`__LP64__` are defined only for Linux.
Explicit `-D`/`-U` options are applied after builtins, in command-line order.

The Windows names are based on
[GCC's MinGW platform definitions](https://github.com/gcc-mirror/gcc/blob/master/gcc/config/mingw/mingw32.h)
and [mingw-w64's architecture aliases](https://github.com/mingw-w64/mingw-w64/blob/master/mingw-w64-headers/crt/_mingw_mac.h),
not a wholesale copy of their compiler or CRT identities.
Chibil does not define MinGW/GCC/MSVC version identities, CRT identities,
or Windows API version defaults. It no longer predefines bare
`linux`/`unix`, and does not define bare `WIN32`/`WIN64`/`WINNT`.
`__ELF__` is absent even on Linux because the output is COFF/PE;
`__gnu_linux__` is absent because a Linux target does not select glibc.
`__STDC_HOSTED__=1` is retained for compatibility, not as a promise of a supplied libc.

Only the bundled include directory is searched automatically. The old implicit
host `/usr` include paths are removed for all targets; supply external headers
explicitly with `-I`/`-idirafter`. There is no SDK or sysroot discovery.

## Command line

| Option | Meaning |
| --- | --- |
| `/out:file` | Output path; otherwise use the first object's base name. |
| `/entry:symbol` | Managed entrypoint, by exact COFF name or unambiguous global method name. |
| `/dll` | Produce a managed DLL; implies `/noentry`. |
| `/noentry` | Accepted with `/dll`. Native DLL startup is not supported. |
| `/subsystem:console` or `/subsystem:windows` | Set the PE subsystem. |
| `/machine:x64` | Validate the supported target machine. |
| `/debug` | Emit a sidecar Portable PDB and matching PE debug-directory entries; default to `/opt:noref`. |
| `/debug:embedded` | Embed the Portable PDB in the output PE instead of writing a sidecar; default to `/opt:noref`. |
| `/pdb:file` | Override the sidecar PDB path (default: the output path with a `.pdb` extension). Ignored without `/debug`, like link.exe, and in embedded mode. |
| `/opt:ref` or `/opt:noref` | Remove unreferenced COMDATs, or retain them; REF is the default without `/debug`. |
| `/opt:noicf` | Disable identical-code folding (the only supported ICF policy). |
| `/include:symbol` | Retain a managed symbol and its dependencies. |
| `/libpath:directory` | Search this directory for bare library names, before `LIB`. Repeat for additional directories. |
| `/nodefaultlib[:name]` | Suppress input default-library directives. |
| `/defaultlib:name` | Accepted only when suppressed by `/nodefaultlib`; default-library resolution is not implemented. |
| `/nologo`, `/?`, `/help` | Banner compatibility and usage. |
| `@file` | Read arguments from a response file, including quoted paths. |

Input objects may use `.obj` or `.o` extensions; archives use `.lib`.
Switch names are case-insensitive
and accept `/` or `-` prefixes; symbol names are case-sensitive.

The bare positional argument `mscoree.lib` is ignored without being opened,
including in response files and regardless of case. This accommodates chibil's
automatic linker argument; pure IL needs no native CLR bootstrap library.
Path-qualified `mscoree.lib` inputs are not special, and `/defaultlib:mscoree.lib`
still requires explicit suppression. Other libraries are real inputs: missing
or malformed archives are errors even when they would supply no retained code.
`/defaultlib` options and object directives still require explicit suppression.
Other unsupported switches, directives and required input features are errors.

An explicit `/opt:ref` or `/opt:noref` overrides the `/debug` default regardless
of argument order. The last `/debug` or `/debug:embedded` selects sidecar or
embedded output, respectively.

### Debug information

`/debug` translates chibil's C13 CodeView into a Portable PDB using
System.Reflection.Metadata. It preserves source paths and available SHA-256
checksums, source lines, local-variable names and slots, lexical scopes present
in the object, and parameter names in the assembly's metadata. Objects without
debug information are allowed. Per-function debug COMDATs use their object's
shared file tables; discarded functions and losing COMDAT definitions do not
contribute debug information. Debug records never retain code or select library
members.

With `/debug`, the PE contains a CodeView reference to the resolved sidecar PDB
path. `/pdb:` also works through response files and accepts quoted paths.
`/debug:embedded` instead stores the Portable PDB in the PE, uses the output
basename with a `.pdb` extension for its CodeView reference, and ignores `/pdb:`
entirely. It does not write or remove a sidecar PDB. Both modes include a separate
SHA-256 checksum entry for the uncompressed Portable PDB and use the same PDB
serialization, source mappings, and metadata identities.

Malformed debug information needed by retained methods is an error; unsupported
source-bearing records produce warnings instead of silently losing information.
General MSVC debug records, `.debug$T` types, native globals, and embedding source
files are not supported.

Use `/Zl /d1clrNoPureCRT` when compiling managed-only MSVC fixtures: `/Zl`
omits default-library directives, and `/d1clrNoPureCRT` suppresses the native CRT
initializer dependency. `/nodefaultlib` can also suppress default libraries,
but does not implement the native initialization code.

### Libraries

For a bare library filename, chilink searches its working directory, each
`/libpath` directory in argument order, then the semicolon-separated directories
in `LIB`. A qualified path is used as given, without search-directory fallback.
`LIB` is read once per invocation. Response files support quoted library paths
and `/libpath` directories containing spaces.

Explicit objects are always loaded. Libraries are searched in command-line
order and supply whole object members on demand; unused members do not become
managed roots or contribute linker directives. A selected member's dependencies
search its own library first, then following libraries, then wrap to earlier
libraries. Libraries can thus have transitive and cyclic dependencies without
requiring repeated arguments. A selected member is loaded only once.

Already-loaded definitions take precedence. A common definition does not force
extraction of a strong archive definition; if that member is selected for
another symbol, its strong definition replaces compatible common storage.
The normal COMDAT, duplicate-definition and metadata checks apply to selected
members. Archive selection and `/opt:ref` stripping are separate: unresolved
managed globals in a selected object can still fail even in dead COMDATs.
Discarded data-address relocations do not select members.

Archive entry/include roots can use exact COFF symbols. The convenient global
metadata-name fallback works for loaded objects; chilink does not open every
unused member to discover metadata-name aliases. Microsoft archives must have
the supported second linker-member symbol index.

### Native import libraries

Named x64 function imports in short COFF import members become static DllImport
methods, not native code. The DLL and exact export name come from the import
record, including its name normalization; neither is inferred from the `.lib`
filename. The unresolved global MemberRef supplies the signature and
native calling-convention modifiers. These are preserved and translated into
the P/Invoke mapping, with exact spelling and `PreserveSig`.

Ordinary C managed-entry names are matched to their native C export names;
explicit decorated-name identities and assisted UCO address references are
also supported. Purely managed `__clrcall` declarations are not native imports.
Conflicting declarations of an imported symbol are errors. The caller is
responsible for native ABI/layout compatibility and the runtime's marshalling
behavior; import libraries contain no signature information with which to
verify those assumptions. No ABI adapters are synthesized.

Direct imports work on .NET Framework and modern .NET for signatures supported
by that runtime. Selected ordinal and data/const imports are errors; unused
unsupported imports and native descriptor objects in the library are harmless.
Existing P/Invoke/FieldMarshal metadata in input objects is not implemented.
Native static-library method bodies are not converted to IL.

For example, compile a declaration/call of `GetCurrentProcessId` into `process.obj`
and supply the Windows SDK import library:

```powershell
chilink /entry:main /out:process.exe process.obj kernel32.lib
```

For native function addresses, compile with `-fnetcore -fuco-entrypoints`.
Retained deferred slots are initialized with actual native export addresses,
not `ldftn` of a DllImport method, a UCO wrapper, or an IJW thunk. The caller
must provide the managed CRT resolver symbol `__chilink_get_native_export`
as an object or a member of an explicit library. Its exact signature is static
`native int (string dllName, string entryPoint)`.

The implementation is included in the modern-.NET build of `crt.dll`. It uses
assembly-aware `NativeLibrary.Load` and `NativeLibrary.GetExport`, caches
handles with thread-safe access for process lifetime, and propagates load/export
errors. The Framework build excludes the resolver. The CRT object also contains
startup and its `__CxxPureMSILEntry` reference, which chibil supplies for C `main`.

```powershell
dotnet build .\crt\crt.csproj -f net10.0
dotnet run --project .\tools\asm2obj\Asm2Obj.csproj -- .\crt\bin\Debug\net10.0\crt.dll --machine x64 -o crt.obj
chilink /entry:main /out:process.dll process.obj crt.obj kernel32.lib
```

The resolver is needed only for retained native-address slots; direct calls and
discarded address slots need no resolver. An address-only import need not emit
a DllImport method. Missing or incorrectly typed resolvers fail during linking.
Legacy/MSVC IJW and Framework native-address paths remain unsupported.
The existing module-initializer conflict rule also applies to native-address
initialization.

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
  requires `System.Runtime.CompilerServices.FixedAddressValueTypeAttribute`
  only when retained input code or data takes the field's address.
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
- Native-convention function pointers from chibil's opt-in
  `-fnetcore -fuco-entrypoints` mode. The compiler replaces native IJW thunks
  with IL UCO forwarding methods, and their initialized pointer slots use the
  same managed TOKEN-fixup lowering.
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

Converted fields must carry
`System.Runtime.CompilerServices.FixedAddressValueTypeAttribute` only when their
storage address is taken by `ldsflda` in retained input IL or by a retained data
address relocation. Loading or storing a field's value does not require the
attribute, even when that value is a data or function pointer. Initializer
destinations remain GC-tracked managed byrefs, so linker-generated writes do not
impose the requirement; data-address relocation targets still need stable storage.
Discarded address-taking code and data do not impose it either.

The attribute keeps GC-allocated value-type statics from moving after C code
takes their addresses. Only its namespace and name are checked, not its assembly
identity; annotations on coalesced definitions are retained. Compile chibil
inputs with `-ffixed-address-statics` to emit the attribute on user-defined
globals. `chibil` target presets enable it; otherwise the switch is opt-in.
An explicit `-fno-fixed-address-statics` disables it even with a preset.
Selecting `-fuse-ld=chilink` does not enable it automatically. Read-only fields
that retain their RVA storage do not require the attribute, even when their
addresses are taken.

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
flags, legacy native NEP/UNEP dependencies, native method bodies, default-library
resolution, `/wholearchive`, ordinal/data imports, input P/Invoke/marshalling
metadata, native exports, native Windows PDBs, manifest resources, security
metadata, properties/events, bigobj,
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
Without UCO mode, native MEP slots retain `0x09`/`0x0A`; native function addresses
still use native entrypoint relocations and remain unsupported by chilink.
Unsupported relocation kinds and targets fail explicitly.

### UCO-assisted native function pointers

Compile matching function definitions and address-taking inputs with
`-fnetcore -fuco-entrypoints`, or select a modern .NET `chibil` target such as
`--target=x86_64-dotnet-windows`. The feature requires modern .NET and is
otherwise opt-in; neither `-fnetcore` nor `-fuse-ld=chilink` alone enables it.
`-fno-uco-entrypoints` disables it, and the final runtime/feature options win.
Modern .NET `chibil` presets also enable fixed-address statics, field-backed
aggregates, and emulated varargs. Without a preset, use
`-ffixed-address-statics` independently when converted globals require it.

The compiler emits an ordinary managed implementation and, for each live
native-convention definition, a static IL forwarding method with
`UnmanagedCallersOnly` and matching `CallConvs`. The wrapper has the former
NEP's bare COFF name and a distinct `__uco@`-prefixed metadata name; static
functions retain translation-unit scoping. Direct calls, recursion, and CRT
startup use the ordinary implementation. Runtime address-taking loads a lazily
generated `__unep@...` field, and indirect calls keep their unmanaged convention
and GC transitions. `__clrcall` addresses continue using `ldftn` of ordinary
managed methods.

Assisted chibil objects have no native NEP thunks or native MEP fixups.
Their `__unep` fields use TOKEN relocations and pointer-width-only managed fixup
records targeting wrapper tokens, as do native function-pointer global
initializers. Each generated field's fixup COMDAT is associated with its storage.
chilink replaces these fixups with `ldftn` stores in the module initializer and
emits no final-image vtfixups. Generated `__unep` fields do not need
`FixedAddressValueTypeAttribute`: input code loads their values, not their storage
addresses, and initialization keeps destination addresses GC-tracked.
Retained runtime address slots require a generated module initializer
even in programs without initialized user globals; an existing module initializer
still conflicts with this synthesis. The runtime provides the native-callable
entrypoints; no native thunk bytes are embedded in the IL-only image.

This indirection leaves address materialization to the linker without rewriting
input method bodies. A UCO-address MemberRef has a `__uco@`-prefixed metadata name
and a `DecoratedName` attribute identifying its bare COFF symbol.
Import-library bindings use this contract to initialize the slot through the CRT
resolver with the actual native export address. Unresolved UCO-address references
still fail, and ordinary ADDR64 relocations to method bodies remain unsupported.

UCO methods remain token-resolvable but are excluded from chilink's COFF-name
index. `/entry:main` therefore selects the ordinary implementation, not its
wrapper. `/include:main` can also select that implementation by metadata name;
to retain the wrapper explicitly, use `/include:__uco@main`. Ordinary
metadata-name lookup and entrypoint validation are unchanged.

The mode encodes `_Bool` as `System.Byte` throughout metadata while preserving
C normalization and one-byte storage. Its managed signatures containing `_Bool`
are not compatible with MSVC or unassisted chibil's Boolean signatures.
Matching signatures are required across input boundaries; adapters are not
synthesized. UCO arguments and returns must be blittable and native ABI layouts
must match. This does not add support for old retained NEP/UNEP dependencies,
native code or native exports, and link.exe
compatibility for assisted objects is not promised.

```powershell
dotnet run --project .\chibil\Chibil.csproj -- --target=x86_64-dotnet-windows program.c -o program.dll "-Wl,/entry:main"
```

As with link.exe, an unresolved managed extern can still be an error when the
function referencing it would otherwise be discarded by `/opt:ref`. Metadata
binding and section retention are separate operations.

## Tests

Tests exercise behavior end to end. `CompilationBuilder.Link` uses the chilink
driver. `MsvcLink` uses link.exe, including `mscoree.lib`, for excluded features.
Tests using `Link` do not also run `MsvcLink`, and there is no automatic fallback.
`LinkOnNetCoreOrMsvcLinkOnFramework` selects chilink for modern .NET and link.exe
for Framework, without retrying a failed link. Native function-pointer tests
use this helper when Framework still needs IJW; cases supported on both targets
use `Link` directly. New coverage prefers compile/link/run behavior over
generated IL or metadata assertions.
Native-import fixtures use cl.exe/link.exe to produce their DLL/import library;
automated tests do not invoke lib.exe. Managed archive extraction is checked
manually with libraries containing used, unused, transitive and conflicting
members, including a resolver supplied as an archive member.
The compilation helper enables `-ffixed-address-statics`; tests for unannotated
inputs override it with `-fno-fixed-address-statics`.
Modern-.NET test compilation also enables `-fuco-entrypoints` by default.
Disabled-mode and genuinely legacy fixtures explicitly override it with
`-fno-uco-entrypoints`.

Run `tests\Chibil.Tests\Chibil.Tests.csproj` in a vcvarsall x64 environment,
locating Visual Studio with vswhere. The existing default, field-backed
aggregate and optimized configurations cover both supported chilink cases and
the retained MSVC cases:

```text
dotnet test tests\Chibil.Tests\Chibil.Tests.csproj
dotnet test tests\Chibil.Tests\Chibil.Tests.csproj -p:FieldBackedAggregateTests=true
dotnet test tests\Chibil.Tests\Chibil.Tests.csproj -p:OptimizedTests=true
dotnet test tests\Chibil.Tests\Chibil.Tests.csproj -p:NetCoreTests=true
dotnet test tests\Chibil.Tests\Chibil.Tests.csproj -p:NetCoreTests=true -p:FieldBackedAggregateTests=true
dotnet test tests\Chibil.Tests\Chibil.Tests.csproj -p:NetCoreTests=true -p:OptimizedTests=true
```
