# Notes for the MSIL Backend

This file captures metadata generation patterns discovered during the
`/clr` mixed-mode IJW research that are **not** covered by a dedicated
scenario but are important for a future compiler backend.

> Most scenarios in this directory now compile with `/clr /BC`. A handful
> of patterns (notably the `__CxxPureMSILEntry` shim in `main-argv.c`)
> still use `/clr:pure /TP` and are flagged inline below.

## Enums are just int32

MSVC `/clr` generates **no TypeDef** for C enum types. Enum values
are inlined as integer constants. The parameter and local signatures use
plain `int32` regardless of the enum type. Verified against `enum.c`
reference .obj: `use_enum(enum Color c)` has signature `int32(int32)`.

```c
enum Color { RED, GREEN = 5, BLUE };
int use_enum(enum Color c) { return c + 1; }
```

In the MSIL metadata, `use_enum` has signature `int32(int32)`. The enum
constants `RED`, `GREEN`, `BLUE` appear as `ldc.i4.0`, `ldc.i4.5`,
`ldc.i4.6` at call sites. The compiler backend does not need to generate
any TypeDef, FieldDef, or Constant table entries for enums.

## Nested and anonymous struct/union members are flattened

When a struct contains a nested struct or an anonymous union:

```c
struct Outer {
    struct Inner { int a; int b; } inner;
    int z;
};
```

MSVC generates a **single TypeDef** for `Outer` with the total size (12
bytes on x86). There is no separate TypeDef for `Inner`. Member access
is done entirely through offset arithmetic on the opaque value type
(`ldloca` + constant offset + `ldind`/`stind`). Verified against
`nested-struct.c` reference .obj.

This means the backend does not need to generate nested TypeDefs or
FieldDefs for struct members. The struct is an opaque bag of bytes with
a known size and alignment.

## Self-referential structs use standard pointer encoding

```c
struct Node { int val; struct Node* next; };
```

The TypeDef for `Node` has size 8 on x86 (int + 4-byte pointer) and
size 16 on arm64 (int + padding + 8-byte pointer). The `next` field
does not produce a FieldDef. Access to `next` in IL is offset arithmetic:
`ldloc.0` + `ldc.i4.4` + `add` + `ldind.i4`. When `Node*` appears in a
parameter signature, it encodes as `Ptr ValueType Node`. Verified against
`self-ref-struct.c` reference .obj.

## Char types and the IsSignUnspecifiedByte modifier

C has three distinct char types with different MSIL encodings:

| C type | MSIL signature |
|--------|---------------|
| `char` | `modopt(IsSignUnspecifiedByte) int8` |
| `signed char` | `int8` |
| `unsigned char` | `uint8` |

The `modopt(IsSignUnspecifiedByte)` marks plain `char` whose signedness
is implementation-defined. This modifier appears in method parameter
signatures, local variable signatures, and field signatures. It requires
a TypeRef to `System.Runtime.CompilerServices.IsSignUnspecifiedByte` in
the mscorlib AssemblyRef.

See the `char-types` scenario for the full pattern.

## Short and long types in signatures

MSVC preserves the original C type in metadata signatures, even though
the IL operates on `int32`-width values:

| C type | MSIL signature type |
|--------|-------------------|
| `short` | `int16` |
| `unsigned short` | `uint16` |
| `int` | `int32` |
| `unsigned int` | `uint32` |
| `long` (32-bit) | `int32` |
| `long long` | `int64` |
| `unsigned long long` | `uint64` |
| `_Bool` | `bool` |
| `float` | `float32` |
| `double` | `float64` |

See the `cast` scenario which demonstrates widening/narrowing casts and
mixed-type signatures.

## Static local variables become static fields

A `static` variable inside a function becomes a static field on
`<Module>` with a hash-mangled name:

```c
int counter(void) {
    static int count;
    count = count + 1;
    return count;
}
```

The field name follows the pattern `?A0x<hash>.?count@?1??counter@@9@9`
where `<hash>` is a translation-unit hash. The field carries flags
`Assembly | Static | HasFieldRVA` (0x0113) and type `int32`. The bytes
themselves live in `.bss` (uninitialized C linkage internal): the
section has `IMAGE_SCN_CNT_UNINITIALIZED_DATA`, `SizeOfRawData = 4`,
and `PointerToRawData = 0` — no file bytes, the loader zero-fills at
image load. chibil supports this via `LogicalSection.Bss` plus the
`bssSize` parameter on `ManagedCoffBuilder` (`AddDataClrToken(...,
LogicalSection.Bss, 0)` binds the field's CLR-token alias to the
section). This differs from external uninitialized globals (e.g.
`int g_uninitialized;` in `global.c`), which use a common symbol
(Sect=0 External, Value=size).

The IL accesses the field via `ldsfld` / `stsfld` with a CLR token
relocation to the field definition.

See the `static-local` scenario for the full pattern.

## Global variable initializers

Under `/clr /BC` MSVC accepts non-constant global initializers
(`char* str = "Hello!";` in `init.c`, `int (*m)() = &get;` in
`global-advanced.c`) and lowers them with the standard managed
FieldRVA pattern: the global gets `HasFieldRVA`, its bytes live in
`.data` (or `.bss` for zero-initialized internal-linkage statics),
and a `DIR32`/`ADDR64` reloc points to whatever the initializer
references (string literal address, NEP thunk via `__unep@`, etc.).

See `global.c`, `init.c`, and `global-advanced.c` for the
`/clr /BC` FieldRVA pattern.

## Atomic operations map to System.Threading.Interlocked

