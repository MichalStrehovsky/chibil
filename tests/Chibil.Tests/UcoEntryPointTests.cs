using Chibil;
using Xunit;

namespace Chibil.Tests;

public sealed class UcoEntryPointTests
{
    private static readonly string[] Main = ["/entry:main", "/OPT:REF"];

    private static string[] Options(bool sections = false) => sections
        ? ["-fuco-entrypoints", "-ffunction-sections", "-fdata-sections"]
        : ["-fuco-entrypoints"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndirectCallsAndInitializedAddressesAgree(bool sections)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            int add(int left, int right) { return left + right; }
            int (*pointer)(int, int) = add;
            int (*array[2])(int, int) = { add, add };
            struct Holder { int before; int (*callback)(int, int); int after; };
            struct Holder holder = { 11, add, 22 };
            void *address = add;
            void **indirect = &address;
            int main(void) {
                int (*local)(int, int) = add;
                if ((void *)local != address || *indirect != (void *)add ||
                    pointer != local || array[0] != local || array[1] != local ||
                    holder.callback != local || holder.before != 11 || holder.after != 22)
                    return 1;
                return pointer(10, 2) + array[1](5, 7) + holder.callback(6, 6) + local(3, 3);
            }
            """, Options(sections)).Link(Main).RunAndCheck(42);
    }

    [Theory]
    [InlineData("")]
    [InlineData("__clrcall")]
    public void ValueOnlyFunctionPointerSlotsDoNotRequireFixedAddresses(string convention)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile($$"""
            int {{convention}} target(int value) { return value + 1; }
            int ({{convention}} *pointer)(int) = target;
            int main(void) {
                int ({{convention}} *local)(int) = target;
                if (pointer != local) return 1;
                return pointer(20) + local(20);
            }
            """, [.. Options(true), "-fno-fixed-address-statics"])
            .Link(Main).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExternAddressesResolveInBothInputOrders(bool reverse, bool sections)
    {
        const string consumer = """
            int target(int);
            int (*pointer)(int) = target;
            void *address(void);
            int main(void) {
                if ((void *)pointer != address() || pointer != target) return 1;
                return pointer(20) + target(20);
            }
            """;
        const string definition = "int target(int value) { return value + 1; }";
        const string observer = "int target(int); void *address(void) { return target; }";
        using var builder = new CompilationBuilder(true);
        builder.Compile(reverse ? definition : consumer, Options(sections))
            .Compile(reverse ? consumer : definition, Options(sections))
            .Compile(observer, Options(sections))
            .Link(Main).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameNamedStaticCallbacksRemainDistinct(bool sections)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            static int target(int value) { return value + 1; }
            int (*pointer)(int) = target;
            void *other_address(void);
            int other(void);
            int main(void) {
                if ((void *)pointer == other_address() || pointer != target) return 1;
                return pointer(20) + other();
            }
            """, Options(sections))
            .Compile("""
                static int target(int value) { return value + 2; }
                static int (*pointer)(int) = target;
                void *other_address(void) { return pointer; }
                int other(void) { return pointer(19); }
                """, Options(sections))
            .Link(Main).RunAndCheck(42);
    }

    [Fact]
    public void BooleanCallbacksKeepCNormalizationAndStorage()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            _Bool identity(_Bool value) { return value; }
            _Bool apply(_Bool (*callback)(_Bool), _Bool value) { return callback(value); }
            _Bool (*pointer)(_Bool) = identity;
            struct Flags { _Bool first; _Bool second; };
            struct Flags flags = { 7, 0 };
            _Bool values[2] = { 255, 0 };
            int main(void) {
                _Bool (*local)(_Bool) = identity;
                _Bool (*invoke)(_Bool (*)(_Bool), _Bool) = apply;
                _Bool *p = &values[0];
                if (sizeof(_Bool) != 1 || sizeof(flags) != 2 ||
                    (char *)&values[1] - (char *)p != 1) return 1;
                if (!pointer(256) || pointer(0) || !local(-1) ||
                    !invoke(pointer, 13) || invoke(local, 0)) return 2;
                if (!flags.first || flags.second || !*p || values[1]) return 3;
                return 42;
            }
            """, Options()).Link(Main).RunAndCheck(42);
    }

    [Theory]
    [InlineData("char")]
    [InlineData("long long")]
    [InlineData("long long padding; long long")]
    public void AggregateCallbacksUseTheNativeCallingConvention(string member)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile($$"""
            struct Value { {{member}} value; };
            struct Value increment(struct Value value) {
                value.value++;
                return value;
            }
            struct Value (*pointer)(struct Value) = increment;
            int main(void) {
                struct Value input = { 0 };
                input.value = 41;
                struct Value result = pointer(input);
                return input.value == 41 ? result.value : 1;
            }
            """, Options()).Link(Main).RunAndCheck(42);
    }

    [Fact]
    public void VoidCallbacksAndManagedPointersCoexist()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            int value;
            void set(int input) { value = input; }
            int __clrcall managed(int input) { return input + 1; }
            void (*setter)(int) = set;
            int (__clrcall *reader)(int) = managed;
            int main(void) {
                void (*local)(int) = set;
                int (__clrcall *other)(int) = managed;
                if (setter != local || reader != other) return 1;
                setter(40);
                local(reader(value));
                return other(value);
            }
            """, Options()).Link(Main).RunAndCheck(42);
    }

    [Fact]
    public void DirectRecursiveAndSelfAddressCallsUseTheCorrectEntries()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            int recurse(int value) {
                int (*self)(int) = recurse;
                return value == 0 ? 1 : self(value - 1) + 1;
            }
            int direct(int value) { return value == 0 ? 1 : direct(value - 1) + 1; }
            int main(void) { return recurse(20) + direct(20); }
            """, Options(true)).Link(Main).RunAndCheck(42);
    }

    [Fact]
    public void ScalarReturnModifiersAndStdcallArePreserved()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            char character(char value) { return value; }
            long __stdcall number(long value) { return value; }
            char (*first)(char) = character;
            long (__stdcall *second)(long) = number;
            int main(void) { return first(20) + second(22); }
            """, Options()).Link(Main).RunAndCheck(42);
    }

    [Fact]
    public void DiscardedPointerDataDoesNotRequireInitialization()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            int target(void) { return 99; }
            int (*unused)(void) = target;
            int main(void) { return 42; }
            """, [.. Options(true), "-fno-fixed-address-statics"])
            .Link(Main).RunAndCheck(42);
    }

    [Fact]
    public void UnassistedNativeFunctionPointersStillFailExplicitly()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            int target(void) { return 42; }
            int (*pointer)(void) = target;
            int main(void) { return pointer(); }
            """, ["-fno-uco-entrypoints"]);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link(Main));
        Assert.Contains("native/IJW", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrameworkTargetRejectsEnabledUcoMode(bool explicitlySelected)
    {
        using var builder = new CompilationBuilder(false);
        var error = Assert.Throws<ChibiException>(() => builder.Compile(
            "int main(void) { return 42; }", explicitlySelected
                ? ["-fnetcore", "-fuco-entrypoints", "-fnetframework"]
                : ["-fuco-entrypoints"]));
        Assert.Contains("requires -fnetcore", error.Message);
    }

    [Fact]
    public void FinalRuntimeAndFeatureOptionsWin()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int main(void) { return 42; }",
            ["-fuco-entrypoints", "-fno-uco-entrypoints", "-fnetframework",
             "-fuco-entrypoints", "-fnetcore"]).Link(Main).RunAndCheck(42);
    }

    [Fact]
    public void DisabledUcoModeCanTargetFramework()
    {
        using var builder = new CompilationBuilder(false);
        builder.Compile("int main(void) { return 42; }",
            ["-fnetcore", "-fuco-entrypoints", "-fno-uco-entrypoints", "-fnetframework"])
            .Link(Main).RunAndCheck(42);
    }

    [Fact]
    public void WrapperCanBeIncludedByMetadataNameWithoutBecomingTheEntryPoint()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int main(void) { return 42; }", Options(true))
            .Link(["/entry:main", "/OPT:REF", "/include:__uco@main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData("void *pointer = (char *)target + 1;", "cannot have an addend")]
    [InlineData("int pointer = (int)target;", "requires pointer-sized storage")]
    public void InvalidFunctionPointerInitializersFailExplicitly(string initializer, string diagnostic)
    {
        using var builder = new CompilationBuilder(true);
        var error = Assert.Throws<ChibiException>(() => builder.Compile($$"""
            int target(void) { return 42; }
            {{initializer}}
            int main(void) { return 42; }
            """, Options()));
        Assert.Contains(diagnostic, error.Message);
    }
}
