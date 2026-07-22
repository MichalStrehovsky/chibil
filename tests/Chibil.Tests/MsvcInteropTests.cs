using Xunit;

namespace Chibil.Tests;

public class MsvcInteropTests : ChibiTestBase
{
    [Fact]
    public void MsvcDeclarationSpecifiers()
    {
        Compile("""
            typedef __int8 i8;
            typedef unsigned __int16 u16;
            typedef signed __int32 i32;
            typedef unsigned __int64 u64;
            __declspec(dllimport) int imported;
            __forceinline int add_one(int value) { return value + 1; }
            int __stdcall stdcall_after_return_type(int value);
            __stdcall int stdcall_before_return_type(int value);

            i8 a;
            u16 b;
            i32 c;
            u64 d;
            """);
    }

    [Theory]
    [InlineData("")]
    [InlineData("__clrcall ")]
    public void ChibiDefine_MsvcConsumeDirect(string cc)
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
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Theory]
    [InlineData("")]
    [InlineData("__clrcall ")]
    public void MsvcDefine_ChibiConsumeDirect(string cc)
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
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Theory]
    [InlineData("")]
    [InlineData("__clrcall ")]
    public void ChibiDefine_MsvcConsumeIndirect(string cc)
    {
        Compile($$"""
            int {{cc}}add(int a, int b) {
                return a + b;
            }
            """)
        .MsvcCompile($$"""
            int {{cc}}add(int, int);

            int main(void) {
                int ({{cc}}*addftn)(int, int) = add;
                return addftn(30, 12);
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Theory]
    [InlineData("")]
    [InlineData("__clrcall ")]
    public void MsvcDefine_ChibiConsumeIndirect(string cc)
    {
        MsvcCompile($$"""
            int {{cc}}multiply(int a, int b) {
                return a * b;
            }
            """)
        .Compile($$"""
            int {{cc}}multiply(int, int);

            int main(void) {
                int ({{cc}}*multiplyftn)(int, int) = multiply;
                return multiplyftn(6, 7);
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }
}
