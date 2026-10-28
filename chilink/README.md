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
chilink -o hello.exe -e main hello.obj

dotnet run --project .\chibil\Chibil.csproj -- --target=x86_64-netfx-windows-chibil hello.c -o hello.exe "-Wl,-e,main"
dotnet run --project .\chibil\Chibil.csproj -- --target=x86_64-dotnet-windows hello.c -o hello.dll "-Wl,--entry=main"
```

chibil launches the executable named by `-fuse-ld=<name>` (resolved through PATH
when no path is supplied); it does not discover or install the linker.
With no target selected, chibil uses link.exe on Windows and chilink on Linux. `chibil` targets select chilink;
see [target presets](#target-presets) below. Executables named `link` or
`link.exe` (including full paths, case-insensitively) receive MSVC-style arguments;
other linker names and custom wrappers receive ld-style arguments.
Chilink receives `-o OUTPUT`, without `mscoree.lib` or a debug-enabling switch.
Portable PDB information is embedded by default. Link.exe retains
`/DEBUG`, `/subsystem:console`, `/out:` and `mscoree.lib`.
Compiler-owned `-shared`, `-L` and `-l` settings are rendered for the selected
linker, but `-Wl,` and `-Xlinker` arguments are forwarded without translation:
use that linker's native syntax. Pass `-Wl,--gc-sections` to chilink to enable
dead stripping; debug output does not change the GC policy.

The linker itself does not require Visual Studio or `mscoree.lib`. Executing
Framework-targeting images requires a compatible runtime. Modern .NET images
require a separately supplied runtimeconfig. The target does not select an
entry point or provide a CRT automatically. MSVC input
generation and MSVC-only tests still need Visual Studio.

On Linux, with both tools on PATH, a parameterless `int main(void)` can be built
using the host defaults:

```sh
dotnet build chilink/Chilink.csproj
dotnet build chibil/Chibil.csproj
export PATH="$PWD/chilink/bin/Debug/net10.0:$PWD/chibil/bin/Debug/net10.0:$PATH"
Chibil hello.c -o hello.dll -Wl,-e,main
```

Supply `hello.runtimeconfig.json` before running `dotnet hello.dll`, for example:

```json
{
  "runtimeOptions": {
    "tfm": "net10.0",
    "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" }
  }
}
```

## Target presets

Chibil accepts `--target=cpu-runtime-os[-abi]` or
`--target cpu-runtime-os[-abi]`. The runtime is `dotnet` or `netfx`;
the ABI is `chibil` (chibil/chilink's private managed ABI) or `msvc`
(MSVC `/clr` compatibility). An omitted ABI means `chibil` for `dotnet`
and `msvc` for `netfx`.

| Target | Explicit alias | Linker | C data model |
| --- | --- | --- | --- |
| `x86_64-netfx-windows` (Windows default) | `x86_64-netfx-windows-msvc` | `link` | LLP64 |
| `x86_64-dotnet-windows` | `x86_64-dotnet-windows-chibil` | `chilink` | LLP64 |
| `x86_64-dotnet-windows-msvc` | None | `link` | LLP64 |
| `x86_64-netfx-windows-chibil` | None | `chilink` | LLP64 |
| `x86_64-dotnet-linux` (Linux default) | `x86_64-dotnet-linux-chibil` | `chilink` | Managed LP64 |

Only these lowercase spellings are supported; no new CPUs are enabled.
The default follows the host OS; explicit targets are independent of the host.
Both `chibil` runtimes enable `-ffixed-address-statics` and
`-fmanaged-aggregate-fields`. Modern .NET `chibil` also enables
`-fuco-entrypoints` and `-femulated-varargs`. MSVC presets leave all four
features disabled and only select the requested runtime binding.
Emulated varargs use a private argument-buffer ABI, not native varargs;
their `va_list` layout follows the target OS. Native variadic imports and
function-pointer calls are lowered separately; only pointers to emulated
definitions require `__clrcall`. See [variadic calls](#variadic-calls).

The last `--target` wins, but every supplied target must be valid.
Explicit low-level switches override preset defaults regardless of position;
the last explicit switch for a setting wins. These include
`-fnetcore`/`-fnetframework`, `-fuse-ld=`, and the positive/negative forms of
the four features above. Overrides do not recompute other preset settings:
modern `chibil` plus `-fnetframework` also requires `-fno-uco-entrypoints`.
Linux requires an effective modern .NET runtime.
Expert overrides may produce incompatible combinations; changing the linker
does not change the selected ABI or C layout.
Overrides apply to the host's default preset when `--target` is omitted.
On Windows, existing switches retain their behavior:
`-fnetcore` alone does not select `chibil`, and `-fuse-ld=chilink` alone does
not enable its emission features.

For a parameterless `int main(void)`, with chibil and the selected linker on PATH:

```powershell
chibil --target=x86_64-netfx-windows hello.c -o hello.exe "-Wl,/entry:main"
chibil --target=x86_64-dotnet-windows hello.c -o hello.dll "-Wl,-e,main"
chibil --target=x86_64-dotnet-linux hello.c -o hello-linux.dll "-Wl,-e,main"
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
| `-o file`, `--output=file` | Output path; default `a.exe`, including shared output unless explicitly named. |
| `-e symbol`, `--entry=symbol` | Managed entrypoint, by exact COFF name or unambiguous global method name; default `mainCRTStartup`. Numeric native addresses are not supported. |
| `-shared`, `--shared` | Produce a managed library without an entry. Native DLL startup is not supported. |
| `--subsystem=console`, `--subsystem=windows` | Set the PE subsystem; default console. |
| `-s`, `--strip-all`, `-S`, `--strip-debug` | Omit embedded PDB information; an explicitly requested external PDB is still generated. |
| `--pdb=file` | Generate an external Portable PDB instead of embedding it. An empty filename (`--pdb=`) uses the output path with a `.pdb` extension. |
| `--target=TRIPLE` | Select Windows or Linux varargs lowering using a target spelling from the table above; default follows the host OS. |
| `--gc-sections`, `--no-gc-sections` | Remove unreferenced COMDATs, or retain them; disabled by default. |
| `-u symbol`, `--undefined=symbol` | Retain a managed symbol and its dependencies. |
| `-L directory`, `--library-path=directory` | Add a library search directory; repeat for additional directories. |
| `-l name`, `--library=name` | Search for an archive or ELF import provider by name; `-l:filename` selects an exact filename. |
| `--help` | Print usage without requiring inputs. |
| `--` | End option parsing. |
| `@file` | Read arguments from a response file, including quoted paths. |

