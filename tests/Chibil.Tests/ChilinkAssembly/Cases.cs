using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Asm2Obj;

[CompilerGlobalScope]
internal static class Cases
{
    [return: CallConvCdecl]
    internal static int load_global_dll()
    {
        var assembly = Assembly.LoadFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "globals.dll"));
        var method = assembly.ManifestModule.GetMethod("read",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
            CallingConventions.Standard, Type.EmptyTypes, null);
        int first = (int)method.Invoke(null, null);
        int second = (int)method.Invoke(null, null);
        return first == 41 && second == 42 ? 42 : 1;
    }

    [return: CallConvCdecl]
    internal static int load_global_dll_value()
    {
        var assembly = Assembly.LoadFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "globals.dll"));
        var field = assembly.ManifestModule.GetField("value",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        return (int)field.GetValue(null);
    }

    [return: CallConvCdecl]
    internal static void collect_globals()
    {
        for (int i = 0; i < 10; i++)
        {
            for (int j = 0; j < 100; j++)
                GC.KeepAlive(new byte[1024]);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    [return: CallConvCdecl]
    internal static int managed_string()
    {
        string text = "chilink user string";
        return text.Length + text[0];
    }

    [return: CallConvCdecl]
    internal static int managed_static()
    {
        return ManagedState.Next() + ManagedState.Next();
    }

    [return: CallConvCdecl]
    internal static int managed_generic()
    {
        var box = new GenericBox<int>(GenericOperations.Identity(42));
        return box.Read();
    }

    [return: CallConvCdecl]
    internal static int managed_eh(int value)
    {
        int result = 0;
        try
        {
            if (value < 0)
                throw new ArgumentException("negative");
            result = 30;
        }
        catch (ArgumentException)
        {
            result = 40;
        }
        finally
        {
            result += 2;
        }
        return result;
    }

    [return: CallConvCdecl]
    internal static unsafe int managed_dll_helper([IsSignUnspecifiedByte] sbyte* path)
    {
        Module module = Assembly.LoadFrom(new string(path)).ManifestModule;
        int result = 0;
        foreach (MethodInfo method in module.GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.Name == "unused")
                return 1;
            if (method.Name == "helper")
                result = (int)method.Invoke(null, null);
        }
        return result;
    }

    [return: CallConvCdecl]
    internal static unsafe int managed_dll_public_root([IsSignUnspecifiedByte] sbyte* path)
    {
        foreach (Type type in Assembly.LoadFrom(new string(path)).GetExportedTypes())
            if (type.Name == "PublicRoot")
                return (int)type.GetMethod("Uncalled").Invoke(null, null);
        throw new TypeLoadException("PublicRoot");
    }
}

internal static class ManagedState
{
    private static int value = 19;

    internal static int Next()
    {
        return ++value;
    }
}

internal static class GenericOperations
{
    internal static T Identity<T>(T value)
    {
        return value;
    }
}

internal sealed class GenericBox<T>
{
    private readonly T value;

    internal GenericBox(T value)
    {
        this.value = value;
    }

    internal T Read()
    {
        return value;
    }
}

public static class PublicRoot
{
    public static int Uncalled()
    {
        return 42;
    }
}
