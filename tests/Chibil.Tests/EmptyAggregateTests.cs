using Xunit;

namespace Chibil.Tests;

public class EmptyAggregateTests : ChibiTestBase
{
    [Theory]
    [InlineData(false, "struct Empty {};")]
    [InlineData(true, "struct Empty {};")]
    [InlineData(false, "union Empty {};")]
    [InlineData(true, "union Empty {};")]
    [InlineData(false, "typedef struct {} Empty;")]
    [InlineData(true, "typedef struct {} Empty;")]
    [InlineData(false, "struct Empty; struct Empty {};")]
    [InlineData(true, "struct Empty; struct Empty {};")]
    [InlineData(false, "struct Empty { int : 0; };")]
    [InlineData(true, "struct Empty { int : 0; };")]
    [InlineData(false, "struct Empty { char bytes[]; };")]
    [InlineData(true, "struct Empty { char bytes[]; };")]
    [InlineData(false, "union Empty { char bytes[]; };")]
    [InlineData(true, "union Empty { char bytes[]; };")]
    [InlineData(false, "struct Empty { char bytes[0]; };")]
    [InlineData(true, "struct Empty { char bytes[0]; };")]
    [InlineData(false, "union Empty { char bytes[0]; };")]
    [InlineData(true, "union Empty { char bytes[0]; };")]
    public void ZeroSizedStructsAndUnionsAreRejected(bool fieldBacked, string source)
    {
        CompileExpectingError(source,
            [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .AssertErrorContains("zero-sized aggregates are not supported");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroLengthArrayLocalsPreserveCSize(bool fieldBacked)
    {
        Compile("""
            int main(void) {
                char value[0] = {};
                return sizeof(value) == 0 ? 42 : 1;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroLengthArrayMembersPreserveLayoutAndCopies(bool fieldBacked)
    {
        Compile("""
            struct Value;
            struct Value { int empty[0]; int value; unsigned char sentinel; };
            union Number { int empty[0]; int value; unsigned char bytes[4]; };
            struct Tail { unsigned char prefix; int bytes[0]; };
            struct Outer { unsigned char prefix; struct Tail tail; unsigned char suffix; };
            struct Outer global = { 11, { 42 }, 23 };
            int answer(void) { return 42; }
            int main(void) {
                struct Value a = { {}, 42, 17 };
                struct Value b = { {}, answer(), 23 };
                union Number number = { .value = 42 };
                if (sizeof(a) != 8 || sizeof(number) != 4) return 1;
                if ((char *)&a.sentinel - (char *)&a != 4) return 2;
                if (*(int *)&a != 42 || ((unsigned char *)&a)[4] != 17) return 3;
                if ((char *)&a.value - (char *)&a != 0 || sizeof(a.empty) != 0)
                    return 4;
                a = b;
                if (a.value != 42 || a.sentinel != 23) return 5;
                if (sizeof(struct Tail) != 4 || sizeof(global) != 12) return 6;
                if ((char *)&global.tail - (char *)&global != 4) return 7;
                if ((char *)global.tail.bytes - (char *)&global.tail != 4) return 8;
                if ((char *)&global.suffix - (char *)&global != 8) return 9;
                if (((unsigned char *)&global)[4] != 42 || global.suffix != 23)
                    return 10;
                return a.value + number.bytes[0] - 42;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlexibleArrayMembersRemainSupported(bool fieldBacked)
    {
        Compile("""
            struct Packet { int value; unsigned char bytes[]; };
            struct Packet global = { 42 };
            int main(void) {
                struct Packet omitted = { 42 };
                struct Packet empty = { 42, {} };
                struct Packet filled = { 40, { 2 } };
                if (sizeof(global) != 4 || sizeof(omitted) != 4 || sizeof(empty) != 4)
                    return 1;
                if ((char *)omitted.bytes - (char *)&omitted != 4) return 2;
                if ((char *)empty.bytes - (char *)&empty != 4) return 3;
                if (global.value != 42 || omitted.value != 42 || empty.value != 42)
                    return 4;
                return filled.value + filled.bytes[0];
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ZeroLengthGlobalArraysHaveStorage(bool fieldBacked, bool dataSections)
    {
        Compile("""
            char first[0] = {};
            char second[] = {};
            int aligned[0] = {};
            char *pointer = first;
            int main(void) {
                if (sizeof(first) || sizeof(second) || sizeof(aligned)) return 1;
                if (!first || !second || !aligned || pointer != first) return 2;
                if (first == second) return 3;
                if ((unsigned long long)aligned % _Alignof(int)) return 4;
                return 42;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields",
                dataSections ? "-fdata-sections" : "-fno-data-sections"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ZeroLengthCommonArraysLinkAcrossTranslationUnits(bool fieldBacked, bool dataSections)
    {
        string[] options = [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields",
            dataSections ? "-fdata-sections" : "-fno-data-sections"];
        Compile("""
            char value[0];
            int aligned[0];
            char *address(void) { return value; }
            """, options)
            .Compile("""
                char value[0];
                int aligned[0];
                char *address(void);
                int main(void) {
                    if (sizeof(value) || sizeof(aligned)) return 1;
                    if (!value || value != address()) return 2;
                    if ((unsigned long long)aligned % _Alignof(int)) return 3;
                    return 42;
                }
                """, options)
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ZeroLengthStaticArraysAndCompoundLiteralsHaveStorage(bool fieldBacked, bool dataSections)
    {
        Compile("""
            static char global[0];
            char *literal = (char[0]){};
            int main(void) {
                static int local[0] = {};
                if (sizeof(global) || sizeof(local)) return 1;
                if (!global || !local || !literal) return 2;
                if (global == literal) return 3;
                if ((unsigned long long)local % _Alignof(int)) return 4;
                return 42;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields",
                dataSections ? "-fdata-sections" : "-fno-data-sections"])
            .LinkOnNetCoreOrMsvcLinkOnFramework(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InferredArraysOfZeroSizedElementsAreCompleted(bool fieldBacked)
    {
        Compile("""
            int global[][0] = { {}, {} };
            int main(void) {
                int local[][0] = { {}, {} };
                int empty[][0] = {};
                return sizeof(global) == 0 && sizeof(local) == 0 && sizeof(empty) == 0
                    ? 42 : 1;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompleteArraysOfZeroSizedElementsAreRejected(bool fieldBacked)
    {
        CompileExpectingError("int main(void) { int value[][0]; return 0; }",
            [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .AssertErrorContains("variable has incomplete type");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlexibleArrayMembersPreserveNestedAlignment(bool fieldBacked)
    {
        Compile("""
            struct Tail { unsigned char prefix; int bytes[]; };
            struct Outer { unsigned char prefix; struct Tail tail; unsigned char suffix; };
            struct Outer global = { 11, { 42 }, 23 };
            int main(void) {
                if (sizeof(struct Tail) != 4 || sizeof(global) != 12) return 1;
                if ((char *)&global.tail - (char *)&global != 4) return 2;
                if ((char *)global.tail.bytes - (char *)&global.tail != 4) return 3;
                if ((char *)&global.suffix - (char *)&global != 8) return 4;
                if (global.tail.prefix != 42 || global.suffix != 23) return 5;
                if (((unsigned char *)&global)[4] != 42) return 6;
                return 42;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroWidthBitfieldsPreserveLayout(bool fieldBacked)
    {
        Compile("""
            struct Value { unsigned char before; int : 0; unsigned char after; };
            struct Outer { unsigned char prefix; struct Value inner; unsigned char suffix; };
            int main(void) {
                struct Outer value;
                value.prefix = 11;
                value.inner.before = 17;
                value.inner.after = 42;
                value.suffix = 23;
                if (sizeof(struct Value) != 8 || sizeof(value) != 16) return 1;
                if ((char *)&value.inner - (char *)&value != 4) return 2;
                if ((char *)&value.inner.after - (char *)&value.inner != 4) return 3;
                if ((char *)&value.suffix - (char *)&value != 12) return 4;
                if (((unsigned char *)&value.inner)[4] != 42) return 5;
                return value.inner.after;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UnnamedBitfieldsDoNotConsumeOrStopInitializers(bool fieldBacked, bool designated)
    {
        string constant = designated ? ".before = 10, .after = 32" : "10, 32";
        string runtime = designated ? ".before = 10, .after = answer()" : "10, answer()";
        Compile($$"""
            struct Value { unsigned char before; int : 0; unsigned : 3; unsigned char after; };
            struct Value global = { {{constant}} };
            int answer(void) { return 32; }
            int main(void) {
                struct Value local = { {{constant}} };
                struct Value dynamic = { {{runtime}} };
                if (global.before + global.after != 42) return 1;
                if (local.before + local.after != 42) return 2;
                if (dynamic.before + dynamic.after != 42) return 3;
                return 42;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroLengthArrayMembersSupportBraceElision(bool fieldBacked)
    {
        Compile("""
            struct Value { int empty[0]; int value; };
            struct Tail { unsigned char prefix; int bytes[0]; };
            struct Outer { unsigned char prefix; struct Tail tail; unsigned char suffix; };
            struct Outer global = { 11, 42, 23 };
            int main(void) {
                struct Value first = { 42 };
                struct Value explicit = { {}, 42 };
                struct Outer local = { 11, 42, 23 };
                struct Outer values[2] = { { 11, 42, 23 }, { 12, 43, 24 } };
                if (first.value != 42 || explicit.value != 42) return 1;
                if (global.tail.prefix != 42 || global.suffix != 23) return 2;
                if (local.tail.prefix != 42 || local.suffix != 23) return 3;
                if (values[0].suffix != 23 || values[1].tail.prefix != 43) return 4;
                return local.tail.prefix;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnionsWithZeroLengthMembersSupportInitializationAndCopies(bool fieldBacked)
    {
        Compile("""
            union Number { int empty[0]; int value; };
            struct Box { int before; union Number number; int after; };
            union Number global = { 42 };
            int main(void) {
                union Number value = { 42 };
                union Number copy = value;
                union Number assigned;
                struct Box box = { 11, copy, 23 };
                assigned = copy;
                if (global.value != 42 || copy.value != 42) return 1;
                if (box.before != 11 || box.number.value != 42 || box.after != 23)
                    return 2;
                return assigned.value;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonconstantUnionGlobalInitializersAreRejected(bool fieldBacked)
    {
        CompileExpectingError("""
            union Number { int empty[0]; int value; };
            union Number first = { .value = 42 };
            union Number second = first;
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .AssertErrorContains("not a compile-time constant");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlexibleArrayClonesAssignToBaseValues(bool fieldBacked)
    {
        Compile("""
            struct Packet { int length; int values[]; };
            struct Holder { struct Packet packet; int sentinel; };
            int calls;
            struct Packet *target(struct Packet *value) { calls++; return value; }
            int main(void) {
                struct Packet filled = { 42, { 1, 2, 3 } };
                struct Packet base = { 0 };
                struct Holder holder = { { 0 }, 17 };
                if (holder.sentinel != 17) return 6;
                if ((*target(&base) = (calls++, filled)).length != 42) return 1;
                if (base.length != 42 || calls != 2) return 2;
                if ((holder.packet = filled).length != 42) return 3;
                if (holder.packet.length != 42) return 4;
                if (holder.sentinel != 17) return 5;
                holder.packet.length = 0;
                if ((*(&holder.packet) = filled).length != 42) return 7;
                if (holder.packet.length != 42 || holder.sentinel != 17) return 8;
                return base.length;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BaseValuesAssignToFlexibleArrayClonesWithoutOverreading(bool fieldBacked)
    {
        Compile("""
            struct Packet { int length; int values[]; };
            struct Holder { struct Packet packet; int sentinel; };
            int main(void) {
                struct Packet filled = { 0, { 11, 22, 33 } };
                struct Holder holder = { { 42 }, 17 };
                if ((filled = holder.packet).length != 42) return 1;
                if (filled.values[0] != 11 || filled.values[1] != 22 ||
                    filled.values[2] != 33 || holder.sentinel != 17) return 2;
                return filled.length;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlexibleArrayCloneCopiesRespectBothStorageSizes(bool fieldBacked)
    {
        Compile("""
            struct Packet { int length; int values[]; };
            int main(void) {
                struct Packet small = { 42, { 7 } };
                struct Packet large = { 0, { 11, 22, 33 } };
                typeof(small) copy = small;
                if (sizeof(copy) != sizeof(small) || copy.values[0] != 7) return 3;
                if ((large = small).length != 42) return 1;
                if (large.values[0] != 7 || large.values[1] != 22 ||
                    large.values[2] != 33) return 2;
                small = large;
                return small.length;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }
}