Short options taking values accept separate or joined arguments (`-e main`,
`-emain`). Long options taking values accept a separate argument or `=`
(`--entry main`, `--entry=main`). Option names are case-sensitive, with no
abbreviations or link.exe-style compatibility aliases. Absolute Unix paths
beginning with `/` are inputs. Input objects use `.obj` or `.o`; archives use
`.lib` or `.a`, but still require the supported COFF archive format.

The last `--gc-sections` or `--no-gc-sections` wins, independently of debug output.
Portable PDB information is embedded by default, without an enabling switch.
Stripping suppresses embedded debug information, but does not cancel an explicit
`--pdb` request, regardless of argument order. The last `--pdb` chooses the
external filename. `--debug` and `--debug=embedded` are not supported.
The PDB format remains Portable PDB, not GNU ld's native Windows PDB format.
Response files preserve Windows backslashes and double-quoted paths,
including spaces, with nested expansion and cycle detection; this is not a
complete GNU response-file lexer.
File-path comparisons follow the host: case-insensitive on Windows and
case-sensitive on Linux, including response-file cycle detection, archive
deduplication, and output/PDB collision checks.

Chibil forwards its selected target to chilink. When linking separately, pass
the same `--target` used for compilation, especially when cross-compiling.
Chilink accepts the compiler's supported target spellings, but uses only their
OS component to select varargs lowering; it does not change input runtime
bindings, library search paths, the PE subsystem, or the output format.
The last `--target` wins; invalid targets are errors.

Object `.drectve` sections are ignored entirely, including embedded
default-library, entry and root instructions. Supply required libraries and
roots explicitly. There are no `/defaultlib` or `/nodefaultlib` CLI replacements.
All positional libraries, including `mscoree.lib`, must resolve as real files;
missing or malformed archives are errors even if unused. Other unsupported
switches and required input features remain errors.

