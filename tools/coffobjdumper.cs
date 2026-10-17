// COFF Object IL Dumper
//
// Reads a managed COFF .obj file and dumps method bodies with decoded IL instructions.
// In COFF .obj files, method RVAs in metadata are 0 — the actual IL is in the .text$mn
// section and tokens in IL operands are 0 (resolved via COFF relocations).
//
// This tool:
//   1. Parses the COFF header, section headers, symbol table, and relocations
//   2. Finds .text$mn (IL method bodies) and .cormeta (metadata)
//   3. Applies CLR token relocations to recover actual token values in IL
//   4. Parses method bodies sequentially and disassembles IL
//
// Usage: dotnet run coffobjdumper.cs <path-to-obj-file>

#:property Nullable=disable
#:property AllowUnsafeBlocks=true
#:project ..\coff\Coff.csproj

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

// ─── COFF structures ──────────────────────────────────────────────────────────

struct CoffFileHeader
{
    public ushort Machine;
    public ushort NumberOfSections;
    public uint TimeDateStamp;
    public uint PointerToSymbolTable;
    public uint NumberOfSymbols;
    public ushort SizeOfOptionalHeader;
    public ushort Characteristics;

    public static CoffFileHeader Read(ReadOnlySpan<byte> data)
    {
        return new CoffFileHeader
        {
            Machine = BinaryPrimitives.ReadUInt16LittleEndian(data),
            NumberOfSections = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            TimeDateStamp = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            PointerToSymbolTable = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            NumberOfSymbols = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
            SizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(data[16..]),
            Characteristics = BinaryPrimitives.ReadUInt16LittleEndian(data[18..]),
        };
    }
}

struct CoffSectionHeader
{
    public string Name;
    public uint VirtualSize;
    public uint VirtualAddress;
    public uint SizeOfRawData;
    public uint PointerToRawData;
    public uint PointerToRelocations;
    public uint PointerToLineNumbers;
    public ushort NumberOfRelocations;
    public ushort NumberOfLineNumbers;
    public uint Characteristics;

    public static CoffSectionHeader Read(ReadOnlySpan<byte> data)
    {
        string name = Encoding.UTF8.GetString(data[..8]).TrimEnd('\0');
        return new CoffSectionHeader
        {
            Name = name,
            VirtualSize = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            VirtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
            SizeOfRawData = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
            PointerToRawData = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
            PointerToRelocations = BinaryPrimitives.ReadUInt32LittleEndian(data[24..]),
            PointerToLineNumbers = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]),
            NumberOfRelocations = BinaryPrimitives.ReadUInt16LittleEndian(data[32..]),
            NumberOfLineNumbers = BinaryPrimitives.ReadUInt16LittleEndian(data[34..]),
            Characteristics = BinaryPrimitives.ReadUInt32LittleEndian(data[36..]),
        };
    }
}

struct CoffRelocation
{
    public uint VirtualAddress; // offset within section
    public uint SymbolTableIndex;
    public ushort Type;

    public static CoffRelocation Read(ReadOnlySpan<byte> data)
    {
        return new CoffRelocation
        {
            VirtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(data),
            SymbolTableIndex = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            Type = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
        };
    }
}

struct CoffSymbol
{
    public string Name;
    public uint Value;
    public short SectionNumber;
    public ushort Type;
    public byte StorageClass;
    public byte NumberOfAuxSymbols;
}

readonly struct MethodBodyLocation
{
    public int SectionNumber { get; }
    public int Offset { get; }

    public MethodBodyLocation(int sectionNumber, int offset)
        => (SectionNumber, Offset) = (sectionNumber, offset);
}

readonly struct FieldDataLocation
{
    public int SectionNumber { get; }
    public int Offset { get; }
    public string SymbolName { get; }

    public FieldDataLocation(int sectionNumber, int offset, string symbolName)
        => (SectionNumber, Offset, SymbolName) = (sectionNumber, offset, symbolName);
}

// ─── COFF File Parser ─────────────────────────────────────────────────────────

class CoffFile
{
    public CoffFileHeader Header;
    public CoffSectionHeader[] Sections;
    public CoffSymbol[] Symbols;
    public byte[] FileData;

    const int CoffHeaderSize = 20;
    const int SectionHeaderSize = 40;
    const int SymbolSize = 18;
    const int RelocationSize = 10;

    const ushort IMAGE_REL_I386_TOKEN = 0x000C;
    const ushort IMAGE_REL_AMD64_TOKEN = 0x000D;
    const byte IMAGE_SYM_CLASS_CLR_TOKEN = 107;

    public static CoffFile Parse(byte[] data)
    {
        var coff = new CoffFile { FileData = data };
        coff.Header = CoffFileHeader.Read(data);

        // Parse section headers (immediately after COFF header + optional header)
        int sectionOffset = CoffHeaderSize + coff.Header.SizeOfOptionalHeader;
        coff.Sections = new CoffSectionHeader[coff.Header.NumberOfSections];
        for (int i = 0; i < coff.Header.NumberOfSections; i++)
        {
            coff.Sections[i] = CoffSectionHeader.Read(data.AsSpan(sectionOffset + i * SectionHeaderSize));
        }

        // Parse symbol table
        if (coff.Header.PointerToSymbolTable > 0 && coff.Header.NumberOfSymbols > 0)
        {
            coff.Symbols = ParseSymbols(data, (int)coff.Header.PointerToSymbolTable, (int)coff.Header.NumberOfSymbols);
        }
        else
        {
            coff.Symbols = Array.Empty<CoffSymbol>();
        }

        // Resolve long section names (format: /NNN where NNN is offset into string table)
        if (coff.Header.PointerToSymbolTable > 0)
        {
            int stringTableOffset = (int)coff.Header.PointerToSymbolTable + (int)coff.Header.NumberOfSymbols * SymbolSize;
            for (int i = 0; i < coff.Sections.Length; i++)
            {
                if (coff.Sections[i].Name.StartsWith("/") && int.TryParse(coff.Sections[i].Name[1..], out int strOff))
                {
                    int strStart = stringTableOffset + strOff;
                    int strEnd = Array.IndexOf(data, (byte)0, strStart);
                    if (strEnd > strStart)
                        coff.Sections[i].Name = Encoding.UTF8.GetString(data, strStart, strEnd - strStart);
                }
            }
        }

        return coff;
    }

    static CoffSymbol[] ParseSymbols(byte[] data, int symTabOffset, int count)
    {
        // String table immediately follows the symbol table
        int stringTableOffset = symTabOffset + count * SymbolSize;
        uint stringTableSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(stringTableOffset));

        var symbols = new CoffSymbol[count];
        int offset = symTabOffset;