MSVC intrinsics `_InterlockedExchange` and `_InterlockedCompareExchange`
compile to `call System.Threading.Interlocked::Exchange(int32&, int32)`
and `CompareExchange(int32&, int32, int32)` in MSIL. The chibil GCC-
style `__atomic_exchange_n` / `__atomic_compare_exchange_n` builtins
should map to the same Interlocked methods.

The first parameter uses `Ptr modreq(IsVolatile) int32` in the method
signature and `modreq(IsVolatile) int32` for volatile locals.

See the `atomic` scenario for the full pattern.

## TLS is blocked under /clr

`__declspec(thread)`, `_Thread_local`, and C11 `thread_local` are all
rejected by MSVC under `/clr` (error C3389/C3403, the same as under
`/clr:pure`). The chibil backend would need to either reject TLS
variables or map them to `[ThreadStatic]` attributes (managed
thread-local storage).

## Function pointers use FnPtr in signatures

When a C function takes a function pointer parameter:

```c
int apply(int (*fn)(int, int), int x, int y) { return fn(x, y); }
```

MSVC encodes the parameter as `FnPtr int32(int32, int32)` in the method
signature — an inline function pointer type, not `native int`. The
indirect call uses `calli` with a matching `StandaloneSignature`.

See the `funcptr` scenario for the full pattern.

## Variadic functions use VARARG calling convention with SENTINEL

C variadic functions (`...`) compile to a `[VARARG]` calling convention
in the MemberRef signature. Two separate MemberRefs are generated:

1. **Declaration MemberRef** — the fixed parameters only, with `[VARARG]`
   calling convention and `modopt(CallConvCdecl)` on the return type:
   ```
   CallCnvntn: [VARARG]
   ReturnType: CMOD_OPT CallConvCdecl I4
   1 Arguments: I4
   ```

2. **Call-site MemberRef** — includes all arguments with an
   `ELEMENT_TYPE_SENTINEL` marker separating fixed from variadic args:
   ```
   CallCnvntn: [VARARG]
   ReturnType: CMOD_OPT CallConvCdecl I4
   4 Arguments: I4, <SENTINEL> I4, I4, I4
   ```

Both MemberRefs carry `DecoratedNameAttribute` with the mangled name.
The `ELEMENT_TYPE_SENTINEL` (0x41) byte in the blob separates the fixed
parameters from the varargs. This is the standard ECMA-335 vararg
mechanism. The backend must emit distinct signatures for declaration vs
each unique call site.

```c
int my_sum(int count, ...);
int main() { return my_sum(3, 10, 20, 30); }
```

## The IsLong modifier distinguishes C `long` from `int`

C `long` and `unsigned long` produce `modopt(IsLong)` in MSIL
signatures, even though they are the same width as `int`/`unsigned int`
on 32-bit Windows:

| C type | MSIL signature |
|--------|---------------|
| `int` | `int32` |
| `long` | `modopt(IsLong) int32` |
| `unsigned int` | `uint32` |
| `unsigned long` | `modopt(IsLong) uint32` |
| `long double` | `modopt(IsLong) float64` |

This requires a TypeRef to
`System.Runtime.CompilerServices.IsLong` in the mscorlib AssemblyRef.
The modifier appears in method signatures (params + return), local
signatures, and field signatures.

Note: `long double` maps to `modopt(IsLong) R8` (float64), not a
special extended-precision type — MSVC treats `long double` as 64-bit
double.

## Const pointer parameters produce modopt(IsConst)

The `const` qualifier on pointer targets produces `modopt(IsConst)` in
method parameter signatures, not just in global/field contexts:

```c
int read_val(const int* p);       // Ptr modopt(IsConst) I4
void copy(int* d, const int* s);  // Ptr I4, Ptr modopt(IsConst) I4
```

The modifier is on the pointee, not the pointer. This means
`const int*` → `Ptr modopt(IsConst) I4`.

## Const-on-pointer-itself produces modopt(IsConst) before Ptr

When `const` qualifies the pointer itself (not just the pointee), the
modifier appears *before* `Ptr` in the signature:

```c
void f(const int* const p);
// → modopt(IsConst) Ptr modopt(IsConst) I4
```

The outer `const` (on the pointer) produces `modopt(IsConst)` before
`Ptr`. The inner `const` (on the int) produces `modopt(IsConst)` after
`Ptr` but before `I4`. This ordering matters for correct signature
encoding.

## Multiple modopt/modreq stack on the same type

When a type has multiple qualifiers, they stack. The ordering in the
signature blob is significant:

| C type | MSIL signature |
|--------|---------------|
| `const char*` | `Ptr modopt(IsConst) modopt(IsSignUnspecifiedByte) I1` |
| `const volatile int*` | `Ptr modopt(IsConst) modreq(IsVolatile) I4` |
| `volatile int*` | `Ptr modreq(IsVolatile) I4` |

For `const volatile`, the `modopt(IsConst)` appears first, then
`modreq(IsVolatile)`, then the base type. The backend must preserve
this ordering when generating signature blobs.

## Volatile parameters produce modreq(IsVolatile) in signatures

`volatile` on pointer targets produces `modreq(IsVolatile)` (not
modopt) in parameter signatures:

```c
int read_volatile(volatile int* p);  // Ptr modreq(IsVolatile) I4
void write_volatile(volatile int* p, int val);  // same encoding
```

Volatile local variables also get `modreq(IsVolatile)`:
```
LOCALSIG: modreq(IsVolatile) I4
```

## char** is Ptr Ptr modopt(IsSignUnspecifiedByte) I1

The classic `main(int argc, char** argv)` signature encodes `char**` as
nested pointer types with the `IsSignUnspecifiedByte` modifier on the
innermost `I1`:

```
Argument #1: I4                                          // argc
Argument #2: Ptr Ptr modopt(IsSignUnspecifiedByte) I1    // argv
```

