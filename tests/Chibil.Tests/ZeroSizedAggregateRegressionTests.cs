using Xunit;

namespace Chibil.Tests;

public class ZeroSizedAggregateRegressionTests : ChibiTestBase
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FlexibleUnionCloneAssignmentsPreserveValues(bool fieldBacked, bool assignToClone)
    {
        string assignment = assignToClone ? "filled = base" : "base = filled";
        Compile($$"""
            union Packet { int length; int values[]; };
            int main(void) {
                union Packet filled = { .values = { 42, 1, 2 } };
                union Packet base = { .length = 42 };
                if (({{assignment}}).length != 42) return 1;
                if (base.length != 42 || filled.length != 42) return 2;
                if (filled.values[1] != 1 || filled.values[2] != 2) return 3;
                return 42;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, "unsigned char")]
    [InlineData(true, "unsigned char")]
    [InlineData(false, "unsigned short")]
    [InlineData(true, "unsigned short")]
    public void SmallUnionCopiesPreserveValues(bool fieldBacked, string valueType)
    {
        Compile($$"""
            union Value { char empty[0]; {{valueType}} value; };
            int main(void) {
                union Value source = { 42 };
                union Value first = source;
                source.value = 17;
                union Value second = source;
                if (first.value != 42 || second.value != 17) return 1;
                first = source;
                if (first.value != 17) return 2;
                return 42;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, "unsigned char")]
    [InlineData(true, "unsigned char")]
    [InlineData(false, "unsigned short")]
    [InlineData(true, "unsigned short")]
    public void NestedSmallUnionAssignmentsPreservePointers(bool fieldBacked, string valueType)
    {
        Compile($$"""
            struct Outer {
                int before;
                union { char empty[0]; {{valueType}} value; } inner;
                int after;
            };
            int main(void) {
                struct Outer source = { 11, { 42 }, 23 };
                struct Outer destination = { 17, { 0 }, 19 };
                destination.inner = source.inner;
                if (destination.inner.value != 42) return 1;
                if (destination.before != 17 || destination.after != 19) return 2;
                return 42;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrameworkOffsetZeroArrayMemberAssignmentsPreserveSentinel(bool fieldBacked)
    {
        using var builder = new CompilationBuilder(netCore: false);
        builder.Compile("""
            struct Packet { int length; int values[]; };
            struct Holder { struct Packet packets[1]; int sentinel; int empty[0]; };
            int main(void) {
                struct Packet filled = { 42, { 1, 2, 3 } };
                struct Holder holder = { { { 0 } }, 17 };
                if ((*holder.packets = filled).length != 42) return 1;
                if (holder.sentinel != 17) return 2;
                return 42;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrameworkFirstArrayElementAssignmentsPreserveNextElement(bool fieldBacked)
    {
        using var builder = new CompilationBuilder(netCore: false);
        builder.Compile("""
            struct Packet { int length; int values[]; };
            int main(void) {
                struct Packet filled = { 42, { 1, 2, 3 } };
                struct Packet packets[2] = { { 0 }, { 19 } };
                if ((*packets = filled).length != 42) return 1;
                if (packets[1].length != 19) return 2;
                return 42;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrameworkPointerCastAssignmentsPreserveSentinel(bool fieldBacked)
    {
        using var builder = new CompilationBuilder(netCore: false);
        builder.Compile("""
            struct Packet { int length; int values[]; };
            struct Holder { struct Packet packet; int sentinel; };
            int main(void) {
                struct Packet filled = { 42, { 1, 2, 3 } };
                struct Holder holder = { { 0 }, 17 };
                if ((*(struct Packet *)&holder = filled).length != 42) return 1;
                if (holder.packet.length != 42 || holder.sentinel != 17) return 2;
                return 42;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcreteFlexibleCloneMembersPreserveLayout(bool fieldBacked)
    {
        Compile("""
            struct Packet { int length; int values[]; };
            int main(void) {
                struct Packet filled = { 10, { 20, 30 } };
                struct Wrapper { typeof(filled) packet; int after; } value = { .after = 42 };
                if (sizeof(value) != sizeof(filled) + sizeof(int)) return 1;
                if ((char *)&value.after - (char *)&value != sizeof(filled)) return 2;
                if (*(int *)((char *)&value + sizeof(filled)) != 42) return 3;
                return value.after;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcreteFlexibleCloneMemberAssignmentsCopyTrailingStorage(bool fieldBacked)
    {
        Compile("""
            struct Packet { int length; int values[]; };
            int main(void) {
                struct Packet filled = { 10, { 20, 30 } };
                struct Wrapper { typeof(filled) packet; int after; } value;
                value.after = 17;
                value.packet = filled;
                if (value.packet.length != 10 || value.packet.values[0] != 20 ||
                    value.packet.values[1] != 30) return 1;
                if (value.after != 17) return 2;
                return value.packet.length + value.packet.values[0] + value.after - 5;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcreteFlexibleCloneParametersUseValueAbi(bool fieldBacked)
    {
        Compile("""
            struct Packet { int length; int values[]; };
            struct Packet global = { 42, { 1, 2, 3 } };
            int read_packet(typeof(global) value) { return value.length; }
            int main(void) { return read_packet(global); }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcreteFlexibleCloneReturnsUseValueAbi(bool fieldBacked)
    {
        Compile("""
            struct Packet { int length; int values[]; };
            struct Packet global = { 42, { 1, 2, 3 } };
            typeof(global) make_packet(void) { return global; }
            int main(void) {
                typeof(global) value = make_packet();
                return value.length;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ConditionalExpressionsNormalizeFlexibleCloneValues(bool fieldBacked, bool chooseClone)
    {
        Compile($$"""
            struct Packet { int length; int values[]; };
            int choose(void) { return {{(chooseClone ? 1 : 0)}}; }
            int main(void) {
                struct Packet filled = { 42, { 1, 2, 3 } };
                struct Packet base = { 10 };
                struct Packet result = choose() ? filled : base;
                return result.length == {{(chooseClone ? 42 : 10)}} ? 42 : 1;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ConcreteFlexibleCloneInitializersZeroOmittedTails(bool fieldBacked, bool runtime)
    {
        Compile($$"""
            struct Packet { int length; int values[]; };
            int answer(void) { return 42; }
            int main(void) {
                struct Packet filled = { 1, { 2 } };
                typeof(filled) value = { {{(runtime ? "answer()" : "42")}} };
                if (sizeof(value) != sizeof(filled)) return 1;
                if (value.values[0] != 0) return 2;
                return value.length;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BraceElidedFlexibleTailInitializersRetainValues(bool fieldBacked)
    {
        Compile("""
            struct Packet { int length; int values[]; };
            struct Packet global = { 3, 10, 20, 9 };
            int main(void) {
                struct Packet local = { 3, 10, 20, 9 };
                if (sizeof(global) != 16 || sizeof(local) != 16) return 1;
                if (global.length + global.values[0] + global.values[1] +
                    global.values[2] != 42) return 2;
                return local.length + local.values[0] + local.values[1] + local.values[2];
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultidimensionalZeroSizedMembersSupportImplicitInitialization(bool fieldBacked)
    {
        Compile("""
            struct Value { int empty[2][0]; int value; };
            int main(void) {
                struct Value value = { 42 };
                if (sizeof(value.empty) != 0) return 1;
                return value.value;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, "int main(void) { static int value[][0]; return 0; }")]
    [InlineData(true, "int main(void) { static int value[][0]; return 0; }")]
    [InlineData(false, "static int value[][0]; int main(void) { return 0; }")]
    [InlineData(true, "static int value[][0]; int main(void) { return 0; }")]
    [InlineData(false, "int main(void) { int value[0][]; return 0; }")]
    [InlineData(true, "int main(void) { int value[0][]; return 0; }")]
    [InlineData(false, "struct Incomplete; int main(void) { struct Incomplete value[0]; return 0; }")]
    [InlineData(true, "struct Incomplete; int main(void) { struct Incomplete value[0]; return 0; }")]
    public void IncompleteZeroSizedArrayStorageIsRejected(bool fieldBacked, string source)
    {
        CompileExpectingError(source,
            [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .AssertErrorContains("incomplete");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TentativeArraysOfZeroSizedElementsAreCompleted(bool fieldBacked)
    {
        Compile("""
            int values[][0];
            int main(void) {
                if (!values) return 1;
                if ((unsigned long long)values % _Alignof(int)) return 2;
                return 42;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyUnionLocalInitializersSkipZeroLengthMembers(bool fieldBacked)
    {
        Compile("""
            union Number { int empty[0]; int value; };
            int main(void) {
                union Number value = {};
                return value.value == 0 ? 42 : 1;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyUnionGlobalInitializersSkipZeroLengthMembers(bool fieldBacked)
    {
        Compile("""
            union Number { int empty[0]; int value; };
            union Number value = {};
            int main(void) { return value.value == 0 ? 42 : 1; }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnnamedBitfieldsWithoutZeroWidthMembersPreserveLayout(bool fieldBacked)
    {
        Compile("""
            struct Value { unsigned char before; unsigned : 3; unsigned char after; };
            struct Value global = { 10, 32 };
            int answer(void) { return 32; }
            int main(void) {
                struct Value local = { 10, 32 };
                struct Value dynamic = { 10, answer() };
                if (sizeof(global) != 4) return 1;
                if ((char *)&global.after - (char *)&global != 2) return 2;
                if (global.before + global.after != 42) return 3;
                if (local.before + local.after != 42) return 4;
                if (dynamic.before + dynamic.after != 42) return 5;
                return 42;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }
}
