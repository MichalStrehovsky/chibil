using Xunit;

namespace Chibil.Tests;

public sealed class ReturnFlowTests : ChibiTestBase
{
    [Fact]
    public void BareReturnsProduceTypedDefaults()
    {
        Compile("""
            struct Pair { int x; double y; };
            int integer(void) { return; }
            double floating(void) { return; }
            int *pointer(void) { return; }
            struct Pair aggregate(void) { return; }
            int main(void) {
                struct Pair value = aggregate();
                if (integer() || floating() != 0 || pointer() != 0) return 1;
                if (value.x || value.y != 0) return 2;
                return 42;
            }
            """)
        .Link(["/entry:main"])
        .RunAndCheck(42);
    }

    [Fact]
    public void NonVoidFunctionCanFallOffEndWithImplicitZero()
    {
        Compile("""
            int f(void) {
            }

            int main(void) {
                return f();
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void IntMainCanFallOffEnd()
    {
        Compile("""
            int main(void) {
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void LabelAtEndReachedByGotoFallsThroughToImplicitZero()
    {
        Compile("""
            int f(int x) {
                if (x)
                    goto end;
                return 1;
            end:
                ;
            }

            int main(void) {
                return f(1);
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void LabelAfterReturnCompilesUnderSimplifiedHeuristic()
    {
        Compile("""
            int f(void) {
                return 1;
            unused:
                ;
            }

            int main(void) {
                return f();
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 1);
    }

    [Fact]
    public void UnreachableStatementAfterReturnCompilesUnderSimplifiedHeuristic()
    {
        Compile("""
            int f(void) {
                int i = 0;
                return 1;
                i++;
            }

            int main(void) {
                return f();
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 1);
    }

    [Fact]
    public void VoidFunctionCanFallOffEnd()
    {
        Compile("""
            void f(int x) {
                if (x)
                    return;
            }

            int main(void) {
                f(0);
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }
}
