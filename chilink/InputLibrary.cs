using System.Reflection.PortableExecutable;
using System.Text;
using Coff;

namespace Chilink;

internal sealed class InputLibrary : IDisposable
{
    public readonly string Path;
    private readonly CoffArchiveReader _reader;
    private readonly CoffArchiveSymbolTableReader _symbols;
    private readonly CoffArchiveMemberTableReader _members;
    private readonly CoffArchiveStringTableReader _strings;
    private readonly HashSet<CoffArchiveMemberHandle> _selected = new();

    public InputLibrary(string path)
    {
        Path = path;
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
                import = new(_strings.GetString(record.SymbolName), _strings.GetString(record.DllName),
                    _strings.GetString(record.ImportName), path);
                if (import.Library.Length == 0 || import.EntryPoint.Length == 0)
                    throw new LinkException($"{path}: import DLL and entrypoint names must not be empty");
            }
            return true;
        }
        return false;
    }

    public void Dispose() => _reader.Dispose();
}
