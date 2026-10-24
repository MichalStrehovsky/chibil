namespace Chibil;

public enum TargetCpu { X86_64 }
public enum TargetRuntime { DotNet, NetFramework }
public enum TargetOS { Windows, Linux }
public enum TargetAbi { Msvc, Chibil }

public sealed record TargetProfile(TargetCpu Cpu, TargetRuntime Runtime, TargetOS OS, TargetAbi Abi)
{
    public const string SupportedTargets =
        "x86_64-netfx-windows[-msvc], x86_64-dotnet-windows[-chibil], " +
        "x86_64-dotnet-windows-msvc, x86_64-netfx-windows-chibil, x86_64-dotnet-linux[-chibil]";

    public static readonly TargetProfile Default =
        new(TargetCpu.X86_64, TargetRuntime.NetFramework, TargetOS.Windows, TargetAbi.Msvc);

    public string DefaultLinker => Abi == TargetAbi.Chibil ? "chilink" : "link";

    public static TargetProfile Parse(string value)
    {
        if (string.IsNullOrEmpty(value))
            Util.Error("missing target for --target; expected cpu-runtime-os[-abi]");

        return value switch
        {
            "x86_64-netfx-windows" or "x86_64-netfx-windows-msvc" => Default,
            "x86_64-dotnet-windows" or "x86_64-dotnet-windows-chibil" =>
                new(TargetCpu.X86_64, TargetRuntime.DotNet, TargetOS.Windows, TargetAbi.Chibil),
            "x86_64-dotnet-windows-msvc" =>
                new(TargetCpu.X86_64, TargetRuntime.DotNet, TargetOS.Windows, TargetAbi.Msvc),
            "x86_64-netfx-windows-chibil" =>
                new(TargetCpu.X86_64, TargetRuntime.NetFramework, TargetOS.Windows, TargetAbi.Chibil),
            "x86_64-dotnet-linux" or "x86_64-dotnet-linux-chibil" =>
                new(TargetCpu.X86_64, TargetRuntime.DotNet, TargetOS.Linux, TargetAbi.Chibil),
            _ => throw new ChibiException($"unsupported target '{value}'; supported targets: {SupportedTargets}"),
        };
    }

    public void ApplyDefaults(CompilerOptions options)
    {
        bool chibilAbi = Abi == TargetAbi.Chibil;
        options.Target = this;
        options.DataModel = OS == TargetOS.Linux ? DataModel.LP64 : DataModel.LLP64;
        options.TargetNetCore = Runtime == TargetRuntime.DotNet;
        options.OptFixedAddressStatics = chibilAbi;
        options.UseFieldBackedManagedAggregates = chibilAbi;
        options.UcoEntryPoints = chibilAbi && options.TargetNetCore;
        options.EmulatedVarargs = chibilAbi && options.TargetNetCore;
    }
}
