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

        // Allocate one extra slot for the trailing NULL pointer that
        // C's argv contract requires (argv[argc] == NULL).
        IntPtr[] argv = new IntPtr[args.Length + 1];

        try
        {
            for (int i = 0; i < args.Length; i++)
                argv[i] = Marshal.StringToHGlobalAnsi(args[i]);
            argv[args.Length] = IntPtr.Zero;

            fixed (IntPtr* pArgv = argv)
                return __CxxPureMSILEntry(args.Length, (sbyte**)pArgv, null);
        }
        finally
        {
            foreach (var a in argv)
                if (a != IntPtr.Zero)
                    Marshal.FreeHGlobal(a);
        }
    }
}
