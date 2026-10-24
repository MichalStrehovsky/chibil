using Xunit;

namespace Chibil.Tests;

public sealed class EmulatedVarargsTests : ChibiTestBase
{
    private static readonly string[] Enabled = ["-femulated-varargs"];

    [Theory]
    [InlineData("target(1)")]
    [InlineData("target(1, 2)")]
    public void CallsRequireTheFlag(string expression)
    {
        CompileExpectingError($$"""
            int target(int, ...);
            int main(void) { return {{expression}}; }
            """).AssertErrorContains("variadic");
    }

    [Fact]
    public void UnusedDeclarationsAndUnprototypedFunctionsRemainSupported()
    {
        Compile("""
            int unused(int, ...);
            typedef int (__clrcall *unused_pointer)(int, ...);
            int target();
            int main() { return target(41); }
            int target(int value) { return value + 1; }
            """).Link().RunAndCheck(42);
    }

    [Theory]
    [InlineData("int (*pointer)(int, ...) = target;", false)]
    [InlineData("int (*pointer)(int, ...) = target;", true)]
    [InlineData("void *pointer = (void *)&target;", false)]
    [InlineData("void *pointer = (void *)&target;", true)]
    public void UnmanagedAddressesAreRejected(string declaration, bool enabled)
    {
        CompileExpectingError($$"""
            int target(int, ...);
            {{declaration}}
            int main(void) { return 0; }
            """, enabled ? Enabled : null).AssertErrorContains("variadic");
    }

