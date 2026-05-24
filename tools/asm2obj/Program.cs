using System;
using System.IO;
using System.Reflection.PortableExecutable;

namespace Asm2Obj;

/// <summary>
/// CLI driver for asm2obj: converts a .NET assembly into a managed COFF .obj
/// linkable with chibil-produced objects via link.exe.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        string inputPath = null;
        string outputPath = null;
        Machine machine = 0;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--machine")
            {
                if (++i >= args.Length) return Usage("missing value for --machine");
                machine = args[i] switch
                {
                    "x86" => Machine.I386,
                    "x64" => Machine.Amd64,
                    "arm64" => Machine.Arm64,
                    _ => 0
                };
                if (machine == 0) return Usage($"unknown --machine value '{args[i]}'");
            }
            else if (a == "-o" || a == "--output")
            {
                if (++i >= args.Length) return Usage("missing value for -o");
                outputPath = args[i];
            }
            else if (a == "-h" || a == "--help")
            {
                return Usage(null);
            }
            else if (a.StartsWith("-"))
            {
                return Usage($"unknown option '{a}'");
            }
            else if (inputPath == null)
            {
                inputPath = a;
            }
            else
            {
                return Usage($"unexpected extra positional argument '{a}'");
            }
        }

        if (inputPath == null) return Usage("missing input assembly path");
        if (outputPath == null) return Usage("missing -o output path");
        if (machine == 0) return Usage("missing --machine");
        if (!File.Exists(inputPath)) { Console.Error.WriteLine($"asm2obj: input '{inputPath}' does not exist"); return 2; }

        try
        {
            byte[] obj = AsmToObjConverter.Convert(inputPath, machine, Path.GetFileName(outputPath));
            File.WriteAllBytes(outputPath, obj);
            return 0;
        }
        catch (NotSupportedException ex)
        {
            Console.Error.WriteLine($"asm2obj: unsupported input: {ex.Message}");
            return 3;
        }
        catch (BadImageFormatException ex)
        {
            Console.Error.WriteLine($"asm2obj: malformed input: {ex.Message}");
            return 4;
        }
    }

    private static int Usage(string error)
    {
        if (error != null)
            Console.Error.WriteLine($"asm2obj: {error}");
        Console.Error.WriteLine("usage: asm2obj <input.dll> --machine x86|x64|arm64 -o <output.obj>");
        return error == null ? 0 : 1;
    }
}
