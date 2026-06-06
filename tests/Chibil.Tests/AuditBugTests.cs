using Xunit;

namespace Chibil.Tests;

/// <summary>
/// Tests for bugs identified in the phase-1 audit of the MSIL backend.
/// </summary>
public class AuditBugTests : ChibiTestBase
{
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
        .Link(["/entry:main", "/subsystem:console"])
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