The modifier propagates through all pointer levels — it is always on
the leaf `I1` type, not on the pointer.

## void* is Ptr Void, void return is Void

`void*` parameters encode as `Ptr Void` (ELEMENT_TYPE_PTR + VOID):

```c
void* get_ptr(int* p);  // ReturnType: Ptr Void
void set_val(int* p, int v);  // ReturnType: Void
```

`void` return encodes as just `Void` (ELEMENT_TYPE_VOID). The `Ptr Void`
local variable signature also uses `Ptr Void`.

## _Bool maps to ELEMENT_TYPE_BOOLEAN

C's `_Bool` type maps directly to the CLR `Boolean` type
(ELEMENT_TYPE_BOOLEAN, 0x02):

```c
_Bool bool_positive(_Bool val);
// ReturnType: Boolean
// Argument #1: Boolean
```

Locals and field signatures also use `Boolean`. This is distinct from
`int` which uses `I4`.

## Array parameters decay to Ptr — size is lost

C array parameters like `int arr[10]` decay to plain pointers in the
metadata signature — the array size is not preserved:

```c
int sum10(int arr[10]);  // Argument #1: Ptr I4 (not ValueClass)
```

The local array `int arr[10]` in the function body still generates a
`$ArrayType$$$BY09H` TypeDef with Size:40.

## 2D array parameters use Ptr to inner array TypeDef

Multi-dimensional array parameters produce an interesting encoding. For
`int arr[3][4]`, the parameter decays to a pointer to the inner
dimension's array TypeDef:

```c
int sum_2d(int arr[3][4]);
// Argument #1: Ptr ValueClass $ArrayType$$$BY03H
```

Where `$ArrayType$$$BY03H` (Size:16) represents `int[4]`. The outer
dimension is lost (pointer decay). The local `int arr[3][4]` generates
a separate TypeDef `$ArrayType$$$BY123H` (Size:48) where `12` encodes
the total dimensions (`3*4 = 12` in hex? No — `BY123H` encodes the
shape as `[3][4]` with size codes `12` and `3`).

## `$ArrayType$` element type codes

The element-type letter in `$ArrayType$$$BY<dims><bounds><elem>` is
the same set of single-letter codes used in function signatures. The
ones that actually appear in the scenarios:

| Code | C type | MSIL type |
|------|--------|-----------|
| `D` | `char` (IsSignUnspecifiedByte) | I1 |
| `G` | `unsigned short` / `wchar_t` | UI2 |
| `H` | `int` | I4 |

See "Array TypeDef names" below for how `<dims>` and `<bounds>` encode
multi-dimensional arrays, and "MSVC number encoding" for the
digit/hex-nibble scheme used by both.

## Wide string literals use $ArrayType with G element type

Wide string literals (`L"Hello"`) produce a `$ArrayType$$$BY05G`
TypeDef (unsigned short / wchar_t array), with `Ptr UI2` as the local
variable type. The string data is stored in the same global field
pattern as narrow strings.

## Struct by-value in parameters uses ValueClass directly

When a struct is passed by value (not by pointer), the parameter
signature uses `ValueClass <TypeName>` directly:

```c
int sum_point(struct Point p);
// Argument #1: ValueClass Point
struct Point make_point(int x, int y);
// ReturnType: ValueClass Point
```

This is distinct from `Ptr ValueClass Point` used for pointer
parameters.

## Forward-declared structs become TypeRef with null ResolutionScope

A forward-declared struct (`struct Opaque;`) used only as a pointer
parameter generates a **TypeRef** (not TypeDef) with
`ResolutionScope: 0x00000000` (null/module scope):

```
TypeRef: Opaque
  ResolutionScope: 0x00000000
  TypeRefName: Opaque
```

The parameter encodes as `Ptr ValueClass Opaque` referencing this
TypeRef. This is the same pattern used in pinvoke-forwardref, but
applies to any forward-declared struct used as a pointer parameter,
not just P/Invoke scenarios.

## Name Mangling Reference

This section documents the MSVC C++ decorated name format used for
C functions compiled under `/clr /BC`. This information is needed
to generate COFF symbols that are link-compatible with MSVC objects.

### Function decorated names

Format: `?<name>@@$$J0YA<return><params>@Z`

| Component | Meaning |
|-----------|---------|
| `?` | Decorated name prefix |
| `<name>` | C function name |
| `@@` | Scope terminator (global scope) |
| `$$J0` | `extern "C"` linkage with C++ decoration |
| `Y` | Calling convention prefix |
| `A` | `__cdecl` — what `/clr /BC` emits for user functions exposed as IJW entry points (the `UnmanagedExport` flag forces a cdecl ABI). Under `/clr:pure /BC` this is `M` (`__clrcall`) instead. |
| `<return>` | Return type code |
| `<params>` | Parameter type codes, or `X` for void (no params) |
| `@Z` | End of parameter list and name |

The `$$J0` prefix only appears under managed compilation (`/clr` or
`/clr:pure`) — native MSVC leaves `extern "C"` C functions undecorated
(e.g. `_main` on x86, plain `main` on x64), so `$$J0` is effectively a
marker that this symbol participates in CLR metadata-token resolution.
The `0` digit identifies this as the standard `extern "C"` IJW form;
`$$J216` is the x86 stdcall thunk variant (see the table below).

Other call-convention codes that appear in MSVC C++ output:

| Code | Meaning |
|------|---------|
| `A` | `__cdecl` — `/clr /BC` user functions, native C functions |
| `M` | `__clrcall` — `/clr:pure /BC` functions, internal CLR shims like `__CxxPureMSILEntry` |
| `G` | `__stdcall` — used by Win32 P/Invoke imports declared `__stdcall` (e.g. x86 `MessageBoxW`) |

