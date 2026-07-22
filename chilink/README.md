# chilink

`chilink` is a C# linker for the managed COFF object-file format used by
chibil, asm2obj, and MSVC `/clr:pure`. It produces a pure-IL Windows executable
using `System.Reflection.Metadata`.

```text
chilink /out:app.exe /entry:main /subsystem:console [/opt:ref] input.obj ...
```

## Goals

The long-term goal is compatibility with `link.exe` for managed COFF inputs,
not merely the ability to combine objects produced by the current version of
chibil.

In particular:

- COFF symbol, COMDAT, entry-point, and `/OPT:REF` behavior should follow
  `link.exe`.
- Objects produced by chibil, asm2obj, and the supported MSVC `/clr:pure`
  subset should interoperate.
- Metadata merging should follow CoreCLR's proven `newmerger.cpp` behavior.
- Unsupported options and input features must fail explicitly instead of being
  ignored or producing partially valid output.
- Input order is significant where it is significant to `link.exe`, such as
  selecting the first `Any` COMDAT contribution.

Byte-for-byte identity with `link.exe` output is not a goal. The executable's
linking semantics, metadata shape, and runtime behavior are.

## Reference implementations

The metadata merger is intentionally based on CoreCLR's native metadata merger:

```text
D:\git\coreclr\src\md\compiler\newmerger.cpp
D:\git\coreclr\src\md\compiler\newmerger.h
D:\git\coreclr\src\md\compiler\filtermanager.cpp
D:\git\coreclr\src\md\compiler\importhelper.cpp
D:\git\coreclr\src\md\inc\rwutil.h
D:\git\coreclr\src\md\enc\rwutil.cpp
D:\git\coreclr\Documentation\design-docs\metadata-merger.md
```

When extending `MetadataMerger`, copy the behavior of `newmerger.cpp`, including
its duplicate, additive, filtering, and token-mapping rules. Do not substitute
a more convenient interpretation of ECMA-335 metadata. The implementation may
reject tables that chilink does not support, but behavior that is implemented
should match `newmerger.cpp`.

The current deliberate exception is AssemblyRef identity: chilink treats a
matching simple assembly name as sufficient for its supported inputs.

`System.Reflection.Metadata` is used to serialize metadata and the final PE. It
does not implement COFF linking, COMDAT selection, dead stripping, or general
metadata merging; those are chilink responsibilities.

## Linking model

The top-level pipeline is:

1. Read every COFF input and its `.cormeta` metadata root.
2. Resolve ordinary symbols, CLR-token symbols, entry aliases, and COMDATs.
3. Compute live section contributions, including `/OPT:REF` reachability.
4. Plan final IL, immutable-data, and transformed global-field initialization.
5. Merge metadata, lower mutable FieldRVAs, and assign final metadata tokens.
6. Copy selected sections, synthesize `<Module>..cctor`, and apply token fixups.
7. Emit the pure-IL PE with `ManagedPEBuilder`.

The implementation is split along these boundaries:

| Component | Responsibility |
| --- | --- |
| `Driver` | link.exe-style command-line parsing |
| `CoffInput` | COFF sections, symbols, auxiliary records, relocations, and metadata |
| `SymbolResolver` | external symbols, CLR-token references, entry aliases, and COMDAT selection |
| `ReachabilityGraph` | section-granular `/OPT:REF` traversal |
| `SectionLayout` | deterministic placement and relocation of selected contributions and synthesized IL |
| `GlobalDataPlanner` | mutable/common field lowering and module-initializer construction |
| `MetadataMerger` | newmerger-compatible metadata selection, merging, and token maps |
| `ManagedPeEmitter` | final `System.Reflection.Metadata` PE serialization |

## Method bodies are opaque

`chilink` does **not** parse or re-emit IL method bodies.

Managed COFF producers place complete method bodies in COFF sections and emit
TOKEN relocations for every metadata token that the linker must rewrite. This
includes:

- instruction operands;
- local-variable signatures in fat method headers;
- user-string tokens;
- exception-handler catch-type tokens.

The linker copies each selected IL section contribution as an opaque byte
range, preserving its headers, padding, branch offsets, switch tables, and
exception sections. It then patches the locations identified by COFF
relocations.

MethodDef body offsets are calculated from:

```text
final contribution offset + CLR-token definition symbol value
```

This is an important design invariant. Do not add an IL decoder merely to find
method boundaries or token operands. A producer that requires unrecorded token
rewrites is not a supported managed COFF producer and should be diagnosed.

This model also defines dead stripping:

- a normal shared `.text$mn` contribution is indivisible;
- `-ffunction-sections` permits individual methods to be selected because each
  method has its own COMDAT contribution;
- metadata selection follows the selected definitions so discarded methods and
  fields also disappear from the output metadata.

## Metadata merging

Metadata tokens are local to each input object. `06000001` in one object is
unrelated to `06000001` in another object. `LinkTokenMap` therefore belongs to
one input and maps its source tokens to final output tokens.

Mappings record both:

- the destination token;
- whether the source was merged into an existing destination row.

The duplicate bit is required to reproduce `newmerger.cpp` behavior for
dependent tables such as InterfaceImpl, Constant, MethodImpl, generic
parameters, and CustomAttribute.

The merger follows the dependency order established by `newmerger.cpp`:

