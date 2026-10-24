using System.Reflection.PortableExecutable;
using System.Text;

namespace Chilink;

public sealed class LinkException : Exception
{
    public LinkException(string message) : base(message) { }
}

internal sealed class LinkOptions
{
    public readonly List<LinkInput> Inputs = new();
    public readonly List<string> LibraryPaths = new();
    public readonly List<string> Includes = new();
    public string Output = "a.exe";
    public string Entry, Pdb;
    public bool Dll, StripDebug, GcSections;
    public bool Debug => !StripDebug || Pdb != null;
    public bool EmbeddedPdb => !StripDebug && Pdb == null;
    public Subsystem Subsystem = Subsystem.WindowsCui;
}

internal readonly record struct LinkInput(string Name, bool SearchLibrary = false);

internal sealed record LinkResult(byte[] Image, byte[] Pdb, IReadOnlyCollection<string> Warnings);

public sealed class Driver
{
    public void Run(string[] args)
    {
        try
        {
            var options = new LinkOptions();
            if (!Parse(options, Expand(args, new HashSet<string>(StringComparer.OrdinalIgnoreCase)).ToArray()))
                return;
            if (options.Inputs.Count == 0)
                throw new LinkException("no input object or library files");
            LinkResult result = new Linker(options).Link();
            foreach (string warning in result.Warnings)
                Console.Error.WriteLine($"chilink: warning: {warning}");
            string temporary = Path.GetFullPath(options.Output) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string temporaryPdb = result.Pdb == null ? null : options.Pdb + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, result.Image);
                if (temporaryPdb != null)
                {
                    File.WriteAllBytes(temporaryPdb, result.Pdb);
                    File.Move(temporaryPdb, options.Pdb, overwrite: true);
                }
                File.Move(temporary, options.Output, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
                if (temporaryPdb != null && File.Exists(temporaryPdb))
                    File.Delete(temporaryPdb);
            }
        }
        catch (IOException ex) { throw new LinkException(ex.Message); }
        catch (UnauthorizedAccessException ex) { throw new LinkException(ex.Message); }
        catch (BadImageFormatException ex) { throw new LinkException($"invalid object, archive or metadata: {ex.Message}"); }
    }

    private static bool Parse(LinkOptions options, string[] args)
    {
        bool positional = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!positional && arg == "--")
            {
                positional = true;
                continue;
            }
            if (positional || !arg.StartsWith('-') || arg == "-")
            {
                string extension = Path.GetExtension(arg);
                if (!new[] { ".obj", ".o", ".lib", ".a" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    throw new LinkException($"unsupported input '{arg}'; expected a managed COFF object or library");
                options.Inputs.Add(new(arg));
                continue;
            }

            string name, value = null;
            if (arg.StartsWith("--"))
            {
                int equals = arg.IndexOf('=');
                name = equals < 0 ? arg[2..] : arg[2..equals];
                if (name.Length == 1)
                    throw new LinkException($"unsupported linker option '{arg}'");
                if (equals >= 0)
                    value = arg[(equals + 1)..];
            }
            else if (arg == "-shared")
                name = "shared";
            else if (arg is "-s" or "-S")
                name = "strip-debug";
            else if (arg.Length >= 2 && "oeLlu".Contains(arg[1]))
            {
                name = arg[1].ToString();
                if (arg.Length > 2)
                    value = arg[2..];
            }
            else
                throw new LinkException($"unsupported linker option '{arg}'");

            string RequiredValue(bool allowEmpty = false)
            {
                if (value == null && i + 1 < args.Length)
                    value = args[++i];
                return value != null && (allowEmpty || value.Length != 0) ? value :
                    throw new LinkException($"missing value for {arg}");
            }
            void NoValue()
            {
                if (value != null)
                    throw new LinkException($"{arg} does not accept a value");
            }
            switch (name)
            {
                case "help":
                    NoValue();
                    Console.WriteLine("""
                        chilink [options] objects/libraries...
                        Portable PDB information is embedded by default unless stripped.
                          -o FILE, --output=FILE       Output path (default: a.exe)
                          -e SYMBOL, --entry=SYMBOL    Managed entry (default: mainCRTStartup)
                          -shared                     Produce a managed library without an entry
                          -L DIR, --library-path=DIR  Library search directory
                          -l NAME, --library=NAME     Search for a library (-l:FILE for an exact name)
                          -u SYMBOL, --undefined=SYMBOL  Retain a managed symbol
                          --[no-]gc-sections          Remove unused COMDATs (default: disabled)
                          --subsystem=console|windows Set the PE subsystem (default: console)
                          -s, -S, --strip-all, --strip-debug  Omit embedded debug information
                          --pdb=FILE                  Emit an external PDB (empty FILE uses default name)
                          @FILE                       Read a response file
                          --                          End options
                        """);
                    return false;
                case "o":
                case "output": options.Output = RequiredValue(); break;
                case "e":
                case "entry": options.Entry = RequiredValue(); break;
                case "shared": NoValue(); options.Dll = true; break;
                case "L":
                case "library-path": options.LibraryPaths.Add(RequiredValue()); break;
                case "l":
                case "library":
                    string library = RequiredValue();
                    if (library == ":")
                        throw new LinkException($"missing library filename for {arg}");
                    options.Inputs.Add(new(library, SearchLibrary: true));
                    break;
                case "u":
                case "undefined": options.Includes.Add(RequiredValue()); break;
                case "gc-sections": NoValue(); options.GcSections = true; break;
                case "no-gc-sections": NoValue(); options.GcSections = false; break;
                case "subsystem":
                    options.Subsystem = RequiredValue() switch
                    {
                        "console" => Subsystem.WindowsCui,
                        "windows" => Subsystem.WindowsGui,
                        _ => throw new LinkException($"unsupported subsystem '{value}'")
                    };
                    break;
                case "strip-all":
                case "strip-debug":
                    NoValue();
                    options.StripDebug = true;
                    break;
                case "pdb": options.Pdb = RequiredValue(allowEmpty: true); break;
                default: throw new LinkException($"unsupported linker option '{arg}'");
            }
        }
        return true;
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

    private static IEnumerable<string> SplitArguments(string text)
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
