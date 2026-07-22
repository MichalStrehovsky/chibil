using Xunit;

namespace Chibil.Tests;

/// <summary>
/// Tests for bugs identified in the phase-1 audit of the MSIL backend.
/// </summary>
public class AuditBugTests : ChibiTestBase
{
    [Fact]
    public void ZeroInitializedBssGlobalsAreAligned()
    {
        Compile("""
            typedef unsigned long long uintptr_t;
            char c = 0;
            long long ll = 0;
            int main(void) { return ((uintptr_t)&ll) & 7; }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void StmtExprValuePreserved()
    {
        Compile("""
            int main() {
                int x = ({ int a = 5; a + 10; });
                return x;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 15);
    }

    [Fact]
    public void FloatCondition()
    {
        Compile("""
            int main() {
                double d = 1.5;
                if (d) return 42;
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void NotPointer()
    {
        Compile("""
            int main() {
                int x = 5;
                int *p = &x;
                if (!p) return 1;
                p = 0;
                if (!p) return 0;
                return 2;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void ArrayAndFunctionConditions()
    {
        Compile("""
            int callee(void) { return 0; }

            int main() {
                int arr[1];
                if (!arr) return 1;
                if (!callee) return 2;
                if (arr && callee) return 0;
                return 3;
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void NotLongLong()
    {
        Compile("""
            int main() {
                long long x = 1;
                if (!x) return 1;
                x = 0;
                if (!x) return 0;
                return 2;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void TlsRejected()
    {
        CompileExpectingError("""
            _Thread_local int tls_var;
            int main() { return tls_var; }
            """)
        .AssertErrorContains("thread");
    }

    [Fact]
    public void LabelsAsValuesRejected()
    {
        CompileExpectingError("""
            int main() {
            label:
                return &&label != 0;
            }
            """)
        .AssertErrorContains("expected an expression");
    }

    [Fact]
    public void ComputedGotoRejected()
    {
        CompileExpectingError("""
            int main() {
                void *p = 0;
                goto *p;
            }
            """)
        .AssertErrorContains("expected an identifier");
    }

    [Fact]
    public void CastToVoid()
    {
        Compile("""
            int side_effect(void) { return 42; }
            int main() {
                (void)side_effect();
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void CastToVoidOnVoidCall()
    {
        Compile("""
            void do_nothing(void) {}
            int main() {
                (void)do_nothing();
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void UnsignedInt32ToFloat()
    {
        Compile("""
            int main() {
                unsigned int big = 4294967295U;
                double d = (double)big;
                if (d > 4.2e9 && d < 4.3e9) return 0;
                return 1;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void AddrOfFunction()
    {
        Compile("""
            int add(int a, int b) { return a + b; }
            int apply(int (*fn)(int,int), int x, int y) { return fn(x,y); }
            int main() { return apply(&add, 10, 3); }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 13);
    }

    [Fact]
    public void FloatLeNaN()
    {
        Compile("""
            int main() {
                double nan = 0.0 / 0.0;
                if (nan <= 1.0) return 1;
                if (1.0 <= nan) return 2;
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void UnsignedInt64ToFloat()
    {
        Compile("""
            int main() {
                unsigned long long big = 18000000000000000000ULL;
                double d = (double)big;
                if (d > 17e18 && d < 19e18) return 0;
                return 1;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void VariadicDefError()
    {
        CompileExpectingError("""
            int my_sum(int n, ...) {
                return n;
            }
            int main() { return 0; }
            """)
        .AssertErrorContains("variadic");
    }

    [Fact]
    public void NestedStructMemberAssignment()
    {
        Compile("""
            struct Outer {
                struct {
                    int x;
                    int y;
                } inner;
            };

            int main(void) {
                struct Outer a;
                struct Outer b;
                a.inner.x = 1;
                a.inner.y = 2;
                b.inner = a.inner;
                return b.inner.x + b.inner.y;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 3);
    }

    [Fact]
    public void NestedStructMemberAssignmentAfterForwardTypeRef()
    {
        Compile("""
            struct inner;
            struct inner *extern_ref(struct inner *p) { return p; }

            struct Outer {
                struct {
                    int x;
                    int y;
                } inner;
            };

            int main(void) {
                struct Outer a;
                struct Outer b;
                extern_ref(0);
                a.inner.x = 1;
                a.inner.y = 2;
                b.inner = a.inner;
                return b.inner.x + b.inner.y;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 3);
    }

    [Fact]
    public void NestedStructDesignatorInitializerContinuesAfterComma()
    {
        Compile("""
            struct Inner {
                int i;
                int j;
            };

            struct Outer {
                int prefix;
                struct Inner inner;
                int suffix;
            };

            int main(void) {
                struct Outer x = { .inner.i = 1, .suffix = 2 };
                struct Outer y = { .inner.i = 3, 4, 5 };

                if (x.inner.i != 1 || x.inner.j != 0 || x.suffix != 2)
                    return 10;
                if (y.inner.i != 3 || y.inner.j != 4 || y.suffix != 5)
                    return 20;
                return 42;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void BitfieldAssignmentExpressionValue()
    {
        Compile("""
            struct Flags {
                unsigned int a : 3;
                unsigned int b : 5;
            };

            int id(int x) { return x; }

            int main(void) {
                struct Flags f;
                f.a = 0;
                f.b = 0;
                return id(f.a = 5) + f.a;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 10);
    }

    [Fact]
    public void BitfieldAssignmentExpressionValueIsStoredValue()
    {
        Compile("""
            struct Flags {
                unsigned int a : 3;
            };

            int main(void) {
                struct Flags f = { 0 };
                int assigned = (f.a = 9);
                if (assigned != 1)
                    return 10;
                if (f.a != 1)
                    return 20;
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void CharBitfieldExtractionUsesStackWidth()
    {
        Compile("""
            struct UnsignedBits {
                unsigned char low : 1;
                unsigned char next : 1;
            };

            struct SignedBits {
                signed char low : 1;
                signed char next : 1;
            };

            int main(void) {
                struct UnsignedBits u;
                struct SignedBits s;

                *(unsigned char *)&u = 0xFE;
                if (u.low != 0)
                    return 10;
                if (u.next != 1)
                    return 20;

                *(unsigned char *)&s = 0x02;
                if (s.low != 0)
                    return 30;
                if (s.next != -1)
                    return 40;

                return 42;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void CharPointerArithmeticDoesNotScaleByOne()
    {
        Compile("""
            int main(void) {
                char data[4];
                char *p = data;
                p[2] = 42;
                return data[2];
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void BitfieldUnsignedInt64Storage()
    {
        Compile("""
            struct S {
                unsigned __int64 x : 40;
                __int64 y : 40;
                unsigned __int64 full : 64;
            };
            struct Offset {
                unsigned __int64 lower : 4;
                unsigned __int64 mid : 40;
                unsigned __int64 upper : 20;
            };

            int main(void) {
                struct S s = { 0 };
                struct Offset o = { 0 };
                unsigned __int64 assigned = (s.x = 0x100000001ULL);
                if (assigned != 0x100000001ULL)
                    return 10;
                if (s.x != 0x100000001ULL)
                    return 20;
                __int64 signedAssigned = (s.y = -1);
                if (signedAssigned != -1)
                    return 30;
                if (s.y != -1)
                    return 40;
                unsigned __int64 fullAssigned = (s.full = 0xFEDCBA9876543210ULL);
                if (fullAssigned != 0xFEDCBA9876543210ULL)
                    return 50;
                if (s.full != 0xFEDCBA9876543210ULL)
                    return 60;
                o.lower = 0xFULL;
                o.upper = 0xABCDEULL;
                unsigned __int64 offsetAssigned = (o.mid = 0x100000002ULL);
                if (offsetAssigned != 0x100000002ULL)
                    return 70;
                if (o.lower != 0xFULL)
                    return 80;
                if (o.mid != 0x100000002ULL)
                    return 90;
                if (o.upper != 0xABCDEULL)
                    return 100;
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void ScalarGlobalLoadStore()
    {
        Compile("""
            int g;

            int main(void) {
                g = 41;
                return g + 1;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void LongLongBranchConditions()
    {
        Compile("""
            int main(void) {
                long long x = 0x100000000LL;
                long long y = 0;
                int r = 0;

                if (x)
                    r += 1;

                while (x) {
                    r += 2;
                    x = 0;
                }

                if (x || y)
                    r += 4;

                if (r == 3 && !y)
                    return 0;
                return 1;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void PointerArithmeticOffsetUsesPointerWidth()
    {
        // Under LLP64 `long` is 32-bit, so the element-size scaling in pointer
        // arithmetic must be done in pointer/ptrdiff_t width (64-bit), not in
        // `long` width. With a 32-bit multiply, `n * sizeof(int)` truncates for
        // large indices and the offset is wrong.
        //
        // `n` is `unsigned`, so `n * sizeof(int)` is a well-defined (modular)
        // operation with no signed overflow UB: in a 32-bit multiply it wraps
        // to 0, in a 64-bit (ptrdiff_t) multiply it is 2^32.
        Compile("""
            int main(void) {
                int *p = (int *)0;
                unsigned n = 0x40000000u;    /* 2^30 */
                int *q = p + n;              /* n * 4 == 2^32; 32-bit wraps to 0 */
                unsigned long long off = (unsigned long long)q;
                if (off == 0x100000000ULL) return 0;
                return 1;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void FuncAndFunctionTest()
    {
        Compile("""
            char* f1() {
                return __func__;
            }

            char* f2() {
                return __FUNCTION__;
            }

            int main(void) {
                char* r1 = f1();
                char* r2 = f2();
                if (r1[0] != 'f' || r1[1] != '1' || r1[2] != 0
                  || r2[0] != 'f' || r2[1] != '2' || r2[2] != 0)
                {
                    return 99;
                }

                if (sizeof(__func__) != 5)
                    return 98;

                return 100;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 100);
    }
}
