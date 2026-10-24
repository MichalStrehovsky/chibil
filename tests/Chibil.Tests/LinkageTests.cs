using Xunit;

namespace Chibil.Tests;

public class LinkageTests : ChibiTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FunctionTypedefDeclarationsWork(bool blockScope)
    {
        Compile($$"""
            typedef int Function(int);
            {{(blockScope ? "" : "Function answer;")}}
            int answer(int value) { return value + 1; }
            int main(void) {
                {{(blockScope ? "Function answer;" : "")}}
                return answer(41);
            }
            """)
        .Link().RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FunctionTypedefCallingConventionsArePreserved(bool useCast)
    {
        Compile($$"""
            typedef int __clrcall Callback(int);
            Callback answer;
            int __clrcall answer(int value) { return value + 1; }
            int main(void) {
                {{(useCast ? "return ((Callback *)answer)(41);" :
                    "Callback *callback = answer; return callback(41);")}}
            }
            """)
        .Link().RunAndCheck(42);
    }

    [Fact]
    public void FunctionTypedefCallingConventionOverridesDoNotMutateTheAlias()
    {
        Compile("""
            typedef int __clrcall Callback(int);
            Callback __cdecl native;
            Callback managed;
            int __cdecl native(int value) { return value; }
            int __clrcall managed(int value) { return value + 1; }
            int main(void) {
                Callback *callback = managed;
                return callback(41);
            }
            """)
        .Link().RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompositeFunctionReturnTypesRetainArrayBounds(bool reverse, bool prototype)
    {
        string incomplete = $"int (*(*pointer)({(prototype ? "int" : "")}))[];";
        const string complete = "int (*(*pointer)())[4];";
        Compile((reverse ? complete + incomplete : incomplete + complete) + """
            int main(void) { return sizeof(*pointer(0)) == 16 ? 42 : 1; }
            """)
        .Link().RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "__clrcall")]
    [InlineData(true, "")]
    [InlineData(true, "__clrcall")]
    public void FunctionLinkageAndCallingConventionsWork(bool isStatic, string callingConvention)
    {
        var builder = Compile($$"""
            {{(isStatic ? "static " : "")}}int {{callingConvention}} target(int value) { return value + 1; }
            int main(void) { return target(41); }
            """, ["-ffunction-sections"]);
        builder.Link().RunAndCheck(42);
    }

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
        .Link()
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
        .Link()
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
        .LinkOnNetCoreOrMsvcLinkOnFramework()
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
        .LinkOnNetCoreOrMsvcLinkOnFramework()
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
        .LinkOnNetCoreOrMsvcLinkOnFramework()
        .RunAndCheck(exitCode: 42);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ManagedFunctionPointerGlobalsWork(bool isStatic, bool dataSections)
    {
        var builder = Compile($$"""
            {{(isStatic ? "static " : "")}}int __clrcall target(int value) { return value + 1; }
            int (__clrcall *pointer)(int) = target;
            void *glob = target;
            void **indirect = &glob;
            int main(void) {
                if (glob != (void *)target || (void *)pointer != (void *)target ||
                    *indirect != (void *)target) return 1;
                return pointer(41);
            }
            """, dataSections ? ["-fdata-sections", "-ffunction-sections"] : null);
        builder.Link(gcSections: true).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ManagedFunctionPointerExternGlobalsWork(bool reverse, bool dataSections)
    {
        const string main = """
            int __clrcall target(int value);
            void *expected(void);
            int (__clrcall *pointer)(int) = target;
            int main(void) {
                if ((void *)pointer != expected()) return 1;
                return pointer(41);
            }
            """;
        const string definition = """
            int __clrcall target(int value) { return value + 1; }
            void *expected(void) { return target; }
            """;
        string[] flags = dataSections ? ["-fdata-sections", "-ffunction-sections"] : null;
        Compile(reverse ? definition : main, flags)
            .Compile(reverse ? main : definition, flags)
            .Link(gcSections: true).RunAndCheck(42);
    }

    [Fact]
    public void DirectExternFunctionCallWorks()
    {
        var builder = Compile("""
            int target(void);
            int main(void) { return target(); }
            """);

        builder.Compile("""
            int target(void) { return 42; }
            """)
        .Link()
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void AddressTakenExternFunctionWorks()
    {
        var builder = Compile("""
            int target(void);
            int main(void) {
                int (*p)(void) = target;
                return p() + target();
            }
            """);

        builder.Compile("""
            int target(void) { return 21; }
            """)
        .LinkOnNetCoreOrMsvcLinkOnFramework()
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
        .LinkOnNetCoreOrMsvcLinkOnFramework()
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
        .LinkOnNetCoreOrMsvcLinkOnFramework()
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void ExternGlobalUsedFromLiveFunctionGetsFieldTokenBeforeCodeGen()
    {
        MsvcCompile("int g_chibil_external = 41;", ["/Zl", "/GS-", "/d1clrNoPureCRT"])
        .Compile("""
            extern int g_chibil_external;
            int main(void) {
                g_chibil_external = g_chibil_external + 1;
                return g_chibil_external;
            }
            """)
        .MsvcLink()
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
        .Link()
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
        .Link()
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void ExternGlobalWithInitializerIsDefinition()
    {
        Compile("""
            extern int value = 42;
            int main(void) { return value; }
            """)
        .Link()
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
        .Link()
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
        .Link()
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
        .Link()
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
        .Link()
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
        .Link()
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
        .Link()
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
        .Link()
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
        .Link()
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
        .Link()
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
            .Link());

        Assert.Contains("getnum", ex.Message);
    }

    [Fact]
    public void StaticInlineDefinitionCanBeUsedInSameTranslationUnit()
    {
        Compile("""
            static inline int getnum(void) { return 42; }
            int main(void) { return getnum(); }
            """)
        .Link()
        .RunAndCheck(exitCode: 42);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("", "-ffunction-sections")]
    [InlineData("__clrcall ", null)]
    [InlineData("__clrcall ", "-ffunction-sections")]
    public void SameNamedStaticFunctionsRemainDistinctAcrossTranslationUnits(string cc, string option)
    {
        string[] options = option == null ? null : [option];

        Compile($$"""
            static int {{cc}}helper(int value) { return value + 10; }
            static int ({{cc}}*first_ptr)(int) = helper;
            int first(void) { return helper(1) == 11 && first_ptr(2) == 12; }
            """, options)
        .Compile($$"""
            static int {{cc}}helper(int value) { return value + 20; }
            static int ({{cc}}*second_ptr)(int) = helper;
            int second(void) { return helper(3) == 23 && second_ptr(4) == 24; }
            """, options)
        .MsvcCompile($$"""
            int first(void);
            int second(void);
            int {{cc}}helper(int value) { return value + 30; }
            int main(void) {
                int ({{cc}}*external_ptr)(int) = helper;
                if (!first()) return 1;
                if (!second()) return 2;
                if (helper(5) != 35) return 3;
                if (external_ptr(6) != 36) return 4;
                return 42;
            }
            """)
        .MsvcLink()
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
        .LinkOnNetCoreOrMsvcLinkOnFramework()
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void ExternInlineDefinitionProvidesExternalDefinition()
    {
        Compile("""
            extern inline int getnum(void) { return 42; }
            int main(void) { return getnum(); }
            """)
        .Link()
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
        .Link()
        .RunAndCheck(exitCode: 42);
    }
}