Other `$$` linkage prefixes that appear in the COFF objects:

| Code | Meaning |
|------|---------|
| `$$J0` | `extern "C"` with C++ decoration (used by C functions) |
| `$$F` | `__clrcall` managed function with C++ linkage (used for CRT helpers like `.cctor`) |
| `$$J216` | `extern "C"` x86 stdcall thunk — `$$J216YG…` is what MSVC emits for x86 P/Invoke imports declared `__stdcall` (e.g. `?MessageBoxW@@$$J216YGHPAX00H@Z`) |

### Type codes for function signatures

Primitive types:

| Code | C type | MSIL signature |
|------|--------|---------------|
| `X` | `void` | `void` |
| `D` | `char` | `modopt(IsSignUnspecifiedByte) int8` |
| `C` | `signed char` | `int8` |
| `E` | `unsigned char` | `uint8` |
| `F` | `short` | `int16` |
| `G` | `unsigned short` / `wchar_t` | `uint16` |
| `H` | `int` | `int32` |
| `I` | `unsigned int` | `uint32` |
| `J` | `long` | `modopt(IsLong) int32` |
| `K` | `unsigned long` | `modopt(IsLong) uint32` |
| `M` | `float` | `float32` |
| `N` | `double` | `float64` |
| `_J` | `long long` | `int64` |
| `_K` | `unsigned long long` | `uint64` |
| `_N` | `_Bool` | `bool` |

Pointer types:

| Code | C type | MSIL signature |
|------|--------|---------------|
| `PA<type>` (x86) / `PEA<type>` (x64/arm64) | `<type>*` | `Ptr <type>` |
| `PAX` / `PEAX` | `void*` | `Ptr Void` |
| `PAH` / `PEAH` | `int*` | `Ptr int32` |
| `PAD` / `PEAD` | `char*` | `Ptr modopt(IsSignUnspecifiedByte) int8` |
| `PAPA<type>` / `PEAPEA<type>` | `<type>**` | `Ptr Ptr <type>` |
| `PAU<name>@@` / `PEAU<name>@@` | `struct <name>*` | `Ptr ValueType <name>` |
| `PB<type>` / `PEB<type>` | `<type> const*` | `Ptr modopt(IsConst) <type>` |
| `PC<type>` / `PEC<type>` | `<type> volatile*` | `Ptr modreq(IsVolatile) <type>` |

The `E` between `P` and the next letter is the MSVC `__ptr64` modifier:
present on x64/arm64, absent on x86. Scenario emitters typically build
the mangled symbol with `string e = is32 ? "" : "E";` and interpolate
`P{e}A<X>` / `P{e}B<X>`.

Struct types:

| Code | C type | MSIL signature |
|------|--------|---------------|
| `U<name>@@` | `struct <name>` (by value) | `ValueType <name>` |
| `?AU<name>@@` | `struct <name>` (as return type) | `ValueType <name>` |

Function pointer types:

| Code | C type |
|------|--------|
| `P6A<ret><params>@Z` | `<ret> (__cdecl*)(<params>)` — what `/clr /BC` emits |
| `P6M<ret><params>@Z` | `<ret> (__clrcall*)(<params>)` — what `/clr:pure /BC` emits |

Examples (assuming `/clr /BC` on x64; on x86 drop the `E` from each `P_A`):
```
?main@@$$J0YAHXZ                          int main(void)
?arith@@$$J0YAHHH@Z                       int arith(int, int)
?char_func@@$$J0YAHDCE@Z                  int char_func(char, signed char, unsigned char)
?cast_float@@$$J0YAHHMN@Z                 int cast_float(int, float, double)
?void_func@@$$J0YAXXZ                     void void_func(void)
?longlong_ret@@$$J0YA_JXZ                 long long longlong_ret(void)
?ptr_param@@$$J0YAHPEAH@Z                 int ptr_param(int*)
?voidptr_param@@$$J0YAHPEAX@Z             int voidptr_param(void*)
?dblptr_param@@$$J0YAHPEAPEAH@Z           int dblptr_param(int**)
?struct_ptr@@$$J0YAHPEAUPoint@@@Z         int struct_ptr(struct Point*)
?struct_ret@@$$J0YA?AUPoint@@HH@Z         struct Point struct_ret(int, int)
?funcptr_param@@$$J0YAHP6AHH@Z@Z          int funcptr_param(int (*)(int))
?apply@@$$J0YAHP6AHHH@ZHH@Z               int apply(int (*)(int,int), int, int)
```

### MSVC number encoding

Used for array dimensions, template arguments, and other numeric values
in decorated names:

| Value | Encoding |
|-------|----------|
| 0 | `A@` |
| 1–10 | Single digit `'0'`–`'9'` (digit = value − 1) |
| ≥ 11 | Hex nibbles `A`–`P` (where A=0, P=15), MSB first, terminated by `@` |

Examples: value 1 → `0`, value 6 → `5`, value 10 → `9`, value 11 →
`L@` (L=11), value 16 → `BA@` (B=1, A=0 → 0x10=16), value 20 →
`BE@` (B=1, E=4 → 0x14=20), value 256 → `BAA@` (1×256+0×16+0=256).

### Array TypeDef names

Format: `$ArrayType$$$BY<ndims><dim1>...<dimN><elemtype>`

| Component | Encoding |
|-----------|----------|
| `$ArrayType$$$BY` | Fixed prefix |
| `<ndims>` | Number of dimensions, MSVC-number-encoded |
| `<dim1>...<dimN>` | Each dimension bound, MSVC-number-encoded |
| `<elemtype>` | Element type code (same codes as function signatures) |

