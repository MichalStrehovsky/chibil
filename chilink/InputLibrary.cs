using System.Reflection.PortableExecutable;
using System.Text;
using Coff;

namespace Chilink;

internal sealed class InputLibrary : IDisposable
{
    public readonly string Path;
    public IReadOnlyList<string> ScriptInputs { get; }
    private readonly CoffArchiveReader _reader;
    private readonly CoffArchiveSymbolTableReader _symbols;
    private readonly CoffArchiveMemberTableReader _members;
    private readonly CoffArchiveStringTableReader _strings;
    private readonly HashSet<CoffArchiveMemberHandle> _selected = new();
    private readonly ElfSharedLibrary _elf;

    public InputLibrary(string path, string runtimeName)
    {
        Path = path;
        using (var probe = File.OpenRead(path))
        {
            Span<byte> header = stackalloc byte[256];
            int count = probe.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            if (header[..count].StartsWith("\u007fELF"u8))
            {
                _elf = new ElfSharedLibrary(path, runtimeName);
                return;
            }
            if (!header[..count].StartsWith("!<arch>\n"u8))
            {
                string prefix = Encoding.ASCII.GetString(header[..count]).TrimStart();
                if (LinkOptions.IsSharedLibraryPath(path) ||
                    new[] { "/*", "INPUT", "GROUP", "OUTPUT_FORMAT" }.Any(p => prefix.StartsWith(p, StringComparison.Ordinal)))
                {
                    probe.Position = 0;
                    ScriptInputs = ElfLinkerScript.Read(path, probe);
                    return;
                }
            }
        }
        _reader = new CoffArchiveReader(File.OpenRead(path));
        try
        {
            _members = _reader.GetMemberTableReader();
            if (!_reader.HasSymbolTable)
                throw new LinkException($"{path}: archive has no supported linker symbol index");
            _symbols = _reader.GetSymbolTableReader();
            _strings = _reader.GetStringTableReader();
        }
        catch (BadImageFormatException ex)
        {
            _reader.Dispose();
            throw new LinkException($"{path}: invalid archive: {ex.Message}");
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    public bool TrySelect(string symbol, int inputIndex, out InputFile input, out NativeImport import)
    {
        input = null;
        import = null;
        if (_elf != null)
            return _elf.TrySelect(symbol, out import);
        foreach (var handle in _symbols.FindSymbols(Encoding.UTF8.GetBytes(symbol)))
        {
            var memberHandle = _symbols.GetCoffArchiveSymbol(handle).Member;
            if (!_selected.Add(memberHandle))
                continue;
            var member = _members.GetCoffArchiveMember(memberHandle);
            string path = $"{Path}({_strings.GetString(member.Name)})";
            CoffImportObject record;
            try
            {
                record = _reader.GetImportObject(member);
            }
            catch (BadImageFormatException ex)
            {
                throw new LinkException($"{path}: invalid import member: {ex.Message}");
            }
            if (record.IsDefault)
                input = new InputFile(path, inputIndex, _reader.GetCoffReader(member), this);
            else
            {
                if (record.Machine != Machine.Amd64)
                    throw new LinkException($"{path}: only x64 imports are supported");
                if (record.Type != CoffImportType.Code)
                    throw new LinkException($"{path}: imported data/const symbol '{symbol}' is not supported");
                if (record.NameType == CoffImportNameType.Ordinal)
                    throw new LinkException($"{path}: ordinal import '{symbol}' is not supported");
                string name = _strings.GetString(record.SymbolName);
                import = new(name, _strings.GetString(record.DllName),
                    _strings.GetString(record.ImportName), path, "__imp_" + name);
                if (import.Library.Length == 0 || import.EntryPoint.Length == 0)
                    throw new LinkException($"{path}: import DLL and entrypoint names must not be empty");
            }
            return true;
        }
        return false;
    }

    public void Dispose() => _reader?.Dispose();
}
