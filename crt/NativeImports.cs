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

    [DecoratedName("__chilink_get_vararg_stub")]
    static IntPtr GetVarargStub(IntPtr target)
        => VarargStubCache.Get(target);
}

/// <summary>
/// Adapts exact-signature unmanaged calls to variadic native callees. On System V x64,
/// variadic callees expect AL to bound the number of vector registers used.
/// </summary>
internal static unsafe class VarargStubCache
{
    private static readonly Dictionary<IntPtr, IntPtr> Stubs = new();

    internal static IntPtr Get(IntPtr target)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 && !OperatingSystem.IsMacOS())
            return target;
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Variadic native calls are not supported on this platform.");

        lock (Stubs)
        {
            if (Stubs.TryGetValue(target, out IntPtr stub))
                return stub;

            IntPtr libc = NativeLibrary.GetMainProgramHandle();
            var mmap = (delegate* unmanaged<IntPtr, nuint, int, int, int, nint, IntPtr>)NativeLibrary.GetExport(libc, "mmap");
            var mprotect = (delegate* unmanaged<IntPtr, nuint, int, int>)NativeLibrary.GetExport(libc, "mprotect");

            // Field constants would not survive asm2obj, so keep them local.
            const int PROT_READ = 1, PROT_WRITE = 2, PROT_EXEC = 4, MAP_PRIVATE = 2, MAP_ANONYMOUS = 0x20;
            const int size = 4096;
            stub = mmap(IntPtr.Zero, size, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
            if (stub == new IntPtr(-1))
                throw new OutOfMemoryException("Unable to allocate a variadic call stub.");

            byte* code = (byte*)stub;
            // mov eax, 8
            code[0] = 0xB8; *(int*)(code + 1) = 8;
            // movabs r11, target
            code[5] = 0x49; code[6] = 0xBB; *(long*)(code + 7) = (long)target;
            // jmp r11
            code[15] = 0x41; code[16] = 0xFF; code[17] = 0xE3;

            if (mprotect(stub, size, PROT_READ | PROT_EXEC) != 0)
                throw new InvalidOperationException("Unable to make a variadic call stub executable.");
            Stubs.Add(target, stub);
            return stub;
        }
    }
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
