using Xunit;

namespace Chibil.Tests;

public class MsvcInteropTests : ChibiTestBase
{
    [Theory]
    [InlineData("")]
    [InlineData("__clrcall ")]
    public void ChibiDefine_MsvcConsume(string cc)
    {
        Compile($$"""
            int {{cc}}add(int a, int b) {
                return a + b;
            }
            """)
        .MsvcCompile($$"""
            int {{cc}}add(int, int);

            int main(void) {
                return add(30, 12);
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Theory]
    [InlineData("")]
    [InlineData("__clrcall ")]
    public void MsvcDefine_ChibiConsume(string cc)
    {
        MsvcCompile($$"""
            int {{cc}}multiply(int a, int b) {
                return a * b;
            }
            """)
        .Compile($$"""
            int {{cc}}multiply(int, int);

            int main(void) {
                return multiply(6, 7);
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }
}
