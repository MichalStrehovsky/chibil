# Notes for the MSIL Backend

This file captures metadata generation patterns discovered during the
`/clr:pure` research that are **not** covered by a dedicated scenario
but are important for a future compiler backend.

## Enums are just int32

MSVC `/clr:pure` generates **no TypeDef** for C enum types. Enum values
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
where `<hash>` is a translation-unit hash. The field has flags
`Assembly | Static` (0x0013) and type `int32`. Unlike global variables,
static locals do **not** get `FixedAddressValueTypeAttribute` and do
**not** need CRTMA initializer functions (when zero-initialized).

The IL accesses the field via `ldsfld` / `stsfld` with a CLR token
relocation to the field definition.

See the `static-local` scenario for the full pattern.

## Global variable initializers require C++ mode

The MSVC `/BC` (C backend) mode rejects any global variable with an
initializer, even `int g = 42;` (error C2099: "initializer is not a
constant"). Global initializers require `/TP` (C++ backend) mode, which
generates:

1. An initializer function `??__E<name>@@YMXXZ`
2. A `.CRTMA$XCC` section entry pointing to the initializer
3. The module constructor (`.cctor`) iterates the CRTMA table at startup

For a compiler backend, this means any global with a non-zero initializer
needs a dynamic initializer function and a CRTMA slot. Zero-initialized
globals (BSS) work without initializers.

See the `init` and `global` scenarios for the full CRTMA pattern.

Note this is a mismatch with C where global initialization fiasco is
not possible. It would be possible to express C initialization
semantics with IL:

```
.assembly extern mscorlib {}
.assembly relocsmethod {}

.class explicit sealed '$ArrayType7'
       extends [mscorlib]System.ValueType
{
    .pack 1
    .size 7
}

// --- RVA data ---
.data D_literal   = bytearray(48)
.data D_literal_1 = bytearray(65 6C 6C 6F 21 00)

.data D_hello = &(D_literal)
.data D_e     = &(D_literal_1)

// VTable slot and fixup â€” pointer-sized
#ifdef TARGET_64BIT
.data D_m = int64(0)
.vtfixup [1] int64 at D_m
#else
.data D_m = int32(0)
.vtfixup [1] int32 at D_m
#endif

// --- RVA-mapped fields ---
.field static valuetype '$ArrayType7' '$literal' at D_literal
.field static int8*      hello at D_hello
.field static int8*      e     at D_e
.field static native int m     at D_m

.method static int32 get() cil managed
{
    .vtentry 1 : 1
    ldc.i4 42
    ret
}

.method static int32 main() cil managed
{
    .entrypoint
    .maxstack 2

    ldsfld     native int m
    calli      int32()

    ldsfld     int8* hello
    ldind.i1
    add

    ldsfld     int8* e
    ldind.i1
    add

    ret
}
```

The above corresponds to:

```
char hello[] = "Hello!";
char* e = &hello[1];

int get()
{
    return 42;
}

int (*m)() = &get;

int main()
{
    return m() + hello[0] + *e;
}
```

However, the CLR only supports this on Windows (it's refused on Linux)
and there's problems with tooling too (trimming doesn't understand this),
native AOT doesn't understand this. ReadyToRun doesn't understand this.
It would all be likely fixable though.

## Atomic operations map to System.Threading.Interlocked

MSVC intrinsics `_InterlockedExchange` and `_InterlockedCompareExchange`
compile to `call System.Threading.Interlocked::Exchange(int32&, int32)`
and `CompareExchange(int32&, int32, int32)` in MSIL. The chibil GCC-
style `__atomic_exchange_n` / `__atomic_compare_exchange_n` builtins
should map to the same Interlocked methods.

The first parameter uses `Ptr modreq(IsVolatile) int32` in the method
signature and `modreq(IsVolatile) int32` for volatile locals.

See the `atomic` scenario for the full pattern.

## TLS is blocked in /clr:pure

`__declspec(thread)`, `_Thread_local`, and C11 `thread_local` are all
rejected by MSVC in `/clr:pure` mode (error C3389/C3403). The chibil
backend would need to either reject TLS variables or map them to
`[ThreadStatic]` attributes (managed thread-local storage).

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

## $ArrayType naming convention for element types

The `$ArrayType$$$BY<count><type>` naming convention uses a single
letter for the element type. Known type codes:

| Code | C type | MSIL type |
|------|--------|-----------|
| `D` | `char` (IsSignUnspecifiedByte) | I1 |
| `G` | `unsigned short` / `wchar_t` | UI2 |
| `H` | `int` | I4 |

The `<count>` is the array dimension minus 1 (zero-based). So `char[6]`
→ `$ArrayType$$$BY05D` (Size:6), `int[10]` → `$ArrayType$$$BY09H`
(Size:40), `wchar_t[6]` → `$ArrayType$$$BY05G` (Size:12).

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
C functions compiled under `/clr:pure /BC`. This information is needed
to generate COFF symbols that are link-compatible with MSVC objects.

### Function decorated names

Format: `?<name>@@$$J0YM<return><params>@Z`

| Component | Meaning |
|-----------|---------|
| `?` | Decorated name prefix |
| `<name>` | C function name |
| `@@` | Scope terminator (global scope) |
| `$$J0` | `extern "C"` linkage with C++ decoration |
| `Y` | Calling convention prefix |
| `M` | `__clrcall` (managed calling convention, forced by `/clr:pure`) |
| `<return>` | Return type code |
| `<params>` | Parameter type codes, or `X` for void (no params) |
| `@Z` | End of parameter list and name |

The `$$J0` prefix is NOT specific to `/clr:pure` — it is the standard
MSVC marker for `extern "C"` linkage with C++ name decoration. The C
frontend always generates `extern "C"` linkage. Under native compilation,
C functions get undecorated names (like `_main`), but under `/clr` they
need C++ decorated names for CLR metadata token resolution.

The `YM` indicates `__clrcall` calling convention (managed). Under native
compilation, this would be `YA` for `__cdecl` instead.

Other `$$` linkage prefixes that appear in the COFF objects:

| Code | Meaning |
|------|---------|
| `$$J0` | `extern "C"` with C++ decoration (used by C functions) |
| `$$F` | `__clrcall` managed function with C++ linkage (used for CRT helpers like `.cctor`) |

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
| `PA<type>` | `<type>*` | `Ptr <type>` |
| `PAX` | `void*` | `Ptr Void` |
| `PAH` | `int*` | `Ptr int32` |
| `PAD` | `char*` | `Ptr modopt(IsSignUnspecifiedByte) int8` |
| `PAPA<type>` | `<type>**` | `Ptr Ptr <type>` |
| `PAU<name>@@` | `struct <name>*` | `Ptr ValueType <name>` |
| `PEAU<name>@@` | `struct <name>*` (64-bit) | `Ptr ValueType <name>` |

Struct types:

| Code | C type | MSIL signature |
|------|--------|---------------|
| `U<name>@@` | `struct <name>` (by value) | `ValueType <name>` |
| `?AU<name>@@` | `struct <name>` (as return type) | `ValueType <name>` |

Function pointer types:

| Code | C type |
|------|--------|
| `P6M<ret><params>@Z` | `<ret> (__clrcall*)(<params>)` |

Examples:
```
?main@@$$J0YMHXZ                        int main(void)
?arith@@$$J0YMHHH@Z                     int arith(int, int)
?char_func@@$$J0YMHDCE@Z                int char_func(char, signed char, unsigned char)
?cast_float@@$$J0YMHHMN@Z               int cast_float(int, float, double)
?void_func@@$$J0YMXXZ                   void void_func(void)
?longlong_ret@@$$J0YM_JXZ               long long longlong_ret(void)
?ptr_param@@$$J0YMHPAH@Z                int ptr_param(int*)
?voidptr_param@@$$J0YMHPAX@Z            int voidptr_param(void*)
?dblptr_param@@$$J0YMHPAPAH@Z           int dblptr_param(int**)
?struct_ptr@@$$J0YMHPAUPoint@@@Z         int struct_ptr(struct Point*)
?struct_ret@@$$J0YM?AUPoint@@HH@Z        struct Point struct_ret(int, int)
?funcptr_param@@$$J0YMHP6MHH@Z@Z        int funcptr_param(int (*)(int))
?apply@@$$J0YMHP6MHHH@ZHH@Z             int apply(int (*)(int,int), int, int)
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

### 3. Global variable strategy
Under `/clr:pure /BC`, global initializers are rejected. The backend must
either:
- Use `/TP` mode for globals with initializers (generates CRTMA)
- Generate its own initializer functions + CRTMA entries
- Restrict to zero-initialized globals only

### 4. Dense vs sparse switch
The backend needs a heuristic to choose between IL `switch` instruction
(for dense cases starting near 0) and a binary compare tree (for sparse
or large-valued cases). Both patterns are demonstrated in scenarios.

### 5. Struct copy strategy
Struct assignment uses `cpblk` for large structs. The backend should use
`cpblk` with the struct's TypeDef size. For small structs accessed
field-by-field, `ldind`/`stind` with offsets works.

### 6. TLS
Thread-local storage is blocked in `/clr:pure`. The backend should reject
TLS variables with an error, or map them to `[ThreadStatic]` fields
(which have different semantics from native TLS).

### 7. VLA and dynamic stack allocation
chibil supports VLAs (`int arr[n]`) which lower to `alloca`. MSVC
rejects VLA syntax in `/BC` mode, but `_alloca()` compiles to the
`localloc` IL instruction. The MSIL backend should:
- Lower VLAs to `localloc` (which allocates from the IL evaluation stack)
- `localloc` takes a byte count from the stack and returns a pointer
- The allocated memory is automatically freed when the method returns
- No deallocation instruction is needed

See the `alloca` scenario for the `localloc` pattern.

### 8. Unsupported C features in MSIL
The following chibil-supported features have NO MSIL equivalent and
must be handled by the backend:

| Feature | chibil support | MSIL strategy |
|---------|----------------|---------------|
| `asm("...")` | NodeKind.Asm | Reject with error — no inline assembly in managed code |
| `({...})` statement exprs | NodeKind.StmtExpr | GCC extension; lower to sequential IL with value on stack |
| `&&label` / `goto *ptr` | NodeKind.LabelVal/GotoExpr | GCC extension; lower to switch-dispatch (no label address in IL) |
| `_Atomic` compound assign | NodeKind.Cas/Exch | Generate `Interlocked.CompareExchange` CAS loop |

### 9. Compile-time-only features
These chibil features resolve entirely at compile time and produce
NO runtime artifact in MSIL:

- `_Alignof(type)` → constant integer
- `sizeof(expr)` → constant integer
- `_Generic(expr, ...)` → selects one branch at compile time
- `typeof(expr)` → GCC extension, resolves to a type at compile time
- Adjacent string literal concatenation → single merged constant

### 10. Alignment and packing
`_Alignas(N)` on struct members maps to `.pack N` in the TypeDef
ClassLayout metadata. `__attribute__((packed))` maps to `.pack 1`.
`_Alignof` is a compile-time constant and produces no metadata.

### 11. Inline functions
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

Several constructs generate different IL for x86 vs ARM64:

| Pattern | x86 | ARM64 |
|---------|-----|-------|
| Pointer index widening | `ldc.i4.N` | `ldc.i4.N` + `conv.i8` |
| Switch instruction | Direct `switch` | Bounds-check (`blt.s`/`bgt.s`) before `switch` |
| Struct TypeDef | No alignment member | `<alignment member>` int32 field added |
| Local count for switch | 2 locals | 3 locals (extra temp for bounds check) |
| CRTMA slot size | 4 bytes (Align4Bytes) | 8 bytes (Align8Bytes) |
| mscorlib hash | `32 CD 81 47...` | `28 DC 37 8B...` |

Note: `conv.i8` is specifically used on ARM64 to widen integer constants
before using them as pointer offsets (because pointers are 8 bytes on
ARM64). On x86, pointer-sized arithmetic uses 4-byte integers directly.
However, `conv.i8` can also appear on x86 in other contexts (e.g., when
the C code uses `long long` types).

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
MSVC `/clr:pure` emits a **TypeRef** (not a TypeDef) with a null
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
// cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC incomplete.c
// link /DEBUG /subsystem:console incomplete.obj /entry:main
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