The number of dimensions and each bound use the MSVC number encoding
described above. For a 1D array, ndims=1, so it encodes as `0`. For a
2D array, ndims=2 → `1`.

Examples:
```
$ArrayType$$$BY00H     int[1]       (ndims=1, bound=1, size=4)
$ArrayType$$$BY05D     char[6]      (ndims=1, bound=6, size=6)
$ArrayType$$$BY09H     int[10]      (ndims=1, bound=10, size=40)
$ArrayType$$$BY0L@H    int[11]      (ndims=1, bound=11, size=44)
$ArrayType$$$BY0BA@H   int[16]      (ndims=1, bound=16, size=64)
$ArrayType$$$BY0BAA@H  int[256]     (ndims=1, bound=256, size=1024)
$ArrayType$$$BY02G     wchar_t[3]   (ndims=1, bound=3, size=6)
$ArrayType$$$BY04F     short[5]     (ndims=1, bound=5, size=10)
$ArrayType$$$BY123H    int[3][4]    (ndims=2, bound1=3, bound2=4, size=48)
$ArrayType$$$BY06$$CBD const char[7] (ndims=1, bound=7, const char element)
```

The element type for qualified types uses MSVC CV-qualifier encoding:
`$$CB` = `const`, `$$CC` = `volatile`, `$$CD` = `const volatile`.

### Translation-unit hash (`?A0x<hash>`)

Static locals and anonymous globals are scoped to a translation-unit
anonymous namespace using `?A0x<hash>`. The hash is a CRC-32 derived
from source file paths with complex logic for reproducible builds.

For the chibil backend, we do NOT need to match MSVC's exact hash. We
should choose a hash that avoids conflicts with MSVC objects (e.g., a
hash of the source file contents). The hash only matters for:
- Static local field names: `?A0x<hash>.?<var>@?1??<func>@@9@9`
- Anonymous global field names: `?A0x<hash>.unnamed-global-N`
- Initializer field names: `?A0x<hash>.<var>$initializer$`

### Global initializer function names

Dynamic initializer functions for global variables follow the pattern:

- **Inner name:** `??__E<var>@@YMXXZ` — "dynamic initializer for `<var>`"
  - `??` = special name prefix
  - `__E` = dynamic initializer operator
  - `<var>` = variable name
  - `@@YMXXZ` = `void __clrcall (void)`

- **Full COFF symbol:** `???__E<var>@@YMXXZ@?A0x<hash>@@$$FYMXXZ`
  - The inner name is re-decorated as a member of the TU anonymous namespace
  - `@?A0x<hash>@` = anonymous namespace scope
  - `@$$F` = managed C++ linkage
  - `YMXXZ` = `void __clrcall (void)`

The `__F` variant (`??__F<var>@@YMXXZ`) is the corresponding `atexit`
destructor, if one is needed.


### Anonymous globals

**The prefixes:**

| Symbol | Meaning |
|--------|---------|
| `$SG` | **S**tring **G**lobal — string literals, constant arrays, format strings |
| `$S` | **S**tatic temp — compiler-generated static data |
| `$T` | **T**emp — generic compiler-generated temporary |
| `$E` | **E**ntry — unnamed function entry points |

**The number** is either:
- the symbol's unique key in the global symbol table (for global symbols)
- a per-function sequential ID (for function-local symbols)

These keys are assigned sequentially as the backend processes symbols, so the numbers
are not stable across builds.

## Backend Implementation Gaps

The following areas need implementation work beyond what the scenarios
demonstrate:

### 1. Signature builder
The backend needs a utility that converts `CType` to the correct MSIL
signature encoding, including all `modopt`/`modreq` modifiers. The
mapping is documented in the type codes table above and the NOTES.md
sections on char types, long types, and const/volatile params.

### 2. Method body shape
The x86 CodeGen uses register-based codegen with push/pop. The MSIL
backend needs to:
- Count locals and build a local variable signature
- Track max stack depth for the fat header
- Map chibil's `Obj.Offset` (stack frame offsets) to IL local slot indices
- Handle struct temporaries as valuetype locals

### 3. Dense vs sparse switch
The backend needs a heuristic to choose between IL `switch` instruction
(for dense cases starting near 0) and a binary compare tree (for sparse
or large-valued cases). Both patterns are demonstrated in scenarios.

### 4. Struct copy strategy
Struct assignment uses `cpblk` for large structs. The backend should use
`cpblk` with the struct's TypeDef size. For small structs accessed
field-by-field, `ldind`/`stind` with offsets works.

### 5. TLS
Thread-local storage is blocked under both `/clr` and `/clr:pure`. The
backend should reject TLS variables with an error, or map them to
`[ThreadStatic]` fields (which have different semantics from native TLS).

### 6. VLA and dynamic stack allocation
chibil supports VLAs (`int arr[n]`) which lower to `alloca`. MSVC
rejects VLA syntax in `/BC` mode, but `_alloca()` compiles to the
`localloc` IL instruction. The MSIL backend should:
- Lower VLAs to `localloc` (which allocates from the IL evaluation stack)
- `localloc` takes a byte count from the stack and returns a pointer
- The allocated memory is automatically freed when the method returns
- No deallocation instruction is needed

See the `alloca` scenario for the `localloc` pattern.

### 7. Unsupported C features in MSIL
The following chibil-supported features have NO MSIL equivalent and
must be handled by the backend:

| Feature | chibil support | MSIL strategy |
|---------|----------------|---------------|
| `asm("...")` | NodeKind.Asm | Reject with error — no inline assembly in managed code |
| `({...})` statement exprs | NodeKind.StmtExpr | GCC extension; lower to sequential IL with value on stack |
| `&&label` / `goto *ptr` | NodeKind.LabelVal/GotoExpr | GCC extension; lower to switch-dispatch (no label address in IL) |
| `_Atomic` compound assign | NodeKind.Cas/Exch | Generate `Interlocked.CompareExchange` CAS loop |

