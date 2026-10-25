#if NET10_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Asm2Obj;

[CompilerGlobalScope]
internal static class NativeImports
{
    [DecoratedName("__chilink_get_native_export")]
    static IntPtr GetNativeExport(string library, string entryPoint)
        => NativeImportCache.GetExport(library, entryPoint);

    [DecoratedName("__chilink_get_native_data_export")]
    static IntPtr GetNativeDataExport(string library, string entryPoint)
        => NativeImportCache.GetDataExport(library, entryPoint);
}

internal static class NativeImportCache
{
    private static readonly Dictionary<string, IntPtr> Libraries = new(StringComparer.Ordinal);

    internal static IntPtr GetExport(string library, string entryPoint)
    {
        lock (Libraries)
        {
            return NativeLibrary.GetExport(LoadLibrary(library), entryPoint);
        }
    }

    internal static IntPtr GetDataExport(string library, string entryPoint)
    {
        lock (Libraries)
        {
            IntPtr handle = LoadLibrary(library);
            if (NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), entryPoint, out IntPtr address))
                return address;
            return NativeLibrary.GetExport(handle, entryPoint);
        }
    }

    private static IntPtr LoadLibrary(string library)
    {
        if (!Libraries.TryGetValue(library, out IntPtr handle))
        {
            handle = NativeLibrary.Load(library, typeof(NativeImportCache).Assembly, null);
            Libraries.Add(library, handle);
        }
        return handle;
    }
}
#endif
