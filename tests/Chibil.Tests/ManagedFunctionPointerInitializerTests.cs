using Xunit;

namespace Chibil.Tests;

public sealed class ManagedFunctionPointerInitializerTests : ChibiTestBase
{
    [Theory]
    [InlineData(null)]
    [InlineData("-fdata-sections")]
    public void GlobalInitializerMatchesManagedFunctionAddress(string option)
    {
        Compile("""
            void __clrcall myfunc(void) { }
            void *f = myfunc;
            int main(void) { return f == myfunc ? 100 : 0; }
            """, option == null ? null : [option])
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(100);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-fdata-sections")]
    public void AggregateInitializersMatchManagedFunctionAddresses(string option)
    {
        Compile("""
            void __clrcall first(void) { }
            void __clrcall second(void) { }
            struct Callbacks {
                int before;
                void *first;
                void *second;
                int after;
            };
            struct Callbacks callbacks = { 11, first, second, 22 };
            int padding = 99;
            int main(void) {
                if (padding != 99 || callbacks.before != 11 || callbacks.after != 22)
                    return 1;
                return callbacks.first == first && callbacks.second == second
                    ? 100 : 0;
            }
            """, option == null ? null : [option])
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(100);
    }
}