### 8. Compile-time-only features
These chibil features resolve entirely at compile time and produce
NO runtime artifact in MSIL:

- `_Alignof(type)` → constant integer
- `sizeof(expr)` → constant integer
- `_Generic(expr, ...)` → selects one branch at compile time
- `typeof(expr)` → GCC extension, resolves to a type at compile time
- Adjacent string literal concatenation → single merged constant

### 9. Alignment and packing
`_Alignas(N)` on struct members maps to `.pack N` in the TypeDef
ClassLayout metadata. `__attribute__((packed))` maps to `.pack 1`.
`_Alignof` is a compile-time constant and produces no metadata.

### 10. Inline functions
The `inline` keyword is advisory in CLR — the JIT decides whether to
inline. For `static inline` functions that are never referenced
externally, chibil marks them as not `IsLive` and does not emit them.
The MSIL backend should similarly skip dead `static inline` functions.

## restrict qualifier is silently dropped

The `__restrict` / `restrict` qualifier produces **no metadata
encoding** — it is silently dropped. The parameter signature is
identical to a non-restrict pointer:

```c
void copy(int* __restrict dest, const int* __restrict src, int n);
// Argument #1: Ptr I4                   (no restrict marker)
// Argument #2: Ptr modopt(IsConst) I4   (const preserved, restrict dropped)
```

The backend does not need to emit any modifier for `restrict`.

## Architecture differences in IL

Several constructs generate different IL across x86, x64, and ARM64:

| Pattern | x86 | x64 | ARM64 |
|---------|-----|-----|-------|
| Pointer index widening | `ldc.i4.N` | `ldc.i4.N` + `conv.i8` | `ldc.i4.N` + `conv.i8` |
| Switch lowering | Direct `switch` (4-arm jump table) | `brfalse`/`beq` chain (no `switch` instruction) | Bounds-check (`blt.s`/`bgt.s`) + `switch` |
| Switch locals | 2 locals (result + return temp) | 3 locals (extra scrutinee temp for the compare chain) | 3 locals (extra scrutinee temp for bounds check) |
| Struct TypeDef | No alignment member | `<alignment member>` int32 field added | `<alignment member>` int32 field added |
| mscorlib hash | `32 CD 81 47...` | `28 DC 37 8B...` | `28 DC 37 8B...` |
| Pointer-parameter COFF mangling | `PA<X>` / `PB<X>` | `PEA<X>` / `PEB<X>` | `PEA<X>` / `PEB<X>` |
| MSVC NEP-thunk section placement | `.text$mn` (per-method COMDAT) | `.nep` | `.text$mn` (per-method COMDAT) |

`conv.i8` appears whenever a 32-bit constant feeds into pointer-sized
arithmetic on a 64-bit target — i.e. both x64 and ARM64. It also
appears under all three architectures whenever the C code itself uses
`long long`/`int64_t` types.

## Unsigned operations use `.un` IL variants

Unsigned C types produce different IL instructions than their signed
counterparts. The backend must select the correct opcode based on the
C type's signedness:

| Operation | Signed IL | Unsigned IL |
|-----------|-----------|-------------|
| Division | `div` | `div.un` |
| Remainder | `rem` | `rem.un` |
| Right shift | `shr` | `shr.un` |
| Less than | `bge.s` (inverted) | `bge.un.s` (inverted) |
| Less/equal | `bgt.s` (inverted) | `bgt.un.s` (inverted) |
| Greater than | `ble.s` (inverted) | `ble.un.s` (inverted) |
| Greater/equal | `blt.s` (inverted) | `blt.un.s` (inverted) |

Comparisons use the inverted condition: `a < b` branches to false with
`bge.s`/`bge.un.s`. Unsigned types use `uint32` in signatures.

See the `unsigned` scenario for the full pattern.

## Pointer subtraction uses shift for element-size division

`ptr_a - ptr_b` compiles to `sub` followed by `shr` with
`log2(sizeof(element))`:

| Element type | Shift amount | IL |
|-------------|-------------|-----|
| `char` (size 1) | no shift | `sub` |
| `int` (size 4) | 2 | `sub, ldc.i4.2, shr` |
| `double` (size 8) | 3 | `sub, ldc.i4.3, shr` |

Pointer comparisons (`p < q`, `p == q`) use unsigned branches
(`bge.un.s`, `bne.un.s`) since pointers are unsigned addresses.

See the `ptrsub` scenario for the full pattern.

## Struct assignment generates cpblk

When a struct is assigned (`*dst = *src` or `b = a`), MSVC generates
the `cpblk` IL instruction: `ldarg.0, ldarg.1, ldc.i4 <size>, cpblk`.
The size is the total byte size of the struct.

For struct-valued locals, assignment between locals uses
`ldloc.N, stloc.M` which copies the entire value type. Member access
uses `ldloca + constant_offset + stind/ldind`.

See the `structcopy` scenario for the full pattern.

## 64-bit (long long) operations

`long long` and `unsigned long long` values use `int64` in signatures.
IL arithmetic instructions (`add`, `mul`, `div`, `shl`, `shr`) are
type-agnostic on the eval stack. Key patterns:

- `ldc.i8 <value>` loads 64-bit constants (9 bytes: opcode + 8 byte imm)
- `conv.i8` widens int32→int64 (sign-extending)
- `conv.i4` narrows int64→int32 (truncating)
- `shr.un` for unsigned right shift on 64-bit values
- `div.un` / `rem.un` for unsigned 64-bit division

See the `longlong` scenario for the full pattern.

## Pre/post increment and compound assignment use starg.s

