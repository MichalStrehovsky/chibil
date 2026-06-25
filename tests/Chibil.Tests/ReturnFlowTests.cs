using Xunit;

namespace Chibil.Tests;

public sealed class ReturnFlowTests : ChibiTestBase
{
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
