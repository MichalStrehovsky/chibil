using System.Reflection.PortableExecutable;
using System.Text;

namespace Chilink;

public sealed class LinkException : Exception
{
    public LinkException(string message) : base(message) { }
}

internal sealed class LinkOptions
{
    public readonly List<string> Inputs = new();
    public readonly List<string> LibraryPaths = new();
    public readonly string LibraryEnvironment = Environment.GetEnvironmentVariable("LIB");
    public readonly List<string> Includes = new();
    public readonly List<string> DefaultLibraries = new();
    public readonly HashSet<string> IgnoredLibraries = new(StringComparer.OrdinalIgnoreCase);
    public string Output, Entry;
    public bool Dll, NoEntry, Debug, NoDefaultLib;
    public bool? RefOptimization;
    public bool OptRef => RefOptimization ?? !Debug;
    public Subsystem Subsystem = Subsystem.WindowsCui;
}

public sealed class Driver
{
    public void Run(string[] args)
    {
        try
        {
            var options = new LinkOptions();
            foreach (string arg in Expand(args, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
            {
                if (arg.Length > 1 && arg[0] is '/' or '-' &&
                    (arg[1..].Equals("?", StringComparison.OrdinalIgnoreCase) ||
                     arg[1..].Equals("help", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine("chilink [/out:file] [/entry:symbol | /dll] [/debug] [/opt:ref|noref] [/libpath:dir] objects/libraries...");
                    return;
                }
                Parse(options, arg);
            }
            if (options.Inputs.Count == 0)
                throw new LinkException("no input object or library files");
            byte[] image = new Linker(options).Link();
            string temporary = Path.GetFullPath(options.Output) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, image);
                File.Move(temporary, options.Output, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        catch (IOException ex) { throw new LinkException(ex.Message); }
        catch (UnauthorizedAccessException ex) { throw new LinkException(ex.Message); }
        catch (BadImageFormatException ex) { throw new LinkException($"invalid object, archive or metadata: {ex.Message}"); }
    }

    internal static void Parse(LinkOptions options, string arg, bool directive = false)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return;
        if (arg[0] != '/' && arg[0] != '-')
        {
            if (directive)
                throw new LinkException($"unsupported linker directive: {arg}");
            if (arg.Equals("mscoree.lib", StringComparison.OrdinalIgnoreCase))
                return;
            if (!Path.GetExtension(arg).Equals(".obj", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetExtension(arg).Equals(".o", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetExtension(arg).Equals(".lib", StringComparison.OrdinalIgnoreCase))
                throw new LinkException($"unsupported input '{arg}'; expected a managed COFF object or library");
            options.Inputs.Add(arg);
            return;
        }

        int colon = arg.IndexOf(':');
        string name = (colon < 0 ? arg[1..] : arg[1..colon]).ToLowerInvariant();
        string value = colon < 0 ? null : arg[(colon + 1)..];
        string RequiredValue() => !string.IsNullOrEmpty(value) ? value :
            throw new LinkException($"missing value for /{name}");
        void NoValue()
        {
            if (value != null)
                throw new LinkException($"/{name} does not accept a value");
        }
        switch (name)
        {
            case "nologo": NoValue(); break;
            case "debug": NoValue(); options.Debug = true; break;
            case "out":
                if (directive) throw new LinkException("/out is not supported in object directives");
                options.Output = RequiredValue();
                break;
            case "entry": options.Entry = RequiredValue(); break;
            case "dll": NoValue(); options.Dll = true; break;
            case "noentry": NoValue(); options.NoEntry = true; break;
            case "include": options.Includes.Add(RequiredValue()); break;
            case "libpath":
                if (directive) throw new LinkException("/libpath is not supported in object directives");
                options.LibraryPaths.Add(RequiredValue());
                break;
            case "machine":
                if (!RequiredValue().Equals("x64", StringComparison.OrdinalIgnoreCase))
                    throw new LinkException("only /machine:x64 is supported");
                break;
            case "subsystem":
                options.Subsystem = RequiredValue().ToLowerInvariant() switch
                {
                    "console" => Subsystem.WindowsCui,
                    "windows" => Subsystem.WindowsGui,
                    _ => throw new LinkException($"unsupported subsystem '{value}'")
                };
                break;
            case "opt":
                foreach (string opt in RequiredValue().Split(','))
                {
                    switch (opt.ToLowerInvariant())
                    {
                        case "ref": options.RefOptimization = true; break;
                        case "noref": options.RefOptimization = false; break;
                        case "noicf": break;
                        default: throw new LinkException($"unsupported /opt option '{opt}'");
                    }
                }
                break;
            case "nodefaultlib":
                if (value == null) options.NoDefaultLib = true;
                else options.IgnoredLibraries.Add(RequiredValue());
                break;
            case "defaultlib":
                options.DefaultLibraries.Add(RequiredValue());
                break;
            default: throw new LinkException($"unsupported linker option '{arg}'");
        }
    }

    private static IEnumerable<string> Expand(IEnumerable<string> args, HashSet<string> active)
    {
        foreach (string arg in args)
        {
            if (!arg.StartsWith('@'))
            {
                yield return arg;
                continue;
            }
            string path = Path.GetFullPath(arg[1..]);
            if (!active.Add(path))
                throw new LinkException($"response file cycle: '{path}'");
            if (active.Count > 64)
                throw new LinkException("response files are nested too deeply");
            foreach (string item in Expand(SplitArguments(File.ReadAllText(path)), active))
                yield return item;
            active.Remove(path);
        }
    }

    internal static IEnumerable<string> SplitArguments(string text)
    {
        var word = new StringBuilder();
        bool quoted = false, started = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\0')
                continue;
            if (c == '\\')
            {
                int start = i;
                while (i < text.Length && text[i] == '\\') i++;
                int count = i - start;
                if (i < text.Length && text[i] == '"')
                {
                    word.Append('\\', count / 2);
                    if ((count & 1) != 0) word.Append('"');
                    else quoted = !quoted;
                }
                else
                {
                    word.Append('\\', count);
                    i--;
                }
                started = true;
            }
            else if (c == '"') { quoted = !quoted; started = true; }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started) { yield return word.ToString(); word.Clear(); started = false; }
            }
            else { word.Append(c); started = true; }
        }
        if (quoted)
            throw new LinkException("unterminated quote in linker arguments");
        if (started)
            yield return word.ToString();
    }
}