1. Module and TypeDef identities.
2. ModuleRefs, AssemblyRefs, TypeRefs, and TypeSpecs.
3. TypeDef members and Params.
4. MemberRefs and ref-to-def binding.
5. Generic parameters and constraints.
6. Interfaces, constants, layouts, FieldRVAs, and MethodImpls.
7. StandAloneSigs and MethodSpecs.
8. Custom attributes last.

Important behaviors include:

- TypeRef-to-TypeDef and MemberRef-to-definition mappings may change token kind.
- Nested TypeDef identity is structural: namespace, name, and enclosing type.
- Duplicate ordinary types verify and map matching members rather than unioning
  arbitrary definitions.
- `<Module>` members and suppressed members follow newmerger's additive rules.
- PrivateScope members are additive and are not lookup candidates.
- duplicate methods match by rewritten signature and map Params by sequence;
- a concrete method can replace a ForwardRef and supplies its body offset;
- generic parameter and constraint sets on duplicate owners are verified rather
  than unioned;
- MemberRef resolution follows base types and preserves vararg call-site
  MemberRefs while replacing their parent with the matched MethodDef;
- TypeRefs, TypeSpecs, MemberRefs, and StandAloneSigs are canonicalized by their
  fully remapped identity;
- custom attributes are processed only after their parents and constructors
  have final mappings.

Selection is prepared before row planning. Selecting a member of a non-global
type retains the complete owning type, while global `<Module>` members remain
individually selectable. Signature dependencies, generic constraints, base
types, interfaces, and relevant custom attributes are included transitively.

## COFF and `/OPT:REF`

Reachability is section-based, as in a traditional COFF linker:

- the resolved entry-point contribution is the initial root;
- relocations from live contributions add their target contributions;
- CLR-token relocations can resolve MemberRefs to MethodDefs or FieldDefs;
- associative COMDAT children follow the selected parent;
- the traversal repeats to a fixed point.

COMDAT support currently includes `NoDuplicates`, `Any`, `SameSize`,
`ExactMatch`, `Associative`, and `Largest`.

Known IJW native-transition sections (`.nep`, `.rdata$ilfixup`, and MEP-only
`.data`) are recognized but omitted from the pure-IL output. The managed method
aliases they provide are still used when resolving link.exe-style entry names.

## Pure-IL PE output

The final executable is built with:

- one synthesized Module and Assembly;
- `CorFlags.ILOnly`;
- the selected MethodDef as the managed entry point;
- the copied IL stream;
- immutable mapped field data, currently used for string literals;
- ordinary static fields initialized by a synthesized `<Module>..cctor`.

`mscoree.lib` is not an input to chilink. `ManagedPEBuilder` emits the managed
PE startup/import structures required by Windows. This does not mean the image
has no runtime dependency on the CLR loader.

## Command line

The initial x64 driver supports:

```text
/OUT:<file>
/ENTRY:<symbol>
/SUBSYSTEM:CONSOLE
/SUBSYSTEM:WINDOWS
/OPT:REF
/OPT:NOREF
/MACHINE:X64
/NOLOGO
```

Option names are case-insensitive. Entry and COFF symbol names are
case-sensitive. `/ENTRY:` first uses COFF/linker aliases and can fall back to a
unique managed MethodDef name, matching the managed object patterns accepted
by `link.exe`.

Unknown options are errors. Only individual `.obj` inputs are accepted.

## Current scope

Supported:

- x64 managed COFF;
- chibil, asm2obj, and scoped MSVC `/clr:pure` objects;
- cross-object managed calls;
- managed aggregate metadata, including `-fmanaged-aggregate-fields`;
- `-ffunction-sections`, string-literal COMDATs, and `/OPT:REF`;
- immutable string-literal FieldRVA data;
- mutable initialized and zero-initialized global/static-local fields;
- compatible tentative/common globals, including strong-definition override;
- x64 data-to-data `ADDR64` initializers with addends;
- the metadata tables accepted by `MetadataMerger.ValidateTables`.

Not currently supported:

- NEP/UNEP or mixed-mode output;
- global function-pointer initializers and fields associated with vtfixups;
- composing generated initialization with an input `<Module>..cctor`;
- P/Invoke, import libraries, static libraries, or archive parsing;
- CodeView/PDB generation;
- declarative security and manifest resources;
- strong-name signing;
- x86 or ARM64 output.

Mutable RVA-backed fields are emitted as normal static fields. Zero-initialized
storage relies on CLR zero initialization; initialized storage is reconstructed
by a synthesized module initializer, including supported data-address
relocations. Read-only relocation-free RVA data remains mapped directly.

## Extending chilink

Before adding linker behavior:

1. Check how `link.exe` handles equivalent chibil and MSVC objects.
2. Inspect the object with `dumpbin`, `ildasm /text`, and
   `tools/coffobjdumper.cs`.
3. Add a focused regression reproducing that input shape.
4. Preserve section-level linking and relocation-driven token rewriting.

Before changing metadata behavior:

1. Locate the corresponding `newmerger.cpp` phase.
2. Follow its dependency order and duplicate/additive rules.
3. Preserve duplicate provenance in `LinkTokenMap`.
4. Add or update a focused `MetadataMergerTests` fixture.
5. Keep unsupported tables explicit in `ValidateTables`.

The end-to-end linker tests are in `tests/Chibil.Tests/ChilinkTests.cs`.
Metadata-merger parity tests are in
`tests/Chibil.Tests/MetadataMergerTests.cs`. Tests that require out-of-scope
link.exe functionality continue to use `CompilationBuilder.MsvcLink`.
