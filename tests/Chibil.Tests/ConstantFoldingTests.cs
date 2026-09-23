using Xunit;

namespace Chibil.Tests;

/// <summary>
/// Behavioral coverage for constant folding in code generation. CodeGen reuses
/// the parser's compile-time evaluator (<see cref="Chibil.ConstEval"/>) to
/// collapse compile-time-constant numeric expressions to a single load. These
/// tests pin down that folding preserves C semantics across signedness, integer
/// overflow width, casts, floating-point arithmetic, and — crucially —
/// comparisons and truthiness tests over floating-point operands, which the
/// evaluator must not compute by truncating the operands to integers first.
/// </summary>
public sealed class ConstantFoldingTests : ChibiTestBase
{
    private static readonly string[] ConsoleMain = ["/entry:main", "/subsystem:console"];

    [Fact]
    public void IntegerConstantExpressionsFold()
    {
        Compile("""
            int main(void) {
                if (320 * 200 + 100 / 2 - 8 != 64042) return 1;
                if ((7 & 3) != 3) return 2;
                if ((5 | 2) != 7) return 3;
                if ((6 ^ 3) != 5) return 4;
                if ((1 << 4) != 16) return 5;
                if ((256 >> 3) != 32) return 6;
                if ((3 < 7) != 1) return 7;
                if ((7 <= 7) != 1) return 8;
                if ((5 == 5) != 1) return 9;
                if ((5 != 6) != 1) return 10;
                if ((1 && 0) != 0) return 11;
                if ((0 || 3) != 1) return 12;
                if ((1 ? 40 : 99) != 40) return 13;
                if (-(5) != -5) return 14;
                if ((~0) != -1) return 15;
                if ((!0) != 1) return 16;
                return 42;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void IntegerOverflowFoldsWithWraparoundWidth()
    {
        // Folding must narrow to the expression's type width just like the
        // runtime `add` would: 2000000000 + 2000000000 = 4000000000 wraps to a
        // 32-bit int of -294967296.
        Compile("""
            int main(void) {
                if (2000000000 + 2000000000 != -294967296) return 1;
                return 42;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void UnsignedDivisionAndCastNarrowingFold()
    {
        Compile("""
            int main(void) {
                if (4294967295u / 2u != 2147483647u) return 1;   // unsigned, not signed -1/2
                if ((signed char)200 != -56) return 2;           // 200 wraps to -56
                if ((unsigned char)300 != 44) return 3;          // 300 & 0xFF
                if ((short)70000 != 4464) return 4;              // 70000 - 65536
                if ((-1) / 2 != 0) return 5;                     // signed truncates toward zero
                return 42;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void FloatingPointConstantExpressionsFold()
    {
        Compile("""
            int main(void) {
                if (3.0 * 2.5 != 7.5) return 1;
                if (1.5f + 2.5f != 4.0f) return 2;
                if (10.0 / 4.0 != 2.5) return 3;
                if ((int)(3.9 * 2.0) != 7) return 4;   // float arithmetic, integer cast
                if (-2.5 != -(2.5)) return 5;
                return 42;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void LongLongConstantFoldingPreserves64Bits()
    {
        // 1000000 * 1000000 = 10^12 overflows 32 bits; a long long constant must
        // fold to a 64-bit load, not be truncated to int.
        Compile("""
            int main(void) {
                long long big = 1000000LL * 1000000LL;
                if (big != 1000000000000LL) return 1;
                if (big >> 32 != 232) return 2;   // 10^12 >> 32 == 232
                return 42;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void ConstantDivisionByZeroDoesNotCrashCompiler()
    {
        // Integer division/modulo by zero and signed MIN / -1 overflow are
        // undefined; folding must treat them as non-constant and fall back to
        // runtime IL rather than throwing (DivideByZero/Overflow) in the
        // compiler. Compiling (in-process, no link) exercises the fallback.
        Compile("""
            int f(int x) { return x ? 1 / 0 : x; }
            int g(int x) { return x ? 7 % 0 : x; }
            long long h(long long x) { return x ? (-9223372036854775807LL - 1) / -1 : x; }
            int i(int x) { return x ? (-2147483647 - 1) / -1 : x; }
            int main(void) { return f(0) + g(0) + (int)h(0) + i(0); }
            """);
    }

    [Fact]
    public void CommaOperatorSideEffectsArePreserved()
    {
        // A comma nested inside a foldable parent must NOT be folded away: its
        // left operand's side effect must still run. `1 + (bump(), 2)` was
        // folding to 3 and dropping the bump() call.
        Compile("""
            int calls = 0;
            int bump(void) { calls++; return 0; }
            int main(void) {
                int x = 1 + (bump(), 2);   // must call bump(); x == 3
                if (x != 3) return 1;
                if (calls != 1) return 2;
                return 42;
            }
            """)
        .MsvcLink(ConsoleMain)
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void IntermediateOverflowIsNarrowedToTypeWidth()
    {
        // Each constant subexpression must wrap at its own type width before the
        // enclosing operation consumes it, matching runtime IL. These leaked an
        // un-narrowed 64-bit value through shifts and widening casts.
        Compile("""
            int main(void) {
                if (((3000000000u * 3u) >> 3) != 51258176u) return 1;   // uint wrap then shift
                if ((long long)(65536u * 65536u) != 0) return 2;        // uint wrap then widen
                if ((unsigned long long)(3000000000u * 3u) != 410065408ull) return 3;
                return 42;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void IntegerPromotionInShiftAndBitwiseNot()
    {
        // Shl/Shr/~ operands are integer-promoted (to at least int) in C, and IL
        // computes them at i4 width. Folding must not narrow the result to a
        // small unpromoted operand type.
        Compile("""
            int main(void) {
                if (((unsigned char)255 << 8) != 65280) return 1;   // promotes to int, not (uchar)0
                if ((~(unsigned char)0) != -1) return 2;            // promotes to int -1, not 255
                if ((~(unsigned short)0) != -1) return 3;
                return 42;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void FloatConstantsRoundToFloatPrecision()
    {
        // float subexpressions must round to float precision per operation (the
        // runtime uses r4 arithmetic), not stay in double.
        Compile("""
            int main(void) {
                if ((int)16777217.0f != 16777216) return 1;                  // not representable as float
                if ((int)((16777216.0f + 1.0f) - 16777216.0f) != 0) return 2; // per-node rounding
                return 42;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void FloatToUnsignedLongLongCastFolds()
    {
        // A float-to-unsigned-64 cast must convert as unsigned, not via a signed
        // long (which would mishandle values in [2^63, 2^64)).
        Compile("""
            int main(void) {
                unsigned long long a = (unsigned long long)9223372036854775808.0; // 2^63
                if (a != 9223372036854775808ull) return 1;
                return 42;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Regression: the shared integer evaluator used to truncate
    //  floating-point operands to 64-bit integers before comparing or
    //  testing them, so a floating-point comparison/truthiness test in a
    //  constant context (array size, enum, case, ?:) produced the wrong
    //  value. e.g. `1.5 < 1.9` folded to `1 < 1` (false) instead of true.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FloatComparisonInArraySizeIsEvaluatedCorrectly()
    {
        // 1.5 < 1.9 is true, so the array has 3 elements. The buggy evaluator
        // truncated both operands to 1 and picked 4.
        Compile("""
            int a[1.5 < 1.9 ? 3 : 4];
            int main(void) {
                return (int)(sizeof(a) / sizeof(a[0]));   // 3
            }
            """)
        .MsvcLink(ConsoleMain)
        .RunAndCheck(exitCode: 3);
    }

    [Fact]
    public void FloatTruthinessInConstantContextsIsEvaluatedCorrectly()
    {
        Compile("""
            int c1[0.5 ? 5 : 6];       // 0.5 is truthy -> size 5
            int c2[!0.5 ? 7 : 8];      // !0.5 is 0 -> size 8
            int c3[0.5 && 0.0 ? 1 : 9];// 0.5 && 0.0 is 0 -> size 9
            int main(void) {
                int n1 = (int)(sizeof(c1) / sizeof(c1[0]));
                int n2 = (int)(sizeof(c2) / sizeof(c2[0]));
                int n3 = (int)(sizeof(c3) / sizeof(c3[0]));
                if (n1 != 5) return 1;
                if (n2 != 8) return 2;
                if (n3 != 9) return 3;
                return 42;
            }
            """)
        .MsvcLink(ConsoleMain)
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void FloatComparisonFoldsInRuntimeExpression()
    {
        // The same evaluator now backs code-generation folding, so a
        // floating-point comparison used for its value must also be correct.
        Compile("""
            int main(void) {
                if ((1.5 < 1.9) != 1) return 1;
                if ((2.5 > 9.5) != 0) return 2;
                if ((0.1 + 0.2 == 0.3) != 0) return 3;   // IEEE: not exactly equal
                return 42;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void BoolCastConstantNormalizesToZeroOrOne()
    {
        // A cast to _Bool normalizes any nonzero value to 1, not a width
        // truncation: folding `(_Bool)3` must yield 1, and `(_Bool)0.5` must
        // yield 1 (not a truncation of 0.5 to 0).
        Compile("""
            int main(void) {
                _Bool a = 3;        // -> 1
                _Bool b = 256;      // -> 1 (not (unsigned char)256 == 0)
                _Bool c = 0.5;      // -> 1 (not (long)0.5 == 0)
                _Bool d = 0;        // -> 0
                if (a != 1) return 1;
                if (b != 1) return 2;
                if (c != 1) return 3;
                if (d != 0) return 4;
                return 42;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Constant-expression validation (integer-constant-expression rules
    //  and ill-formed-vs-non-constant classification).
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void IllFormedConstantArraySizeIsRejected()
    {
        // A constant-but-invalid array size is a hard error, not a silent VLA
        // (and must not crash the compiler).
        CompileExpectingError("int arr[1 / 0]; int main(void){ return 0; }")
            .AssertErrorContains("division by zero");
        CompileExpectingError("int arr[1 << 40]; int main(void){ return 0; }")
            .AssertErrorContains("shift count out of range");
    }

    [Fact]
    public void IllFormedConstantInitializerIsRejected()
    {
        // Ill-formed constant static initializers are diagnosed, not silently
        // downgraded to a runtime/degenerate initializer.
        CompileExpectingError("int x = 1 / 0; int main(void){ return 0; }")
            .AssertErrorContains("division by zero");
    }

    [Fact]
    public void NonIntegerConstantExpressionIsRejectedInIntegerContexts()
    {
        // enum values and case labels require an integer constant expression;
        // a floating or pointer-typed expression is not one.
        CompileExpectingError("enum { A = 1.5 }; int main(void){ return 0; }")
            .AssertErrorContains("integer constant expression");
        CompileExpectingError("int main(void){ switch (0) { case (int*)0: return 1; } return 0; }")
            .AssertErrorContains("integer constant expression");
    }

    [Fact]
    public void CommaOperatorInConstantExpressionIsRejected()
    {
        // The comma operator is only allowed in a constant expression inside an
        // unevaluated subexpression; an evaluated comma is a hard error.
        CompileExpectingError("enum { A = (1, 2) }; int main(void){ return 0; }")
            .AssertErrorContains("comma operator");
        CompileExpectingError("int arr[(1, 2)]; int main(void){ return 0; }")
            .AssertErrorContains("comma operator");
    }

    [Fact]
    public void ConstantLogicalOperatorsShortCircuit()
    {
        // `0 && x` and `1 || x` are valid constant expressions: the right operand
        // (a non-constant) is not evaluated, so these fold to 0 / 1.
        Compile("""
            int x;
            enum { A = 0 && x, B = 1 || x };
            int main(void) {
                if (A != 0) return 1;
                if (B != 1) return 2;
                return 42;
            }
            """)
        .MsvcLink(ConsoleMain)
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void CommaOperatorInStaticInitializerIsRejected()
    {
        // A static/file-scope initializer must be a constant expression; an
        // evaluated comma operator is not one.
        CompileExpectingError("int x = (1, 2); int main(void){ return x; }")
            .AssertErrorContains("comma operator");
    }

    [Fact]
    public void LocalAggregateWithIllFormedConstantElementFallsBackToRuntime()
    {
        // The constant preinitialization probe for a local aggregate must not
        // hard-error on an ill-formed constant element (e.g. division by zero);
        // it falls back to element-wise runtime initialization. Compiling (no
        // link/run, since the element traps at runtime) exercises the fallback.
        Compile("int f(void){ int a[1] = { 1 / 0 }; return a[0]; } int main(void){ return 0; }");
    }

    [Fact]
    public void NegativeArrayDesignatorIsRejected()
    {
        // A negative array designator index must be diagnosed, not crash.
        CompileExpectingError("int main(void){ int b[3] = { [-1] = 5 }; return b[0]; }")
            .AssertErrorContains("negative");
    }

    [Fact]
    public void InvalidBitFieldWidthIsRejected()
    {
        CompileExpectingError("struct S { int x : -1; }; int main(void){ return 0; }")
            .AssertErrorContains("bit-field width");
        CompileExpectingError("struct S { int x : 40; }; int main(void){ return 0; }")
            .AssertErrorContains("bit-field width");
    }

    [Fact]
    public void InvalidAlignmentIsRejected()
    {
        CompileExpectingError("_Alignas(3) int x; int main(void){ return 0; }")
            .AssertErrorContains("power of 2");
        CompileExpectingError("_Alignas(7) int y; int main(void){ return 0; }")
            .AssertErrorContains("power of 2");
    }

    // ═══════════════════════════════════════════════════════════════
    //  KNOWN-FAILING (disabled): pre-existing integer-promotion bug.
    //
    //  AddType sets the result type of a shift / bitwise-not to the
    //  UNPROMOTED left-operand type (TypeSystem.cs:344-348: `node.Ty =
    //  node.Lhs.Ty`). C requires integer promotion first, so for a small
    //  unsigned operand the `>>` should be arithmetic (signed int) after
    //  promotion, but chibil performs a logical (unsigned) shift. This
    //  affects BOTH the constant-folding path (ConstEval) and the runtime
    //  code path (CodeGen emits shr.un), so both are covered below. The
    //  folding change deliberately matches the runtime for transparency;
    //  fixing this requires promoting shift/~ operands in AddType (and it
    //  then flows to both paths). Re-enable both tests once that is fixed.
    //
    //  (~(unsigned char)0) >> 1:
    //    correct C  : (unsigned char)0 -> int 0 -> ~0 == -1 -> -1 >> 1 == -1
    //    chibil now  : logical shift of 0xFFFFFFFF -> 2147483647
    // ═══════════════════════════════════════════════════════════════

    [Fact(Skip = "Known pre-existing integer-promotion bug (TypeSystem.cs:344-348): the " +
        "constant fold of (~(unsigned char)0) >> 1 yields 2147483647 instead of -1 because " +
        "the shift is treated as unsigned (unpromoted operand type) rather than a signed " +
        "arithmetic shift after integer promotion.")]
    public void ShiftPromotion_ConstantFoldIsArithmeticAfterPromotion()
    {
        Compile("""
            int main(void) {
                // (unsigned char)0 promotes to int; ~0 == -1; -1 >> 1 == -1.
                return ((~(unsigned char)0) >> 1) == -1 ? 42 : 1;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "Known pre-existing integer-promotion bug (TypeSystem.cs:344-348): at run " +
        "time CodeGen emits shr.un for (~c) where c is unsigned char, so (~c) >> 1 yields " +
        "2147483647 instead of -1. The operand should be integer-promoted to signed int and " +
        "use an arithmetic shift.")]
    public void ShiftPromotion_RuntimeIsArithmeticAfterPromotion()
    {
        Compile("""
            int shift_it(unsigned char c) {
                // ~c promotes to int; the >> must be arithmetic (signed), not logical.
                return (~c) >> 1;
            }
            int main(void) {
                return shift_it(0) == -1 ? 42 : 1;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 42);
    }
}
