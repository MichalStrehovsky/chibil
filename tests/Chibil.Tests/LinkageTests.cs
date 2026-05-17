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

    [Fact]
    public void StructArrayLocals_SameTag()
    {
        // Regression: MangleArrayTypeName must not be affected by name-backref
        // state. Two struct-element arrays of the same tag in one function must
        // both get correct $ArrayType$ TypeDefs, not degrade to pointers.
        Compile("""
            struct Point { int x; int y; };
            int test(void) {
                struct Point arr1[5];
                struct Point arr2[3];
                int i;
                for (i = 0; i < 5; i++) { arr1[i].x = i*10;  arr1[i].y = i; }
                for (i = 0; i < 3; i++) { arr2[i].x = i+100; arr2[i].y = i; }
                return arr1[3].x + arr2[2].x;
            }
            int main(void) { return test(); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 132);
    }

    [Fact]
    public void FuncPtrArrayGlobal()
    {
        // Regression: MangleFuncPtr must not crash when called from
        // MangleArrayTypeName (outside MangleFunctionName context where
        // backref tables are null).
        Compile("""
            int f(int x) { return x; }
            int (*arr[2])(int);
            int main(void) { arr[0] = f; return arr[0](42); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void FuncPtrInvocation()
    {
        // Exercises the calli standalone signature encoding across TUs —
        // apply() is defined in one TU, add() in another, main calls apply
        // which invokes add through a function pointer.
        Compile("""
            int add(int a, int b) { return a + b; }
            """)
        .Compile("""
            int add(int, int);
            int apply(int (*fn)(int, int), int x, int y) { return fn(x, y); }
            int main(void) { return apply(add, 30, 12); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }
}
