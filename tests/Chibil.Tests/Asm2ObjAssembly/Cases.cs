// Test assembly that asm2obj converts to a managed COFF object and the
// Chibil.Tests harness links against a chibil-compiled C translation
// unit defining (or extern-declaring) the matching functions. The
// chibil-emitted signatures and the asm2obj-rewritten ECMA blobs MUST
// agree byte-for-byte (and the COFF mangled symbol name too) — link.exe
// enforces both, so a successful link verifies modifier-injection
// correctness in BOTH the rewriter and the mangler at once.
//
// Each case below exercises a specific modifier-injection pattern in
// one of two directions:
//
//   Direction 1: extern in C# (ForwardRef), body in C. asm2obj
//     synthesizes a MemberRef parented on <Module>; chibil emits the
//     MethodDef + IL body. link.exe binds the MemberRef → MethodDef.
//
//   Direction 2: body in C# (regular static), extern in C. asm2obj
//     emits a MethodDef in <Module>; chibil emits a MemberRef for the
//     extern declaration. link.exe binds the MemberRef → MethodDef.
//
// mainCRTStartup is the linker's default entry point: it drives every
// case with known inputs and returns a fixed checksum so the test
// asserts on both linkability AND runtime correctness of the
// modifier-rewritten calls.

using System.Runtime.CompilerServices;
using Asm2Obj;

[CompilerGlobalScope]
unsafe static class Cases
{
    // ═══════════════════════════════════════════════════════════════════
    //   Direction 1: extern in C# (ForwardRef), body in C
    // ═══════════════════════════════════════════════════════════════════
    //
    // Every C function chibil compiles is __cdecl by default on x64,
    // so the matching C# extern needs [return: CallConvCdecl] to
    // produce the modopt(CallConvCdecl) on the return type.

    // 1. Baseline: no modifiers other than the cdecl return-type modopt.
    [MethodImpl(MethodImplOptions.ForwardRef)]
    [return: CallConvCdecl]
    extern static int c_basic(int a, int b);

    // 2. Plain `char` on both return and param. Chibil encodes plain
    //    `char` as modopt(IsSignUnspecifiedByte) int8. On the return,
    //    the cdecl modopt comes first, then the IsSignUnspecifiedByte
    //    leaf marker (canonical order).
    [MethodImpl(MethodImplOptions.ForwardRef)]
    [return: CallConvCdecl, IsSignUnspecifiedByte]
    extern static sbyte c_char([IsSignUnspecifiedByte] sbyte c);

    // 3. char* — one Pointer layer + leaf modopt at slot 1.
    [MethodImpl(MethodImplOptions.ForwardRef)]
    [return: CallConvCdecl]
    extern static int c_charptr([IsSignUnspecifiedByte] sbyte* s);

    // 4. char** — two Pointer layers + leaf modopt at slot 2.
    //    Exercises slot-N+1 injection inside Pointer recursion.
    [MethodImpl(MethodImplOptions.ForwardRef)]
    [return: CallConvCdecl]
    extern static int c_charptrptr([IsSignUnspecifiedByte] sbyte** p);

    // 5. const char* — slot 1 has BOTH IsConst AND IsSignUnspecifiedByte.
    //    Verifies canonical ordering of multiple modifiers at the same
    //    slot (IsConst then IsSignUnspecifiedByte, matching chibil).
    [MethodImpl(MethodImplOptions.ForwardRef)]
    [return: CallConvCdecl]
    extern static int c_const_charptr([IsConst(1)][IsSignUnspecifiedByte] sbyte* s);

    // 6. `long` — chibil's LLP64 maps `long` to modopt(IsLong) int32
    //    on both return and param. Both sides need the leaf modopt.
    [MethodImpl(MethodImplOptions.ForwardRef)]
    [return: CallConvCdecl, IsLong]
    extern static int c_long([IsLong] int x);

    // 7. volatile int* — modreq(IsVolatile) at slot 1. Verifies the
    //    modreq emission path (modopt for everything else).
    [MethodImpl(MethodImplOptions.ForwardRef)]
    [return: CallConvCdecl]
    extern static int c_volatile_intptr([IsVolatile(1)] int* p);

