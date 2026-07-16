using System.Text;
using Xunit;

namespace Chibil.Tests;

public class LinkageTests : ChibiTestBase
{
    private static string ReadLastObjectText(CompilationBuilder builder)
        => Encoding.Latin1.GetString(File.ReadAllBytes(builder._objFiles[^1]));

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

    [Fact]
    public void GlobalFuncPtrInitializerToExternFunction()
    {
        Compile("""
            int target(void);
            int (*p)(void) = target;
            int main(void) { return p(); }
            """)
        .Compile("""
            int target(void) { return 42; }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void DirectExternFunctionCallDoesNotReserveUnepField()
    {
        var builder = Compile("""
            int target(void);
            int main(void) { return target(); }
            """);

        Assert.DoesNotContain("__unep@", ReadLastObjectText(builder));

        builder.Compile("""
            int target(void) { return 42; }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void AddressTakenExternFunctionReservesUnepField()
    {
        var builder = Compile("""
            int target(void);
            int main(void) {
                int (*p)(void) = target;
                return p() + target();
            }
            """);

        Assert.Contains("__unep@", ReadLastObjectText(builder));

        builder.Compile("""
            int target(void) { return 21; }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-ffunction-sections")]
    public void AddressTakenFunctionCanBeDiscoveredAfterItsBodyIsEmitted(string option)
    {
        string[] options = option == null ? null : [option];

        Compile("""
            int target(void) {
                return 21;
            }

            int call_target(void) {
                int (*p)(void) = target;
                return p();
            }

            int main(void) {
                return call_target() * 2;
            }
            """, options)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-ffunction-sections")]
    public void AddressTakenFunctionCanReferenceItself(string option)
    {
        string[] options = option == null ? null : [option];

        Compile("""
            int recurse(int value) {
                int (*self)(int) = recurse;
                return value == 0 ? 1 : self(value - 1) + 1;
            }

            int main(void) {
                return recurse(41);
            }
            """, options)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void ExternGlobalUsedFromLiveFunctionGetsFieldTokenBeforeCodeGen()
    {
        MsvcCompile("int g_chibil_external = 41;")
        .Compile("""
            extern int g_chibil_external;
            int main(void) {
                g_chibil_external = g_chibil_external + 1;
                return g_chibil_external;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void ExternGlobalDeclarationAfterDefinitionUsesDefinitionField()
    {
        Compile("""
            int g = 42;
            extern int g;
            int main(void) { return g; }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void StaticGlobalFollowedByExternRetainsInternalLinkage()
    {
        Compile("""
            static int value = 41;
            extern int value;

            int main(void) {
                value = value + 1;
                return value;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void ExternGlobalWithInitializerIsDefinition()
    {
        Compile("""
            extern int value = 42;
            int main(void) { return value; }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void BlockScopeExternUsesFileScopeDefinition()
    {
        Compile("""
            int read_value(void) {
                extern int value;
                return value;
            }

            int value = 42;
            int main(void) { return read_value(); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void IncompleteArrayDeclarationMergesWithCompletedDefinition()
    {
        Compile("""
            extern int values[];
            int read_value(void) { return values[1]; }
            int values[2] = { 20, 42 };
            int main(void) { return read_value(); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void FixedArrayDeclarationsAreCompatible()
    {
        Compile("""
            extern int values[2];
            int values[2] = { 20, 42 };

            int main(void) {
                return __builtin_types_compatible_p(int[2], int[2])
                    ? values[1]
                    : 1;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Theory]
    [InlineData("int value; int value = 42;")]
    [InlineData("int value = 42; int value;")]
    public void TentativeAndInitializedDefinitionsMerge(string declarations)
    {
        Compile($$"""
            {{declarations}}
            int main(void) { return value; }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void NestedIncompleteArrayDeclarationMergesWithCompletedDefinition()
    {
        Compile("""
            extern int (*values)[];
            int (*values)[3];

            int main(void) {
                return sizeof *values;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 12);
    }

    [Fact]
    public void FunctionPointerDeclarationMergesNestedCompletedReturnType()
    {
        Compile("""
            extern int (*(*factory)(void))[];
            int (*(*factory)(void))[3];

            int main(void) {
                return sizeof *factory();
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 12);
    }

    [Fact]
    public void ConflictingGlobalArrayBoundsAreRejected()
    {
        CompileExpectingError("""
            extern int values[2];
            int values[3];
            """)
        .AssertErrorContains("conflicting types");
    }

    [Fact]
    public void ExternalGlobalFollowedByStaticIsRejected()
    {
        CompileExpectingError("""
            extern int value;
            static int value;
            """)
        .AssertErrorContains("static declaration follows a non-static declaration");
    }

    [Fact]
    public void DuplicateGlobalInitializerIsRejected()
    {
        CompileExpectingError("""
            int value = 1;
            int value = 2;
            """)
        .AssertErrorContains("redefinition");
    }

    [Fact]
    public void FunctionThenGlobalWithSameNameIsRejected()
    {
        CompileExpectingError("""
            int item(void);
            int item;
            """)
        .AssertErrorContains("redeclared as a different kind of symbol");
    }

    [Fact]
    public void ExternGlobalReferencedOnlyByGlobalInitializerUsesDataRelocation()
    {
        Compile("""
            extern int a;
            int *b = &a;
            int main(void) { return *b; }
            """)
        .Compile("""
            int a = 42;
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-ffunction-sections")]
    public void SameTranslationUnitForwardFunctionReferencesLink(string option)
    {
        string[] options = option == null ? null : [option];

        Compile("""
            int later(int value);

            int first(int value) {
                return later(value) + 1;
            }

            int later(int value) {
                return value * 2;
            }

            int recurse(int value) {
                if (value == 0)
                    return 1;
                return recurse(value - 1) + 1;
            }

            int even(int value);
            int odd(int value) {
                return value == 0 ? 0 : even(value - 1);
            }
            int even(int value) {
                return value == 0 ? 1 : odd(value - 1);
            }

            int main(void) {
                return first(10) + recurse(3) + even(4);
            }
            """, options)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 26);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-fdata-sections")]
    public void SameTranslationUnitForwardGlobalReferencesLink(string option)
    {
        string[] options = option == null ? null : [option];

        Compile("""
            extern int value;

            int before_definition(void) {
                return value + 1;
            }

            int value = 40;

            int after_definition(void) {
                value = value + 1;
                return value;
            }

            int main(void) {
                return before_definition() + after_definition() - 40;
            }
            """, options)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void PlainInlineDefinitionDoesNotProvideExternalDefinition()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Compile("""
                inline int getnum(void) { return 42; }
                int main(void) { return getnum(); }
                """)
            .Link(["/entry:main", "/subsystem:console"]));

        Assert.Contains("getnum", ex.Message);
    }

    [Fact]
    public void StaticInlineDefinitionCanBeUsedInSameTranslationUnit()
    {
        Compile("""
            static inline int getnum(void) { return 42; }
            int main(void) { return getnum(); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void StaticInlineReferencedByGlobalInitializerStaysLiveAfterRedeclaration()
    {
        Compile("""
            static inline int getnum(void) { return 42; }
            int (*p)(void) = getnum;
            static inline int getnum(void);
            int main(void) { return p(); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void ExternInlineDefinitionProvidesExternalDefinition()
    {
        Compile("""
            extern inline int getnum(void) { return 42; }
            int main(void) { return getnum(); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void ExternInlineRedeclarationMakesInlineDefinitionExternal()
    {
        Compile("""
            inline int getnum(void) { return 42; }
            extern inline int getnum(void);
            int main(void) { return getnum(); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }
}