        for (int i = 0; i < count; i++)
        {
            var span = data.AsSpan(offset, SymbolSize);

            // Name: first 4 bytes zero → use string table offset in next 4 bytes
            string name;
            uint nameCheck = BinaryPrimitives.ReadUInt32LittleEndian(span);
            if (nameCheck == 0)
            {
                uint strOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                int strStart = stringTableOffset + (int)strOffset;
                int strEnd = Array.IndexOf(data, (byte)0, strStart);
                name = Encoding.UTF8.GetString(data, strStart, strEnd - strStart);
            }
            else
            {
                name = Encoding.UTF8.GetString(data, offset, 8).TrimEnd('\0');
            }

            symbols[i] = new CoffSymbol
            {
                Name = name,
                Value = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]),
                SectionNumber = BinaryPrimitives.ReadInt16LittleEndian(span[12..]),
                Type = BinaryPrimitives.ReadUInt16LittleEndian(span[14..]),
                StorageClass = span[16],
                NumberOfAuxSymbols = span[17],
            };

            offset += SymbolSize;

            // Skip aux symbols
            int auxCount = symbols[i].NumberOfAuxSymbols;
            if (auxCount > 0)
            {
                for (int a = 0; a < auxCount && (i + 1) < count; a++)
                {
                    i++;
                    symbols[i] = new CoffSymbol { Name = $"<aux>", NumberOfAuxSymbols = 0 };
                    offset += SymbolSize;
                }
            }
        }

        return symbols;
    }

    public CoffSectionHeader? FindSection(string name)
    {
        foreach (var s in Sections)
            if (s.Name == name) return s;
        return null;
    }

    public ReadOnlySpan<byte> GetSectionData(CoffSectionHeader section)
    {
        return FileData.AsSpan((int)section.PointerToRawData, (int)section.SizeOfRawData);
    }

    public CoffSectionHeader GetSection(int sectionNumber)
    {
        if (sectionNumber <= 0 || sectionNumber > Sections.Length)
            throw new ArgumentOutOfRangeException(nameof(sectionNumber));

        return Sections[sectionNumber - 1];
    }

    public CoffRelocation[] GetRelocations(CoffSectionHeader section)
    {
        if (section.NumberOfRelocations == 0)
            return Array.Empty<CoffRelocation>();

        var relocs = new CoffRelocation[section.NumberOfRelocations];
        int offset = (int)section.PointerToRelocations;
        for (int i = 0; i < relocs.Length; i++)
        {
            relocs[i] = CoffRelocation.Read(FileData.AsSpan(offset + i * RelocationSize));
        }
        return relocs;
    }

    /// <summary>
    /// Builds a map of offset-within-section → resolved token value,
    /// by examining relocations whose symbols have storage class CLR_TOKEN (107)
    /// and names that are hex token strings. Architecture-independent: identifies
    /// token relocs by symbol class rather than relocation type code.
    /// </summary>
    public Dictionary<int, int> BuildTokenRelocationMap(CoffSectionHeader section)
    {
        var map = new Dictionary<int, int>();
        var relocs = GetRelocations(section);

        foreach (var r in relocs)
        {
            if (r.SymbolTableIndex >= (uint)Symbols.Length)
                continue;

            var sym = Symbols[r.SymbolTableIndex];
            if (sym.StorageClass == IMAGE_SYM_CLASS_CLR_TOKEN &&
                sym.Name.Length == 8 &&
                int.TryParse(sym.Name, System.Globalization.NumberStyles.HexNumber, null, out int token))
            {
                map[(int)r.VirtualAddress] = token;
            }
        }

        return map;
    }

    public Dictionary<int, MethodBodyLocation> BuildMethodBodyLocationMap()
    {
        var map = new Dictionary<int, MethodBodyLocation>();

        foreach (var sym in Symbols)
        {
            if (sym.StorageClass != IMAGE_SYM_CLASS_CLR_TOKEN ||
                sym.SectionNumber <= 0 ||
                sym.Name.Length != 8 ||
                !int.TryParse(sym.Name, System.Globalization.NumberStyles.HexNumber, null, out int token) ||
                (token & unchecked((int)0xFF000000)) != 0x06000000)
            {
                continue;
            }

            var section = GetSection(sym.SectionNumber);
            if (sym.Value < section.SizeOfRawData)
                map[token] = new MethodBodyLocation(sym.SectionNumber, (int)sym.Value);
        }

        return map;
    }

    public Dictionary<int, FieldDataLocation> BuildFieldDataLocationMap()
    {
        var map = new Dictionary<int, FieldDataLocation>();

        for (int i = 0; i < Symbols.Length; i++)
        {
            var sym = Symbols[i];
            if (sym.StorageClass != IMAGE_SYM_CLASS_CLR_TOKEN ||
                sym.Name.Length != 8 ||
                !int.TryParse(sym.Name, System.Globalization.NumberStyles.HexNumber, null, out int token) ||
                (token & unchecked((int)0xFF000000)) != 0x04000000)
            {
                continue;
            }

            if (sym.SectionNumber > 0)
            {
                var section = GetSection(sym.SectionNumber);
                if (sym.Value >= section.SizeOfRawData)
                    continue;
            }

            map[token] = new FieldDataLocation(sym.SectionNumber, (int)sym.Value, FindAssociatedSymbolName(i, sym));
        }

        return map;
    }

    string FindAssociatedSymbolName(int tokenSymbolIndex, CoffSymbol tokenSymbol)
    {
        for (int i = tokenSymbolIndex - 1; i >= 0; i--)
        {
            var sym = Symbols[i];
            if (sym.Name == "<aux>")
                continue;

            if (sym.StorageClass != IMAGE_SYM_CLASS_CLR_TOKEN &&
                sym.SectionNumber == tokenSymbol.SectionNumber &&
                sym.Value == tokenSymbol.Value)
            {
                return sym.Name;
            }

            break;
        }

        return null;
    }

    /// <summary>
    /// Builds a map of offset-within-section → (symbol name, symbol section number)
    /// for non-token relocations. Used to resolve section/secrel references in debug data.
    /// </summary>
    public Dictionary<int, (string Name, short Section)> BuildSymbolRelocationMap(CoffSectionHeader section)
    {
        var map = new Dictionary<int, (string, short)>();
        var relocs = GetRelocations(section);

        foreach (var r in relocs)
        {
            if (r.SymbolTableIndex >= (uint)Symbols.Length)
                continue;

            var sym = Symbols[r.SymbolTableIndex];
            if (sym.StorageClass != IMAGE_SYM_CLASS_CLR_TOKEN)
            {
                map[(int)r.VirtualAddress] = (sym.Name, sym.SectionNumber);
            }
        }

        return map;
    }

    public Dictionary<int, (string Name, short Section, ushort Type)> BuildNonTokenRelocationMap(CoffSectionHeader section)
    {
        var map = new Dictionary<int, (string, short, ushort)>();
        var relocs = GetRelocations(section);

        foreach (var r in relocs)
        {
            if (r.SymbolTableIndex >= (uint)Symbols.Length)
                continue;

            var sym = Symbols[r.SymbolTableIndex];
            if (sym.StorageClass != IMAGE_SYM_CLASS_CLR_TOKEN)
            {
                map[(int)r.VirtualAddress] = (sym.Name, sym.SectionNumber, r.Type);
            }
        }

        return map;
    }

    /// <summary>
    /// Apply token relocations to a copy of section data, returning patched bytes
    /// where CLR token operand slots are filled in with actual token values.
    /// </summary>
    public byte[] GetPatchedSectionData(CoffSectionHeader section)
    {
        var data = GetSectionData(section).ToArray();
        var tokenMap = BuildTokenRelocationMap(section);

        foreach (var (offset, token) in tokenMap)
        {
            if (offset + 4 <= data.Length)
            {
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), token);
            }
        }

        return data;
    }
}

// ─── Main Program ─────────────────────────────────────────────────────────────

