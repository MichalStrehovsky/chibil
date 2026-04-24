# Notes for the MSIL Backend

This file captures metadata generation patterns discovered during the
`/clr:pure` research that are **not** covered by a dedicated scenario
but are important for a future compiler backend.

## Enums are just int32

MSVC `/clr:pure` generates **no TypeDef** for C enum types. Enum values
are inlined as integer constants. The parameter and local signatures use
plain `int32` regardless of the enum type.

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
    union { int x; float y; };
    int z;
};
```

MSVC generates a **single TypeDef** for `Outer` with the total size (16
bytes). There are no separate TypeDefs for `Inner` or the anonymous
union. Member access is done entirely through offset arithmetic on the
opaque value type (`ldloca` + constant offset + `ldind`/`stind`).

This means the backend does not need to generate nested TypeDefs or
FieldDefs for struct members. The struct is an opaque bag of bytes with
a known size and alignment.

## Self-referential structs use standard pointer encoding

```c
struct Node { int val; struct Node* next; };
```

The TypeDef for `Node` has the expected size (8 on x86, 16 on arm64).
The `next` field does not produce a FieldDef. Access to `next` in IL is
`ldloc.0` (pointer to Node) + `ldc.i4.4` + `add` + `ldind.i4` — just
offset arithmetic. The pointer-to-self is not encoded in the metadata at
all; it only exists in the method signature when `Node*` is a parameter:
`Ptr ValueType Node`.

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

## Atomic operations map to System.Threading.Interlocked

MSVC intrinsics `_InterlockedExchange` and `_InterlockedCompareExchange`
compile to `call System.Threading.Interlocked::Exchange(int32&, int32)`
and `CompareExchange(int32&, int32, int32)` in MSIL. The chibicc GCC-
style `__atomic_exchange_n` / `__atomic_compare_exchange_n` builtins
should map to the same Interlocked methods.

The first parameter uses `Ptr modreq(IsVolatile) int32` in the method
signature and `modreq(IsVolatile) int32` for volatile locals.

See the `atomic` scenario for the full pattern.

## TLS is blocked in /clr:pure

`__declspec(thread)`, `_Thread_local`, and C11 `thread_local` are all
rejected by MSVC in `/clr:pure` mode (error C3389/C3403). The chibicc
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

## Architecture differences in IL

Several constructs generate different IL for x86 vs ARM64:

| Pattern | x86 | ARM64 |
|---------|-----|-------|
| Pointer arithmetic constants | `ldc.i4.N` | `ldc.i4.N` + `conv.i8` |
| Switch instruction | Direct `switch` | Bounds-check (`blt.s`/`bgt.s`) before `switch` |
| Struct TypeDef | No alignment member | `<alignment member>` int32 field added |
| Local count for switch | 2 locals | 3 locals (extra temp for bounds check) |
| CRTMA slot size | 4 bytes (Align4Bytes) | 8 bytes (Align8Bytes) |
| mscorlib hash | `32 CD 81 47...` | `28 DC 37 8B...` |

The backend needs to handle these per-architecture differences when
generating IL.