    [Fact]
    public void ManagedAddressesInStaticDataRequireTheFlag()
    {
        CompileExpectingError("""
            int __clrcall target(int, ...);
            int (__clrcall *pointer)(int, ...) = target;
            int main(void) { return 0; }
            """).AssertErrorContains("-femulated-varargs");
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "__clrcall")]
    [InlineData(true, "")]
    public void UnemittedFunctionBodiesDoNotValidateAddresses(bool enabled, string convention)
    {
        Compile($$"""
            int {{convention}} target(int, ...);
            static inline int unused(void) {
                void *address = target;
                return address != 0;
            }
            int main(void) { return 42; }
            """, enabled ? Enabled : null).Link().RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "__clrcall")]
    [InlineData(true, "")]
    public void DiscardedFunctionDesignatorsDoNotMaterializeAddresses(bool enabled, string convention)
    {
        Compile($$"""
            int {{convention}} target(int, ...);
            int main(void) { target; (void)target; return 42; }
            """, enabled ? Enabled : null).Link().RunAndCheck(42);
    }

    [Fact]
    public void MixedPromotedArgumentsAndIndependentCursors()
    {
        Compile("""
            int read(int named, ...) {
                __builtin_va_list ap, copy;
                __builtin_va_start(ap, named);
                __builtin_va_copy(copy, ap);
                if (__builtin_va_arg(copy, int) != -3) return 1;
                __builtin_va_end(copy);
                if (__builtin_va_arg(ap, int) != -3) return 2;
                if (__builtin_va_arg(ap, int) != 65535) return 3;
                if (__builtin_va_arg(ap, double) != 2.5) return 4;
                if (*__builtin_va_arg(ap, int *) != 7) return 5;
                if (__builtin_va_arg(ap, long long) != 1234567890123LL) return 6;
                __builtin_va_end(ap);
                __builtin_va_start(ap, named);
                int first = __builtin_va_arg(ap, int);
                __builtin_va_end(ap);
                return named - first;
            }
            int main(void) {
                signed char a = -3;
                unsigned short b = 65535;
                float c = 2.5f;
                int values[1] = { 7 };
                return (read)(39, a, b, c, values, 1234567890123LL);
            }
            """, Enabled).Link().RunAndCheck(42);
    }

    [Fact]
    public void NestedCallsLoopsAndForwarding()
    {
        Compile("""
            int consume(__builtin_va_list *ap) { return __builtin_va_arg(*ap, int); }
            int sum(int count, ...) {
                __builtin_va_list ap;
                __builtin_va_start(ap, count);
                int result = 0;
                for (int i = 0; i < count; ++i) result += consume(&ap);
                __builtin_va_end(ap);
                return result;
            }
            int empty(int result, ...) {
                __builtin_va_list ap;
                __builtin_va_start(ap, result);
                __builtin_va_end(ap);
                return result;
            }
            int main(void) {
                for (int i = 0; i < 100000; ++i)
                    if (sum(3, 10, sum(2, 3, 4), 4) != 21) return 1;
                return 1 + sum(2, empty(20), sum(1, 21));
            }
            """, Enabled).Link().RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointerMemberAssignmentsPreserveTheArgumentArea(bool assignNamedArgument)
    {
        string arguments = assignNamedArgument
            ? "holder.pointer = values, values, 22"
            : "values, holder.pointer = values, 22";
        Compile($$"""
            struct Holder { int *pointer; };
            int read(int *expected, ...) {
                __builtin_va_list ap;
                __builtin_va_start(ap, expected);
                int *pointer = __builtin_va_arg(ap, int *);
                int last = __builtin_va_arg(ap, int);
                __builtin_va_end(ap);
                if (pointer != expected || last != 22) return 1;
                return *pointer + last;
            }
            int main(void) {
                int values[8] = { 20 };
                struct Holder holder = { 0 };
                int result = read({{arguments}});
                if (holder.pointer != values || values[0] != 20) return 2;
                return result;
            }
            """, Enabled).Link().RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManagedPointersAndCrossTranslationUnitCalls(bool reverse)
    {
        const string definition = """
            int __clrcall sum(int count, ...) {
                __builtin_va_list ap;
                __builtin_va_start(ap, count);
                int result = 0;
                for (int i = 0; i < count; ++i) result += __builtin_va_arg(ap, int);
                __builtin_va_end(ap);
                return result;
            }
            """;
        const string consumer = """
            typedef int __clrcall function(int, ...);
            function sum;
            function *pointer = sum;
            struct Holder { function *callback; };
            struct Holder holder = { sum };
            int main(void) {
                function *local = sum;
                return pointer(2, 3, 4) + local(1, 14) + holder.callback(2, 10, 11);
            }
            """;
        Compile(reverse ? definition : consumer, Enabled)
            .Compile(reverse ? consumer : definition, Enabled)
            .Link().RunAndCheck(42);
    }

    [Fact]
    public void AggregateValuesPackedFieldsAndFlexibleStorage()
    {
        Compile("""
            struct Pair { int left; int right; };
            struct __attribute__((packed)) Packed { char tag; long long value; };
            struct Flex { int value; int items[]; };
            union Number { int value; double alignment; };
            struct Pair make(int left, int right) {
                struct Pair value = { left, right };
                return value;
            }
            struct Pair read(int named, ...) {
                __builtin_va_list ap;
                __builtin_va_start(ap, named);
                struct Pair first = __builtin_va_arg(ap, struct Pair);
                struct Packed packed = __builtin_va_arg(ap, struct Packed);
                struct Flex flexible = __builtin_va_arg(ap, struct Flex);
                union Number number = __builtin_va_arg(ap, union Number);
                int last = __builtin_va_arg(ap, int);
                __builtin_va_end(ap);
                struct Pair failure = { 0, 0 };
                if (first.left != 4 || first.right != 5) { failure.left = 1; return failure; }
                if (packed.tag != 1 || packed.value != 10) { failure.left = 2; return failure; }
                if (flexible.value != 2) { failure.left = 3; return failure; }
                if (number.value != 3) { failure.left = 4; return failure; }
                if (last != 17) { failure.left = 5; return failure; }
                struct Pair result = { first.left + packed.tag + flexible.value,
                    first.right + packed.value + number.value + last };
                return result;
            }
            int main(void) {
                struct Packed packed = { 1, 10 };
                struct Flex flexible = { 2, { 100, 200 } };
                union Number number = { 3 };
                struct Pair result = read(0, make(4, 5), packed, flexible, number, 17);
                return result.left + result.right;
            }
            """, Enabled).Link().RunAndCheck(42);
    }

    [Fact]
    public void AggregateVaArgMemberAccessPreservesMultipleResults()
    {
        Compile("""
            struct Outer { struct { int value; } inner; };
            int combine(int a, int b) { return a + b; }
            int read(int named, ...) {
                __builtin_va_list ap;
                __builtin_va_start(ap, named);
                int result = combine(__builtin_va_arg(ap, struct Outer).inner.value,
                                     __builtin_va_arg(ap, struct Outer).inner.value);
                __builtin_va_end(ap);
                return result;
            }
            int main(void) {
                struct Outer a = { { 20 } }, b = { { 22 } };
                return read(0, a, b);
            }
            """, Enabled).Link().RunAndCheck(42);
    }

    [Fact(Skip = "BUG: Passing a zero-sized struct as a variadic argument causes an access violation.")]
    public void EmptyAggregatesDoNotDereferenceAnEmptyArea()
    {
        Compile("""
            struct Empty {};
            int calls;
            int read(int named, ...) {
                __builtin_va_list ap;
                __builtin_va_start(ap, named);
                struct Empty value = __builtin_va_arg(ap, struct Empty);
                __builtin_va_end(ap);
                return calls == 1 ? 42 : 1;
            }
            int main(void) { struct Empty value = {}; return read(0, (calls++, value)); }
            """, Enabled).Link().RunAndCheck(42);
    }

    [Fact]
    public void CursorOperandsAreEvaluatedOnceAndPromoteBitfields()
    {
        Compile("""
            struct Bits { unsigned int value : 3; };
            struct SignedBits { signed int other : 4; };
            int read(int named, ...) {
                __builtin_va_list lists[2];
                int index = 0;
                __builtin_va_start(lists[index++], named);
                __builtin_va_copy(lists[index++], lists[0]);
                if (index != 2) return 1;
                index = 0;
                int first = __builtin_va_arg(lists[index++], int);
                int second = __builtin_va_arg(lists[0], int);
                int again = __builtin_va_arg(lists[index++], int);
                __builtin_va_end(lists[--index]);
                __builtin_va_end(lists[--index]);
                return first == 3 && second == -2 && again == 3 && index == 0 ? 42 : 2;
            }
            int main(void) {
                struct Bits bits = { 3 };
                struct SignedBits other = { -2 };
                return read(0, bits.value, other.other);
            }
            """, Enabled).Link().RunAndCheck(42);
    }

    [Theory]
    [InlineData("int target(int); int target(int, ...);")]
    [InlineData("int target(int, ...); int target(int);")]
    [InlineData("int target(); int target(int, ...);")]
    [InlineData("int target(int, ...); int target();")]
    public void IncompatibleRedeclarationsAreRejected(string declarations)
    {
        CompileExpectingError(declarations, Enabled).AssertErrorContains("conflicting");
    }

    [Theory]
    [InlineData("int (*pointer)(int, ...) = 0; int main(void) { return pointer(0, 1); }", "__clrcall")]
    [InlineData("int target(int, ...); int main(void) { void *p = target; return 0; }", "__clrcall")]
    [InlineData("int target(int value, ...) { return 0; } int main(void) { return 0; }", null)]
    [InlineData("int main(int value, ...) { return 0; }", "main")]
    [InlineData("int target(void); int main(void) { return target(1); }", "too many")]
    public void UnsupportedUsesAreDiagnosed(string source, string diagnostic)
    {
        if (diagnostic == null)
            CompileExpectingError(source).AssertErrorContains("variadic");
        else
            CompileExpectingError(source, Enabled).AssertErrorContains(diagnostic);
    }

    [Theory]
    [InlineData("int f(int named, ...) { __builtin_va_list ap; __builtin_va_start(ap, named); return __builtin_va_arg(ap, void); }", "complete object")]
    [InlineData("int f(int named, ...) { __builtin_va_list ap; { int named; __builtin_va_start(ap, named); } return 0; }", "last named")]
    [InlineData("int f(int named) { __builtin_va_list ap; __builtin_va_start(ap, named); return 0; }", "variadic function")]
    [InlineData("int f(int named, ...) { const __builtin_va_list ap = 0; __builtin_va_start(ap, named); return 0; }", "modifiable")]
    [InlineData("int f(int named, ...) { int ap; __builtin_va_end(ap); return 0; }", "va_list")]
    public void InvalidBuiltinOperandsAreDiagnosed(string source, string diagnostic)
    {
        CompileExpectingError(source, Enabled).AssertErrorContains(diagnostic);
    }

    [Fact]
    public void DisabledVaListAndUnevaluatedAddressesRemainUsable()
    {
        Compile("""
            int target(int, ...);
            __builtin_va_list unused;
            void *pointer = 0 ? (void *)target : 0;
            int main(void) {
                return sizeof(&target) == sizeof(void *) && !pointer
                    ? _Generic(0, int: 42, default: (long long)target) : 1;
            }
            """).Link().RunAndCheck(42);
    }

    [Fact]
    public void FeatureFlagUsesTheLastOption()
    {
        CompileExpectingError("int target(int, ...); int main(void) { return target(1); }",
            ["-femulated-varargs", "-fno-emulated-varargs"]).AssertErrorContains("variadic");
        Compile("""
            int target(int named, ...) { return named; }
            int main(void) { return target(42); }
            """, ["-fno-emulated-varargs", "-femulated-varargs"]).Link().RunAndCheck(42);
    }

    [Fact]
    public void NativeImportsCannotSatisfyTheCustomAbi()
    {
        var builder = Compile("""
            int target(int, ...);
            int main(void) { return target(42, 1); }
            """, Enabled)
            .NativeLibrary("int target(int value, ...) { return value; }", "target");
        Assert.Contains("unresolved managed global 'target'", Assert.Throws<InvalidOperationException>(
            () => builder.Link()).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlobalPointerObjectsAndStaticCallbacksKeepTheirIdentity(bool sections)
    {
        string[] options = sections ? [.. Enabled, "-ffunction-sections", "-fdata-sections"] : Enabled;
        Compile("""
            typedef int __clrcall Function(int, ...);
            static int __clrcall target(int named, ...) {
                __builtin_va_list ap; __builtin_va_start(ap, named);
                int result = named + __builtin_va_arg(ap, int);
                __builtin_va_end(ap); return result;
            }
            Function *pointer = target;
            Function *array[2] = { target, target };
            struct Holder { Function *callback; } holder = { target };
            int other(void);
            int main(void) {
                Function *local = target;
                return local(3, 4) + pointer(3, 4) + array[1](3, 4)
                    + holder.callback(3, 4) + other();
            }
            """, options)
            .Compile("""
                typedef int __clrcall Function(int, ...);
                extern Function *pointer;
                extern Function *array[2];
                struct Holder { Function *callback; };
                extern struct Holder holder;
                static int __clrcall target(int named, ...) { return named; }
                int other(void) {
                    if (pointer == target || array[0] != pointer ||
                        holder.callback != pointer) return 1;
                    return pointer(7, 7);
                }
                """, options)
            .Link(gcSections: true).RunAndCheck(42);
    }

    [Fact]
    public void OptionalFunctionPointersEnumsAndBooleanValues()
    {
        Compile("""
            enum Value { answer = 20 };
            typedef int (*Callback)(int);
            int increment(int value) { return value + 1; }
            int read(int named, ...) {
                __builtin_va_list ap;
                __builtin_va_start(ap, named);
                int value = __builtin_va_arg(ap, int);
                int flag = __builtin_va_arg(ap, int);
                Callback callback = __builtin_va_arg(ap, Callback);
                __builtin_va_end(ap);
                return callback(value + flag + named);
            }
            int main(void) { return read(20, answer, (_Bool)1, increment); }
            """, Enabled).LinkOnNetCoreOrMsvcLinkOnFramework().RunAndCheck(42);
    }

    [Fact]
    public void RecursionAndManagedAggregateReturns()
    {
        Compile("""
            struct Pair { int left; int right; };
            struct Pair __clrcall read(int count, ...) {
                __builtin_va_list ap;
                __builtin_va_start(ap, count);
                struct Pair result = { count, __builtin_va_arg(ap, int) };
                __builtin_va_end(ap);
                return result;
            }
            int recurse(int depth, ...) {
                __builtin_va_list ap; __builtin_va_start(ap, depth);
                int value = __builtin_va_arg(ap, int);
                int result = depth ? recurse(depth - 1, value + 1) : value;
                if (value + depth != 16) result = 1;
                __builtin_va_end(ap);
                return result;
            }
            int main(void) {
                struct Pair (__clrcall *pointer)(int, ...) = read;
                struct Pair result = pointer(20, 22);
                return recurse(3, 13) == 16 ? result.left + result.right : 1;
            }
            """, Enabled).Link().RunAndCheck(42);
    }

    [Fact]
    public void OverAlignedAreasUseAnAlignedInteriorPointer()
    {
        Compile("""
            struct __attribute__((aligned(32))) Value { long long value; };
            int read(int named, ...) {
                __builtin_va_list ap; __builtin_va_start(ap, named);
                if (__builtin_va_arg(ap, int) != 1) return 1;
                struct Value value = __builtin_va_arg(ap, struct Value);
                if ((unsigned long long)ap & 31) return 2;
                int result = __builtin_va_arg(ap, int);
                __builtin_va_end(ap);
                return value.value + result;
            }
            int main(void) {
                struct Value value = { 20 };
                return read(0, 1, value, 22);
            }
            """, [.. Enabled, "-fno-managed-aggregate-fields"]).Link().RunAndCheck(42);
    }

    [Theory]
    [InlineData("__builtin_va_end(ap)")]
    [InlineData("__builtin_va_copy(ap, ap)")]
    [InlineData("__builtin_va_arg(ap, int)")]
    public void AllVaOperationsRequireTheFlag(string expression)
    {
        CompileExpectingError($$"""
            int main(void) { __builtin_va_list ap; {{expression}}; return 0; }
            """).AssertErrorContains("variadic");
    }

    [Fact]
    public void ShortCircuitedStaticInitializersDoNotMaterializeAddresses()
    {
        Compile("""
            int target(int, ...);
            void *first = (void *)(0 && (long long)target);
            void *second = (void *)(1 || (long long)target);
            int main(void) { return !first && second == (void *)1 ? 42 : 1; }
            """).Link().RunAndCheck(42);
    }

    [Fact]
    public void OrdinaryCallbacksWithVariadicPointerParametersKeepTheirNativeEntry()
    {
        Compile("""
            typedef int __clrcall Variable(int, ...);
            int __clrcall sum(int named, ...) {
                __builtin_va_list ap; __builtin_va_start(ap, named);
                int result = named + __builtin_va_arg(ap, int);
                __builtin_va_end(ap); return result;
            }
            int invoke(Variable *function, int value) { return function(1, value); }
            int (*pointer)(Variable *, int) = invoke;
            int main(void) { return pointer(sum, 41); }
            """, Enabled).LinkOnNetCoreOrMsvcLinkOnFramework().RunAndCheck(42);
    }
}
