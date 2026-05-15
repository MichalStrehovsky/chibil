using Xunit;

namespace Chibil.Tests;

public class LinkageTests : ChibiTestBase
{
    [Fact]
    public void TestMacrosAndReferences()
    {
        Compile($$"""
            int other(void);

            int main(void) {
                return other();
            }
            """)
        .Compile($$"""
            int other(void) {
            #if SOME_MACRO
                return 100;
            #else
                return 0;
            #endif
            }
            """, ["-DSOME_MACRO"])
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 100);
    }
}
