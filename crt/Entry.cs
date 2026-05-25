using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[CompilerGlobalScope]
unsafe static class Entry
{
    [DecoratedName("?__CxxPureMSILEntry@@$$J0YMHHPEAPEAD0@Z")]
    [MethodImpl(MethodImplOptions.ForwardRef)]
    extern static int __CxxPureMSILEntry(int argc, sbyte** argv, sbyte** envp);

    [DecoratedName("mainCRTStartup")]
    static int mainCRTStartup(string[] args)
    {
        string arg0 = Assembly.GetEntryAssembly().Location;

        IntPtr[] argv = new IntPtr[1 + args.Length];

        argv[0] = Marshal.StringToHGlobalAnsi(arg0);
        for (int i = 0; i < args.Length; i++)
            argv[i + 1] = Marshal.StringToHGlobalAnsi(args[i]);

        int exit;
        fixed (IntPtr* pArgv = argv)
            exit = __CxxPureMSILEntry(argv.Length, (sbyte**)pArgv, null);

        foreach (var a in argv)
            Marshal.FreeHGlobal(a);

        return exit;
    }
}

namespace System.Runtime.CompilerServices
{
    class DecoratedNameAttribute : Attribute
    {
        public DecoratedNameAttribute(string name) { }
    }
}