class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: dotnet run coffobjdumper.cs <path-to-obj-file>");
            return 1;
        }

        string objPath = args[0];
        if (!File.Exists(objPath))
        {
            Console.Error.WriteLine($"File not found: {objPath}");
            return 1;
        }

        byte[] fileData = File.ReadAllBytes(objPath);
        var coff = CoffFile.Parse(fileData);

        Console.WriteLine($"File: {objPath}");
        Console.WriteLine($"Machine: 0x{coff.Header.Machine:X4}");
        Console.WriteLine($"Sections: {coff.Header.NumberOfSections}");
        Console.WriteLine($"Symbols: {coff.Header.NumberOfSymbols}");
        Console.WriteLine();

        for (int i = 0; i < coff.Sections.Length; i++)
        {
            var sh = coff.Sections[i];
            Console.WriteLine($"  Section[{i}]: {sh.Name,-12} RawPtr=0x{sh.PointerToRawData:X4}  RawSize={sh.SizeOfRawData,5}  Relocs={sh.NumberOfRelocations}");
        }
        Console.WriteLine();

        // Print symbol table
        Console.WriteLine("Symbol Table:");
        for (int i = 0; i < coff.Symbols.Length; i++)
        {
            var sym = coff.Symbols[i];
            if (sym.Name == "<aux>") continue;
            Console.WriteLine($"  [{i,3}] {sym.Name,-20} Value=0x{sym.Value:X4}  Sect={sym.SectionNumber,2}  Type=0x{sym.Type:X4}  Class={sym.StorageClass,3}  Aux={sym.NumberOfAuxSymbols}");
        }
        Console.WriteLine();

        // Find .cormeta section for metadata
        var cormetaSection = coff.FindSection(".cormeta");
        if (cormetaSection == null)
        {
            Console.Error.WriteLine("No .cormeta section found — not a managed COFF object file.");
            return 1;
        }

        // Collect all .text$mn and .text$di sections for IL method bodies
        // MSVC COMDAT objs have each method in its own section; our emitter merges them into one
        var textSectionNumbers = new List<int>();
        for (int i = 0; i < coff.Sections.Length; i++)
        {
            if (coff.Sections[i].Name == ".text$mn" || coff.Sections[i].Name == ".text$di")
                textSectionNumbers.Add(i + 1);
        }

        if (textSectionNumbers.Count == 0)
        {
            Console.Error.WriteLine("No .text$mn section found.");
            return 1;
        }

        // MSVC emits one or more .text$mn COMDAT sections and records each method
        // body location with a CLR token symbol. Metadata method order is unrelated.
        var patchedSectionData = new Dictionary<int, byte[]>();
        var methodBodyLocations = coff.BuildMethodBodyLocationMap();
        int totalTextBytes = 0;
        int totalTokenRelocs = 0;
        foreach (int sectionNumber in textSectionNumbers)
        {
            var ts = coff.GetSection(sectionNumber);
            byte[] patched = coff.GetPatchedSectionData(ts);
            totalTokenRelocs += coff.BuildTokenRelocationMap(ts).Count;
            patchedSectionData[sectionNumber] = patched;
            totalTextBytes += patched.Length;
        }

        Console.WriteLine($".text sections: {textSectionNumbers.Count} ({totalTextBytes} bytes, {totalTokenRelocs} token relocations applied, {methodBodyLocations.Count} method locations)");
        Console.WriteLine();

        // Read metadata
        var metadataBytes = coff.GetSectionData(cormetaSection.Value).ToArray();
        MetadataReader reader;
        unsafe
        {
            fixed (byte* ptr = metadataBytes)
            {
                reader = new MetadataReader(ptr, metadataBytes.Length);
            }
        }

        foreach (var methodHandle in reader.MethodDefinitions)
        {
            MethodDefinition method = reader.GetMethodDefinition(methodHandle);
            string name = reader.GetString(method.Name);
            int token = MetadataTokens.GetToken(methodHandle);

            if ((method.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL)
            {
                Console.WriteLine($"=== Method: {name} ({token:X8}) ===");
                Console.WriteLine($"  (not IL — ImplAttributes: {method.ImplAttributes})");
                Console.WriteLine();
                continue;
            }

            if (method.Attributes.HasFlag(MethodAttributes.Abstract))
            {
                Console.WriteLine($"=== Method: {name} ({token:X8}) ===");
                Console.WriteLine($"  (abstract — no body)");
                Console.WriteLine();
                continue;
            }

            if (!methodBodyLocations.TryGetValue(token, out MethodBodyLocation location))
            {
                Console.WriteLine($"=== Method: {name} ({token:X8}) ===");
                Console.WriteLine($"  (no method body location in COFF symbols)");
                Console.WriteLine();
                continue;
            }

            var section = coff.GetSection(location.SectionNumber);
            if (!patchedSectionData.TryGetValue(location.SectionNumber, out byte[] ilData))
                ilData = coff.GetPatchedSectionData(section);

            if (location.Offset >= ilData.Length)
            {
                Console.WriteLine($"=== Method: {name} ({token:X8}) ===");
                Console.WriteLine($"  (method body offset 0x{location.Offset:X4} is outside section {location.SectionNumber})");
                Console.WriteLine();
                continue;
            }

            unsafe
            {
                fixed (byte* ptr = ilData)
                {
                    var bodyReader = new BlobReader(ptr + location.Offset, ilData.Length - location.Offset);
                    MethodBodyBlock body = MethodBodyBlock.Create(bodyReader);

                    Console.WriteLine($"=== Method: {name} ({token:X8}) ===");
                    Console.WriteLine($"  Location: section[{location.SectionNumber - 1}] {section.Name} + 0x{location.Offset:X4}");
                    Console.WriteLine($"  MaxStack: {body.MaxStack}");
                    Console.WriteLine($"  LocalsInit: {body.LocalVariablesInitialized}");
                    Console.WriteLine($"  LocalSignature: {(body.LocalSignature.IsNil ? "(none)" : $"0x{MetadataTokens.GetToken(body.LocalSignature):X8}")}");

                    byte[] ilBytes = body.GetILBytes();
                    Console.WriteLine($"  CodeSize: {ilBytes.Length}");
                    Console.WriteLine();

                    DumpIL(reader, ilBytes);

                    if (body.ExceptionRegions.Length > 0)
                    {
                        Console.WriteLine();
                        DumpExceptionRegions(reader, body);
                    }

                    Console.WriteLine();
                }
            }
        }

        DumpRvaFields(coff, reader);
        DumpIlFixups(coff);

        // ─── Debug Information ────────────────────────────────────────────────
        DumpDebugInfo(coff);

        return 0;
    }

    static void DumpRvaFields(CoffFile coff, MetadataReader reader)
    {
        var fieldLocations = coff.BuildFieldDataLocationMap();
        if (fieldLocations.Count == 0)
            return;

        Console.WriteLine("========================================");
        Console.WriteLine("=== RVA Fields / Data Symbols ===");
        Console.WriteLine("========================================");
        Console.WriteLine();

        foreach (var fieldHandle in reader.FieldDefinitions)
        {
            FieldDefinition field = reader.GetFieldDefinition(fieldHandle);
            int token = MetadataTokens.GetToken(fieldHandle);
            int rva = field.GetRelativeVirtualAddress();

            if (rva == 0 && !fieldLocations.ContainsKey(token))
                continue;

            Console.WriteLine($"=== Field: {FormatFieldName(reader, field)} ({token:X8}) ===");
            Console.WriteLine($"  Attributes: {field.Attributes}");
            Console.WriteLine($"  Metadata RVA: 0x{rva:X8}");

            if (fieldLocations.TryGetValue(token, out FieldDataLocation location))
            {
                Console.WriteLine($"  COFF symbol: {(location.SymbolName ?? "(none)")}");
                Console.WriteLine($"  Location: {FormatFieldLocation(coff, location)}");

                if (location.SectionNumber > 0)
                    DumpFieldData(coff, reader, fieldLocations, location);
            }

            Console.WriteLine();
        }
    }

    static string FormatFieldName(MetadataReader reader, FieldDefinition field)
    {
        string name = reader.GetString(field.Name);
        var declaringType = field.GetDeclaringType();
        if (declaringType.IsNil)
            return name;

        var typeDef = reader.GetTypeDefinition(declaringType);
        string typeName = reader.GetString(typeDef.Name);
        string ns = reader.GetString(typeDef.Namespace);
        if (!string.IsNullOrEmpty(ns))
            typeName = $"{ns}.{typeName}";

        return $"{typeName}::{name}";
    }

    static string FormatFieldLocation(CoffFile coff, FieldDataLocation location)
    {
        if (location.SectionNumber > 0)
        {
            var section = coff.GetSection(location.SectionNumber);
            return $"section[{location.SectionNumber - 1}] {section.Name} + 0x{location.Offset:X4}";
        }

        if (location.SectionNumber == 0)
            return $"undefined + 0x{location.Offset:X4}";

        if (location.SectionNumber == -1)
            return $"absolute + 0x{location.Offset:X4}";

        return $"special section {location.SectionNumber} + 0x{location.Offset:X4}";
    }

    static void DumpFieldData(CoffFile coff, MetadataReader reader, Dictionary<int, FieldDataLocation> fieldLocations, FieldDataLocation location)
    {
        var section = coff.GetSection(location.SectionNumber);
        var sectionData = coff.GetPatchedSectionData(section);
        if (location.Offset >= sectionData.Length)
            return;

        int end = sectionData.Length;
        foreach (var other in fieldLocations.Values)
        {
            if (other.SectionNumber == location.SectionNumber &&
                other.Offset > location.Offset &&
                other.Offset < end)
            {
                end = other.Offset;
            }
        }

        int length = Math.Min(end - location.Offset, 64);
        var data = sectionData.AsSpan(location.Offset, length);
        Console.WriteLine($"  Data ({length} bytes): {FormatHexBytes(data)}");

        string ascii = FormatAscii(data);
        if (ascii.Length > 0)
            Console.WriteLine($"  ASCII: {ascii}");

        var tokenRelocs = coff.BuildTokenRelocationMap(section);
        foreach (var (offset, token) in tokenRelocs)
        {
            if (offset >= location.Offset && offset < location.Offset + length)
                Console.WriteLine($"  Reloc +0x{offset - location.Offset:X4}: {FormatToken(reader, token)}");
        }

        var nonTokenRelocs = coff.BuildNonTokenRelocationMap(section);
        var nonTokenOffsets = new List<int>(nonTokenRelocs.Keys);
        nonTokenOffsets.Sort();
        foreach (int offset in nonTokenOffsets)
        {
            if (offset >= location.Offset && offset < location.Offset + length)
            {
                var reloc = nonTokenRelocs[offset];
                Console.WriteLine($"  Reloc +0x{offset - location.Offset:X4}: {FormatRelocationType(coff.Header.Machine, reloc.Type)} {reloc.Name}");
            }
        }
    }

    static void DumpIlFixups(CoffFile coff)
    {
        bool wroteHeader = false;

        for (int i = 0; i < coff.Sections.Length; i++)
        {
            var section = coff.Sections[i];
            if (section.Name != ".rdata$ilfixup")
                continue;

            if (!wroteHeader)
            {
                Console.WriteLine("========================================");
                Console.WriteLine("=== ILFixups (.rdata$ilfixup) ===");
                Console.WriteLine("========================================");
                Console.WriteLine();
                wroteHeader = true;
            }

            byte[] data = coff.GetSectionData(section).ToArray();
            var relocs = coff.BuildNonTokenRelocationMap(section);

            Console.WriteLine($"Section[{i}] {section.Name}: {data.Length} bytes, {section.NumberOfRelocations} relocations");
            for (int offset = 0; offset + 8 <= data.Length; offset += 8)
            {
                uint rawRva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
                ushort count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));
                ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 6));

                string target = relocs.TryGetValue(offset, out var reloc)
                    ? $"{reloc.Name} ({FormatRelocationType(coff.Header.Machine, reloc.Type)})"
                    : $"RVA=0x{rawRva:X8}";

                Console.WriteLine($"  +0x{offset:X4}: Target={target}, Count={count}, Type=0x{type:X4}");
            }
            Console.WriteLine();
        }
    }

    static string FormatHexBytes(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i != 0)
                sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    static string FormatAscii(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length);
        bool hasPrintable = false;
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b >= 0x20 && b <= 0x7E)
            {
                sb.Append((char)b);
                hasPrintable = true;
            }
            else
            {
                sb.Append('.');
            }
        }
        return hasPrintable ? sb.ToString() : "";
    }

    static string FormatRelocationType(ushort machine, ushort type)
    {
        return machine switch
        {
            0x014C => type switch
            {
                0x0006 => "DIR32",
                0x0007 => "DIR32NB",
                0x000A => "SECTION",
                0x000B => "SECREL",
                0x000C => "TOKEN",
                0x0014 => "REL32",
                _ => $"0x{type:X4}",
            },
            0x8664 => type switch
            {
                0x0001 => "ADDR64",
                0x0003 => "ADDR32NB",
                0x0004 => "REL32",
                0x000A => "SECTION",
                0x000B => "SECREL",
                0x000D => "TOKEN",
                _ => $"0x{type:X4}",
            },
            0xAA64 => type switch
            {
                0x0002 => "ADDR32NB",
                0x0003 => "BRANCH26",
                0x0008 => "SECREL",
                0x000C => "TOKEN",
                0x000D => "SECTION",
                0x000E => "ADDR64",
                _ => $"0x{type:X4}",
            },
            _ => $"0x{type:X4}",
        };
    }

    static void DumpIL(MetadataReader reader, byte[] ilBytes)
    {
        int offset = 0;
        while (offset < ilBytes.Length)
        {
            int instrStart = offset;
            ILOpCode opCode = ReadOpCode(ilBytes, ref offset);
            string operandStr = ReadOperand(reader, ilBytes, ref offset, opCode);

            if (operandStr.Length > 0)
                Console.WriteLine($"  IL_{instrStart:X4}: {FormatOpCode(opCode),-16} {operandStr}");
            else
                Console.WriteLine($"  IL_{instrStart:X4}: {FormatOpCode(opCode)}");
        }
    }

    static ILOpCode ReadOpCode(byte[] il, ref int offset)
    {
        byte b = il[offset++];
        if (b == 0xFE)
            return (ILOpCode)(0xFE00 | il[offset++]);
        return (ILOpCode)b;
    }

    static int ReadInt32(byte[] il, ref int offset)
    {
        int val = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset));
        offset += 4;
        return val;
    }

    static long ReadInt64(byte[] il, ref int offset)
    {
        long val = BinaryPrimitives.ReadInt64LittleEndian(il.AsSpan(offset));
        offset += 8;
        return val;
    }

    static short ReadInt16(byte[] il, ref int offset)
    {
        short val = BinaryPrimitives.ReadInt16LittleEndian(il.AsSpan(offset));
        offset += 2;
        return val;
    }

    static float ReadSingle(byte[] il, ref int offset)
    {
        float val = BitConverter.ToSingle(il, offset);
        offset += 4;
        return val;
    }

    static double ReadDouble(byte[] il, ref int offset)
    {
        double val = BitConverter.ToDouble(il, offset);
        offset += 8;
        return val;
    }

    static string FormatOpCode(ILOpCode opCode)
    {
        return opCode.ToString().ToLowerInvariant().Replace('_', '.');
    }

    static string ReadOperand(MetadataReader reader, byte[] il, ref int offset, ILOpCode opCode)
    {
        switch (opCode)
        {
            // InlineNone
            case ILOpCode.Nop:
            case ILOpCode.Break:
            case ILOpCode.Ldarg_0:
            case ILOpCode.Ldarg_1:
            case ILOpCode.Ldarg_2:
            case ILOpCode.Ldarg_3:
            case ILOpCode.Ldloc_0:
            case ILOpCode.Ldloc_1:
            case ILOpCode.Ldloc_2:
            case ILOpCode.Ldloc_3:
            case ILOpCode.Stloc_0:
            case ILOpCode.Stloc_1:
            case ILOpCode.Stloc_2:
            case ILOpCode.Stloc_3:
            case ILOpCode.Ldnull:
            case ILOpCode.Ldc_i4_m1:
            case ILOpCode.Ldc_i4_0:
            case ILOpCode.Ldc_i4_1:
            case ILOpCode.Ldc_i4_2:
            case ILOpCode.Ldc_i4_3:
            case ILOpCode.Ldc_i4_4:
            case ILOpCode.Ldc_i4_5:
            case ILOpCode.Ldc_i4_6:
            case ILOpCode.Ldc_i4_7:
            case ILOpCode.Ldc_i4_8:
            case ILOpCode.Dup:
            case ILOpCode.Pop:
            case ILOpCode.Ret:
            case ILOpCode.Ldind_i1:
            case ILOpCode.Ldind_u1:
            case ILOpCode.Ldind_i2:
            case ILOpCode.Ldind_u2:
            case ILOpCode.Ldind_i4:
            case ILOpCode.Ldind_u4:
            case ILOpCode.Ldind_i8:
            case ILOpCode.Ldind_i:
            case ILOpCode.Ldind_r4:
            case ILOpCode.Ldind_r8:
            case ILOpCode.Ldind_ref:
            case ILOpCode.Stind_ref:
            case ILOpCode.Stind_i1:
            case ILOpCode.Stind_i2:
            case ILOpCode.Stind_i4:
            case ILOpCode.Stind_i8:
            case ILOpCode.Stind_r4:
            case ILOpCode.Stind_r8:
            case ILOpCode.Add:
            case ILOpCode.Sub:
            case ILOpCode.Mul:
            case ILOpCode.Div:
            case ILOpCode.Div_un:
            case ILOpCode.Rem:
            case ILOpCode.Rem_un:
            case ILOpCode.And:
            case ILOpCode.Or:
            case ILOpCode.Xor:
            case ILOpCode.Shl:
            case ILOpCode.Shr:
            case ILOpCode.Shr_un:
            case ILOpCode.Neg:
            case ILOpCode.Not:
            case ILOpCode.Conv_i1:
            case ILOpCode.Conv_i2:
            case ILOpCode.Conv_i4:
            case ILOpCode.Conv_i8:
            case ILOpCode.Conv_r4:
            case ILOpCode.Conv_r8:
            case ILOpCode.Conv_u4:
            case ILOpCode.Conv_u8:
            case ILOpCode.Conv_r_un:
            case ILOpCode.Throw:
            case ILOpCode.Conv_ovf_i1_un:
            case ILOpCode.Conv_ovf_i2_un:
            case ILOpCode.Conv_ovf_i4_un:
            case ILOpCode.Conv_ovf_i8_un:
            case ILOpCode.Conv_ovf_u1_un:
            case ILOpCode.Conv_ovf_u2_un:
            case ILOpCode.Conv_ovf_u4_un:
            case ILOpCode.Conv_ovf_u8_un:
            case ILOpCode.Conv_ovf_i_un:
            case ILOpCode.Conv_ovf_u_un:
            case ILOpCode.Ldlen:
            case ILOpCode.Ldelem_i1:
            case ILOpCode.Ldelem_u1:
            case ILOpCode.Ldelem_i2:
            case ILOpCode.Ldelem_u2:
            case ILOpCode.Ldelem_i4:
            case ILOpCode.Ldelem_u4:
            case ILOpCode.Ldelem_i8:
            case ILOpCode.Ldelem_i:
            case ILOpCode.Ldelem_r4:
            case ILOpCode.Ldelem_r8:
            case ILOpCode.Ldelem_ref:
            case ILOpCode.Stelem_i:
            case ILOpCode.Stelem_i1:
            case ILOpCode.Stelem_i2:
            case ILOpCode.Stelem_i4:
            case ILOpCode.Stelem_i8:
            case ILOpCode.Stelem_r4:
            case ILOpCode.Stelem_r8:
            case ILOpCode.Stelem_ref:
            case ILOpCode.Conv_ovf_i1:
            case ILOpCode.Conv_ovf_u1:
            case ILOpCode.Conv_ovf_i2:
            case ILOpCode.Conv_ovf_u2:
            case ILOpCode.Conv_ovf_i4:
            case ILOpCode.Conv_ovf_u4:
            case ILOpCode.Conv_ovf_i8:
            case ILOpCode.Conv_ovf_u8:
            case ILOpCode.Conv_u2:
            case ILOpCode.Conv_u1:
            case ILOpCode.Conv_i:
            case ILOpCode.Conv_ovf_i:
            case ILOpCode.Conv_ovf_u:
            case ILOpCode.Add_ovf:
            case ILOpCode.Add_ovf_un:
            case ILOpCode.Mul_ovf:
            case ILOpCode.Mul_ovf_un:
            case ILOpCode.Sub_ovf:
            case ILOpCode.Sub_ovf_un:
            case ILOpCode.Endfinally:
            case ILOpCode.Stind_i:
            case ILOpCode.Conv_u:
            case ILOpCode.Arglist:
            case ILOpCode.Ceq:
            case ILOpCode.Cgt:
            case ILOpCode.Cgt_un:
            case ILOpCode.Clt:
            case ILOpCode.Clt_un:
            case ILOpCode.Localloc:
            case ILOpCode.Endfilter:
            case ILOpCode.Volatile:
            case ILOpCode.Tail:
            case ILOpCode.Cpblk:
            case ILOpCode.Initblk:
            case ILOpCode.Rethrow:
            case ILOpCode.Refanytype:
            case ILOpCode.Readonly:
                return "";

            // ShortInlineBrTarget (1-byte signed)
            case ILOpCode.Br_s:
            case ILOpCode.Brfalse_s:
            case ILOpCode.Brtrue_s:
            case ILOpCode.Beq_s:
            case ILOpCode.Bge_s:
            case ILOpCode.Bgt_s:
            case ILOpCode.Ble_s:
            case ILOpCode.Blt_s:
            case ILOpCode.Bne_un_s:
            case ILOpCode.Bge_un_s:
            case ILOpCode.Bgt_un_s:
            case ILOpCode.Ble_un_s:
            case ILOpCode.Blt_un_s:
            case ILOpCode.Leave_s:
            {
                int delta = (sbyte)il[offset++];
                int target = offset + delta;
                return $"IL_{target:X4}";
            }

            // InlineBrTarget (4-byte signed)
            case ILOpCode.Br:
            case ILOpCode.Brfalse:
            case ILOpCode.Brtrue:
            case ILOpCode.Beq:
            case ILOpCode.Bge:
            case ILOpCode.Bgt:
            case ILOpCode.Ble:
            case ILOpCode.Blt:
            case ILOpCode.Bne_un:
            case ILOpCode.Bge_un:
            case ILOpCode.Bgt_un:
            case ILOpCode.Ble_un:
            case ILOpCode.Blt_un:
            case ILOpCode.Leave:
            {
                int delta = ReadInt32(il, ref offset);
                int target = offset + delta;
                return $"IL_{target:X4}";
            }

            // ShortInlineI
            case ILOpCode.Ldc_i4_s:
                return $"{(sbyte)il[offset++]}";
            case ILOpCode.Unaligned:
                return $"{il[offset++]}";

            // InlineI
            case ILOpCode.Ldc_i4:
            {
                int val = ReadInt32(il, ref offset);
                return $"0x{val:X}";
            }

            // InlineI8
            case ILOpCode.Ldc_i8:
            {
                long val = ReadInt64(il, ref offset);
                return $"0x{val:X}";
            }

            // ShortInlineR
            case ILOpCode.Ldc_r4:
            {
                float val = ReadSingle(il, ref offset);
                return $"{val}";
            }

            // InlineR
            case ILOpCode.Ldc_r8:
            {
                double val = ReadDouble(il, ref offset);
                return $"{val}";
            }

            // ShortInlineVar (1-byte index)
            case ILOpCode.Ldloc_s:
            case ILOpCode.Stloc_s:
            case ILOpCode.Ldloca_s:
            case ILOpCode.Ldarg_s:
            case ILOpCode.Starg_s:
            case ILOpCode.Ldarga_s:
                return $"V_{il[offset++]}";

            // InlineVar (2-byte index)
            case ILOpCode.Ldloc:
            case ILOpCode.Stloc:
            case ILOpCode.Ldloca:
            case ILOpCode.Ldarg:
            case ILOpCode.Starg:
            case ILOpCode.Ldarga:
            {
                short idx = ReadInt16(il, ref offset);
                return $"V_{idx}";
            }

            // InlineMethod / InlineField / InlineType / InlineTok (4-byte token)
            case ILOpCode.Call:
            case ILOpCode.Callvirt:
            case ILOpCode.Newobj:
            case ILOpCode.Ldftn:
            case ILOpCode.Ldvirtftn:
            case ILOpCode.Jmp:
            case ILOpCode.Ldfld:
            case ILOpCode.Ldflda:
            case ILOpCode.Stfld:
            case ILOpCode.Ldsfld:
            case ILOpCode.Ldsflda:
            case ILOpCode.Stsfld:
            case ILOpCode.Castclass:
            case ILOpCode.Isinst:
            case ILOpCode.Newarr:
            case ILOpCode.Box:
            case ILOpCode.Unbox:
            case ILOpCode.Unbox_any:
            case ILOpCode.Ldobj:
            case ILOpCode.Stobj:
            case ILOpCode.Initobj:
            case ILOpCode.Cpobj:
            case ILOpCode.Sizeof:
            case ILOpCode.Ldelem:
            case ILOpCode.Stelem:
            case ILOpCode.Mkrefany:
            case ILOpCode.Refanyval:
            case ILOpCode.Constrained:
            case ILOpCode.Ldtoken:
            {
                int token = ReadInt32(il, ref offset);
                return FormatToken(reader, token);
            }

            // InlineString
            case ILOpCode.Ldstr:
            {
                int token = ReadInt32(il, ref offset);
                return FormatStringToken(reader, token);
            }

            // InlineSig
            case ILOpCode.Calli:
            {
                int token = ReadInt32(il, ref offset);
                return FormatToken(reader, token);
            }

            // InlineSwitch
            case ILOpCode.Switch:
            {
                uint count = (uint)ReadInt32(il, ref offset);
                int baseOffset = offset + (int)(count * 4);
                var targets = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    int delta = ReadInt32(il, ref offset);
                    targets[i] = $"IL_{baseOffset + delta:X4}";
                }
                return $"({string.Join(", ", targets)})";
            }

            default:
                return $"<unknown opcode 0x{(ushort)opCode:X}>";
        }
    }

    static string FormatToken(MetadataReader reader, int token)
    {
        string resolved = ResolveToken(reader, token);
        if (resolved != null)
            return $"{resolved} /* {token:X8} */";
        return $"/* {token:X8} */";
    }

    static string FormatStringToken(MetadataReader reader, int token)
    {
        try
        {
            var handle = MetadataTokens.UserStringHandle(token & 0x00FFFFFF);
            string value = reader.GetUserString(handle);
            return $"\"{Escape(value)}\" /* {token:X8} */";
        }
        catch
        {
            return $"/* {token:X8} */";
        }
    }

    static string Escape(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }

    static string ResolveToken(MetadataReader reader, int token)
    {
        int tableIndex = token >> 24;
        int rowId = token & 0x00FFFFFF;

        if (rowId == 0)
            return null;

        try
        {
            switch (tableIndex)
            {
                case 0x01: // TypeRef
                {
                    var handle = MetadataTokens.TypeReferenceHandle(rowId);
                    var typeRef = reader.GetTypeReference(handle);
                    string ns = reader.GetString(typeRef.Namespace);
                    string name = reader.GetString(typeRef.Name);
                    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                }
                case 0x02: // TypeDef
                {
                    var handle = MetadataTokens.TypeDefinitionHandle(rowId);
                    var typeDef = reader.GetTypeDefinition(handle);
                    string ns = reader.GetString(typeDef.Namespace);
                    string name = reader.GetString(typeDef.Name);
                    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                }
                case 0x04: // FieldDef
                {
                    var handle = MetadataTokens.FieldDefinitionHandle(rowId);
                    var fieldDef = reader.GetFieldDefinition(handle);
                    string name = reader.GetString(fieldDef.Name);
                    var declaringType = fieldDef.GetDeclaringType();
                    if (!declaringType.IsNil)
                    {
                        var typeDef = reader.GetTypeDefinition(declaringType);
                        string typeName = reader.GetString(typeDef.Name);
                        return $"{typeName}::{name}";
                    }
                    return name;
                }
                case 0x06: // MethodDef
                {
                    var handle = MetadataTokens.MethodDefinitionHandle(rowId);
                    var methodDef = reader.GetMethodDefinition(handle);
                    string name = reader.GetString(methodDef.Name);
                    var declaringType = methodDef.GetDeclaringType();
                    if (!declaringType.IsNil)
                    {
                        var typeDef = reader.GetTypeDefinition(declaringType);
                        string typeName = reader.GetString(typeDef.Name);
                        return $"{typeName}::{name}";
                    }
                    return name;
                }
                case 0x0A: // MemberRef
                {
                    var handle = MetadataTokens.MemberReferenceHandle(rowId);
                    var memberRef = reader.GetMemberReference(handle);
                    string name = reader.GetString(memberRef.Name);
                    var parent = memberRef.Parent;
                    string parentName = ResolveToken(reader, MetadataTokens.GetToken(parent));
                    if (parentName != null)
                        return $"{parentName}::{name}";
                    return name;
                }
                case 0x11: // StandaloneSig
                {
                    return $"StandaloneSig({rowId})";
                }
                case 0x1B: // TypeSpec
                {
                    return $"TypeSpec({rowId})";
                }
                case 0x2B: // MethodSpec
                {
                    var handle = MetadataTokens.MethodSpecificationHandle(rowId);
                    var methodSpec = reader.GetMethodSpecification(handle);
                    string methodName = ResolveToken(reader, MetadataTokens.GetToken(methodSpec.Method));
                    return methodName ?? $"MethodSpec({rowId})";
                }
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    static void DumpExceptionRegions(MetadataReader reader, MethodBodyBlock body)
    {
        Console.WriteLine("  Exception Handlers:");
        for (int i = 0; i < body.ExceptionRegions.Length; i++)
        {
            var region = body.ExceptionRegions[i];
            Console.Write($"    [{i}] {region.Kind}: ");
            Console.Write($"Try IL_{region.TryOffset:X4}..IL_{region.TryOffset + region.TryLength:X4} ");
            Console.Write($"Handler IL_{region.HandlerOffset:X4}..IL_{region.HandlerOffset + region.HandlerLength:X4}");

            if (region.Kind == ExceptionRegionKind.Catch)
            {
                int catchToken = MetadataTokens.GetToken(region.CatchType);
                string catchName = ResolveToken(reader, catchToken);
                Console.Write($" Catch={catchName ?? $"0x{catchToken:X8}"}");
            }
            else if (region.Kind == ExceptionRegionKind.Filter)
            {
                Console.Write($" Filter=IL_{region.FilterOffset:X4}");
            }

            Console.WriteLine();
        }
    }

    // ─── Debug Information Dumper ──────────────────────────────────────────────

    static void DumpDebugInfo(CoffFile coff)
    {
        using var owner = new global::Coff.CoffReader(new MemoryStream(coff.FileData), PEStreamOptions.PrefetchEntireImage);
        var sections = owner.GetSectionTableReader();
        var names = owner.GetStringTableReader();
        var readers = new List<(int Section, global::Coff.CodeViewReader Reader)>();
        var strings = new List<(int Section, global::Coff.CodeViewStringTable Table)>();
        var checksums = new List<(int Section, global::Coff.CodeViewFileChecksumTable Table)>();
        int si = 0;
        foreach (var handle in sections.Sections)
        {
            var section = sections.GetCoffSection(handle);
            if (names.GetString(section.Name) == ".debug$S")
            {
                var reader = owner.GetCodeViewReader(section);
                readers.Add((si, reader));
                foreach (var subsectionHandle in reader.Subsections)
                {
                    var subsection = reader.GetSubsection(subsectionHandle);
                    if (subsection.IsIgnored)
                        continue;
                    if (subsection.Kind == global::Coff.CodeViewSubsectionKind.StringTable)
                        strings.Add((si, subsection.GetStringTable()));
                    else if (subsection.Kind == global::Coff.CodeViewSubsectionKind.FileChecksums)
                        checksums.Add((si, subsection.GetFileChecksums()));
                }
            }
            si++;
        }
        foreach (var (index, reader) in readers)
        {
            Console.WriteLine("========================================");
            Console.WriteLine($"=== Debug Symbols (.debug$S, section {index}) ===");
            Console.WriteLine("========================================");
            Console.WriteLine();
            DumpDebugS(coff, coff.Sections[index], reader,
                SelectDebugTable(strings, index), SelectDebugTable(checksums, index));
        }

        // .debug$T — type records
        for (si = 0; si < coff.Sections.Length; si++)
        {
            var section = coff.Sections[si];
            if (section.Name == ".debug$T" && section.SizeOfRawData > 4)
            {
                Console.WriteLine("========================================");
                Console.WriteLine($"=== Debug Types (.debug$T, section {si}) ===");
                Console.WriteLine("========================================");
                Console.WriteLine();
                DumpDebugT(coff, section);
            }
        }
    }

    // ─── .debug$S parsing ─────────────────────────────────────────────────────

    static T? SelectDebugTable<T>(List<(int Section, T Table)> tables, int section) where T : struct
    {
        T? result = null;
        int count = 0;
        foreach (var item in tables)
        {
            if (item.Section != section)
                continue;
            result = item.Table;
            count++;
        }
        if (count != 0)
            return count == 1 ? result : null;
        return tables.Count == 1 ? tables[0].Table : null;
    }

    static void DumpDebugS(CoffFile coff, CoffSectionHeader section, global::Coff.CodeViewReader reader,
        global::Coff.CodeViewStringTable? strings, global::Coff.CodeViewFileChecksumTable? checksums)
    {
        var relocations = new Dictionary<int, CoffRelocation>();
        foreach (var relocation in coff.GetRelocations(section))
        {
            if (relocation.SymbolTableIndex >= coff.Symbols.Length)
                throw new BadImageFormatException("Invalid CodeView relocation symbol.");
            relocations.Add(checked((int)relocation.VirtualAddress), relocation);
        }
        var tokens = coff.BuildTokenRelocationMap(section);
        foreach (var handle in reader.Subsections)
        {
            var subsection = reader.GetSubsection(handle);
            Console.WriteLine($"  Subsection +0x{subsection.Offset:X4}, payload={subsection.PayloadLength}");
            if (subsection.IsIgnored)
            {
                Console.WriteLine($"*** IGNORED SUBSECTION type=0x{subsection.RawKind:X8}");
                DumpDebugPayload(subsection.GetPayloadReader());
                continue;
            }
            switch (subsection.Kind)
            {
                case global::Coff.CodeViewSubsectionKind.Symbols:
                    Console.WriteLine("*** SYMBOLS");
                    DumpSymbolRecords(coff, section, reader, subsection, relocations, tokens);
                    break;
                case global::Coff.CodeViewSubsectionKind.Lines:
                    DumpLineNumbers(coff, subsection.GetLines(), relocations, strings, checksums);
                    break;
                case global::Coff.CodeViewSubsectionKind.StringTable:
                    Console.WriteLine("*** STRINGTABLE");
                    var stringTable = subsection.GetStringTable();
                    foreach (var text in stringTable.Strings)
                        Console.WriteLine($"  [{text.Offset:X4}] {stringTable.GetString(text)}");
                    break;
                case global::Coff.CodeViewSubsectionKind.FileChecksums:
                    Console.WriteLine("*** FILECHKSUMS");
                    Console.WriteLine($"  {"FileId",-8} {"StrOff",-8} {"Type",-10} Checksum");
                    var checksumTable = subsection.GetFileChecksums();
                    foreach (var fileHandle in checksumTable.Files)
                    {
                        var file = checksumTable.GetFileChecksum(fileHandle);
                        var bytes = file.GetChecksumReader();
                        string typeName = (byte)file.ChecksumType switch { 0 => "None", 1 => "MD5", 2 => "SHA1", 3 => "SHA_256", var kind => $"0x{kind:X2}" };
                        string name = strings.HasValue ? strings.Value.GetString(file.Name) : "<unresolved: missing or ambiguous string table>";
                        Console.WriteLine($"  {file.Handle.Offset,-8:X4} {file.Name.Offset,-8} {typeName,-10} {Convert.ToHexString(bytes.ReadBytes(bytes.RemainingBytes))}  ({name})");
                    }
                    break;
                default:
                    Console.WriteLine($"*** SUBSECTION type=0x{subsection.RawKind:X} size={subsection.PayloadLength}");
                    DumpDebugPayload(subsection.GetPayloadReader());
                    break;
            }
            Console.WriteLine();
        }
    }

    static void DumpDebugPayload(BlobReader payload)
    {
        Console.WriteLine($"    Payload: {Convert.ToHexString(payload.ReadBytes(Math.Min(32, payload.RemainingBytes)))}{(payload.RemainingBytes == 0 ? "" : "...")}");
    }

    static uint ResolveDebugOffset(CoffFile coff, Dictionary<int, CoffRelocation> relocations, int fieldOffset, uint raw)
    {
        ushort type = coff.Header.Machine == 0xAA64 ? (ushort)0x0008 : (ushort)0x000B;
        return relocations.TryGetValue(fieldOffset, out var relocation) && relocation.Type == type
            ? unchecked(raw + coff.Symbols[relocation.SymbolTableIndex].Value) : raw;
    }

    static ushort ResolveDebugSegment(CoffFile coff, Dictionary<int, CoffRelocation> relocations, int fieldOffset, ushort raw)
    {
        ushort type = coff.Header.Machine == 0xAA64 ? (ushort)0x000D : (ushort)0x000A;
        return relocations.TryGetValue(fieldOffset, out var relocation) && relocation.Type == type
            ? checked((ushort)(raw + coff.Symbols[relocation.SymbolTableIndex].SectionNumber)) : raw;
    }

    static void DumpDebugRelocation(CoffFile coff, Dictionary<int, CoffRelocation> relocations, int fieldOffset, uint raw)
    {
        if (relocations.TryGetValue(fieldOffset, out var relocation))
        {
            var symbol = coff.Symbols[relocation.SymbolTableIndex];
            Console.WriteLine($"    Field +0x{fieldOffset:X4}: raw=0x{raw:X8}, reloc=0x{relocation.Type:X4} -> {symbol.Name} [section {symbol.SectionNumber} +0x{symbol.Value:X}]");
        }
    }

    // ─── Symbol record dumping ────────────────────────────────────────────────

    static void DumpSymbolRecords(CoffFile coff, CoffSectionHeader section, global::Coff.CodeViewReader reader,
        global::Coff.CodeViewSubsection subsection, Dictionary<int, CoffRelocation> relocations, Dictionary<int, int> tokens)
    {
        foreach (var handle in subsection.GetSymbols())
        {
            var symbol = reader.GetSymbol(handle);
            Console.WriteLine($"  Record +0x{symbol.Offset:X4}, size={symbol.Size}");
            switch (symbol.Kind)
            {
                case global::Coff.CodeViewSymbolKind.ObjName:
                    var obj = symbol.GetObjectName();
                    Console.WriteLine($"  S_OBJNAME: Signature: {obj.Signature:X8}, {obj.Name}");
                    break;
                case global::Coff.CodeViewSymbolKind.Compile3:
                    var compile = symbol.GetCompile3();
                    Console.WriteLine($"  S_COMPILE3: Language={compile.Language}, Machine={compile.Machine}, Managed={(compile.Flags & global::Coff.CodeViewCompileFlags.ManagedPresent) != 0}, Flags=0x{compile.RawFlags:X8}");
                    Console.WriteLine($"    Frontend: {compile.FrontendMajorVersion}.{compile.FrontendMinorVersion}.{compile.FrontendBuildVersion}.{compile.FrontendQfeVersion}, Backend: {compile.BackendMajorVersion}.{compile.BackendMinorVersion}.{compile.BackendBuildVersion}.{compile.BackendQfeVersion}");
                    Console.WriteLine($"    {compile.CompilerVersion}");
                    break;
                case global::Coff.CodeViewSymbolKind.GManProc:
                    var proc = symbol.GetManagedProcedure();
                    uint token = tokens.TryGetValue(proc.TokenFieldOffset, out int resolvedToken) ? (uint)resolvedToken : proc.Token;
                    uint codeOffset = ResolveDebugOffset(coff, relocations, proc.CodeOffsetFieldOffset, proc.CodeOffset);
                    ushort segment = ResolveDebugSegment(coff, relocations, proc.SegmentFieldOffset, proc.Segment);
                    Console.WriteLine($"  S_GMANPROC: [{segment:X4}:{codeOffset:X8}], Cb: {proc.CodeSize:X8}, Token: {token:X8}, {proc.Name}");
                    Console.WriteLine($"    Parent: {proc.ParentSymbolOffset:X8}, End: {proc.EndSymbolOffset:X8}, Next: {proc.NextSymbolOffset:X8}");
                    Console.WriteLine($"    Debug start: {proc.DebugStart:X8}, Debug end: {proc.DebugEnd:X8}");
                    Console.WriteLine($"    Flags: 0x{proc.Flags:X2}, Return Reg: {proc.ReturnRegister}");
                    DumpDebugRelocation(coff, relocations, proc.TokenFieldOffset, proc.Token);
                    DumpDebugRelocation(coff, relocations, proc.CodeOffsetFieldOffset, proc.CodeOffset);
                    DumpDebugRelocation(coff, relocations, proc.SegmentFieldOffset, proc.Segment);
                    break;
                case global::Coff.CodeViewSymbolKind.FrameProc:
                    var frame = symbol.GetFrameProcedure();
                    Console.WriteLine($"  S_FRAMEPROC: frame={frame.FrameSize}, pad={frame.PaddingSize}, saveRegs={frame.SavedRegistersSize}, flags=0x{(uint)frame.Flags:X8}");
                    Console.WriteLine($"    Padding offset: 0x{frame.PaddingOffset:X8}, Exception handler: [{frame.ExceptionHandlerSegment:X4}:{frame.ExceptionHandlerOffset:X8}]");
                    DumpDebugRelocation(coff, relocations, frame.ExceptionHandlerOffsetFieldOffset, frame.ExceptionHandlerOffset);
                    DumpDebugRelocation(coff, relocations, frame.ExceptionHandlerSegmentFieldOffset, frame.ExceptionHandlerSegment);
                    break;
                case global::Coff.CodeViewSymbolKind.ManSlot:
                    var slot = symbol.GetManagedSlot();
                    uint typeIndex = tokens.TryGetValue(slot.TypeIndexFieldOffset, out int resolvedType) ? (uint)resolvedType : slot.TypeIndex;
                    Console.WriteLine($"  S_MANSLOT: slot={slot.SlotIndex}, typind=0x{typeIndex:X8}, name={slot.Name}");
                    Console.WriteLine($"    Attributes: [{slot.Segment:X4}:{slot.CodeOffset:X8}], Flags=0x{slot.Flags:X4}");
                    DumpDebugRelocation(coff, relocations, slot.TypeIndexFieldOffset, slot.TypeIndex);
                    DumpDebugRelocation(coff, relocations, slot.CodeOffsetFieldOffset, slot.CodeOffset);
                    DumpDebugRelocation(coff, relocations, slot.SegmentFieldOffset, slot.Segment);
                    break;
                case global::Coff.CodeViewSymbolKind.Block32:
                    var block = symbol.GetBlock();
                    Console.WriteLine($"  S_BLOCK32: [{ResolveDebugSegment(coff, relocations, block.SegmentFieldOffset, block.Segment):X4}:{ResolveDebugOffset(coff, relocations, block.CodeOffsetFieldOffset, block.CodeOffset):X8}], Cb: {block.CodeSize:X8}, {block.Name}");
                    Console.WriteLine($"    Parent: {block.ParentSymbolOffset:X8}, End: {block.EndSymbolOffset:X8}");
                    DumpDebugRelocation(coff, relocations, block.CodeOffsetFieldOffset, block.CodeOffset);
                    DumpDebugRelocation(coff, relocations, block.SegmentFieldOffset, block.Segment);
                    break;
                case global::Coff.CodeViewSymbolKind.End:
                    Console.WriteLine("  S_END");
                    break;
                case global::Coff.CodeViewSymbolKind.ProcIdEnd:
                    Console.WriteLine("  S_PROC_ID_END");
                    break;
                default:
                    var payloadReader = symbol.GetPayloadReader();
                    byte[] payload = payloadReader.ReadBytes(payloadReader.RemainingBytes);
                    switch ((ushort)symbol.Kind)
                    {
                        case 0x112B:
                            if (payload.Length >= 28 && tokens.TryGetValue(symbol.PayloadOffset + 24, out int localToken))
                                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24), localToken);
                            DumpManProc(0x112B, payload, 0, symbol.Offset, coff.BuildSymbolRelocationMap(section));
                            break;
                        case 0x111C:
                        case 0x111D:
                            DumpManData((ushort)symbol.Kind, payload);
                            break;
                        case 0x112D:
                            DumpManConstant(payload);
                            break;
                        case 0x114C:
                            var build = symbol.GetPayloadReader();
                            Console.WriteLine($"  S_BUILDINFO: TypeIndex=0x{build.ReadUInt32():X4}");
                            break;
                        default:
                            Console.WriteLine($"  Symbol 0x{(ushort)symbol.Kind:X4} (len={symbol.Size - 2})");
                            DumpDebugPayload(symbol.GetPayloadReader());
                            break;
                    }
                    break;
            }
        }
    }

    static void DumpManProc(ushort recType, ReadOnlySpan<byte> payload, int recOffset, int baseOffset,
        Dictionary<int, (string Name, short Section)> symRelocMap)
    {
        string typeName = recType == 0x112A ? "S_GMANPROC" : "S_LMANPROC";
        if (payload.Length < 34) { Console.WriteLine($"  {typeName}: (too short)"); return; }

        uint pParent = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint pEnd = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        uint pNext = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        uint codeLen = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]);
        uint dbgStart = BinaryPrimitives.ReadUInt32LittleEndian(payload[16..]);
        uint dbgEnd = BinaryPrimitives.ReadUInt32LittleEndian(payload[20..]);
        int token = BinaryPrimitives.ReadInt32LittleEndian(payload[24..]);
        int codeOff = BinaryPrimitives.ReadInt32LittleEndian(payload[28..]);
        ushort seg = BinaryPrimitives.ReadUInt16LittleEndian(payload[32..]);
        byte procFlags = payload[34];

        // Try to resolve segment from relocation if it's 0
        if (seg == 0)
        {
            // The seg field is at recOffset + 4 (reclen+rectyp) + 32 relative to subsection start
            // = baseOffset + recOffset + 4 + 32 within section data
            int segFieldOffset = baseOffset + recOffset + 4 + 32;
            if (symRelocMap.TryGetValue(segFieldOffset, out var symInfo))
                seg = (ushort)symInfo.Section;
        }

        ushort retReg = (payload.Length > 36) ? BinaryPrimitives.ReadUInt16LittleEndian(payload[35..]) : (ushort)0;
        string name = (payload.Length > 37) ? ReadNullTermString(payload[37..]) : "?";

        Console.WriteLine($"  {typeName}: [{seg:X4}:{codeOff:X8}], Cb: {codeLen:X8}, Token: {token:X8}, {name}");
        Console.WriteLine($"    Parent: {pParent:X8}, End: {pEnd:X8}, Next: {pNext:X8}");
        Console.WriteLine($"    Debug start: {dbgStart:X8}, Debug end: {dbgEnd:X8}");
        Console.WriteLine($"    Flags: 0x{procFlags:X2}, Return Reg: {retReg}");
    }

    static void DumpManData(ushort recType, ReadOnlySpan<byte> payload)
    {
        string typeName = recType == 0x111D ? "S_GMANDATA" : "S_LMANDATA";
        if (payload.Length < 8) { Console.WriteLine($"  {typeName}: (too short)"); return; }

        uint typind = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        int off = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        ushort seg = (payload.Length >= 10) ? BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]) : (ushort)0;
        string name = (payload.Length > 10) ? ReadNullTermString(payload[10..]) : "?";

        Console.WriteLine($"  {typeName}: [{seg:X4}:{off:X8}], typind=0x{typind:X8}, {name}");
    }

    static void DumpManConstant(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 6) { Console.WriteLine("  S_MANCONSTANT: (too short)"); return; }
        uint typind = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        string name = (payload.Length > 6) ? ReadNullTermString(payload[6..]) : "?";
        Console.WriteLine($"  S_MANCONSTANT: typind=0x{typind:X8}, value={value}, name={name}");
    }

    // ─── Line number dumping ──────────────────────────────────────────────────

    static void DumpLineNumbers(CoffFile coff, global::Coff.CodeViewLineTable lines,
        Dictionary<int, CoffRelocation> relocations, global::Coff.CodeViewStringTable? strings,
        global::Coff.CodeViewFileChecksumTable? checksums)
    {
        uint offset = ResolveDebugOffset(coff, relocations, lines.CodeOffsetFieldOffset, lines.CodeOffset);
        ushort segment = ResolveDebugSegment(coff, relocations, lines.SegmentFieldOffset, lines.Segment);
        Console.WriteLine($"*** LINES [{segment:X4}:{offset:X8}-{(ulong)offset + lines.CodeSize:X8}], flags=0x{lines.Flags:X4}");
        DumpDebugRelocation(coff, relocations, lines.CodeOffsetFieldOffset, lines.CodeOffset);
        DumpDebugRelocation(coff, relocations, lines.SegmentFieldOffset, lines.Segment);
        foreach (var block in lines.FileBlocks)
        {
            string name = "<unresolved: missing or ambiguous checksum/string table>";
            if (checksums.HasValue)
            {
                var file = checksums.Value.GetFileChecksum(block.File);
                if (strings.HasValue)
                    name = strings.Value.GetString(file.Name);
            }
            Console.WriteLine($"  File: {name} (FileId={block.File.Offset:X4}, block=+0x{block.Offset:X4}, size={block.Size}, lines={block.LineCount})");
            foreach (var line in block.Lines)
                Console.WriteLine($"    line {line.StartLine,5} at 0x{line.CodeOffset:X4}{(line.IsStatement ? "" : " (expr)")}{(line.EndLineDelta == 0 ? "" : $" endLineDelta={line.EndLineDelta}")}{(line.StartColumn.HasValue ? $" columns={line.StartColumn}-{line.EndColumn}" : "")}");
        }
    }

    // ─── .debug$T parsing ─────────────────────────────────────────────────────

    static void DumpDebugT(CoffFile coff, CoffSectionHeader section)
    {
        var data = coff.GetSectionData(section);
        if (data.Length < 4) return;

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data);
        Console.WriteLine($"  Version: {version}");
        if (version != 4) { Console.WriteLine("  (unsupported version)"); return; }

        int pos = 4;
        int typeIndex = 0x1000; // first user type index

        while (pos + 4 <= data.Length)
        {
            ushort recLen = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
            if (recLen < 2 || pos + 2 + recLen > data.Length) break;

            ushort leaf = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 2)..]);
            var payload = data.Slice(pos + 4, recLen - 2);

            string desc = DecodeTypeRecord(leaf, payload);
            Console.WriteLine($"  0x{typeIndex:X4}: {desc}");

            typeIndex++;
            pos += 2 + recLen;
        }
        Console.WriteLine();
    }

    static string DecodeTypeRecord(ushort leaf, ReadOnlySpan<byte> payload)
    {
        switch (leaf)
        {
            case 0x1008: // LF_PROCEDURE
                if (payload.Length >= 12)
                {
                    uint rvtype = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    byte calltype = payload[4];
                    byte funcattr = payload[5];
                    ushort parmcount = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]);
                    uint arglist = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
                    string callName = calltype switch
                    {
                        0x00 => "C Near", 0x01 => "C Far", 0x02 => "Pascal Near",
                        0x07 => "Stdcall", 0x08 => "Syscall", 0x09 => "Thiscall",
                        0x0B => "CLR Call", _ => $"0x{calltype:X2}"
                    };
                    return $"LF_PROCEDURE retType={FormatTypeIndex(rvtype)}, callType={callName}, parms={parmcount}, argList=0x{arglist:X4}";
                }
                return $"LF_PROCEDURE (len={payload.Length})";

            case 0x1201: // LF_ARGLIST
                if (payload.Length >= 4)
                {
                    uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    var args = new List<string>();
                    for (int i = 0; i < count && 4 + i * 4 + 4 <= payload.Length; i++)
                    {
                        uint argType = BinaryPrimitives.ReadUInt32LittleEndian(payload[(4 + i * 4)..]);
                        args.Add(FormatTypeIndex(argType));
                    }
                    return $"LF_ARGLIST count={count}: ({string.Join(", ", args)})";
                }
                return "LF_ARGLIST";

            case 0x1503: // LF_ARRAY
                if (payload.Length >= 8)
                {
                    uint elemType = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    uint idxType = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
                    // size is a numeric leaf starting at offset 8
                    (ulong size, int sizeBytes) = ReadNumericLeaf(payload[8..]);
                    string name = (8 + sizeBytes < payload.Length) ? ReadNullTermString(payload[(8 + sizeBytes)..]) : "";
                    return $"LF_ARRAY elemType={FormatTypeIndex(elemType)}, idxType={FormatTypeIndex(idxType)}, size={size}";
                }
                return "LF_ARRAY";

            case 0x1601: // LF_FUNC_ID
                if (payload.Length >= 8)
                {
                    uint scopeId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    uint type = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
                    string name = ReadNullTermString(payload[8..]);
                    return $"LF_FUNC_ID scope=0x{scopeId:X4}, type=0x{type:X4}, name={name}";
                }
                return "LF_FUNC_ID";

            case 0x1605: // LF_STRING_ID
                if (payload.Length >= 4)
                {
                    uint id = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    string name = ReadNullTermString(payload[4..]);
                    string truncated = name.Length > 80 ? name[..80] + "..." : name;
                    return id != 0
                        ? $"LF_STRING_ID subStrings=0x{id:X4}, \"{truncated}\""
                        : $"LF_STRING_ID \"{truncated}\"";
                }
                return "LF_STRING_ID";

            case 0x1604: // LF_SUBSTR_LIST
                if (payload.Length >= 4)
                {
                    uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    var ids = new List<string>();
                    for (int i = 0; i < count && 4 + i * 4 + 4 <= payload.Length; i++)
                    {
                        uint id = BinaryPrimitives.ReadUInt32LittleEndian(payload[(4 + i * 4)..]);
                        ids.Add($"0x{id:X4}");
                    }
                    return $"LF_SUBSTR_LIST count={count}: ({string.Join(", ", ids)})";
                }
                return "LF_SUBSTR_LIST";

            case 0x1603: // LF_BUILDINFO
                if (payload.Length >= 2)
                {
                    ushort count = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                    var ids = new List<string>();
                    for (int i = 0; i < count && 2 + i * 4 + 4 <= payload.Length; i++)
                    {
                        uint id = BinaryPrimitives.ReadUInt32LittleEndian(payload[(2 + i * 4)..]);
                        ids.Add($"0x{id:X4}");
                    }
                    return $"LF_BUILDINFO count={count}: ({string.Join(", ", ids)})";
                }
                return "LF_BUILDINFO";

            case 0x1002: // LF_POINTER
                if (payload.Length >= 8)
                {
                    uint utype = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    uint attr = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
                    int ptrKind = (int)((attr >> 0) & 0x1F);
                    int ptrMode = (int)((attr >> 5) & 0x07);
                    int ptrSize = (int)((attr >> 13) & 0xFF);
                    return $"LF_POINTER to={FormatTypeIndex(utype)}, size={ptrSize}";
                }
                return "LF_POINTER";

            default:
                return $"Leaf=0x{leaf:X4} (len={payload.Length})";
        }
    }

    static string FormatTypeIndex(uint ti)
    {
        if (ti < 0x1000)
        {
            // Predefined type
            string name = ti switch
            {
                0x0000 => "T_NOTYPE", 0x0003 => "T_VOID", 0x0008 => "T_HRESULT",
                0x0010 => "T_CHAR", 0x0011 => "T_SHORT", 0x0012 => "T_LONG",
                0x0013 => "T_QUAD", 0x0020 => "T_UCHAR", 0x0021 => "T_USHORT",
                0x0022 => "T_ULONG", 0x0023 => "T_UQUAD",
                0x0040 => "T_REAL32", 0x0041 => "T_REAL64",
                0x0068 => "T_INT1", 0x0069 => "T_UINT1",
                0x0070 => "T_RCHAR", 0x0071 => "T_WCHAR",
                0x0072 => "T_INT2", 0x0073 => "T_UINT2",
                0x0074 => "T_INT4", 0x0075 => "T_UINT4",
                0x0076 => "T_INT8", 0x0077 => "T_UINT8",
                0x0103 => "T_32PVOID", 0x0403 => "T_32PVOID",
                0x0603 => "T_64PVOID",
                _ => null
            };
            if (name != null) return $"{name}(0x{ti:X4})";
            return $"T_(0x{ti:X4})";
        }
        return $"0x{ti:X4}";
    }

    static (ulong Value, int BytesRead) ReadNumericLeaf(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return (0, 0);
        ushort leaf = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (leaf < 0x8000)
            return (leaf, 2);

        switch (leaf)
        {
            case 0x8000: // LF_CHAR
                return (data.Length >= 3 ? data[2] : 0u, 3);
            case 0x8001: // LF_SHORT
                return (data.Length >= 4 ? (ulong)(ushort)BinaryPrimitives.ReadInt16LittleEndian(data[2..]) : 0, 4);
            case 0x8002: // LF_USHORT
                return (data.Length >= 4 ? BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) : 0u, 4);
            case 0x8003: // LF_LONG
                return (data.Length >= 6 ? (ulong)(uint)BinaryPrimitives.ReadInt32LittleEndian(data[2..]) : 0, 6);
            case 0x8004: // LF_ULONG
                return (data.Length >= 6 ? BinaryPrimitives.ReadUInt32LittleEndian(data[2..]) : 0, 6);
            default:
                return (0, 2);
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    static string ReadNullTermString(ReadOnlySpan<byte> data)
    {
        int end = data.IndexOf((byte)0);
        if (end < 0) end = data.Length;
        return Encoding.UTF8.GetString(data[..end]);
    }
}