    // 8. int* const — modopt(IsConst) at slot 0 (modifies the pointer
    //    itself, not the pointee). Verifies level=0 (default) targets
    //    the outermost type slot.
    [MethodImpl(MethodImplOptions.ForwardRef)]
    [return: CallConvCdecl]
    extern static int c_const_intptr([IsConst] int* p);

    // 9. const void* — slot 1 IsConst + Void leaf. Exercises the
    //    no-VoidPointer-shortcut path: the rewriter must emit
    //    `Ptr modopt(IsConst) Void` rather than the bare `Ptr Void`
    //    shortcut that would drop the const modifier.
    [MethodImpl(MethodImplOptions.ForwardRef)]
    [return: CallConvCdecl]
    extern static int c_const_voidptr([IsConst(1)] void* p);

    // ═══════════════════════════════════════════════════════════════════
    //   Direction 2: body in C# (regular static), extern in C
    // ═══════════════════════════════════════════════════════════════════
    //
    // These methods have bodies (no MethodImpl(ForwardRef)). asm2obj
    // emits them as MethodDefs in <Module>. The C side declares them
    // as `extern __clrcall` and calls them via a trampoline. Direction
    // 2 uses __clrcall (NOT cdecl) because chibil's __cdecl extern
    // calls trigger /clr IJW NEP-thunk emission (bare-name COFF symbol
    // + __unep@?fn slot + ADDR reloc) that asm2obj does not yet
    // produce. __clrcall extern calls are managed-only and resolve
    // via the metadata token alone — exactly what asm2obj's emitted
    // MethodDef supports.
    //
    // The trampolines themselves are Direction 1 (extern in C#,
    // defined in C) so mainCRTStartup can reach them.

    static int cs_double(int x)
    {
        return x * 2;
    }

    static int cs_charptr_strlen([IsConst(1)][IsSignUnspecifiedByte] sbyte* s)
    {
        int len = 0;
        while (s[len] != 0) len++;
        return len;
    }

    [return: IsLong]
    static int cs_long_negate([IsLong] int x)
    {
        return -x;
    }

    // Direction 1 trampolines that the C side defines and which call
    // back into the Direction 2 C# bodies above.
    [MethodImpl(MethodImplOptions.ForwardRef)]
    [return: CallConvCdecl]
    extern static int call_cs_double(int x);

    [MethodImpl(MethodImplOptions.ForwardRef)]
    [return: CallConvCdecl]
    extern static int call_cs_charptr_strlen([IsConst(1)][IsSignUnspecifiedByte] sbyte* s);

    [MethodImpl(MethodImplOptions.ForwardRef)]
    [return: CallConvCdecl, IsLong]
    extern static int call_cs_long_negate([IsLong] int x);

    // ═══════════════════════════════════════════════════════════════════
    //   Entry point
    // ═══════════════════════════════════════════════════════════════════
    static int mainCRTStartup()
    {
        int sum = 0;

        // ── Direction 1 ────────────────────────────────────────────────
        sum += c_basic(2, 3);                       // 5

        sum += c_char((sbyte)'X');                  // 88 ('X')

        sbyte* str = stackalloc sbyte[6];
        str[0] = (sbyte)'A';   // 65
        str[1] = (sbyte)'B';
        str[2] = (sbyte)'C';
        str[3] = (sbyte)'D';
        str[4] = (sbyte)'E';
        str[5] = 0;

        sum += c_charptr(str);                      // 65 ('A')
        sum += c_charptrptr(&str);                  // 65
        sum += c_const_charptr(str);                // 65
        sum += c_const_voidptr(str);                // 65

        sum += c_long(100);                         // 100

        int volatile_val = 42;
        sum += c_volatile_intptr(&volatile_val);    // 42

        int const_val = 7;
        sum += c_const_intptr(&const_val);          // 7

        // ── Direction 2 (via C trampolines) ────────────────────────────
        sum += call_cs_double(11);                  // 22

        sum += call_cs_charptr_strlen(str);         // 5 (length of "ABCDE")

        sum += call_cs_long_negate(-9);             // 9

        // Expected total:
        //   5 + 88 + 65 + 65 + 65 + 65 + 100 + 42 + 7 + 22 + 5 + 9 = 538
        return sum;
    }
}