Subsystem is a PE property, not a link.exe CLI requirement.
[MinGW GCC](https://gcc.gnu.org/onlinedocs/gcc/Cygwin-and-MinGW-Options.html)
defaults to `-mconsole` and offers `-mwindows`; its
[GNU linker](https://sourceware.org/binutils/docs/ld/WIN32.html) supports
`--subsystem`. Chilink likewise defaults to console without requiring an option,
including for Linux-targeted managed assemblies, which remain PE rather than ELF.
The standalone `a.exe` default follows GNU's PE linker convention; chibil still
supplies an explicit runtime-dependent output name.

### Debug information

By default, chilink translates C13 CodeView into an embedded Portable PDB using
System.Reflection.Metadata. It preserves source paths and available SHA-256
checksums, source lines, local-variable names and slots, lexical scopes present
in the object, and parameter names in the assembly's metadata. Objects without
debug information are allowed. Per-function debug COMDATs use their object's
shared file tables; discarded functions and losing COMDAT definitions do not
contribute debug information. Debug records never retain code or select library
members.

Embedded output uses the output basename with a `.pdb` extension for its CodeView
reference, without writing or removing a sidecar PDB.
`--pdb=FILE` selects external output instead, and the PE contains a CodeView
reference to the resolved sidecar path. `--pdb` also works through response files
and accepts quoted paths, with either `--pdb FILE` or `--pdb=FILE`.
Both modes include a separate
SHA-256 checksum entry for the uncompressed Portable PDB and use the same PDB
serialization, source mappings, and metadata identities.

`-s`, `-S`, `--strip-all` and `--strip-debug` all disable embedded PDB generation.
Without an explicit `--pdb` request, stripped images have no debug-directory
entries and no PDB is generated. As with MinGW's GNU linker, an explicit external
PDB request remains effective when stripping, independent of option order.
Neither stripping nor external PDB selection changes section GC.

Malformed debug information needed by retained methods is an error; unsupported
source-bearing records produce warnings instead of silently losing information.
General MSVC debug records, `.debug$T` types, native globals, and embedding source
files are not supported.

Use `/Zl /d1clrNoPureCRT` when compiling managed-only MSVC fixtures: `/Zl`
omits default-library directives, and `/d1clrNoPureCRT` suppresses the native CRT
initializer dependency. Ignoring `.drectve` does not implement native initialization.

### Libraries

Positional paths are opened as given, relative to the working directory when
not absolute. On Windows, `-lfoo` searches each explicit `-L` directory in argument
order for `libfoo.dll.a`, `foo.dll.a`, `libfoo.a`, `foo.lib`, then `libfoo.lib`.
On Linux, the filename order is `libfoo.so`, `libfoo.a`, `libfoo.dll.a`,
`foo.dll.a`, `foo.lib`, then `libfoo.lib`. Directory order takes precedence
over filename order.
`-l:filename` searches for only that filename. All `-L` directories are collected
before resolving requests, so their position relative to `-l` does not matter.
On Linux, explicit directories are followed by these built-in x64 directories:

```text
/usr/local/lib/x86_64-linux-gnu
/lib/x86_64-linux-gnu
/usr/lib/x86_64-linux-gnu
/usr/local/lib64
/lib64
/usr/lib64
/usr/local/lib
/lib
/usr/lib
```

These are conventional defaults, not a query of the installed linker or its
configuration. Other hosts retain explicit-only discovery. There is no implicit
cwd, `LIB`, `LIBRARY_PATH`, SDK, or runtime-loader-cache search; pass `-L.` to
search cwd. Response files support quoted paths and directories containing
spaces. No sysroot, rpath options, or highest-version `.so.N` probing is provided.

Explicit objects are always loaded. Libraries are searched in command-line
order and supply whole object members on demand; unused members do not become
managed roots. Embedded linker directives are ignored in selected members too.
A selected member's dependencies search its own library first, then following
libraries, then wrap to earlier
libraries. Libraries can thus have transitive and cyclic dependencies without
requiring repeated arguments. A selected member is loaded only once.

Already-loaded definitions take precedence. A common definition does not force
extraction of a strong archive definition; if that member is selected for
another symbol, its strong definition replaces compatible common storage.
The normal COMDAT, duplicate-definition and metadata checks apply to selected
members. Archive selection and `--gc-sections` stripping are separate: unresolved
managed globals in a selected object can still fail even in dead COMDATs.
Discarded data-address relocations do not select members.

Archive entry/undefined roots can use exact COFF symbols. The convenient global
metadata-name fallback works for loaded objects; chilink does not open every
unused member to discover metadata-name aliases. Microsoft archives must have
the supported second linker-member symbol index. Accepting `.a` filenames does
not add support for arbitrary GNU archive indexes, ELF members or direct DLL
linking. This is an ld-like CLI, not a full GNU ld implementation.

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
verify those assumptions. Apart from the varargs lowering described below,
no ABI adapters are synthesized.

Direct imports work on .NET Framework and modern .NET for signatures supported
by that runtime. Selected ordinal and data/const imports are errors; unused
unsupported imports and native descriptor objects in the library are harmless.
Existing P/Invoke/FieldMarshal metadata in input objects is not implemented.
Native static-library method bodies are not converted to IL.

For example, compile a declaration/call of `GetCurrentProcessId` into `process.obj`
and supply the Windows SDK import library:

```powershell
chilink -e main -o process.exe process.obj -L .\libs -lkernel32
```

### ELF shared libraries

An ELF shared library is itself the native import provider; no separate import
archive is needed. Both `.so` and versioned `.so.N` positional paths are accepted
by chilink and the compiler driver. Inputs must be little-endian x86-64 ELF64
shared objects with section tables. Ordinary stripped libraries work, but
section-header-free libraries, extended section indexes, PIE executables, and
native ELF object/archive linking are not supported.

Defined global/weak function exports with default or protected visibility can
provide imports, including GNU indirect functions. Undefined, local, hidden,
internal, and non-default-only versioned symbols are excluded. Only unversioned
or default-version exports are matched by plain name; explicit symbol-version
imports are not supported. Data exports of kind `STT_OBJECT` or `STT_COMMON`
are supported through the protocol below. Selected TLS or other unsupported
export kinds are errors; unused such exports are harmless. Signatures and calling conventions
still come from the managed caller; only the varargs-specific adaptations
described below are added, not general native ABI/layout adapters.
ELF export names are exact and do not acquire COFF `__imp_` aliases.

#### Data-export access protocol

With `-ffixed-address-statics`, chibil accesses every C global through
`ldsflda <field>` followed by the existing indirect load/store operations
(`ldind.*`, `stind.*`, `ldobj`/`stobj`, or aggregate copies as appropriate).
The field MemberRef remains the symbol identity in the COFF object. C global
accesses do not use `ldsfld`, `stsfld`, or `ldtoken` in this mode. Compiler-generated
function-address slots and linker-generated initializer code are separate from
this C-global protocol and may still load/store their slot fields directly.
MSVC-profile defaults and explicit `-fno-fixed-address-statics` retain their
existing direct scalar loads/stores.

When a field MemberRef resolves to an ELF data import, chilink requires every
retained code TOKEN relocation for it to be immediately preceded by `ldsflda`
(`0x7F`). Any other opcode is a link error, for example:

```text
foo.obj: field 'x' resolves to native data import in 'libc.so.6'; object accesses it with ldsfld (compile with -ffixed-address-statics)
```

Chilink creates one static native-int `<Module>` field named
`__chilink_data@<symbol>` per live data import. It rewrites each such access
to `ldsfld` (`0x7E`) with the slot's FieldDef token. Both instructions occupy
one opcode byte and four token bytes, so existing IL offsets, branch targets,
exception regions, max-stack values, and PDB sequence points are unchanged.
The resulting native-int address replaces a managed byref; this relies on the
unverifiable pointer IL already used by chibil. No fake accessor methods are
introduced. Managed field definitions are not rewritten into native imports.
Binding a method to a data export, or a field to a function export, is an error.

Retained ADDR64 data relocations such as `int *p = &counter;`,
`int *q = &arr[1];`, and `FILE **pp = &stdout;` use the same address slot,
including their pointer addends. A data-only reference need not have a field
MemberRef. Dead COMDAT accesses/initializers do not require live slots or a
runtime resolver. An opt-out object that only takes the address can satisfy the
opcode contract; chilink checks the actual instructions, not a compiler flag.

Supply an asm2obj-converted modern-.NET CRT object explicitly. Its
`__chilink_get_native_data_export` resolver must be a static managed
`native int(string, string)` method. Without it, a live data import fails with
`data import 'x' requires a native export resolver`. The Framework CRT does not
provide this resolver; COFF `IMPORT_OBJECT_DATA`/const imports and ELF TLS remain
unsupported.

The generated module `.cctor` resolves all data slots before copying global
initialization data and applying address relocations, and runs before user
`<Module>` code accesses them. Slots contain the exported storage address,
not a copy of its contents: for glibc `stdout`, the address identifies the
native `FILE *` variable, whose value is loaded indirectly.
An existing input `<Module>..cctor` still conflicts with synthesized initialization.

Data resolution first loads/caches the provider library, then tries
`NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), symbol)`,
falling back to that library's handle. On Linux this global-scope lookup honors
host COPY relocations, so `stdout`, `stderr`, `environ`, and other copied data
refer to the storage used by the native executable. Function resolution remains
library-specific. Missing libraries or exports fail explicitly at runtime.
Libraries remain cached for the lifetime of the linked assembly's resolver.
The caller remains responsible for native storage layout and ABI compatibility.

#### Library identity and linker scripts

`DT_SONAME`, when present, supplies the library name for both P/Invoke methods
and native-address resolution. Without SONAME, `-lfoo` records `libfoo.so` and
`-l:filename` records the searched filename. A positional input instead retains
its supplied path, including relative directories or an absolute path, matching
GNU ld's fallback. Repeated references to the same resolved input use the first
input's identity. Filesystem symlinks do not replace that identity with their
target filename.

The limited GNU linker-script subset used by glibc's `libc.so` and `libm.so`
is supported, so `-lc -lm` can select their actual shared libraries. Supported
commands are `INPUT(...)`, `GROUP(...)`, and `OUTPUT_FORMAT(...)`,
with nested `AS_NEEDED(...)` input lists. C-style comments, double-quoted
filenames, whitespace/comma-separated lists, and command-ending semicolons
are accepted. For example:

```text
OUTPUT_FORMAT(elf64-x86-64)
GROUP ( libc.so.6 libc_nonshared.a AS_NEEDED ( ld-linux-x86-64.so.2 ) )
```

`INPUT`, `GROUP`, and `AS_NEEDED` contribute ordered import providers; only
selected exports become runtime imports. They do not introduce native ELF
output, native archive rescanning, or `DT_NEEDED` retention semantics.
Script references ending in `.a` are skipped with a warning, without inspecting
their members. Symbols available only in those archives remain unresolved;
chilink cannot convert native static code to IL. Explicit command-line archives
still use normal COFF archive handling.

Absolute script filenames are opened directly. Other filenames are searched
first relative to the script, then cwd, then explicit `-L` and built-in
directories. Script `-lname`/`-l:filename` references use the normal library
search paths. Referenced scripts are expanded in place, with cycle and nesting
checks. Missing inputs, malformed syntax, and unsupported commands are errors,
even for unused providers. `INCLUDE`, `SEARCH_DIR`, `SECTIONS`, symbol
assignments and sysroot paths are not supported. `OUTPUT_FORMAT` arguments
are ignored; the ELF reader still validates each library's actual format and
target. The directive does not change chilink's managed PE output.

Runtime identities come from the referenced ELF libraries, never the script.
SONAME takes precedence; without it, script file references use their resolved
absolute path and script `-l` references use the searched filename. Explicit
`-l:libc.so.6`/`-l:libm.so.6` remain available to bypass scripts. Dependencies
named by `DT_NEEDED` are not scanned for additional import providers.

The linker reads ELF metadata without loading or executing the native library.
It still emits a managed PE assembly, not ELF. `-L` affects link-time discovery
only: it neither deploys libraries nor configures runtime loading. Deploy the
native library under its runtime identity where .NET/the OS loader can find it.

For example, create `native.c` and `main.c`:

```c
/* native.c */
int answer(void) { return 42; }
```

```c
/* main.c */
int answer(void);
int main(void) { return answer(); }
```

Then build and run on Linux with .NET 10 and a native C compiler:

```sh
cc -shared -fPIC native.c -Wl,-soname,libanswer.so.1 -o libanswer.so.1
ln -s libanswer.so.1 libanswer.so
dotnet run --project chibil/Chibil.csproj -- --target=x86_64-dotnet-linux -c main.c -o main.obj
dotnet run --project chilink/Chilink.csproj -- -e main -o answer.dll main.obj -L. -lanswer
printf '%s\n' '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' > answer.runtimeconfig.json
dotnet answer.dll
echo $? # 42
```

### Native function addresses

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
chilink -e main -o process.dll process.obj crt.obj -L .\libs -lkernel32
```

The resolver is needed for retained native-address slots and direct native
variadic calls on Linux. Ordinary direct calls and discarded address slots
need no resolver. An address-only import need not emit
a DllImport method. Missing or incorrectly typed resolvers fail during linking.
Legacy/MSVC IJW and Framework native-address paths remain unsupported.
The existing module-initializer conflict rule also applies to native-address
initialization.

EXE entrypoints must be static, non-generic methods returning `void` or `int`,
with no parameters or one `string[]` parameter. Without `--entry`, the managed
startup symbol is `mainCRTStartup`; chilink does not synthesize a CRT. The
repository's asm2obj-converted CRT supplies this startup method.

`-shared` produces a managed library without an entry: chilink never tries to link
native `_DllMainCRTStartup`. It cannot be combined with `--entry`.
Public managed types remain assembly roots in both EXEs and DLLs.
With `--gc-sections`, unreferenced global C functions in individual COMDATs can
disappear from a library; use `--undefined` roots or `--no-gc-sections` when they
must remain available through reflection.

### Variadic calls

Chibil emits CLR vararg call-site metadata rather than packing optional
arguments in the caller. Chilink lowers retained sites into ordinary,
fixed-signature IL thunks, selecting the implementation from the resolved
callee and the linker's target OS. Calls and external declarations do not
require `-femulated-varargs`; C variadic definitions and the `va_*` intrinsics
do. Modern .NET `chibil` presets enable that switch; Framework inputs can
enable it explicitly.

#### Object-file contract

A direct variadic `call` has a TOKEN relocation to a MemberRef with the CLR
`VARARG` calling convention (`0x05`). Its signature lists the fixed parameters,
then a sentinel (`0x41`) and the actual optional argument types after C default
promotions, including `float` to `double` and integer promotions. With no
optional arguments, there is no sentinel. Each distinct call-site signature
has its own MemberRef carrying the callee's `DecoratedName` COFF identity,
even for a definition in the same object. An indirect variadic `calli` instead relocates a
StandAloneSig with the same signature shape; its function address is pushed
after all arguments. Native conventions are identified by return-type
`modopt(System.Runtime.CompilerServices.CallConv*)` modifiers; `__clrcall`
sites have no native convention modifier.

An emulated definition is not a CLR vararg MethodDef. It is an ordinary static
managed method with its fixed parameters followed by
`modopt(System.ArgIterator) void*`. The modifier marks the hidden argument-area
parameter for chilink; it does not request CLR `ArgIterator` behavior. The
linker matches a vararg reference to this definition by name, return type and
fixed parameter types, excluding the hidden parameter and optional call-site
arguments.

For a site resolving to an emulated definition or a native import, and for
variadic `calli` sites, chilink synthesizes a private static
`__chilink_vararg@...` method accepting the exact call-site arguments.
An indirect site's thunk also takes a final native-int function address.
The linker patches the original instruction to `call` and replaces its token
with the thunk's MethodDef. Both `call` and `calli` occupy one opcode byte and
four token bytes, so this preserves input IL offsets, branches, exception
regions and PDB sequence points without decoding/re-encoding method bodies.
Thunk dependencies participate in retention; discarded sites need no thunk
or runtime resolver.

#### Emulated definitions and managed pointers

For an emulated definition or an indirect `__clrcall` call, the thunk allocates
an argument area with `localloc`, copies the optional arguments into it, then
passes the fixed arguments and its address to the implementation. Indirect
calls use an ordinary managed `calli` signature with a trailing `void*`.
The area lives only until the thunk returns and has at least eight bytes even
when there are no optional arguments.

On Windows x64, each optional argument occupies an eight-byte slot.
Aggregates of size 1, 2, 4 or 8 bytes are stored inline; other aggregates use
a slot pointing to the thunk's by-value argument copy. `va_list` is `char*`;
`va_start` points it at the hidden area, `va_arg` advances it by eight bytes
and dereferences aggregate-copy pointers when required, and `va_copy` copies
the cursor.

On Linux x64, arguments are stored inline in order, with each size rounded
up to eight bytes. Aggregates need explicit size metadata; over-aligned
aggregates with metadata packing greater than eight are rejected.
`va_list` is the SysV array of one 24-byte `__va_list_tag`:

```c
typedef struct __va_list_tag {
    unsigned int gp_offset;  /* offset 0 */
    unsigned int fp_offset;  /* offset 4 */
    void *overflow_arg_area; /* offset 8 */
    void *reg_save_area;     /* offset 16 */
} va_list[1];
```

`va_start` sets `gp_offset = 48`, `fp_offset = 176`,
`overflow_arg_area` to the hidden area and `reg_save_area` to null: all
emulated arguments are overflow arguments. `va_arg` also understands native
SysV lists, reading INTEGER/SSE register-save slots when available and falling
back to the overflow area for exhausted registers or memory-class aggregates.
`va_copy` copies the entire tag, giving an independent cursor; `va_end` is a
no-op on both targets. These target-native list representations allow lists
to be passed to native `vprintf`-style functions, and native lists to be read,
copied and forwarded by chibil code, subject to matching C layouts.

Emulated definitions have no native variadic entrypoint or UCO wrapper.
Taking their address requires `__clrcall`, including across translation units;
chilink rejects native-address references that resolve to such definitions.
External native variadic addresses are allowed and use the existing native
export-address protocol when materialized by chibil.

#### Native variadic calls

Native imports still come from explicit COFF import libraries or ELF shared
libraries. Declarations for the same import must agree on the return and fixed
parameter types; optional argument types may differ between call sites.

On Windows x64, chilink emits a fixed-signature P/Invoke per direct call site,
or an unmanaged cdecl `calli` for a native pointer. The thunk reinterprets
optional floating-point values as integer bits so they use integer
registers/stack slots rather than an XMM-only calling sequence. Optional
aggregates of size 1, 2, 4 or 8 are lowered to integer values; other sizes
are passed by address to a copy. This supports direct and indirect native
varargs on Framework and modern .NET without a CRT vararg resolver.

On Linux x64, the thunk uses an exact-signature unmanaged cdecl `calli`.
Direct imports first obtain the export address through
`__chilink_get_native_export`; both direct and indirect calls then pass the
address through `__chilink_get_vararg_stub`. Supply the asm2obj-converted
modern-.NET CRT explicitly, as an object or an explicit archive member.
The stub resolver must be a static managed `native int(native int)` method;
missing or incorrectly typed resolvers are link errors. An indirect call
does not need the export resolver unless address materialization needs it.

The CRT caches a runtime-generated executable stub per native target. It sets
`eax = 8` and tail-jumps to the target, preserving argument registers and the
stack while supplying SysV's `AL` upper bound on vector-register arguments.
Stub memory is allocated writable, then made read/execute, and retained for
process lifetime. Thus the linked assembly remains IL-only, but native Linux
varargs require executable-memory support at runtime. Native SysV aggregate
argument passing is not supported; reading aggregates from native `va_list`
values and forwarding emulated lists are separate supported operations.
General native ABI adapters, native variadic entrypoints for managed
definitions, and link.exe compatibility for this private lowering protocol
are not provided.

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
- ELF OBJECT/COMMON data imports through fixed-address-global access rewriting,
  with native-address slots initialized by the modern-.NET CRT resolver.
- Retained CLR vararg call sites lowered to fixed-signature thunks for emulated
  definitions, managed variadic pointers, and supported native variadic calls.
- Function COMDAT selection and section-granularity stripping. A shared section
  retains all its functions; `-ffunction-sections` permits separate removal.
  Chibil scopes static function metadata names to their translation unit using
  MSVC's `?A0x<hash>.<name>` format, so same-named static functions in different
  objects remain distinct. Their COFF symbol base names retain the
  `<name>_?A0x<hash>` format.
- Unused compiler-generated native-entrypoint sidecars are omitted. A retained
  dependency on them is not converted into managed code.

Retained IL sections are copied and their COFF TOKEN relocations are
patched with merged metadata tokens. Native-data accesses additionally replace
`ldsflda` with the same-width `ldsfld` instruction as described above.
Variadic sites replace their `call`/`calli` and token with a same-width `call`
to a synthesized thunk.
Method bodies are not decoded or re-encoded;
method RVAs come from the output section location plus the COFF symbol offset.
Vararg thunks and the generated module initializer are appended as new methods.
The initializer is emitted only when retained globals need copied
initialization data, pointer relocations, or
native-data slots; BSS/common globals themselves rely on CLR zero initialization.
Dead COMDAT initializers do not retain their dependencies.

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

Fixed-address chibil code takes the address even for a scalar global read/write.
Consequently, a fixed-address consumer of an unannotated MSVC `/clr` global can
now fail this validation where a direct value-only load previously linked.
Keep the definition annotated, or use `-fno-fixed-address-statics` for a
value-only consumer. Explicit address-taking still requires stable storage.
Annotations on coalesced managed definitions continue to be retained.

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
resolution, `/wholearchive`, ordinal and COFF data/const imports, ELF TLS imports, input P/Invoke/marshalling
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
index. `--entry=main` therefore selects the ordinary implementation, not its
wrapper. `--undefined=main` can also select that implementation by metadata name;
to retain the wrapper explicitly, use `--undefined=__uco@main`. Ordinary
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
dotnet run --project .\chibil\Chibil.csproj -- --target=x86_64-dotnet-windows program.c -o program.dll "-Wl,-e,main"
```

As with link.exe, an unresolved managed extern can still be an error when the
function referencing it would otherwise be discarded by `--gc-sections`. Metadata
binding and section retention are separate operations.

## Tests

Tests exercise behavior end to end. `CompilationBuilder.Link` uses the chilink
driver with semantic settings; `Chilink` accepts additional native chilink
arguments for linker-specific tests. `MsvcLink` uses link.exe, including
`mscoree.lib`, and accepts native MSVC arguments for excluded features.
Executable helpers default to entry `main` and console behavior; CRT tests use
`entryPoint: "mainCRTStartup"`. Shared output and output paths use named parameters.
The runtime-selecting helper takes semantic settings, not raw argument strings.
There is no link.exe-to-ld argument translator or globally forced section GC.
Use `Link(gcSections: true)` to opt into section GC for a test.
Tests using `Link` do not also run `MsvcLink`, and there is no automatic fallback.
`LinkOnNetCoreOrMsvcLinkOnFramework` selects chilink for modern .NET and link.exe
for Framework, without retrying a failed link. Native function-pointer tests
use this helper when Framework still needs IJW; cases supported on both targets
use `Link` directly. New coverage prefers compile/link/run behavior over
generated IL or metadata assertions.
Shared native-import coverage uses `NativeLibrary(source, exports)`, which
selects cl.exe/link.exe on Windows and `cc -shared -fPIC` on Linux, honoring
the same plain-name export list on both. `NativeRuntimePath` identifies the
runtime library when a test needs to remove or replace it. Use
`NativeWindowsLibrary` for Windows-specific export directives or import-archive
details and `NativeLinuxLibrary` for ELF-specific filenames, SONAME, visibility,
or symbol versions. The Windows system-CRT varargs test uses lib.exe to create
an import library. The ELF data tests build small managed COFF archives to
cover member selection and an archived
resolver. They also host a linked managed application in a native executable
with a verified `R_X86_64_COPY` relocation to check global-scope data lookup.
Broader archive extraction is checked manually with libraries containing
used, unused, transitive and conflicting members.
The compilation helper enables `-ffixed-address-statics`; tests for unannotated
inputs override it with `-fno-fixed-address-statics`.
Modern-.NET test compilation also enables `-fuco-entrypoints` by default.
Disabled-mode and genuinely legacy fixtures explicitly override it with
`-fno-uco-entrypoints`.

On Linux, run `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj` with .NET 10
and `cc` available. Modern-.NET tests are selected automatically; Windows-only
fixtures are skipped. Default, field-backed aggregate, and optimized
configurations run in Linux CI.

On Windows, run `tests\Chibil.Tests\Chibil.Tests.csproj` in a vcvarsall x64 environment,
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
