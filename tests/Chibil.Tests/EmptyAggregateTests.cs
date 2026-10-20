using Xunit;

namespace Chibil.Tests;

public class EmptyAggregateTests : ChibiTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyStructLocalsPreserveCSize(bool fieldBacked)
    {
        Compile("""
            struct Empty {};
            int main(void) {
                struct Empty value = {};
                struct Empty copy = value;
                return sizeof(value) == 0 && sizeof(copy) == 0 ? 42 : 1;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OmittedEmptyUnionMembersAreInitialized(bool fieldBacked)
    {
        Compile("""
            union Empty {};
            struct Value { union Empty empty; int value; };
            int answer(void) { return 42; }
            int main(void) {
                struct Value value = { .value = answer() };
                return value.value + sizeof(value.empty);
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .Link(["/entry:main"]).RunAndCheck(42);
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
}
