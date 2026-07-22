# chilink

`chilink` is a pure-IL linker for managed COFF object files produced by chibil,
asm2obj, and the supported MSVC `/clr:pure` subset.

```text
chilink /out:app.exe /entry:main /subsystem:console [/opt:ref] input.obj ...
```

The initial implementation targets x64 and supports `/OUT`, `/ENTRY`,
`/SUBSYSTEM:CONSOLE|WINDOWS`, `/OPT:REF|NOREF`, `/MACHINE:X64`, and `/NOLOGO`.
Options and input features that are not implemented fail with an explicit
diagnostic.

The linker copies selected IL section contributions without parsing method
bodies, patches their COFF TOKEN relocations after metadata tokens are merged,
and emits the final executable with `System.Reflection.Metadata`.

Mutable global data, NEP/UNEP output, P/Invoke and libraries, debug information,
security metadata, manifest resources, x86, and ARM64 are currently out of
scope. Immutable string-literal data is supported.
