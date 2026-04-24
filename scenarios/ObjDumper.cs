// Normalized COFF object file dumper for test comparison.
//
// Produces a deterministic text dump of a managed COFF .obj file that is
// invariant across COMDAT (MSVC) vs merged (our emitter) section layouts.
// Skips TypeRefs/MemberRefs, .debug$T, S_OBJNAME, S_BUILDINFO.
// Normalizes paths, S_MANSLOT flags, and debug offsets.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Collections.Immutable;
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
    public uint VirtualAddress;
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

    const byte IMAGE_SYM_CLASS_CLR_TOKEN = 107;

    public static CoffFile Parse(byte[] data)
    {
        var coff = new CoffFile { FileData = data };
        coff.Header = CoffFileHeader.Read(data);

        int sectionOffset = CoffHeaderSize + coff.Header.SizeOfOptionalHeader;
        coff.Sections = new CoffSectionHeader[coff.Header.NumberOfSections];
        for (int i = 0; i < coff.Header.NumberOfSections; i++)
            coff.Sections[i] = CoffSectionHeader.Read(data.AsSpan(sectionOffset + i * SectionHeaderSize));

        if (coff.Header.PointerToSymbolTable > 0 && coff.Header.NumberOfSymbols > 0)
            coff.Symbols = ParseSymbols(data, (int)coff.Header.PointerToSymbolTable, (int)coff.Header.NumberOfSymbols);
        else
            coff.Symbols = Array.Empty<CoffSymbol>();

        // Resolve long section names
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
        int stringTableOffset = symTabOffset + count * SymbolSize;
        var symbols = new CoffSymbol[count];
        int offset = symTabOffset;

        for (int i = 0; i < count; i++)
        {
            var span = data.AsSpan(offset, SymbolSize);
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
            int auxCount = symbols[i].NumberOfAuxSymbols;
            for (int a = 0; a < auxCount && (i + 1) < count; a++)
            {
                i++;
                symbols[i] = new CoffSymbol { Name = "<aux>", NumberOfAuxSymbols = 0 };
                offset += SymbolSize;
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

    public CoffRelocation[] GetRelocations(CoffSectionHeader section)
    {
        if (section.NumberOfRelocations == 0)
            return Array.Empty<CoffRelocation>();

        var relocs = new CoffRelocation[section.NumberOfRelocations];
        int offset = (int)section.PointerToRelocations;
        for (int i = 0; i < relocs.Length; i++)
            relocs[i] = CoffRelocation.Read(FileData.AsSpan(offset + i * RelocationSize));
        return relocs;
    }

    public Dictionary<int, int> BuildTokenRelocationMap(CoffSectionHeader section)
    {
        var map = new Dictionary<int, int>();
        var relocs = GetRelocations(section);
        foreach (var r in relocs)
        {
            if (r.SymbolTableIndex >= (uint)Symbols.Length) continue;
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

    public Dictionary<int, (string Name, short Section)> BuildSymbolRelocationMap(CoffSectionHeader section)
    {
        var map = new Dictionary<int, (string, short)>();
        var relocs = GetRelocations(section);
        foreach (var r in relocs)
        {
            if (r.SymbolTableIndex >= (uint)Symbols.Length) continue;
            var sym = Symbols[r.SymbolTableIndex];
            if (sym.StorageClass != IMAGE_SYM_CLASS_CLR_TOKEN)
                map[(int)r.VirtualAddress] = (sym.Name, sym.SectionNumber);
        }
        return map;
    }

    public byte[] GetPatchedSectionData(CoffSectionHeader section)
    {
        var data = GetSectionData(section).ToArray();
        var tokenMap = BuildTokenRelocationMap(section);
        foreach (var (offset, token) in tokenMap)
        {
            if (offset + 4 <= data.Length)
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), token);
        }
        return data;
    }

    /// <summary>
    /// For each CLR token symbol that represents a field (0x04XXXXXX), find the
    /// associated real symbol (immediately preceding in the symbol table) and
    /// return (section number, offset, token, data size).
    /// </summary>
    public List<(int Token, short Section, uint Offset, int DataSize)> FindFieldDataLocations(MetadataReader reader)
    {
        var result = new List<(int, short, uint, int)>();

        for (int i = 0; i < Symbols.Length; i++)
        {
            var sym = Symbols[i];
            if (sym.StorageClass != IMAGE_SYM_CLASS_CLR_TOKEN) continue;
            if (!int.TryParse(sym.Name, System.Globalization.NumberStyles.HexNumber, null, out int token)) continue;
            if ((token >> 24) != 0x04) continue; // only field tokens

            // Find the preceding real symbol (skip <aux> entries)
            int realIdx = i - 1;
            while (realIdx >= 0 && Symbols[realIdx].Name == "<aux>") realIdx--;
            if (realIdx < 0) continue;

            var realSym = Symbols[realIdx];
            if (realSym.SectionNumber <= 0) continue;

            // Determine data size from type layout
            int row = token & 0x00FFFFFF;
            var fieldHandle = MetadataTokens.FieldDefinitionHandle(row);
            var fieldDef = reader.GetFieldDefinition(fieldHandle);
            int dataSize = GetFieldDataSize(reader, fieldDef, Header.Machine);

            result.Add((token, realSym.SectionNumber, realSym.Value, dataSize));
        }

        return result;
    }

    static int GetFieldDataSize(MetadataReader reader, FieldDefinition fieldDef, ushort machine)
    {
        // Decode the field signature to find the type
        var sigReader = reader.GetBlobReader(fieldDef.Signature);
        sigReader.ReadByte(); // calling convention (FIELD = 0x06)

        // Walk through modifiers
        while (true)
        {
            int peek = sigReader.ReadByte();
            if (peek == (int)SignatureTypeCode.OptionalModifier || peek == (int)SignatureTypeCode.RequiredModifier)
            {
                sigReader.ReadCompressedInteger(); // skip the modifier type token
                continue;
            }

            if (peek == (int)SignatureTypeCode.FunctionPointer)
            {
                // FNPTR — size is pointer-sized
                return machine == 0x014C ? 4 : 8; // I386 = 4, ARM64 = 8
            }

            if (peek == (int)SignatureTypeCode.GenericTypeParameter ||
                peek == (int)SignatureTypeCode.GenericMethodParameter)
                return -1;

            // Check for value type
            if (peek == 0x11 || peek == 0x12) // VALUETYPE or CLASS
            {
                int codedIndex = sigReader.ReadCompressedInteger();
                // TypeDefOrRefOrSpec: low 2 bits = tag (0=TypeDef, 1=TypeRef, 2=TypeSpec)
                int tag = codedIndex & 0x03;
                int row = codedIndex >> 2;

                if (tag == 0 && row > 0) // TypeDef
                {
                    var typeDef = reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(row));
                    var layout = typeDef.GetLayout();
                    if (!layout.IsDefault && layout.Size > 0)
                        return layout.Size;
                }
            }

            // Pointer type
            if (peek == (int)SignatureTypeCode.Pointer)
            {
                return machine == 0x014C ? 4 : 8;
            }

            // Primitive types
            return peek switch
            {
                0x02 => 1, // Boolean
                0x03 => 2, // Char
                0x04 => 1, // I1
                0x05 => 1, // U1
                0x06 => 2, // I2
                0x07 => 2, // U2
                0x08 => 4, // I4
                0x09 => 4, // U4
                0x0A => 8, // I8
                0x0B => 8, // U8
                0x0C => 4, // R4
                0x0D => 8, // R8
                0x18 => (machine == 0x014C ? 4 : 8), // IntPtr
                0x19 => (machine == 0x014C ? 4 : 8), // UIntPtr
                _ => -1,
            };
        }
    }
    /// <summary>
    /// For each CLR token symbol that represents a method (0x06XXXXXX), find the
    /// associated real symbol and return (token, section number, offset within section).
    /// </summary>
    public Dictionary<int, (short Section, uint Offset)> FindMethodBodyLocations()
    {
        var result = new Dictionary<int, (short, uint)>();

        for (int i = 0; i < Symbols.Length; i++)
        {
            var sym = Symbols[i];
            if (sym.StorageClass != IMAGE_SYM_CLASS_CLR_TOKEN) continue;
            if (!int.TryParse(sym.Name, System.Globalization.NumberStyles.HexNumber, null, out int token)) continue;
            if ((token >> 24) != 0x06) continue; // only method tokens

            // Find the preceding real symbol
            int realIdx = i - 1;
            while (realIdx >= 0 && Symbols[realIdx].Name == "<aux>") realIdx--;
            if (realIdx < 0) continue;

            var realSym = Symbols[realIdx];
            if (realSym.SectionNumber <= 0) continue;

            result[token] = (realSym.SectionNumber, realSym.Value);
        }

        return result;
    }
}

class CompileInfo
{
    public string Language;
    public string Machine;
    public uint Flags;
    public ushort FeMajor, FeMinor, FeBuild;
    public ushort BeMajor, BeMinor, BeBuild;
    public string CompilerName;
}

class MethodDebugInfo
{
    public List<string> Records = new();
    public List<string> Lines = new();
}

// ─── Main dumper ──────────────────────────────────────────────────────────────

static class ObjDumper
{
    /// <summary>
    /// Normalizes ?A0x&lt;hash&gt; prefixes in names to ?A0x* since the hash depends
    /// on compilation context (source file path) which differs between environments.
    /// </summary>
    static string NormalizeName(string name)
    {
        // Replace ?A0x<hex>.  with ?A0x*.
        int idx = name.IndexOf("?A0x", StringComparison.Ordinal);
        if (idx >= 0)
        {
            int dotIdx = name.IndexOf('.', idx + 4);
            if (dotIdx > idx + 4)
            {
                // Verify the part between ?A0x and . is hex
                string hashPart = name.Substring(idx + 4, dotIdx - idx - 4);
                if (hashPart.Length > 0 && hashPart.All(c => "0123456789abcdef".Contains(c)))
                    return name.Substring(0, idx) + "?A0x*" + name.Substring(dotIdx);
            }
        }
        return name;
    }

    public static string DumpForComparison(byte[] objData)
    {
        var sb = new StringBuilder();
        var coff = CoffFile.Parse(objData);

        var cormetaSection = coff.FindSection(".cormeta");
        if (cormetaSection == null)
            throw new InvalidOperationException("No .cormeta section found");

        var metadataBytes = coff.GetSectionData(cormetaSection.Value).ToArray();
        unsafe
        {
            fixed (byte* ptr = metadataBytes)
            {
                var reader = new MetadataReader(ptr, metadataBytes.Length);

                DumpTypeDefs(sb, reader);
                DumpFieldDefs(sb, reader, coff);
                DumpMethodBodies(sb, reader, coff);
                DumpDebugInfo(sb, reader, coff);
            }
        }

        return sb.ToString();
    }

    // ─── TypeDefs ─────────────────────────────────────────────────────────

    static bool IsBoilerplateType(string ns, string name)
    {
        // MSVC boilerplate: vc.cppcli.attributes.*, vc.cppcli.modopts.*
        if (ns.StartsWith("vc.cppcli.", StringComparison.Ordinal)) return true;
        // MSVC anonymous namespace types: ?A0x<hash>.*
        if (ns.StartsWith("?A0x", StringComparison.Ordinal) && name.StartsWith("__clr_", StringComparison.Ordinal)) return true;
        return false;
    }

    static bool IsBoilerplateMethod(string methodName)
    {
        // MSVC CRT initialization stubs
        return methodName.StartsWith("__CxxPure", StringComparison.Ordinal);
    }

    static HashSet<TypeDefinitionHandle> GetBoilerplateTypes(MetadataReader reader)
    {
        var result = new HashSet<TypeDefinitionHandle>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var typeDef = reader.GetTypeDefinition(handle);
            string ns = reader.GetString(typeDef.Namespace);
            string name = reader.GetString(typeDef.Name);
            if (IsBoilerplateType(ns, name))
                result.Add(handle);
        }
        return result;
    }

    static void DumpTypeDefs(StringBuilder sb, MetadataReader reader)
    {
        sb.AppendLine("=== TypeDefs ===");
        foreach (var handle in reader.TypeDefinitions)
        {
            var typeDef = reader.GetTypeDefinition(handle);
            string ns = reader.GetString(typeDef.Namespace);
            string name = reader.GetString(typeDef.Name);
            string fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

            // Skip MSVC boilerplate types
            if (IsBoilerplateType(ns, name)) continue;

            sb.Append($"{NormalizeName(fullName)} (Flags=0x{(uint)typeDef.Attributes:X8}");

            if (!typeDef.BaseType.IsNil)
                sb.Append($", Base={NormalizeName(ResolveHandle(reader, typeDef.BaseType))}");

            var layout = typeDef.GetLayout();
            if (!layout.IsDefault)
                sb.Append($", Pack={layout.PackingSize}, Size={layout.Size}");

            sb.AppendLine(")");

            foreach (var caHandle in typeDef.GetCustomAttributes())
            {
                var ca = reader.GetCustomAttribute(caHandle);
                string attrType = ResolveConstructorType(reader, ca.Constructor);
                sb.AppendLine($"  Attr: {attrType}");
            }
        }
        sb.AppendLine();
    }

    // ─── Fields ───────────────────────────────────────────────────────────

    static void DumpFieldDefs(StringBuilder sb, MetadataReader reader, CoffFile coff)
    {
        sb.AppendLine("=== Fields ===");

        var boilerplate = GetBoilerplateTypes(reader);
        var fieldDataLocations = coff.FindFieldDataLocations(reader);
        var fieldDataMap = new Dictionary<int, (short Section, uint Offset, int DataSize)>();
        foreach (var (token, section, offset, dataSize) in fieldDataLocations)
            fieldDataMap[token] = (section, offset, dataSize);

        int fieldCount = reader.GetTableRowCount(TableIndex.Field);
        for (int i = 1; i <= fieldCount; i++)
        {
            var fieldHandle = MetadataTokens.FieldDefinitionHandle(i);
            var fieldDef = reader.GetFieldDefinition(fieldHandle);

            // Skip fields on boilerplate types
            var declaringType = fieldDef.GetDeclaringType();
            if (!declaringType.IsNil && boilerplate.Contains(declaringType)) continue;

            string fieldName = reader.GetString(fieldDef.Name);
            string typeName = declaringType.IsNil ? "" : reader.GetString(reader.GetTypeDefinition(declaringType).Name);
            string fullName = string.IsNullOrEmpty(typeName) ? fieldName : $"{typeName}::{fieldName}";

            sb.AppendLine($"{NormalizeName(fullName)} (Flags=0x{(ushort)fieldDef.Attributes:X4})");

            // Field signature (type)
            var sigProvider = new SignatureTypeProvider(reader);
            string fieldType = fieldDef.DecodeSignature(sigProvider, null);
            sb.AppendLine($"  FieldSig: {fieldType}");

            // RVA data
            if (fieldDef.Attributes.HasFlag(FieldAttributes.HasFieldRVA))
            {
                int token = MetadataTokens.GetToken(fieldHandle);
                if (fieldDataMap.TryGetValue(token, out var loc) && loc.DataSize > 0)
                {
                    int sectionIdx = loc.Section - 1;
                    if (sectionIdx >= 0 && sectionIdx < coff.Sections.Length)
                    {
                        var section = coff.Sections[sectionIdx];
                        // Check if this data has token relocations
                        var tokenMap = coff.BuildTokenRelocationMap(section);
                        bool hasTokenReloc = false;
                        foreach (var (relocOff, _) in tokenMap)
                        {
                            if (relocOff >= (int)loc.Offset && relocOff < (int)loc.Offset + loc.DataSize)
                            {
                                hasTokenReloc = true;
                                break;
                            }
                        }

                        if (hasTokenReloc)
                        {
                            // Data contains token references — resolve them to names
                            byte[] rawData = coff.GetSectionData(section).ToArray();
                            var parts = new List<string>();
                            int dataStart = (int)loc.Offset;
                            int dataEnd = Math.Min(dataStart + loc.DataSize, rawData.Length);
                            foreach (var (relocOff, relocToken) in tokenMap)
                            {
                                if (relocOff >= dataStart && relocOff + 4 <= dataEnd)
                                {
                                    string resolved = NormalizeName(ResolveTokenForDisplay(reader, relocToken));
                                    parts.Add($"Token({resolved})");
                                }
                            }
                            sb.AppendLine($"  Data: {string.Join(", ", parts)}");
                        }
                        else
                        {
                            byte[] patchedData = coff.GetPatchedSectionData(section);
                            int end = Math.Min((int)loc.Offset + loc.DataSize, patchedData.Length);
                            byte[] fieldData = patchedData[(int)loc.Offset..end];
                            sb.AppendLine($"  Data[{fieldData.Length}]: {FormatHexBytes(fieldData)}");
                        }
                    }
                }
            }

            foreach (var caHandle in fieldDef.GetCustomAttributes())
            {
                var ca = reader.GetCustomAttribute(caHandle);
                string attrType = ResolveConstructorType(reader, ca.Constructor);
                sb.AppendLine($"  Attr: {attrType}");
            }
        }
        sb.AppendLine();
    }

    // ─── Method bodies ────────────────────────────────────────────────────

    static void DumpMethodBodies(StringBuilder sb, MetadataReader reader, CoffFile coff)
    {
        sb.AppendLine("=== Methods ===");

        var boilerplate = GetBoilerplateTypes(reader);
        var methodLocations = coff.FindMethodBodyLocations();
        var sigProvider = new SignatureTypeProvider(reader);

        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(methodHandle);
            string name = reader.GetString(method.Name);
            var declaringType = method.GetDeclaringType();
            string typeName = declaringType.IsNil ? "" : reader.GetString(reader.GetTypeDefinition(declaringType).Name);
            string fullName = string.IsNullOrEmpty(typeName) ? name : $"{typeName}::{name}";

            bool isBoilerplate = !declaringType.IsNil && boilerplate.Contains(declaringType);
            if (isBoilerplate || IsBoilerplateMethod(name)) continue;

            int token = MetadataTokens.GetToken(methodHandle);

            if ((method.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL ||
                method.Attributes.HasFlag(MethodAttributes.Abstract))
            {
                sb.AppendLine($"{NormalizeName(fullName)} (Flags=0x{(ushort)method.Attributes:X4}, Impl=0x{(ushort)method.ImplAttributes:X4})");
                sb.AppendLine($"  Sig: {FormatMethodSignature(method, sigProvider)}");
                sb.AppendLine("  (no body)");
                continue;
            }

            // Find the method body via COFF symbol mapping
            if (!methodLocations.TryGetValue(token, out var loc))
            {
                sb.AppendLine($"{NormalizeName(fullName)} (Flags=0x{(ushort)method.Attributes:X4}, Impl=0x{(ushort)method.ImplAttributes:X4})");
                sb.AppendLine($"  Sig: {FormatMethodSignature(method, sigProvider)}");
                sb.AppendLine("  (body not found in symbol table)");
                continue;
            }

            int sectionIdx = loc.Section - 1;
            if (sectionIdx < 0 || sectionIdx >= coff.Sections.Length) continue;

            var section = coff.Sections[sectionIdx];
            byte[] patchedData = coff.GetPatchedSectionData(section);
            int bodyOffset = (int)loc.Offset;

            if (bodyOffset >= patchedData.Length) continue;

            unsafe
            {
                fixed (byte* ptr = patchedData)
                {
                    var bodyReader = new BlobReader(ptr + bodyOffset, patchedData.Length - bodyOffset);
                    var body = MethodBodyBlock.Create(bodyReader);

                    string locals = body.LocalSignature.IsNil ? "none" : "yes";
                    byte[] ilBytes = body.GetILBytes();
                    sb.AppendLine($"{NormalizeName(fullName)} (Flags=0x{(ushort)method.Attributes:X4}, Impl=0x{(ushort)method.ImplAttributes:X4})");
                    sb.AppendLine($"  Sig: {FormatMethodSignature(method, sigProvider)}");
                    sb.AppendLine($"  CodeSize={ilBytes.Length}, Locals={locals}");

                    if (!body.LocalSignature.IsNil)
                    {
                        var localSig = reader.GetStandaloneSignature(body.LocalSignature);
                        var localTypes = localSig.DecodeLocalSignature(sigProvider, null);
                        sb.AppendLine($"  LocalSig: ({string.Join(", ", localTypes)})");
                    }

                    DumpIL(sb, reader, ilBytes);

                    if (body.ExceptionRegions.Length > 0)
                        DumpExceptionRegions(sb, reader, body);
                }
            }
        }
        sb.AppendLine();
    }

    // ─── IL dump ──────────────────────────────────────────────────────────

    static void DumpIL(StringBuilder sb, MetadataReader reader, byte[] ilBytes)
    {
        int offset = 0;
        while (offset < ilBytes.Length)
        {
            int instrStart = offset;
            ILOpCode opCode = ReadOpCode(ilBytes, ref offset);
            string operandStr = ReadOperand(reader, ilBytes, ref offset, opCode);

            string opCodeStr = FormatOpCode(opCode);
            if (operandStr.Length > 0)
                sb.AppendLine($"  IL_{instrStart:X4}: {opCodeStr,-16} {operandStr}");
            else
                sb.AppendLine($"  IL_{instrStart:X4}: {opCodeStr}");
        }
    }

    static ILOpCode ReadOpCode(byte[] il, ref int offset)
    {
        byte b = il[offset++];
        if (b == 0xFE)
            return (ILOpCode)(0xFE00 | il[offset++]);
        return (ILOpCode)b;
    }

    static string FormatOpCode(ILOpCode opCode)
    {
        return opCode.ToString().ToLowerInvariant().Replace('_', '.');
    }

    static string ReadOperand(MetadataReader reader, byte[] il, ref int offset, ILOpCode opCode)
    {
        switch (GetOperandType(opCode))
        {
            case OperandType.InlineNone:
                return "";

            case OperandType.ShortInlineBrTarget:
            {
                int delta = (sbyte)il[offset++];
                return $"IL_{offset + delta:X4}";
            }

            case OperandType.InlineBrTarget:
            {
                int delta = ReadInt32(il, ref offset);
                return $"IL_{offset + delta:X4}";
            }

            case OperandType.ShortInlineI:
                return opCode == ILOpCode.Ldc_i4_s ? $"{(sbyte)il[offset++]}" : $"{il[offset++]}";

            case OperandType.InlineI:
                return $"0x{ReadInt32(il, ref offset):X}";

            case OperandType.InlineI8:
                return $"0x{ReadInt64(il, ref offset):X}";

            case OperandType.ShortInlineR:
            {
                float val = BitConverter.ToSingle(il, offset);
                offset += 4;
                return $"{val}";
            }

            case OperandType.InlineR:
            {
                double val = BitConverter.ToDouble(il, offset);
                offset += 8;
                return $"{val}";
            }

            case OperandType.ShortInlineVar:
                return $"V_{il[offset++]}";

            case OperandType.InlineVar:
            {
                short idx = BinaryPrimitives.ReadInt16LittleEndian(il.AsSpan(offset));
                offset += 2;
                return $"V_{idx}";
            }

            case OperandType.InlineToken:
            {
                int token = ReadInt32(il, ref offset);
                return FormatToken(reader, token);
            }

            case OperandType.InlineString:
            {
                int token = ReadInt32(il, ref offset);
                return FormatStringToken(reader, token);
            }

            case OperandType.InlineSig:
            {
                int token = ReadInt32(il, ref offset);
                return FormatToken(reader, token);
            }

            case OperandType.InlineSwitch:
            {
                uint count = (uint)ReadInt32(il, ref offset);
                int baseOff = offset + (int)(count * 4);
                var targets = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    int delta = ReadInt32(il, ref offset);
                    targets[i] = $"IL_{baseOff + delta:X4}";
                }
                return $"({string.Join(", ", targets)})";
            }

            default:
                return $"<unknown opcode 0x{(ushort)opCode:X}>";
        }
    }

    static string FormatToken(MetadataReader reader, int token)
    {
        string resolved = ResolveTokenForDisplay(reader, token);
        return NormalizeName(resolved);
    }

    static string FormatStringToken(MetadataReader reader, int token)
    {
        try
        {
            var handle = MetadataTokens.UserStringHandle(token & 0x00FFFFFF);
            string value = reader.GetUserString(handle);
            return $"\"{EscapeString(value)}\"";
        }
        catch
        {
            return $"0x{token:X8}";
        }
    }

    // ─── Exception regions ────────────────────────────────────────────────

    static void DumpExceptionRegions(StringBuilder sb, MetadataReader reader, MethodBodyBlock body)
    {
        sb.AppendLine("  ExceptionHandlers:");
        for (int i = 0; i < body.ExceptionRegions.Length; i++)
        {
            var region = body.ExceptionRegions[i];
            sb.Append($"    [{i}] {region.Kind}: ");
            sb.Append($"Try IL_{region.TryOffset:X4}..IL_{region.TryOffset + region.TryLength:X4} ");
            sb.Append($"Handler IL_{region.HandlerOffset:X4}..IL_{region.HandlerOffset + region.HandlerLength:X4}");
            if (region.Kind == ExceptionRegionKind.Catch)
            {
                int catchToken = MetadataTokens.GetToken(region.CatchType);
                sb.Append($" Catch={ResolveTokenForDisplay(reader, catchToken)}");
            }
            sb.AppendLine();
        }
    }

    // ─── Debug info ───────────────────────────────────────────────────────

    static void DumpDebugInfo(StringBuilder sb, MetadataReader reader, CoffFile coff)
    {
        sb.AppendLine("=== Debug ===");

        var boilerplate = GetBoilerplateTypes(reader);
        // Build a set of method tokens that belong to boilerplate types
        var boilerplateMethodTokens = new HashSet<int>();
        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(methodHandle);
            string methodName = reader.GetString(method.Name);
            var dt = method.GetDeclaringType();
            if ((!dt.IsNil && boilerplate.Contains(dt)) || IsBoilerplateMethod(methodName))
                boilerplateMethodTokens.Add(MetadataTokens.GetToken(methodHandle));
        }

        CompileInfo compile = null;
        var methodDebug = new SortedDictionary<int, MethodDebugInfo>();

        for (int si = 0; si < coff.Sections.Length; si++)
        {
            if (coff.Sections[si].Name == ".debug$S" && coff.Sections[si].SizeOfRawData > 4)
                ParseDebugS(coff, coff.Sections[si], reader, ref compile, methodDebug);
        }

        // S_COMPILE3
        if (compile != null)
        {
            sb.AppendLine($"S_COMPILE3: Lang={compile.Language}, Mach={compile.Machine}, Flags=0x{compile.Flags:X4}");
            sb.AppendLine($"  FE={compile.FeMajor}.{compile.FeMinor}.{compile.FeBuild}, BE={compile.BeMajor}.{compile.BeMinor}.{compile.BeBuild}");
            sb.AppendLine($"  {compile.CompilerName}");
        }
        sb.AppendLine();

        // Per-method debug records, in metadata order, skipping boilerplate
        foreach (var methodHandle in reader.MethodDefinitions)
        {
            int token = MetadataTokens.GetToken(methodHandle);
            if (boilerplateMethodTokens.Contains(token)) continue;

            var method = reader.GetMethodDefinition(methodHandle);
            string name = reader.GetString(method.Name);
            var dt = method.GetDeclaringType();
            string typeName = dt.IsNil ? "" : reader.GetString(reader.GetTypeDefinition(dt).Name);
            string fullName = string.IsNullOrEmpty(typeName) ? name : $"{typeName}::{name}";

            if (methodDebug.TryGetValue(token, out var info))
            {
                sb.AppendLine($"Method {NormalizeName(fullName)}:");
                foreach (var rec in info.Records)
                    sb.AppendLine($"  {rec}");
            }
        }
        sb.AppendLine();

        // Lines
        sb.AppendLine("=== Lines ===");
        foreach (var methodHandle in reader.MethodDefinitions)
        {
            int token = MetadataTokens.GetToken(methodHandle);
            if (boilerplateMethodTokens.Contains(token)) continue;

            var method = reader.GetMethodDefinition(methodHandle);
            string name = reader.GetString(method.Name);
            var dt = method.GetDeclaringType();
            string typeName = dt.IsNil ? "" : reader.GetString(reader.GetTypeDefinition(dt).Name);
            string fullName = string.IsNullOrEmpty(typeName) ? name : $"{typeName}::{name}";

            if (methodDebug.TryGetValue(token, out var info) && info.Lines.Count > 0)
            {
                sb.AppendLine($"Method {NormalizeName(fullName)}:");
                foreach (var line in info.Lines)
                    sb.AppendLine($"  {line}");
            }
            else
            {
                sb.AppendLine($"Method {NormalizeName(fullName)}: (no lines)");
            }
        }
        sb.AppendLine();
    }

    static void ParseDebugS(CoffFile coff, CoffSectionHeader section, MetadataReader reader,
        ref CompileInfo compile, SortedDictionary<int, MethodDebugInfo> methodDebug)
    {
        byte[] data = coff.GetSectionData(section).ToArray();

        // Apply token relocations
        var tokenMap = coff.BuildTokenRelocationMap(section);
        foreach (var (offset, token) in tokenMap)
        {
            if (offset + 4 <= data.Length)
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), token);
        }

        var symRelocMap = coff.BuildSymbolRelocationMap(section);

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (version != 4) return;

        // First pass: string table + file checksums
        var stringTable = new Dictionary<int, string>();
        var fileChecksums = new List<(int FileId, int StringOffset, byte ChecksumType, byte[] Checksum)>();
        int pos = 4;
        while (pos + 8 <= data.Length)
        {
            uint subType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            int subSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 4));
            if (subSize < 0 || pos + 8 + subSize > data.Length) break;

            var subData = data.AsSpan(pos + 8, subSize);
            if (subType == 0xF3) ParseStringTable(subData, stringTable);
            else if (subType == 0xF4) ParseFileChecksums(subData, fileChecksums);

            pos += 8 + subSize;
            pos = (pos + 3) & ~3;
        }

        // Track current method for grouping
        int currentMethodToken = 0;
        int currentMethodCodeOff = 0;

        // Second pass: symbol records + lines
        pos = 4;
        while (pos + 8 <= data.Length)
        {
            uint subType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            int subSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 4));
            if (subSize < 0 || pos + 8 + subSize > data.Length) break;

            int subStart = pos + 8;
            var subData = data.AsSpan(subStart, subSize);

            if (subType == 0xF1) // SYMBOLS
            {
                ParseSymbolRecords(subData, subStart, symRelocMap, reader,
                    ref compile, methodDebug, ref currentMethodToken, ref currentMethodCodeOff);
            }
            else if (subType == 0xF2) // LINES
            {
                ParseLineNumbers(subData, subStart, symRelocMap, stringTable, fileChecksums,
                    methodDebug, currentMethodToken, currentMethodCodeOff);
            }

            pos += 8 + subSize;
            pos = (pos + 3) & ~3;
        }
    }

    static void ParseStringTable(ReadOnlySpan<byte> data, Dictionary<int, string> table)
    {
        int spos = 0;
        while (spos < data.Length)
        {
            int end = data[spos..].IndexOf((byte)0);
            if (end < 0) break;
            string s = Encoding.UTF8.GetString(data.Slice(spos, end));
            table[spos] = s;
            spos += end + 1;
        }
    }

    static void ParseFileChecksums(ReadOnlySpan<byte> data, List<(int, int, byte, byte[])> checksums)
    {
        checksums.Clear();
        int fpos = 0;
        while (fpos + 6 <= data.Length)
        {
            int strOff = BinaryPrimitives.ReadInt32LittleEndian(data[fpos..]);
            byte cbChk = data[fpos + 4];
            byte chkType = data[fpos + 5];
            byte[] chkData = (fpos + 6 + cbChk <= data.Length)
                ? data.Slice(fpos + 6, cbChk).ToArray()
                : Array.Empty<byte>();
            checksums.Add((fpos, strOff, chkType, chkData));
            fpos += 6 + cbChk;
            fpos = (fpos + 3) & ~3;
        }
    }

    static void ParseSymbolRecords(ReadOnlySpan<byte> data, int baseOffset,
        Dictionary<int, (string Name, short Section)> symRelocMap, MetadataReader reader,
        ref CompileInfo compile, SortedDictionary<int, MethodDebugInfo> methodDebug,
        ref int currentMethodToken, ref int currentMethodCodeOff)
    {
        int spos = 0;
        while (spos + 4 <= data.Length)
        {
            ushort recLen = BinaryPrimitives.ReadUInt16LittleEndian(data[spos..]);
            if (recLen < 2 || spos + 2 + recLen > data.Length) break;

            ushort recType = BinaryPrimitives.ReadUInt16LittleEndian(data[(spos + 2)..]);
            var payload = data.Slice(spos + 4, recLen - 2);

            switch (recType)
            {
                case 0x1101: // S_OBJNAME — skip
                    break;

                case 0x113C: // S_COMPILE3
                    if (compile == null)
                        compile = ParseCompile3(payload);
                    break;

                case 0x112A: // S_GMANPROC
                case 0x112B: // S_LMANPROC
                {
                    if (payload.Length >= 34)
                    {
                        uint codeLen = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]);
                        int token = BinaryPrimitives.ReadInt32LittleEndian(payload[24..]);
                        int codeOff = BinaryPrimitives.ReadInt32LittleEndian(payload[28..]);
                        string procName = (payload.Length > 37) ? ReadNullTermString(payload[37..]) : "?";

                        currentMethodToken = token;
                        currentMethodCodeOff = codeOff;

                        if (!methodDebug.ContainsKey(token))
                            methodDebug[token] = new MethodDebugInfo();

                        string typeName = recType == 0x112A ? "S_GMANPROC" : "S_LMANPROC";
                        methodDebug[token].Records.Add($"{typeName}: Len=0x{codeLen:X}, Name={NormalizeName(procName)}");
                    }
                    break;
                }

                case 0x1012: // S_FRAMEPROC
                {
                    if (payload.Length >= 24 && currentMethodToken != 0)
                    {
                        uint cbFrame = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                        uint cbPad = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
                        uint cbSaveRegs = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]);
                        // S_FRAMEPROC flags omitted: fOptSpeed and fSecurityChecks
                        // differ between our emitter and MSVC based on arch/method shape.
                        // Our emitter hardcodes 0x00100200; MSVC varies per method.

                        methodDebug[currentMethodToken].Records.Add(
                            $"S_FRAMEPROC: frame={cbFrame}, pad={cbPad}, saveRegs={cbSaveRegs}");
                    }
                    break;
                }

                case 0x1120: // S_MANSLOT
                {
                    if (payload.Length >= 10 && currentMethodToken != 0)
                    {
                        uint iSlot = BinaryPrimitives.ReadUInt32LittleEndian(payload);

                        // Check fIsParam flag — skip parameter slots
                        ushort attrFlags = 0;
                        if (payload.Length >= 18)
                            attrFlags = BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]);

                        if ((attrFlags & 0x0001) != 0) // fIsParam
                            break;

                        string slotName = (payload.Length > 16) ? ReadNullTermString(payload[16..]) : "?";

                        methodDebug[currentMethodToken].Records.Add(
                            $"S_MANSLOT: slot={iSlot}, name={slotName}");
                    }
                    break;
                }

                case 0x1103: // S_BLOCK32
                {
                    if (payload.Length >= 18 && currentMethodToken != 0)
                    {
                        uint len = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
                        int off = BinaryPrimitives.ReadInt32LittleEndian(payload[12..]);
                        // Normalize offset relative to method body
                        int relOff = off - currentMethodCodeOff;

                        methodDebug[currentMethodToken].Records.Add(
                            $"S_BLOCK32: off=0x{relOff:X}, len=0x{len:X}");
                    }
                    break;
                }

                case 0x114F: // S_PROC_ID_END
                case 0x0006: // S_END
                {
                    if (currentMethodToken != 0)
                    {
                        string endName = recType == 0x114F ? "S_PROC_ID_END" : "S_END";
                        methodDebug[currentMethodToken].Records.Add(endName);
                        if (recType == 0x114F)
                            currentMethodToken = 0;
                    }
                    break;
                }

                case 0x114C: // S_BUILDINFO — skip
                    break;

                case 0x111C: // S_LMANDATA — skip (not in our scenarios)
                case 0x111D: // S_GMANDATA — skip
                    break;
            }

            spos += 2 + recLen;
        }
    }

    static CompileInfo ParseCompile3(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 22) return null;

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);

        byte lang = (byte)(flags & 0xFF);
        string langName = lang switch
        {
            0x00 => "C", 0x01 => "C++", 0x0A => "C#", 0x0C => "ILASM",
            _ => $"0x{lang:X2}"
        };

        string machineName = machine switch
        {
            0x03 => "80386", 0x07 => "PentiumIII", 0xD0 => "x64", 0xF6 => "ARM64",
            _ => $"0x{machine:X4}"
        };

        return new CompileInfo
        {
            Language = langName,
            Machine = machineName,
            Flags = flags,
            FeMajor = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]),
            FeMinor = BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]),
            FeBuild = BinaryPrimitives.ReadUInt16LittleEndian(payload[10..]),
            BeMajor = BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]),
            BeMinor = BinaryPrimitives.ReadUInt16LittleEndian(payload[16..]),
            BeBuild = BinaryPrimitives.ReadUInt16LittleEndian(payload[18..]),
            CompilerName = ReadNullTermString(payload[22..]),
        };
    }

    static void ParseLineNumbers(ReadOnlySpan<byte> data, int baseOffset,
        Dictionary<int, (string Name, short Section)> symRelocMap,
        Dictionary<int, string> stringTable,
        List<(int FileId, int StringOffset, byte ChecksumType, byte[] Checksum)> fileChecksums,
        SortedDictionary<int, MethodDebugInfo> methodDebug,
        int currentMethodToken, int currentMethodCodeOff)
    {
        if (data.Length < 12 || currentMethodToken == 0) return;

        int offCon = BinaryPrimitives.ReadInt32LittleEndian(data);

        int lpos = 12;
        while (lpos + 12 <= data.Length)
        {
            int offFile = BinaryPrimitives.ReadInt32LittleEndian(data[lpos..]);
            int nLines = BinaryPrimitives.ReadInt32LittleEndian(data[(lpos + 4)..]);
            int cbBlock = BinaryPrimitives.ReadInt32LittleEndian(data[(lpos + 8)..]);
            lpos += 12;

            // Resolve file name — use filename only
            string fileName = ResolveFileIdNormalized(offFile, fileChecksums, stringTable);

            // File checksum
            string checksumStr = "";
            foreach (var (fid, strOff, chkType, chkData) in fileChecksums)
            {
                if (fid == offFile)
                {
                    string typeName = chkType switch { 1 => "MD5", 2 => "SHA1", 3 => "SHA256", _ => $"0x{chkType:X2}" };
                    checksumStr = $" [{typeName}: {Convert.ToHexString(chkData)}]";
                    break;
                }
            }

            if (!methodDebug.ContainsKey(currentMethodToken))
                methodDebug[currentMethodToken] = new MethodDebugInfo();

            methodDebug[currentMethodToken].Lines.Add($"File: {fileName}{checksumStr}");

            for (int ln = 0; ln < nLines && lpos + 8 <= data.Length; ln++)
            {
                uint lineOff = BinaryPrimitives.ReadUInt32LittleEndian(data[lpos..]);
                uint lineFlags = BinaryPrimitives.ReadUInt32LittleEndian(data[(lpos + 4)..]);
                int lineNum = (int)(lineFlags & 0x00FFFFFF);
                lpos += 8;

                // Normalize offset relative to method body
                int relOff = (int)lineOff - (offCon - currentMethodCodeOff);
                // For COMDAT: offCon == currentMethodCodeOff == 0, so relOff == lineOff
                // For merged: offCon == currentMethodCodeOff, so relOff == lineOff
                // Either way, relOff should equal lineOff since LINES offsets are relative to offCon
                // Actually, lineOff is already relative to offCon within the contribution
                // So for both COMDAT and merged, lineOff IS the relative offset within the method

                methodDebug[currentMethodToken].Lines.Add($"  line {lineNum} at +0x{lineOff:X4}");
            }

            // Skip column info
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            if ((flags & 0x0001) != 0)
                lpos += nLines * 4;
        }
    }

    static string ResolveFileIdNormalized(int fileId,
        List<(int FileId, int StringOffset, byte ChecksumType, byte[] Checksum)> fileChecksums,
        Dictionary<int, string> stringTable)
    {
        foreach (var (fid, strOff, _, _) in fileChecksums)
        {
            if (fid == fileId)
            {
                if (stringTable.TryGetValue(strOff, out string path))
                    return Path.GetFileName(path); // filename only
                return $"strOff={strOff}";
            }
        }
        return $"fileId=0x{fileId:X}";
    }

    // ─── Token resolution ─────────────────────────────────────────────────

    static string ResolveTokenForDisplay(MetadataReader reader, int token)
    {
        int table = token >> 24;
        int row = token & 0x00FFFFFF;
        if (row == 0) return $"0x{token:X8}";

        string prefix = table switch
        {
            0x01 => "TypeRef",
            0x02 => "TypeDef",
            0x04 => "Field",
            0x06 => "Method",
            0x0A => "MemberRef",
            0x11 => "StandaloneSig",
            0x1B => "TypeSpec",
            0x2B => "MethodSpec",
            _ => $"Table0x{table:X2}"
        };

        string name = ResolveTokenName(reader, token);
        return name != null ? $"{prefix}:{name}" : $"0x{token:X8}";
    }

    static string ResolveTokenName(MetadataReader reader, int token)
    {
        int table = token >> 24;
        int row = token & 0x00FFFFFF;
        if (row == 0) return null;

        try
        {
            switch (table)
            {
                case 0x01: // TypeRef
                {
                    var typeRef = reader.GetTypeReference(MetadataTokens.TypeReferenceHandle(row));
                    string ns = reader.GetString(typeRef.Namespace);
                    string name = reader.GetString(typeRef.Name);
                    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                }
                case 0x02: // TypeDef
                {
                    var typeDef = reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(row));
                    string ns = reader.GetString(typeDef.Namespace);
                    string name = reader.GetString(typeDef.Name);
                    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                }
                case 0x04: // Field
                {
                    var fieldDef = reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(row));
                    string name = reader.GetString(fieldDef.Name);
                    var dt = fieldDef.GetDeclaringType();
                    if (!dt.IsNil)
                    {
                        string tn = reader.GetString(reader.GetTypeDefinition(dt).Name);
                        return $"{tn}::{name}";
                    }
                    return name;
                }
                case 0x06: // Method
                {
                    var methodDef = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(row));
                    string name = reader.GetString(methodDef.Name);
                    var dt = methodDef.GetDeclaringType();
                    if (!dt.IsNil)
                    {
                        string tn = reader.GetString(reader.GetTypeDefinition(dt).Name);
                        return $"{tn}::{name}";
                    }
                    return name;
                }
                case 0x0A: // MemberRef
                {
                    var memberRef = reader.GetMemberReference(MetadataTokens.MemberReferenceHandle(row));
                    string name = reader.GetString(memberRef.Name);
                    string parentName = ResolveTokenName(reader, MetadataTokens.GetToken(memberRef.Parent));
                    return parentName != null ? $"{parentName}::{name}" : name;
                }
                case 0x11: // StandaloneSig
                    return $"StandaloneSig({row})";
                case 0x1B: // TypeSpec
                    return $"TypeSpec({row})";
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    static string ResolveHandle(MetadataReader reader, EntityHandle handle)
    {
        return ResolveTokenName(reader, MetadataTokens.GetToken(handle)) ?? $"0x{MetadataTokens.GetToken(handle):X8}";
    }

    static string ResolveConstructorType(MetadataReader reader, EntityHandle ctorHandle)
    {
        try
        {
            if (ctorHandle.Kind == HandleKind.MemberReference)
            {
                var mr = reader.GetMemberReference((MemberReferenceHandle)ctorHandle);
                return ResolveTokenName(reader, MetadataTokens.GetToken(mr.Parent)) ?? "?";
            }
            if (ctorHandle.Kind == HandleKind.MethodDefinition)
            {
                var md = reader.GetMethodDefinition((MethodDefinitionHandle)ctorHandle);
                var dt = md.GetDeclaringType();
                if (!dt.IsNil)
                {
                    var td = reader.GetTypeDefinition(dt);
                    string ns = reader.GetString(td.Namespace);
                    string name = reader.GetString(td.Name);
                    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                }
            }
        }
        catch { }
        return "?";
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

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

    static string ReadNullTermString(ReadOnlySpan<byte> data)
    {
        int end = data.IndexOf((byte)0);
        if (end < 0) end = data.Length;
        return Encoding.UTF8.GetString(data[..end]);
    }

    static string EscapeString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    static string FormatHexBytes(byte[] data)
    {
        return string.Join(" ", data.Select(b => b.ToString("X2")));
    }

    // ─── Operand type classification ──────────────────────────────────────

    enum OperandType
    {
        InlineNone,
        ShortInlineBrTarget,
        InlineBrTarget,
        ShortInlineI,
        InlineI,
        InlineI8,
        ShortInlineR,
        InlineR,
        ShortInlineVar,
        InlineVar,
        InlineToken,
        InlineString,
        InlineSig,
        InlineSwitch,
        Unknown,
    }

    static OperandType GetOperandType(ILOpCode opCode)
    {
        switch (opCode)
        {
            case ILOpCode.Nop: case ILOpCode.Break:
            case ILOpCode.Ldarg_0: case ILOpCode.Ldarg_1: case ILOpCode.Ldarg_2: case ILOpCode.Ldarg_3:
            case ILOpCode.Ldloc_0: case ILOpCode.Ldloc_1: case ILOpCode.Ldloc_2: case ILOpCode.Ldloc_3:
            case ILOpCode.Stloc_0: case ILOpCode.Stloc_1: case ILOpCode.Stloc_2: case ILOpCode.Stloc_3:
            case ILOpCode.Ldnull:
            case ILOpCode.Ldc_i4_m1: case ILOpCode.Ldc_i4_0: case ILOpCode.Ldc_i4_1: case ILOpCode.Ldc_i4_2:
            case ILOpCode.Ldc_i4_3: case ILOpCode.Ldc_i4_4: case ILOpCode.Ldc_i4_5: case ILOpCode.Ldc_i4_6:
            case ILOpCode.Ldc_i4_7: case ILOpCode.Ldc_i4_8:
            case ILOpCode.Dup: case ILOpCode.Pop: case ILOpCode.Ret:
            case ILOpCode.Ldind_i1: case ILOpCode.Ldind_u1: case ILOpCode.Ldind_i2: case ILOpCode.Ldind_u2:
            case ILOpCode.Ldind_i4: case ILOpCode.Ldind_u4: case ILOpCode.Ldind_i8: case ILOpCode.Ldind_i:
            case ILOpCode.Ldind_r4: case ILOpCode.Ldind_r8: case ILOpCode.Ldind_ref:
            case ILOpCode.Stind_ref: case ILOpCode.Stind_i1: case ILOpCode.Stind_i2: case ILOpCode.Stind_i4:
            case ILOpCode.Stind_i8: case ILOpCode.Stind_r4: case ILOpCode.Stind_r8: case ILOpCode.Stind_i:
            case ILOpCode.Add: case ILOpCode.Sub: case ILOpCode.Mul: case ILOpCode.Div: case ILOpCode.Div_un:
            case ILOpCode.Rem: case ILOpCode.Rem_un:
            case ILOpCode.And: case ILOpCode.Or: case ILOpCode.Xor: case ILOpCode.Shl: case ILOpCode.Shr: case ILOpCode.Shr_un:
            case ILOpCode.Neg: case ILOpCode.Not:
            case ILOpCode.Conv_i1: case ILOpCode.Conv_i2: case ILOpCode.Conv_i4: case ILOpCode.Conv_i8:
            case ILOpCode.Conv_r4: case ILOpCode.Conv_r8: case ILOpCode.Conv_u4: case ILOpCode.Conv_u8:
            case ILOpCode.Conv_r_un: case ILOpCode.Conv_u2: case ILOpCode.Conv_u1: case ILOpCode.Conv_i:
            case ILOpCode.Conv_u:
            case ILOpCode.Conv_ovf_i1: case ILOpCode.Conv_ovf_u1: case ILOpCode.Conv_ovf_i2:
            case ILOpCode.Conv_ovf_u2: case ILOpCode.Conv_ovf_i4: case ILOpCode.Conv_ovf_u4:
            case ILOpCode.Conv_ovf_i8: case ILOpCode.Conv_ovf_u8:
            case ILOpCode.Conv_ovf_i: case ILOpCode.Conv_ovf_u:
            case ILOpCode.Conv_ovf_i1_un: case ILOpCode.Conv_ovf_i2_un: case ILOpCode.Conv_ovf_i4_un:
            case ILOpCode.Conv_ovf_i8_un: case ILOpCode.Conv_ovf_u1_un: case ILOpCode.Conv_ovf_u2_un:
            case ILOpCode.Conv_ovf_u4_un: case ILOpCode.Conv_ovf_u8_un:
            case ILOpCode.Conv_ovf_i_un: case ILOpCode.Conv_ovf_u_un:
            case ILOpCode.Throw: case ILOpCode.Rethrow:
            case ILOpCode.Ldlen:
            case ILOpCode.Ldelem_i1: case ILOpCode.Ldelem_u1: case ILOpCode.Ldelem_i2: case ILOpCode.Ldelem_u2:
            case ILOpCode.Ldelem_i4: case ILOpCode.Ldelem_u4: case ILOpCode.Ldelem_i8: case ILOpCode.Ldelem_i:
            case ILOpCode.Ldelem_r4: case ILOpCode.Ldelem_r8: case ILOpCode.Ldelem_ref:
            case ILOpCode.Stelem_i: case ILOpCode.Stelem_i1: case ILOpCode.Stelem_i2: case ILOpCode.Stelem_i4:
            case ILOpCode.Stelem_i8: case ILOpCode.Stelem_r4: case ILOpCode.Stelem_r8: case ILOpCode.Stelem_ref:
            case ILOpCode.Add_ovf: case ILOpCode.Add_ovf_un: case ILOpCode.Mul_ovf: case ILOpCode.Mul_ovf_un:
            case ILOpCode.Sub_ovf: case ILOpCode.Sub_ovf_un:
            case ILOpCode.Endfinally: case ILOpCode.Endfilter:
            case ILOpCode.Arglist: case ILOpCode.Ceq: case ILOpCode.Cgt: case ILOpCode.Cgt_un:
            case ILOpCode.Clt: case ILOpCode.Clt_un: case ILOpCode.Localloc:
            case ILOpCode.Volatile: case ILOpCode.Tail: case ILOpCode.Cpblk: case ILOpCode.Initblk:
            case ILOpCode.Refanytype: case ILOpCode.Readonly:
                return OperandType.InlineNone;

            case ILOpCode.Br_s: case ILOpCode.Brfalse_s: case ILOpCode.Brtrue_s:
            case ILOpCode.Beq_s: case ILOpCode.Bge_s: case ILOpCode.Bgt_s: case ILOpCode.Ble_s: case ILOpCode.Blt_s:
            case ILOpCode.Bne_un_s: case ILOpCode.Bge_un_s: case ILOpCode.Bgt_un_s: case ILOpCode.Ble_un_s: case ILOpCode.Blt_un_s:
            case ILOpCode.Leave_s:
                return OperandType.ShortInlineBrTarget;

            case ILOpCode.Br: case ILOpCode.Brfalse: case ILOpCode.Brtrue:
            case ILOpCode.Beq: case ILOpCode.Bge: case ILOpCode.Bgt: case ILOpCode.Ble: case ILOpCode.Blt:
            case ILOpCode.Bne_un: case ILOpCode.Bge_un: case ILOpCode.Bgt_un: case ILOpCode.Ble_un: case ILOpCode.Blt_un:
            case ILOpCode.Leave:
                return OperandType.InlineBrTarget;

            case ILOpCode.Ldc_i4_s: case ILOpCode.Unaligned:
                return OperandType.ShortInlineI;

            case ILOpCode.Ldc_i4:
                return OperandType.InlineI;

            case ILOpCode.Ldc_i8:
                return OperandType.InlineI8;

            case ILOpCode.Ldc_r4:
                return OperandType.ShortInlineR;

            case ILOpCode.Ldc_r8:
                return OperandType.InlineR;

            case ILOpCode.Ldloc_s: case ILOpCode.Stloc_s: case ILOpCode.Ldloca_s:
            case ILOpCode.Ldarg_s: case ILOpCode.Starg_s: case ILOpCode.Ldarga_s:
                return OperandType.ShortInlineVar;

            case ILOpCode.Ldloc: case ILOpCode.Stloc: case ILOpCode.Ldloca:
            case ILOpCode.Ldarg: case ILOpCode.Starg: case ILOpCode.Ldarga:
                return OperandType.InlineVar;

            case ILOpCode.Call: case ILOpCode.Callvirt: case ILOpCode.Newobj:
            case ILOpCode.Ldftn: case ILOpCode.Ldvirtftn: case ILOpCode.Jmp:
            case ILOpCode.Ldfld: case ILOpCode.Ldflda: case ILOpCode.Stfld:
            case ILOpCode.Ldsfld: case ILOpCode.Ldsflda: case ILOpCode.Stsfld:
            case ILOpCode.Castclass: case ILOpCode.Isinst:
            case ILOpCode.Newarr: case ILOpCode.Box: case ILOpCode.Unbox: case ILOpCode.Unbox_any:
            case ILOpCode.Ldobj: case ILOpCode.Stobj: case ILOpCode.Initobj: case ILOpCode.Cpobj:
            case ILOpCode.Sizeof: case ILOpCode.Ldelem: case ILOpCode.Stelem:
            case ILOpCode.Mkrefany: case ILOpCode.Refanyval:
            case ILOpCode.Constrained: case ILOpCode.Ldtoken:
                return OperandType.InlineToken;

            case ILOpCode.Ldstr:
                return OperandType.InlineString;

            case ILOpCode.Calli:
                return OperandType.InlineSig;

            case ILOpCode.Switch:
                return OperandType.InlineSwitch;

            default:
                return OperandType.Unknown;
        }
    }

    // ─── Signature decoding ───────────────────────────────────────────────

    static string FormatMethodSignature(MethodDefinition method, SignatureTypeProvider sigProvider)
    {
        try
        {
            var methodSig = method.DecodeSignature(sigProvider, null);
            string retType = methodSig.ReturnType;
            string paramTypes = string.Join(", ", methodSig.ParameterTypes);
            return $"{retType}({paramTypes})";
        }
        catch
        {
            return "?";
        }
    }

    class SignatureTypeProvider : ISignatureTypeProvider<string, object>
    {
        private readonly MetadataReader _reader;
        public SignatureTypeProvider(MetadataReader reader) => _reader = reader;

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Void => "void",
            PrimitiveTypeCode.Boolean => "bool",
            PrimitiveTypeCode.SByte => "int8",
            PrimitiveTypeCode.Byte => "uint8",
            PrimitiveTypeCode.Int16 => "int16",
            PrimitiveTypeCode.UInt16 => "uint16",
            PrimitiveTypeCode.Int32 => "int32",
            PrimitiveTypeCode.UInt32 => "uint32",
            PrimitiveTypeCode.Int64 => "int64",
            PrimitiveTypeCode.UInt64 => "uint64",
            PrimitiveTypeCode.Single => "float32",
            PrimitiveTypeCode.Double => "float64",
            PrimitiveTypeCode.IntPtr => "native int",
            PrimitiveTypeCode.UIntPtr => "native uint",
            PrimitiveTypeCode.String => "string",
            PrimitiveTypeCode.Object => "object",
            PrimitiveTypeCode.TypedReference => "typedref",
            PrimitiveTypeCode.Char => "char",
            _ => $"primitive({typeCode})"
        };

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var td = reader.GetTypeDefinition(handle);
            string name = reader.GetString(td.Name);
            return $"ValueType {NormalizeName(name)}";
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var tr = reader.GetTypeReference(handle);
            string name = reader.GetString(tr.Name);
            return NormalizeName(name);
        }

        public string GetPointerType(string elementType) => $"Ptr {elementType}";
        public string GetByReferenceType(string elementType) => $"ByRef {elementType}";

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
        {
            string prefix = isRequired ? "modreq" : "modopt";
            string shortMod = modifier;
            if (shortMod.Contains('.')) shortMod = shortMod.Substring(shortMod.LastIndexOf('.') + 1);
            return $"{prefix}({shortMod}) {unmodifiedType}";
        }

        public string GetPinnedType(string elementType) => $"pinned {elementType}";
        public string GetSZArrayType(string elementType) => $"{elementType}[]";
        public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{shape.Rank}]";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            $"{genericType}<{string.Join(", ", typeArguments)}>";
        public string GetGenericMethodParameter(object genericContext, int index) => $"!!{index}";
        public string GetGenericTypeParameter(object genericContext, int index) => $"!{index}";

        public string GetFunctionPointerType(MethodSignature<string> signature)
        {
            string args = string.Join(", ", signature.ParameterTypes);
            return $"FnPtr {signature.ReturnType}({args})";
        }

        public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        }
    }
}
