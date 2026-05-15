using Xunit;

namespace Chibil.Tests;

/// <summary>
/// Tests for bugs identified in the phases 2-5 audit of the MSIL backend.
/// </summary>
public class AuditPhase2Tests : ChibiTestBase
{
    [Fact]
    public void ScratchLocalStructTypeSafety()
    {
        // Two distinct struct types with same size must not share a scratch local.
        // This exercises the GenAddr FunCall spill path where both calls produce
        // struct values that need scratch locals in the same expression.
        Compile("""
            struct A { int x; int y; };
            struct B { int a; int b; };
            struct A make_a(void) { struct A r; r.x = 10; r.y = 20; return r; }
            struct B make_b(void) { struct B r; r.a = 30; r.b = 40; return r; }
            int main() {
                return make_a().x + make_b().a;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 40);
    }

    [Fact(Skip = "Requires minicrt.obj for __CxxPureMSILEntry linkage")]
    public void CxxPureMSILEntryEnvp()
    {
        // BUG-15: main(argc, argv, envp) — envp must be passed from __CxxPureMSILEntry
        // Hard to test directly since we don't control envp in our harness.
        // Instead, just verify 3-param main compiles and links.
        Compile("""
            int main(int argc, char **argv, char **envp) {
                return argc;
            }
            """)
        .Link(["/subsystem:console"])
        .RunAndCheck(exitCode: 1);
    }

    [Fact]
    public void ShiftByLongLong()
    {
        // BUG-17: Shift amount must be coerced to int32
        Compile("""
            int main() {
                int x = 1;
                long long shift = 3;
                return x << shift;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 8);
    }

    [Fact]
    public void SwitchLongLongCase()
    {
        Compile("""
            long long test(long long x) {
                switch (x) {
                    case 0x100000000LL: return 1;
                    case 0x200000000LL: return 2;
                    default: return 0;
                }
            }
            int main() {
                return (int)test(0x100000000LL);
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 1);
    }

    [Fact]
    public void BareReturnInNonVoid()
    {
        // BUG-19: Bare return in non-void function should not crash
        Compile("""
            int f(int x) {
                if (x > 0) return x;
                return;
            }
            int main() {
                return f(42);
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void StructReturnMemberAccess()
    {
        Compile("""
            struct Point { int x; int y; };
            struct Point make(int a, int b) { struct Point p; p.x = a; p.y = b; return p; }
            int main() {
                return make(10, 20).x + make(30, 40).y;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 50);
    }
}