`x++`, `++x`, and `a += b` on function parameters generate the
`starg.s` instruction to write back to a parameter slot:

- **post_inc** `x++`: push old value, compute new, `starg.s`, return old
- **pre_inc** `++x`: compute new, `starg.s`, load new, return
- **compound** `a += b`: `ldarg.0, ldarg.1, add, starg.s V_0`
- **ptr++**: `ldc.i4.4, add` (pointer step by element size)

See the `incdec` scenario for the full pattern.

## Constant loading uses multiple ldc variants

MSVC selects the most compact `ldc.i4` encoding:

| Value | IL instruction | Encoding size |
|-------|---------------|---------------|
| -1 | `ldc.i4.m1` | 1 byte |
| 0–8 | `ldc.i4.0` .. `ldc.i4.8` | 1 byte |
| -128..127 | `ldc.i4.s <byte>` | 2 bytes |
| -2^31..2^31-1 | `ldc.i4 <int32>` | 5 bytes |
| 64-bit | `ldc.i8 <int64>` | 9 bytes |

Notable edge cases: `UINT_MAX` (0xFFFFFFFF) loads as `ldc.i4.m1`
since the bit pattern is identical to -1. `INT_MIN` (0x80000000)
uses `ldc.i4 0x80000000`.

See the `negconst` scenario for the full pattern.

## Sparse switch uses binary tree comparison

Dense switch statements (consecutive case values 0..N) compile to the
IL `switch` instruction (a jump table). Sparse switch statements
(widely separated values) compile to a **binary tree of comparisons**:

1. Pick a pivot case: `bgt.s` to split high/low halves
2. Within each half: `beq.s` for individual cases
3. Fall through to default

This is fundamentally different from the dense `switch` instruction
and requires different codegen logic.

See the `sparse-switch` scenario for both patterns.

## void* encodes as Ptr Void in signatures

`void*` parameters and returns encode as `Ptr Void`
(`ELEMENT_TYPE_PTR ELEMENT_TYPE_VOID`) in MSIL method signatures.
Casting `void*` to a typed pointer and dereferencing generates
`ldind`/`stind` directly with no explicit conversion instruction.
Byte-level memory access through `char*` uses `ldind.i1`/`stind.i1`.

See the `voidptr` scenario for the full pattern.

## Incomplete (forward-declared) structs produce TypeRef and LNK4248

When a struct is forward-declared but never defined in the translation unit,
MSVC `/clr` (and `/clr:pure`) emits a **TypeRef** (not a TypeDef) with a null
ResolutionScope. The linker issues warning LNK4248 if no other object file
provides a matching TypeDef:

```
warning LNK4248: unresolved typeref token (01000005) for 'opaque'; image may not run
```

This is expected and harmless when the struct is only used through pointers
and never instantiated or dereferenced. If another translation unit provides
the complete struct definition (and thus a TypeDef), the linker resolves the
TypeRef silently with no warning.

Minimal reproducer (generates LNK4248 with both MSVC and chibil):

```c
// incomplete.c
// cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC incomplete.c
// link /DEBUG /subsystem:console incomplete.obj mscoree.lib /entry:main
//   -> warning LNK4248: unresolved typeref token for 'opaque'

struct opaque;  // forward declaration, never defined

struct opaque* get_opaque(void);

int main() {
    struct opaque* p = 0;
    return 0;
}
```

In a multi-TU build where another file defines `struct opaque`, the warning
disappears:

```c
// provider.c — defines the struct
struct opaque { int x; int y; };
struct opaque instance;
struct opaque* get_opaque(void) { return &instance; }
```

```
link /DEBUG /subsystem:console incomplete.obj provider.obj /entry:main
   -> no LNK4248 warning
```

A real-world example is PureDOOM.h, which declares `struct hostent* hostentry`
(a POSIX networking type) as a local variable outside of `#if defined(I_NET_ENABLED)`
guards. Since `struct hostent` is never defined, the linker warns — but the
pointer is never dereferenced when networking is disabled, so the warning is
benign.

## /clr mixed-mode IJW entry-point thunks (`__mep@` / `__m2mep@` / `__unep@`)

This is now the default compilation mode for the scenarios. Each managed
C function exposed across the managed/native boundary gets a small fan
of compiler-generated COFF symbols that wire up the transitions:

| Symbol | Stands for | Lives in | Filled by | Purpose |
|--------|-----------|----------|-----------|---------|
| `_foo` / `foo` (bare name) | The NEP thunk | `.text$mn` (per-method COMDAT) on x86 and arm64, dedicated `.nep` section on x64 | The compiler emits the thunk body | The actual native entry-point code: a per-arch indirect jump through `__mep@?foo`. On x86 a single 6-byte `FF 25 [imm32]` jump; on arm64 three ADRP / LDR / BR instructions (`09 00 00 90 / 29 01 40 F9 / 20 01 1F D6`, with `PAGEBASE_REL21` + `PAGEOFFSET_12L` relocs against `__mep@?foo`). On x64 MSVC emits a 16-byte sequence `EB 08 / 0F 0B / FF 25 [→__m2mep@] / FF 25 [→__mep@]` — the leading `jmp +8` short-jumps past the first indirect jump straight into the `__mep@` jump, and the CLR can rewrite the leading byte at load time to fall through to the `__m2mep@` jump when the caller is known to be managed (the double-thunk-avoidance optimization). chibil emits only the single jump form on all three architectures, which is sufficient for correctness. External-linkage symbol — this is what native code calls when it calls `foo` by name, and what `ldsfld __unep@?foo` ultimately yields. |
| `__mep@?foo` | Managed Entry-Point | `.data` slot, ptr-sized | The CLR loader at module load, driven by a `.rdata$ilfixup` entry with `Type=0x0009`/`0x000A` (`COR_VTABLE_FROM_UNMANAGED_RETAIN_APPDOMAIN | *BIT`) | The vtable-fixup slot. Compiler initializes the bytes with the MethodDef CLR token via a TOKEN reloc; at load time the CLR replaces them with the address of a from-unmanaged stub that performs the managed transition. The NEP thunk above does the actual indirect jump through this slot. |
| `__m2mep@?foo` | Managed-to-Managed Entry-Point | `.data` slot, ptr-sized | The CLR loader, driven by a second `.rdata$ilfixup` entry with `Type=0x0001`/`0x0002` (`COR_VTABLE_*BIT` only) | Performance optimization for managed→managed calls. Avoids the double-thunk penalty of `managed → native NEP thunk → from-unmanaged stub → managed` when the caller is already managed. **MSVC only emits this on x64** (the leading `EB 08` in the NEP thunk above is what gates the optimization); x86 and arm64 references never contain `__m2mep@` symbols. Skipped entirely by chibil — not required for correctness on any architecture. |
| `__unep@?foo` | Unmanaged Native Entry-Point | `.rdata` slot, ptr-sized | The linker at link time, via an `ADDR64`/`DIR32` reloc to the bare `_foo` / `foo` symbol | A constant function pointer to the NEP thunk. When *managed* code takes the address of a managed C function (e.g. `fp = foo;` or passing `&foo` to a callback), the compiler emits `ldsfld __unep@?foo` instead of `ldftn foo`. The loaded value is a native function pointer that can be invoked through `calli [C] modopt(CallConvCdecl) <ret>(<args>)`. The slot has its own ADDR reloc — it is **not** referenced by any `.rdata$ilfixup` entry. |

**The flows:**

```
Native caller →  `foo` bare-name → `jmp [__mep@?foo]` → from-unmanaged stub
                   (NEP thunk in .nep on x64,           (CLR-installed at load)
                    .text$mn on x86/arm64)                      ↓
                                                       managed `foo` body

Managed caller (`call foo`)        → direct managed call into `foo`
                                     (the __m2mep@ slot would optimize the
                                     reverse direction if we emitted it)

Managed caller wanting a native FP → `ldsfld __unep@?foo`
                                     (yields address of the NEP thunk above)
                                     → `calli [C] <sig>` invokes through it
```

`__clrcall` functions in `/clr:pure` skip all of this — there is no
managed↔native boundary, so no thunk is needed.

chibil emits a minimal subset of this machinery for `/clr` scenarios
(see `scenarios/ClrIjw.cs::EmitNepMachinery`). The emitter takes a
hybrid approach across architectures:

- **Where the NEP thunk lives.** MSVC puts the thunk in `.text$mn` as
  a per-method COMDAT section on x86 and on arm64, and in a dedicated
  `.nep` section on x64. chibil always uses a `.nep` section
  regardless of target architecture. Because `ObjDumper` only iterates
  method bodies via the metadata's `MethodDefinitions` (resolved
  through the corresponding `06xxxxxx` CLR-token COFF symbol) and
  never dumps the COFF symbol table directly, the bare-name NEP
  thunk — which has no `06xxxxxx` token — is invisible to the
  comparison no matter which section it lives in. The thunk's section
  placement therefore doesn't need to match MSVC for tests to pass.

- **What the NEP thunk contains.** chibil always emits the single
  indirect-jump form: 6 bytes (`FF 25 [imm32]`) on x86/x64, 12 bytes
  (ADRP/LDR/BR) on arm64. The double-thunk-avoidance variant MSVC
  emits on x64 (`EB 08 / 0F 0B / FF 25 [→__m2mep] / FF 25 [→__mep]`)
  is skipped entirely — the optimization is functionally unobservable
  outside performance, and `ObjDumper` does not compare NEP-thunk
  bytes.

- **What metadata accompanies it.** Per managed user function: one
  `__mep@?fn` fixup slot in `.data` with a TOKEN reloc to the
  function's MethodDef CLR-token symbol, and one `.rdata$ilfixup`
  entry of type `0x0009` (32-bit) / `0x000A` (64-bit) targeting that
  slot. For scenarios that take a function's address from managed
  code (funcptr, funcptr-array, struct-funcptr) chibil also emits an
  `__unep@?fn` FieldDef with `HasFieldRVA` plus an `ADDR64`/`DIR32`
  reloc to the bare-name NEP thunk symbol.

- **What's intentionally not emitted.** The `__m2mep@?fn` companion
  slot, its matching `Type=0x0001`/`0x0002` ilfixup entry, and the
  leading `EB 08 / 0F 0B` byte sequence in the NEP thunk are MSVC-x64
  performance extras. `ObjDumper.IsClrThunkSymbol` filters
  `__m2mep@` (and `__unep@`) FieldDefs from `DumpFieldDefs`, and
  `IsClrThunkOptimizationSymbol` filters `.rdata$ilfixup` entries
  whose target is `__m2mep@`/`__unep@`, so reference objects and
  emitted objects compare equal despite the missing optimization
  slots. (`__unep@` never actually appears as an ilfixup target in
  MSVC output — it has its own ADDR reloc — but the filter is
  defensive.)

- **Storage class.** The bare-name NEP thunk and the `__mep@`
  fixup-slot symbol must both carry `IMAGE_SYM_CLASS_EXTERNAL` (use
  `ManagedCoffSymbolTableBuilder.AddExternalDataSymbol`) so that
  foreign translation units referencing the C name can resolve the
  function. `IMAGE_SYM_CLASS_STATIC` would link cleanly when only
  this one object is involved but break the moment another `.obj`
  has an extern reference to the same C name.

