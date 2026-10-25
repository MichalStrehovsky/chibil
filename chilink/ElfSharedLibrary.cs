using System.Buffers.Binary;
using System.Text;

namespace Chilink;

internal sealed class ElfSharedLibrary
{
    private readonly record struct Section(uint Type, ulong Offset, ulong Size, uint Link, ulong EntrySize);

    private readonly string _path;
    private readonly byte[] _data;
    private readonly Dictionary<string, byte> _exports = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly string _library;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public ElfSharedLibrary(string path, string runtimeName)
    {
        _path = path;
        _data = File.ReadAllBytes(path);
        var header = Read(0, 64, "ELF header");
        if (!header[..4].SequenceEqual("\u007fELF"u8))
            throw Error("invalid ELF signature");
        if (header[4] != 2)
            throw Error("only ELF64 shared libraries are supported");
        if (header[5] != 1)
            throw Error("only little-endian ELF shared libraries are supported");
        if (header[6] != 1 || BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) != 1)
            throw Error("unsupported ELF version");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[16..]) != 3)
            throw Error("ELF input must be a shared object");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[18..]) != 62)
            throw Error("only x86-64 ELF shared libraries are supported");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[52..]) != 64)
            throw Error("invalid ELF header size");

        ulong sectionOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[40..]);
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(header[60..]);
        if (sectionOffset == 0)
            throw Error("ELF shared libraries without section tables are not supported");
        if (sectionCount == 0 || BinaryPrimitives.ReadUInt16LittleEndian(header[62..]) == 0xffff)
            throw Error("extended ELF section indexes are not supported");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[58..]) != 64)
            throw Error("invalid ELF section entry size");
        Read(sectionOffset, (ulong)sectionCount * 64, "ELF section table");
        var sections = new Section[sectionCount];
        for (int i = 0; i < sections.Length; i++)
        {
            var entry = Read(sectionOffset + (ulong)i * 64, 64, "ELF section");
            sections[i] = new(
                BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]),
                BinaryPrimitives.ReadUInt64LittleEndian(entry[24..]),
                BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[40..]),
                BinaryPrimitives.ReadUInt64LittleEndian(entry[56..]));
            if (sections[i].Type != 8) // SHT_NOBITS has no file payload.
                Read(sections[i].Offset, sections[i].Size, "ELF section");
        }

        int symbolsIndex = FindSection(sections, 11, required: true);
        Section symbols = sections[symbolsIndex];
        Section symbolStrings = Strings(sections, symbols);
        ValidateEntries(symbols, 24, "dynamic symbol table");
        ulong symbolCount = symbols.Size / 24;
        int versionsIndex = FindSection(sections, 0x6fffffff, required: false);
        Section versions = versionsIndex < 0 ? default : sections[versionsIndex];
        if (versionsIndex >= 0)
        {
            ValidateEntries(versions, 2, "symbol version table");
            if (versions.Link != symbolsIndex || versions.Size / 2 != symbolCount)
                throw Error("invalid ELF symbol version table");
        }

        Section dynamic = sections[FindSection(sections, 6, required: true)];
        Section dynamicStrings = Strings(sections, dynamic);
        ValidateEntries(dynamic, 16, "dynamic table");
        string soname = null;
        bool terminated = false;
        for (ulong offset = 0; offset < dynamic.Size; offset += 16)
        {
            var entry = Read(dynamic.Offset + offset, 16, "ELF dynamic entry");
            ulong tag = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            if (tag == 0)
            {
                terminated = true;
                break;
            }
            if (tag == 14)
            {
                if (soname != null)
                    throw Error("duplicate ELF DT_SONAME");
                soname = String(dynamicStrings, BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]));
                if (soname.Length == 0)
                    throw Error("ELF DT_SONAME must not be empty");
            }
            if (tag == 0x6ffffffb && (BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]) & 0x08000000) != 0)
                throw Error("ELF PIE executables are not supported as shared libraries");
        }
        if (!terminated)
            throw Error("unterminated ELF dynamic table");
        _library = soname ?? runtimeName;

        for (ulong i = 0; i < symbolCount; i++)
        {
            var entry = Read(symbols.Offset + i * 24, 24, "ELF dynamic symbol");
            uint nameIndex = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            int binding = entry[4] >> 4;
            byte type = (byte)(entry[4] & 15);
            int visibility = entry[5] & 3;
            ushort section = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
            if (section == 0xffff)
                throw Error("extended ELF symbol section indexes are not supported");
            if (section >= sections.Length && section < 0xff00)
                throw Error("invalid ELF symbol section index");
            string name = String(symbolStrings, nameIndex);
            if (section == 0 || binding is not (1 or 2) || visibility is 1 or 2 || name.Length == 0)
                continue;
            if (versionsIndex >= 0)
            {
                ushort version = BinaryPrimitives.ReadUInt16LittleEndian(
                    Read(versions.Offset + i * 2, 2, "ELF symbol version"));
                if (version == 0 || (version & 0x8000) != 0)
                    continue;
            }
            if (_exports.TryGetValue(name, out byte previous) && previous != type)
                throw Error($"conflicting ELF export kinds for '{name}'");
            _exports[name] = type;
        }
    }

    public bool TrySelect(string symbol, out NativeImport import)
    {
        import = null;
        if (!_exports.TryGetValue(symbol, out byte type) || !_selected.Add(symbol))
            return false;
        if (type is not (1 or 2 or 5 or 10))
            throw Error(type switch
            {
                6 => $"imported TLS symbol '{symbol}' is not supported",
                _ => $"unsupported ELF export kind {type} for '{symbol}'",
            });
        import = new(type is 1 or 5 ? NativeImportKind.Data : NativeImportKind.Function,
            symbol, _library, symbol, _path);
        return true;
    }

    private ReadOnlySpan<byte> Read(ulong offset, ulong size, string description)
    {
        if (offset > (ulong)_data.Length || size > (ulong)_data.Length - offset)
            throw Error($"invalid or truncated {description}");
        return _data.AsSpan((int)offset, (int)size);
    }

    private int FindSection(Section[] sections, uint type, bool required)
    {
        int result = -1;
        for (int i = 0; i < sections.Length; i++)
            if (sections[i].Type == type)
            {
                if (result >= 0)
                    throw Error($"multiple ELF sections of type 0x{type:x} are not supported");
                result = i;
            }
        return result >= 0 || !required ? result :
            throw Error($"missing ELF section of type 0x{type:x}");
    }

    private Section Strings(Section[] sections, Section section)
    {
        if (section.Link >= sections.Length || sections[section.Link].Type != 3)
            throw Error("invalid ELF string table link");
        return sections[section.Link];
    }

    private void ValidateEntries(Section section, ulong size, string description)
    {
        if (section.EntrySize != size || section.Size == 0 || section.Size % size != 0)
            throw Error($"invalid ELF {description} entry size");
    }

    private string String(Section table, ulong index)
    {
        if (index >= table.Size)
            throw Error("invalid ELF string table index");
        var bytes = Read(table.Offset + index, table.Size - index, "ELF string table");
        int end = bytes.IndexOf((byte)0);
        if (end < 0)
            throw Error("unterminated ELF string");
        try { return Utf8.GetString(bytes[..end]); }
        catch (DecoderFallbackException) { throw Error("invalid UTF-8 in ELF string table"); }
    }

    private LinkException Error(string message) => new($"{_path}: {message}");
}
