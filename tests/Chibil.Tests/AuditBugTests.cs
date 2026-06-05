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
}
