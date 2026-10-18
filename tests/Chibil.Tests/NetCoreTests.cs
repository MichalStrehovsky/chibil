using Xunit;

namespace Chibil.Tests;

public class NetCoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TargetOptionsProduceRunnableProgram(bool netCore)
    {
        using var builder = new CompilationBuilder(netCore);
        builder.Compile("""
            struct Pair { char c; long n; };
            struct Pair value = { 2, 40 };
            char identity_char(char c) { return c; }
            long identity_long(long n) { return n; }
            int __stdcall read(const volatile int *p) { return *p; }
            int main(void) {
                int n = 0;
                return identity_char(value.c) + identity_long(value.n) + read(&n);
            }
            """, netCore ? ["-fnetframework", "-fnetcore"] : ["-fnetcore", "-fnetframework"])
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Int32InterlockedCalls(bool netCore)
    {
        using var builder = new CompilationBuilder(netCore);
        builder.Compile("""
            int main(void) {
                int value = 1;
                int old = 1;
                if (!__builtin_compare_and_swap(&value, &old, 2)) return 10;
                if (value != 2) return 11;
                old = 3;
                if (__builtin_compare_and_swap(&value, &old, 4)) return 12;
                if (__builtin_atomic_exchange(&value, 42) != 2) return 13;
                return value;
            }
            """).Link(["/entry:main"]).RunAndCheck(42);
    }
}
