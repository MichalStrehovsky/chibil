using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[CompilerGlobalScope]
unsafe static class Entry
{
    [MethodImpl(MethodImplOptions.ForwardRef)]
    extern static int __CxxPureMSILEntry(int argc, sbyte** argv, sbyte** envp);

    static int mainCRTStartup()
    {
        // C# doesn't include the program name as args[0]. C does.
        // So use Environment.GetCommandLineArgs().
        string[] args = Environment.GetCommandLineArgs();

        IntPtr[] argv = new IntPtr[args.Length];

        for (int i = 0; i < args.Length; i++)
            argv[i] = Marshal.StringToHGlobalAnsi(args[i]);

        int exit;
        fixed (IntPtr* pArgv = argv)
            exit = __CxxPureMSILEntry(argv.Length, (sbyte**)pArgv, null);

        foreach (var a in argv)
            Marshal.FreeHGlobal(a);

        return exit;
    }
}
