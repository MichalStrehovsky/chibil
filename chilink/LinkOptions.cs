using System.Reflection.PortableExecutable;

namespace Chilink;

public sealed class LinkOptions
{
    public required IReadOnlyList<string> InputFiles { get; init; }

    public required string OutputFile { get; init; }

    public required string EntryPoint { get; init; }

    public Machine Machine { get; init; } = Machine.Amd64;

    public Subsystem Subsystem { get; init; } = Subsystem.WindowsCui;

    public bool OptimizeReferences { get; init; }
}
