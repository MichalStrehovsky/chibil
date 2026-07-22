using System.Reflection.PortableExecutable;

namespace Chilink;

public sealed class Driver
{
    public void Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Linker.Link(ParseArguments(args));
    }

    internal static LinkOptions ParseArguments(string[] args)
    {
        string output = null;
        string entryPoint = null;
        Machine machine = Machine.Amd64;
        Subsystem subsystem = Subsystem.WindowsCui;
        bool optimizeReferences = false;
        var inputs = new List<string>();

        foreach (string argument in args)
        {
            if (string.IsNullOrWhiteSpace(argument))
            {
                throw new ChilinkException("empty command-line argument");
            }

            if (!argument.StartsWith('/'))
            {
                inputs.Add(argument);
                continue;
            }

            int colon = argument.IndexOf(':');
            string name = colon < 0 ? argument[1..] : argument[1..colon];
            string value = colon < 0 ? null : argument[(colon + 1)..];

            switch (name.ToUpperInvariant())
            {
                case "OUT":
                    output = RequireValue(argument, value);
                    break;

                case "ENTRY":
                    entryPoint = RequireValue(argument, value);
                    break;

                case "MACHINE":
                    value = RequireValue(argument, value);
                    if (!value.Equals("X64", StringComparison.OrdinalIgnoreCase) &&
                        !value.Equals("AMD64", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ChilinkException($"unsupported machine '{value}'; the initial chilink implementation supports x64 only");
                    }
                    machine = Machine.Amd64;
                    break;

                case "SUBSYSTEM":
                    value = RequireValue(argument, value);
                    subsystem = value.ToUpperInvariant() switch
                    {
                        "CONSOLE" => Subsystem.WindowsCui,
                        "WINDOWS" => Subsystem.WindowsGui,
                        _ => throw new ChilinkException($"unsupported subsystem '{value}'"),
                    };
                    break;

                case "OPT":
                    value = RequireValue(argument, value);
                    optimizeReferences = value.ToUpperInvariant() switch
                    {
                        "REF" => true,
                        "NOREF" => false,
                        _ => throw new ChilinkException($"unsupported /OPT value '{value}'"),
                    };
                    break;

                case "NOLOGO":
                    if (value != null)
                    {
                        throw new ChilinkException($"/NOLOGO does not accept a value");
                    }
                    break;

                default:
                    throw new ChilinkException($"unsupported option '{argument}'");
            }
        }

        if (inputs.Count == 0)
        {
            throw new ChilinkException("no input object files");
        }
        if (output == null)
        {
            throw new ChilinkException("missing /OUT:<file>");
        }
        if (entryPoint == null)
        {
            throw new ChilinkException("missing /ENTRY:<symbol>");
        }

        foreach (string input in inputs)
        {
            if (!Path.GetExtension(input).Equals(".obj", StringComparison.OrdinalIgnoreCase))
            {
                throw new ChilinkException($"unsupported input '{input}'; only COFF .obj files are supported");
            }
            if (!File.Exists(input))
            {
                throw new ChilinkException($"input file '{input}' does not exist");
            }
        }

        return new LinkOptions
        {
            InputFiles = inputs,
            OutputFile = output,
            EntryPoint = entryPoint,
            Machine = machine,
            Subsystem = subsystem,
            OptimizeReferences = optimizeReferences,
        };
    }

    private static string RequireValue(string argument, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ChilinkException($"option '{argument}' requires a value");
        }

        return value;
    }
}
