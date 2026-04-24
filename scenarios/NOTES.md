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
