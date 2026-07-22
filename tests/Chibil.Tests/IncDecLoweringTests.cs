using Xunit;

namespace Chibil.Tests;

/// <summary>
/// Behavioral tests for scalar increment/decrement and compound-assignment
/// lowering. These assert observable behavior only (exit codes), so they must
/// pass both before and after the lowering optimizations in
/// <c>Parser.ToAssign</c> and <c>CodeGen.GenExprDiscard</c>.
/// </summary>
public class IncDecLoweringTests : ChibiTestBase
{
    [Fact]
    public void PostfixIncrementDiscardedInLoop()
    {
        Compile("""
            int main() {
                int n = 0;
                for (int i = 0; i < 10; i++)
                    n++;
                return n;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 10);
    }

    [Fact]
    public void PostfixDecrementDiscardedInLoop()
    {
        Compile("""
            int main() {
                int n = 100;
                int i = 10;
                while (i-- > 0)
                    n--;
                return n;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 90);
    }

    [Fact]
    public void PrefixIncrementAndCompoundOnLocal()
    {
        Compile("""
            int main() {
                int x = 5;
                ++x;        // 6
                x += 4;     // 10
                x -= 3;     // 7
                x *= 6;     // 42
                return x;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void CompoundAssignOnParameter()
    {
        Compile("""
            int bump(int p) {
                p += 7;
                ++p;
                return p;
            }
            int main() {
                return bump(34);
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void CompoundAssignOnGlobal()
    {
        Compile("""
            int g = 10;
            int main() {
                g += 5;     // 15
                g--;        // 14
                ++g;        // 15
                g *= 2;     // 30
                return g;
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 30);
    }

    [Fact]
    public void PointerPostIncrementStrlen()
    {
        Compile("""
            int my_strlen(const char *s) {
                const char *p = s;
                while (*p)
                    p++;
                return (int)(p - s);
            }
            int main() {
                return my_strlen("hello, world!");  // 13
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 13);
    }

    [Fact]
    public void PointerPostIncrementAsAddressProducer()
    {
        Compile("""
            int main() {
                char buf[8];
                char *p = buf;
                *p++ = 'a';
                *p++ = 'b';
                *p++ = 'c';
                *p = 0;
                if (buf[0] != 'a' || buf[1] != 'b' || buf[2] != 'c' || buf[3] != 0)
                    return 1;
                if ((int)(p - buf) != 3)
                    return 2;
                return 42;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void PostfixUsedForItsValue()
    {
        Compile("""
            int main() {
                int x = 41;
                int y = x++;    // y = 41, x = 42
                if (y != 41) return 1;
                return x;       // 42
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void PostfixIndexUsedForItsValue()
    {
        Compile("""
            int main() {
                int a[4] = {0, 0, 0, 0};
                int i = 0;
                a[i++] = 10;    // a[0] = 10, i = 1
                a[i++] = 32;    // a[1] = 32, i = 2
                return a[0] + a[1] + (i - 2);  // 42
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void CompoundAssignEvaluatesRhsOnce()
    {
        Compile("""
            int calls = 0;
            int next() { calls++; return 3; }
            int main() {
                int x = 36;
                x += next();        // 39, calls == 1
                if (calls != 1) return 1;
                return x + calls + 2;   // 39 + 1 + 2 = 42
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void VlaPostIncrementRejected()
    {
        // A VLA is an array type (not a modifiable scalar lvalue), so `a++`
        // must be rejected like any array. Regression guard: the post-inc/dec
        // fast path must not treat a VLA Var as a scalar (which previously
        // crashed the compiler instead of reporting a diagnostic).
        CompileExpectingError("""
            int main() {
                int n = 4;
                int a[n];
                a++;
                return 0;
            }
            """)
        .AssertErrorContains("not an lvalue");
    }
}
