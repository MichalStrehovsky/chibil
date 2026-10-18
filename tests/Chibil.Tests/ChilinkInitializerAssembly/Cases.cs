using System.Runtime.CompilerServices;
using Asm2Obj;

[CompilerGlobalScope]
internal static class InitializerCases
{
    private static int initializer_state;

    static InitializerCases()
    {
        initializer_state = 42;
    }

    [return: CallConvCdecl]
    internal static int initialized_module()
    {
        return initializer_state;
    }
}
